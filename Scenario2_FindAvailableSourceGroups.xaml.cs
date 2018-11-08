using SDKTemplate.MediaFrameQrProcessing.ZXing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using ZXing;

namespace SDKTemplate
{
    public sealed partial class Scenario2_FindAvailableSourceGroups : Page
    {
        private MediaCapture _mediaCapture;
        private MediaFrameSource _source;
        private MediaFrameReader _reader;
        private FrameRenderer _frameRenderer;
        private bool _streaming = false;

        private SourceGroupCollection _groupCollection;
        private readonly SimpleLogger _logger;
        MainFrameViewModel viewModel;
        public Scenario2_FindAvailableSourceGroups()
        {
            InitializeComponent();
            viewModel = new MainFrameViewModel();
            DataContext = viewModel;
            _logger = new SimpleLogger(outputTextBlock);
            _frameRenderer = new FrameRenderer(PreviewImage);

        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            /// SourceGroupCollection will setup device watcher to monitor
            /// SourceGroup devices enabled or disabled from the system.
            _groupCollection = new SourceGroupCollection(this.Dispatcher);
            GroupComboBox.ItemsSource = _groupCollection.FrameSourceGroups;
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            _groupCollection?.Dispose();
            await StopReaderAsync();
            DisposeMediaCapture();
        }

        /// <summary>
        /// Disposes of the MediaCapture object and clears the items from the Format and Source ComboBoxes.
        /// </summary>
        private void DisposeMediaCapture()
        {
            FormatComboBox.ItemsSource = null;
            SourceComboBox.ItemsSource = null;

            _source = null;

            _mediaCapture?.Dispose();
            _mediaCapture = null;
        }

        /// <summary>
        /// Stops reading from the previous selection and starts reading frames from the newly selected source group.
        /// </summary>
        private async void GroupComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await StopReaderAsync();
            DisposeMediaCapture();

            var group = GroupComboBox.SelectedItem as FrameSourceGroupModel;
            if (group != null)
            {
                await InitializeCaptureAsync();

                SourceComboBox.ItemsSource = group.SourceInfos;
                SourceComboBox.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// Starts reading from the newly selected source.
        /// </summary>
        private async void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await StopReaderAsync();

            if (SourceComboBox.SelectedItem != null)
            {
                await StartReaderAsync();

                IEnumerable<FrameFormatModel> formats = null;
                if (_mediaCapture != null && _source != null)
                {
                    formats = _source.SupportedFormats
                        .Where(format => FrameRenderer.GetSubtypeForFrameReader(_source.Info.SourceKind, format) != null)
                        .Select(format => new FrameFormatModel(format));
                }

                FormatComboBox.ItemsSource = formats;
            }
        }

        /// <summary>
        /// Sets the video format for the current frame source.
        /// </summary>
        private async void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var format = FormatComboBox.SelectedItem as FrameFormatModel;
            await ChangeMediaFormatAsync(format);
        }

        /// <summary>
        /// Starts reading frames from the current reader.
        /// </summary>
        private async Task StartReaderAsync()
        {
            await CreateReaderAsync();

            if (_reader != null && !_streaming)
            {
                MediaFrameReaderStartStatus result = await _reader.StartAsync();
                _logger.Log($"Start reader with result: {result}");

                if (result == MediaFrameReaderStartStatus.Success)
                {
                    _streaming = true;
                }
            }
        }

        /// <summary>
        /// Creates a frame reader from the current frame source and registers to handle its frame events.
        /// </summary>
        private async Task CreateReaderAsync()
        {
            await InitializeCaptureAsync();

            UpdateFrameSource();

            if (_source != null)
            {
                string requestedSubtype = FrameRenderer.GetSubtypeForFrameReader(_source.Info.SourceKind, _source.CurrentFormat);
                if (requestedSubtype != null)
                {
                    _reader = await _mediaCapture.CreateFrameReaderAsync(_source, requestedSubtype);

                    _reader.FrameArrived += Reader_FrameArrived;

                    _logger.Log($"Reader created on source: {_source.Info.Id}");
                }
                else
                {
                    _logger.Log($"Cannot render current format on source: {_source.Info.Id}");
                }
            }
        }

