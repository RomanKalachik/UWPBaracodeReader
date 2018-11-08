using System;
using System.Collections.Generic;
using Windows.UI.Xaml.Controls;

namespace SDKTemplate
{
    public partial class MainPage : Page
    {
        public const string FEATURE_NAME = "Camera Frames";

        List<Scenario> scenarios = new List<Scenario>
        {
            new Scenario() { Title="Find and display all media frame sources", ClassType=typeof(Scenario2_FindAvailableSourceGroups)},
        };
    }

    public class Scenario
    {
        public string Title { get; set; }
        public Type ClassType { get; set; }
    }
}
