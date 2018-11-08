using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics.Imaging;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace SDKTemplate
{
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    class FrameRenderer
    {
        private Image _imageElement;
        private SoftwareBitmap _backBuffer;
        private bool _taskRunning = false;

        public FrameRenderer(Image imageElement)
        {
            _imageElement = imageElement;
            _imageElement.Source = new SoftwareBitmapSource();
        }

        public SoftwareBitmap ProcessFrame(MediaFrameReference frame)
        {
            var softwareBitmap = FrameRenderer.ConvertToDisplayableImage(frame?.VideoMediaFrame);

            var softwareBitmap2 = softwareBitmap != null ? SoftwareBitmap.Copy(softwareBitmap) : null;
            if (softwareBitmap != null)
            {
                softwareBitmap =
                    Interlocked.Exchange(ref _backBuffer, softwareBitmap);

                var task = _imageElement.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    async () =>
                    {
                        if (_taskRunning)
                        {
                            return;
                        }
                        _taskRunning = true;

                        SoftwareBitmap latestBitmap;
                        while ((latestBitmap = Interlocked.Exchange(ref _backBuffer, null)) != null)
                        {
                            var imageSource = (SoftwareBitmapSource)_imageElement.Source;
                            await imageSource.SetBitmapAsync(latestBitmap);
                            latestBitmap.Dispose();
                        }

                        _taskRunning = false;
                    });
            }
            return softwareBitmap2;
        }

        private unsafe delegate void TransformScanline(int pixelWidth, byte* inputRowBytes, byte* outputRowBytes);

        /// <summary>
        /// Determines the subtype to request from the MediaFrameReader that will result in
        /// a frame that can be rendered by ConvertToDisplayableImage.
        /// </summary>
        /// <returns>Subtype string to request, or null if subtype is not renderable.</returns>
        public static string GetSubtypeForFrameReader(MediaFrameSourceKind kind, MediaFrameFormat format)
        {
            string subtype = format.Subtype;
            switch (kind)
            {
                case MediaFrameSourceKind.Color:
                    return MediaEncodingSubtypes.Bgra8;

                case MediaFrameSourceKind.Depth:
                    return String.Equals(subtype, MediaEncodingSubtypes.D16, StringComparison.OrdinalIgnoreCase) ? subtype : null;

                case MediaFrameSourceKind.Infrared:
                    return (String.Equals(subtype, MediaEncodingSubtypes.L8, StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(subtype, MediaEncodingSubtypes.L16, StringComparison.OrdinalIgnoreCase)) ? subtype : null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Converts a frame to a SoftwareBitmap of a valid format to display in an Image control.
        /// </summary>
        /// <param name="inputFrame">Frame to convert.</param>
        public static unsafe SoftwareBitmap ConvertToDisplayableImage(VideoMediaFrame inputFrame)
        {
            SoftwareBitmap result = null;
            using (var inputBitmap = inputFrame?.SoftwareBitmap)
            {
                if (inputBitmap != null)
                {
                    switch (inputFrame.FrameReference.SourceKind)
                    {
                        case MediaFrameSourceKind.Color:
                            if (inputBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                            {
                                Debug.WriteLine("Color frame in unexpected format.");
                            }
                            else if (inputBitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied)
                            {
                                result = SoftwareBitmap.Copy(inputBitmap);
                            }
                            else
                            {
                                result = SoftwareBitmap.Convert(inputBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                            }
                            break;

                        case MediaFrameSourceKind.Depth:
                            if (inputBitmap.BitmapPixelFormat == BitmapPixelFormat.Gray16)
                            {
                                var depthScale = (float)inputFrame.DepthMediaFrame.DepthFormat.DepthScaleInMeters;
                                var minReliableDepth = inputFrame.DepthMediaFrame.MinReliableDepth;
                                var maxReliableDepth = inputFrame.DepthMediaFrame.MaxReliableDepth;
                                result = TransformBitmap(inputBitmap, (w, i, o) => PseudoColorHelper.PseudoColorForDepth(w, i, o, depthScale, minReliableDepth, maxReliableDepth));
                            }
                            else
                            {
                                Debug.WriteLine("Depth frame in unexpected format.");
                            }
                            break;

                        case MediaFrameSourceKind.Infrared:
                            switch (inputBitmap.BitmapPixelFormat)
                            {
                                case BitmapPixelFormat.Gray16:
                                    result = TransformBitmap(inputBitmap, PseudoColorHelper.PseudoColorFor16BitInfrared);
                                    break;

                                case BitmapPixelFormat.Gray8:

                                    result = TransformBitmap(inputBitmap, PseudoColorHelper.PseudoColorFor8BitInfrared);
                                    break;

                                default:
                                    Debug.WriteLine("Infrared frame in unexpected format.");
                                    break;
                            }
                            break;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Transform image into Bgra8 image using given transform method.
        /// </summary>
        /// <param name="softwareBitmap">Input image to transform.</param>
        /// <param name="transformScanline">Method to map pixels in a scanline.</param>
        private static unsafe SoftwareBitmap TransformBitmap(SoftwareBitmap softwareBitmap, TransformScanline transformScanline)
        {
            var outputBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8,
                softwareBitmap.PixelWidth, softwareBitmap.PixelHeight, BitmapAlphaMode.Premultiplied);

            using (var input = softwareBitmap.LockBuffer(BitmapBufferAccessMode.Read))
            using (var output = outputBitmap.LockBuffer(BitmapBufferAccessMode.Write))
            {
                int inputStride = input.GetPlaneDescription(0).Stride;
                int outputStride = output.GetPlaneDescription(0).Stride;
                int pixelWidth = softwareBitmap.PixelWidth;
                int pixelHeight = softwareBitmap.PixelHeight;

                using (var outputReference = output.CreateReference())
                using (var inputReference = input.CreateReference())
                {
                    byte* inputBytes;
                    uint inputCapacity;
                    ((IMemoryBufferByteAccess)inputReference).GetBuffer(out inputBytes, out inputCapacity);
                    byte* outputBytes;
                    uint outputCapacity;
                    ((IMemoryBufferByteAccess)outputReference).GetBuffer(out outputBytes, out outputCapacity);

                    for (int y = 0; y < pixelHeight; y++)
                    {
                        byte* inputRowBytes = inputBytes + y * inputStride;
                        byte* outputRowBytes = outputBytes + y * outputStride;

                        transformScanline(pixelWidth, inputRowBytes, outputRowBytes);
                    }
                }
            }
            return outputBitmap;
        }

        /// <summary>
        /// A helper class to manage look-up-table for pseudo-colors.
        /// </summary>
        private static class PseudoColorHelper
        {
            #region Constructor, private members and methods

            private const int TableSize = 1024;
            private static readonly uint[] PseudoColorTable;
            private static readonly uint[] InfraredRampTable;

            private static readonly Color[] ColorRamp =
            {
                Color.FromArgb(a:0xFF, r:0x7F, g:0x00, b:0x00),
                Color.FromArgb(a:0xFF, r:0xFF, g:0x00, b:0x00),
                Color.FromArgb(a:0xFF, r:0xFF, g:0x7F, b:0x00),
                Color.FromArgb(a:0xFF, r:0xFF, g:0xFF, b:0x00),
                Color.FromArgb(a:0xFF, r:0x7F, g:0xFF, b:0x7F),
                Color.FromArgb(a:0xFF, r:0x00, g:0xFF, b:0xFF),
                Color.FromArgb(a:0xFF, r:0x00, g:0x7F, b:0xFF),
                Color.FromArgb(a:0xFF, r:0x00, g:0x00, b:0xFF),
                Color.FromArgb(a:0xFF, r:0x00, g:0x00, b:0x7F),
            };

            static PseudoColorHelper()
            {
                PseudoColorTable = InitializePseudoColorLut();
                InfraredRampTable = InitializeInfraredRampLut();
            }

            /// <summary>
            /// Maps an input infrared value between [0, 1] to corrected value between [0, 1].
            /// </summary>
            /// <param name="value">Input value between [0, 1].</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static uint InfraredColor(float value)
            {
                int index = (int)(value * TableSize);
                index = index < 0 ? 0 : index > TableSize - 1 ? TableSize - 1 : index;
                return InfraredRampTable[index];
            }

            /// <summary>
            /// Initializes the pseudo-color look up table for infrared pixels
            /// </summary>
            private static uint[] InitializeInfraredRampLut()
            {
                uint[] lut = new uint[TableSize];
                for (int i = 0; i < TableSize; i++)
                {
                    var value = (float)i / TableSize;
                    var alpha = (float)Math.Pow(1 - value, 12);
                    lut[i] = ColorRampInterpolation(alpha);
                }
                return lut;
            }

            /// <summary>
            /// Initializes pseudo-color look up table for depth pixels
            /// </summary>
            private static uint[] InitializePseudoColorLut()
            {
                uint[] lut = new uint[TableSize];
                for (int i = 0; i < TableSize; i++)
                {
                    lut[i] = ColorRampInterpolation((float)i / TableSize);
                }
                return lut;
            }

            /// <summary>
            /// Maps a float value to a pseudo-color pixel
            /// </summary>
            private static uint ColorRampInterpolation(float value)
            {
                int rampSteps = ColorRamp.Length - 1;
                float scaled = value * rampSteps;
                int integer = (int)scaled;
                int index =
                    integer < 0 ? 0 :
                    integer >= rampSteps - 1 ? rampSteps - 1 :
                    integer;
                Color prev = ColorRamp[index];
                Color next = ColorRamp[index + 1];

                uint alpha = (uint)((scaled - integer) * 255);
                uint beta = 255 - alpha;
                return
                    ((prev.A * beta + next.A * alpha) / 255) << 24 |
                    ((prev.R * beta + next.R * alpha) / 255) << 16 |
                    ((prev.G * beta + next.G * alpha) / 255) << 8 |
                    ((prev.B * beta + next.B * alpha) / 255);
            }

            /// <summary>
            /// Maps a value in [0, 1] to a pseudo RGBA color.
            /// </summary>
            /// <param name="value">Input value between [0, 1].</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static uint PseudoColor(float value)
            {
                int index = (int)(value * TableSize);
                index = index < 0 ? 0 : index > TableSize - 1 ? TableSize - 1 : index;
                return PseudoColorTable[index];
            }

            #endregion

            /// <summary>
            /// Maps each pixel in a scanline from a 16 bit depth value to a pseudo-color pixel.
            /// </summary>
            /// <param name="pixelWidth">Width of the input scanline, in pixels.</param>
            /// <param name="inputRowBytes">Pointer to the start of the input scanline.</param>
            /// <param name="outputRowBytes">Pointer to the start of the output scanline.</param>
            /// <param name="depthScale">Physical distance that corresponds to one unit in the input scanline.</param>
            /// <param name="minReliableDepth">Shortest distance at which the sensor can provide reliable measurements.</param>
            /// <param name="maxReliableDepth">Furthest distance at which the sensor can provide reliable measurements.</param>
            public static unsafe void PseudoColorForDepth(int pixelWidth, byte* inputRowBytes, byte* outputRowBytes, float depthScale, float minReliableDepth, float maxReliableDepth)
            {
                float minInMeters = minReliableDepth * depthScale;
                float maxInMeters = maxReliableDepth * depthScale;
                float one_min = 1.0f / minInMeters;
                float range = 1.0f / maxInMeters - one_min;

                ushort* inputRow = (ushort*)inputRowBytes;
                uint* outputRow = (uint*)outputRowBytes;
                for (int x = 0; x < pixelWidth; x++)
                {
                    var depth = inputRow[x] * depthScale;

                    if (depth == 0)
                    {
                        outputRow[x] = 0;
                    }
                    else
                    {
                        var alpha = (1.0f / depth - one_min) / range;
                        outputRow[x] = PseudoColor(alpha * alpha);
                    }
                }
            }

            /// <summary>
            /// Maps each pixel in a scanline from a 8 bit infrared value to a pseudo-color pixel.
            /// </summary>
            /// /// <param name="pixelWidth">Width of the input scanline, in pixels.</param>
            /// <param name="inputRowBytes">Pointer to the start of the input scanline.</param>
            /// <param name="outputRowBytes">Pointer to the start of the output scanline.</param>
            public static unsafe void PseudoColorFor8BitInfrared(
                int pixelWidth, byte* inputRowBytes, byte* outputRowBytes)
            {
                byte* inputRow = inputRowBytes;
                uint* outputRow = (uint*)outputRowBytes;
                for (int x = 0; x < pixelWidth; x++)
                {
                    outputRow[x] = InfraredColor(inputRow[x] / (float)Byte.MaxValue);
                }
            }

            /// <summary>
            /// Maps each pixel in a scanline from a 16 bit infrared value to a pseudo-color pixel.
            /// </summary>
            /// <param name="pixelWidth">Width of the input scanline.</param>
            /// <param name="inputRowBytes">Pointer to the start of the input scanline.</param>
            /// <param name="outputRowBytes">Pointer to the start of the output scanline.</param>
            public static unsafe void PseudoColorFor16BitInfrared(int pixelWidth, byte* inputRowBytes, byte* outputRowBytes)
            {
                ushort* inputRow = (ushort*)inputRowBytes;
                uint* outputRow = (uint*)outputRowBytes;
                for (int x = 0; x < pixelWidth; x++)
                {
                    outputRow[x] = InfraredColor(inputRow[x] / (float)UInt16.MaxValue);
                }
            }
        }
    }
}