        /// <summary>
        /// Updates the current frame source to the one corresponding to the user's selection.
        /// </summary>
        private void UpdateFrameSource()
        {
            var info = SourceComboBox.SelectedItem as FrameSourceInfoModel;
            if (_mediaCapture != null && info != null && info.SourceGroup != null)
            {
                var groupModel = GroupComboBox.SelectedItem as FrameSourceGroupModel;
                if (groupModel == null || groupModel.Id != info.SourceGroup.Id)
                {
                    SourceComboBox.SelectedItem = null;
                    return;
                }

                if (_source == null || _source.Info.Id != info.SourceInfo.Id)
                {
                    _mediaCapture.FrameSources.TryGetValue(info.SourceInfo.Id, out _source);
                }
            }
            else
            {
                _source = null;
            }
        }

        /// <summary>
        /// Initializes the MediaCapture object with the current source group.
        /// </summary>
        private async Task InitializeCaptureAsync()
        {
            var groupModel = GroupComboBox.SelectedItem as FrameSourceGroupModel;
            if (_mediaCapture == null && groupModel != null)
            {
                _mediaCapture = new MediaCapture();

                var settings = new MediaCaptureInitializationSettings()
                {
                    SourceGroup = groupModel.SourceGroup,

                    SharingMode = MediaCaptureSharingMode.ExclusiveControl,

                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,

                    StreamingCaptureMode = StreamingCaptureMode.Video,
                };

                try
                {
                    await _mediaCapture.InitializeAsync(settings);
                    _logger.Log($"Successfully initialized MediaCapture for {groupModel.DisplayName}");
                }
                catch (Exception exception)
                {
                    _logger.Log(exception.Message);
                    DisposeMediaCapture();
                }
            }
        }

        /// <summary>
        /// Sets the frame format of the current frame source.
        /// </summary>
        private async Task ChangeMediaFormatAsync(FrameFormatModel format)
        {
            if (_source == null)
            {
                _logger.Log("Unable to set format when source is not set.");
                return;
            }

            if (format != null && !format.HasSameFormat(_source.CurrentFormat))
            {
                await _source.SetFormatAsync(format.Format);
                _logger.Log($"Format set to {format.DisplayName}");
            }
        }

        /// <summary>
        /// Stops reading from the frame reader, disposes of the reader and updates the button state.
        /// </summary>
        private async Task StopReaderAsync()
        {
            _streaming = false;

            if (_reader != null)
            {
                await _reader.StopAsync();
                _reader.FrameArrived -= Reader_FrameArrived;
                _reader.Dispose();
                _reader = null;

                _logger.Log("Reader stopped.");
            }

        }

        async void Send(string message)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.BaseAddress = new Uri(viewModel.SendUrl);
                    var users = await User.FindAllAsync(UserType.LocalUser);
                    string name = (string)(await users.FirstOrDefault().GetPropertyAsync(KnownUserProperties.AccountName));

                    var content = new FormUrlEncodedContent(new[]
                    {
                new KeyValuePair<string, string>("worker",name),
                new KeyValuePair<string, string>("code",message),

            });
                    var result = await client.PostAsync(string.Empty, content);
                    string resultContent = await result.Content.ReadAsStringAsync();
                    _logger.Log($"Sent with result: {resultContent}");

                }
            }
            catch
            {
                _logger.Log("Sent failed");
            }
        }
        /// <summary>
        /// Handles the frame arrived event by converting the frame to a displayable
        /// format and rendering it to the screen.
        /// </summary>
        private void Reader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            using (var frame = sender.TryAcquireLatestFrame())
            {
                UpdateStatus("Acquiring");
                using (var softwareBitmap = _frameRenderer.ProcessFrame(frame))
                {
                    UpdateStatus("Recognizing");
                    var message = ProcessFrame(softwareBitmap);
                    if (!string.IsNullOrEmpty(message))
                    {
                        UpdateStatus("Sending " + message);
                        Send(message);
                    }
                    UpdateStatus("Done");
                }
            }
        }

        void UpdateStatus(string strMessage)
        {
            _logger.Log(strMessage);

        }

        string ProcessFrame(SoftwareBitmap bitmap)
        {
            if (bitmap == null) return null;
            try
            {
                if (this.buffer == null)
                {
                    this.buffer = new byte[4 * bitmap.PixelHeight * bitmap.PixelWidth];
                }
                bitmap.CopyToBuffer(buffer.AsBuffer());

                var zxingResult = ZXingQRCodeDecoder.DecodeBufferToQRCode(
                  buffer, bitmap.PixelWidth, bitmap.PixelHeight, BitmapFormat.BGR32);

                if (zxingResult != null)
                {
                    return zxingResult.Text;
                }
            }
            catch
            {
            }
            return null;
        }
        byte[] buffer = null;
    }
}
