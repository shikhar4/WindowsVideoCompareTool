using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.Composition;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace VisualVideoCompareTool
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        // TimelineController to synchronize videos 
        private MediaTimelineController timelineController = null;
        private TimeSpan maxNaturalDurationForController = TimeSpan.Zero; // the maximum duration for when video lengths are different 

        // Variables to display the videos/images side by side 
        private MediaPlayer mediaPlayer1 = null;
        private SpriteVisual playerVisual1;
        private Vector2 videoSize1;
        LoadedImageSurface loadedImageSurface1;
        IRandomAccessStream fileStream1;

        private MediaPlayer mediaPlayer2 = null;
        private SpriteVisual playerVisual2;
        private Vector2 videoSize2;
        LoadedImageSurface loadedImageSurface2;
        IRandomAccessStream fileStream2;

        private SpriteVisual lineVisual;
        private Vector2 lineSize;
        private Vector3 lineOffset;

        // Frame alignment 
        private int PTSIndex1 = 0;
        private int PTSIndex2 = 0;

        // Files 
        private StorageFile file1;
        private StorageFile file2;
        private StorageFile PTSFile1;
        private StorageFile PTSFile2;

        // File info 
        private string fileType1;
        private string fileType2;
        private uint fileHeight;
        private uint fileWidth;
        private double frameRate = 0; // ideally should be same for both videos 
        private double frameRate2 = 0;
        private ToolTip leftToolTip;
        private ToolTip rightToolTip;
        private bool selectedImage;

        // Total duration of videos (can be different) 
        private double duration1;
        private double duration2;
        // Total number of frames (can be different) 
        private int numFrames1;
        private int numFrames2;
        // List data structure to help get frame rate 
        List<string> encodingPropertiesToRetrieve = new List<string>(new string[] { "System.Video.FrameRate" });

        // PTS arrays  
        IList<string> ptsList1 = null;
        IList<string> ptsList2 = null;

        private double EPSILON_1 = 0.002;
        public MainPage()
        {
            this.InitializeComponent();
            Window.Current.CoreWindow.KeyDown += CoreWindow_KeyDown; ;
            timelineController = new MediaTimelineController();
            // Since this is a sample of video on demand, constrain the MediaTimelineController
            // to pause when it reaches the natural end of the last video.
            timelineController.PositionChanged += MediaTimelineController_PositionChanged;
            
            // Create new ToolTips to show the filenames on the buttons 
            leftToolTip = new ToolTip();
            rightToolTip = new ToolTip();

            // Set version number 
            Windows.ApplicationModel.PackageVersion version = Windows.ApplicationModel.Package.Current.Id.Version;
            VersionNumber.Text = "V" + string.Format("{0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision);
        }

        private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Change the width of the scroll viewers so that they actually scroll 
            MenuScrollViewer.Width = e.NewSize.Width;
            //CanvasScrollViewer.Width = e.NewSize.Width;
            // Set the canvas scroll viewer element to be the height - height of menu 
            var canvasScrollViewerVisual = CanvasScrollViewer.TransformToVisual(Window.Current.Content);
            var canvasScrollViewerCoordinate = canvasScrollViewerVisual.TransformPoint(new Windows.Foundation.Point(0, 0));
            CanvasScrollViewer.Height = e.NewSize.Height - canvasScrollViewerCoordinate.Y;

            DisplayInformation displayInformation = DisplayInformation.GetForCurrentView();
            double realWidth = e.NewSize.Width * displayInformation.RawPixelsPerViewPixel;
            MediaStackPanel.Width = realWidth; // Set the stack panel width to avoid part of the slider bar from being hidden
            SliderScrollViewer.Width = realWidth;
            CanvasScrollViewer.Width = realWidth;

            // Responsive - change the video size 
            if (!SelectFullWidth.IsEnabled) // If Full Width is not selected, the user is in this mode 
            {
                ChangeScale_Click(SelectFullWidth, new RoutedEventArgs());
            }
            else if (!SelectFullScreen.IsEnabled)
            {
                ChangeScale_Click(SelectFullScreen, new RoutedEventArgs());
            }
        }

        /* Workaround for Tanu bhaiya to upload the pts files */
        /*
         * Select the Presentation time stamp file for the videos 
         */
        private async void SelectPTSFile1_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".txt");

            PTSFile1 = await picker.PickSingleFileAsync();
            if (PTSFile1 != null)
            {
                ptsList1 = await Windows.Storage.FileIO.ReadLinesAsync(PTSFile1);
                numFrames1 = ptsList1.Count;
                UserNotifications.Visibility = Visibility.Visible;
                UserNotifications.Text = "Uploaded " + PTSFile1.Name;
                System.Diagnostics.Debug.WriteLine("PTS File 1 Count: " + ptsList1.Count);
            }
            SelectPTSFile2.Visibility = Visibility.Visible;
        }

        private async void SelectPTSFile2_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".txt");

            PTSFile2 = await picker.PickSingleFileAsync();
            if (PTSFile2 != null)
            {
                ptsList2 = await Windows.Storage.FileIO.ReadLinesAsync(PTSFile2);
                numFrames2 = ptsList2.Count;
                UserNotifications.Text = "Uploaded " + PTSFile2.Name;
                System.Diagnostics.Debug.WriteLine("PTS File 2 Count: " + ptsList2.Count);
            }
            SelectFile2.IsEnabled = true;
        }

        // Calls the C++ console app to generate the PTS file for given video file 
        private async Task Generate_PTS(string vidFilePath, bool firstFile)
        {
            // Remove any previous error message that was generated
            if (ApplicationData.Current.LocalSettings.Values.ContainsKey("errorMsg"))
            {
                ApplicationData.Current.LocalSettings.Values.Remove("errorMsg");
            }

            // Create output PTS text file name in the LocalCache Folder 
            string ofileName = Regex.Replace(vidFilePath, "(.*\\\\)|(\\.mp4)|(\\.avi)", "");
            ofileName += "_PTS.txt";

            StorageFolder localCacheFolder = ApplicationData.Current.LocalCacheFolder;
            StorageFile ptsFile = (StorageFile)await localCacheFolder.TryGetItemAsync(ofileName);
            StorageFile successFile = null;

            /* 
             * Update on 2/9/21: delete the PTS text file if it already exists to account for 
             * case where the video file might have been modified but still has the original file name. 
             */
            if (ptsFile != null)
            {
                await ptsFile.DeleteAsync();
            }

            // Create the txt file and get the path
            ptsFile = await localCacheFolder.CreateFileAsync(ofileName, CreationCollisionOption.ReplaceExisting);
            string ofilePath = ptsFile.Path;

            // Launch the console app 
            if (ApiInformation.IsApiContractPresent("Windows.ApplicationModel.FullTrustAppContract", 1, 0))
            {
                // Store command line parameters in local settings so Launcher can retrieve them 
                ApplicationData.Current.LocalSettings.Values["vidFilePath"] = vidFilePath;
                ApplicationData.Current.LocalSettings.Values["ptsFilePath"] = ofilePath;
                ApplicationData.Current.LocalSettings.Values["cacheFolderPath"] = localCacheFolder.Path;

                await Windows.ApplicationModel.FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("VideoFile");
            }
            // TODO - check for errors before filling the array 
            // Fill the PTS arrays 
            while (successFile == null)
            {
                successFile = (StorageFile)await localCacheFolder.TryGetItemAsync("success.txt");
            }
            await successFile.DeleteAsync();

            if (ApplicationData.Current.LocalSettings.Values.ContainsKey("errorMsg"))
            {
                UserNotifications.Visibility = Visibility.Visible;
                UserNotifications.Text = "Error from PTS Extraction: " + (string)ApplicationData.Current.LocalSettings.Values["errorMsg"];
            }
            else
            {
                if (firstFile)
                {
                    PTSFile1 = ptsFile;
                    ptsList1 = await Windows.Storage.FileIO.ReadLinesAsync(PTSFile1);
                    numFrames1 = ptsList1.Count;
                    System.Diagnostics.Debug.WriteLine("***********PTS COUNT FILE 1: " + ptsList1.Count);
                    if (numFrames1 == 0)
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "Error in retrieving pts";
                    }
                    else
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "Created " + PTSFile1.Name;
                    }
                }
                else
                {
                    PTSFile2 = ptsFile;
                    ptsList2 = await Windows.Storage.FileIO.ReadLinesAsync(PTSFile2);
                    numFrames2 = ptsList2.Count;
                    System.Diagnostics.Debug.WriteLine("************PTS COUNT FILE 2: " + ptsList2.Count);
                    if (numFrames2 == 0)
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "Error in retrieving pts";
                    }
                    else
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "Created " + PTSFile2.Name;
                    }
                }
            }
        }

        private async void SelectFile1_Click(object sender, RoutedEventArgs e)
        {
            SelectFile2.IsEnabled = false; // Disable SelectFile2 until File 1 is ready

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".avi");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".bmp");

            file1 = await picker.PickSingleFileAsync();

            if (file1 != null)
            {
                fileType1 = file1.FileType;

                if (fileType1 == ".mp4" || fileType1 == ".avi") // file is video 
                {
                    selectedImage = false;

                    if (SelectPTSFile1.Visibility == Visibility.Collapsed)
                    {
                        try
                        {
                            // Generate PTS file using FC's application
                            await Generate_PTS(file1.Path, true);
                            //throw new System.UnauthorizedAccessException(); 
                        }
                        catch(System.UnauthorizedAccessException)
                        {
                            SelectPTSFile1.Visibility = Visibility.Visible;
                            //UserNotifications.Text = "Upload BOTH PTS files before selecting the "
                            //SelectPTSFile2.Visibility = Visibility.Visible;
                        }
                    }

                    VideoProperties videoProperties1 = await file1.Properties.GetVideoPropertiesAsync();

                    // Get file properties 
                    fileHeight = videoProperties1.Height;
                    fileWidth = videoProperties1.Width;
                    duration1 = videoProperties1.Duration.TotalSeconds;

                    // NOTE - this might not be the most efficient way to get frame rate, but can change once we get things working 
                    /*
                     * Documentation for FrameRate
                     * https://docs.microsoft.com/en-us/windows/win32/properties/props-system-video-framerate
                     */
                    //List<string> encodingPropertiesToRetrieve = new List<string>();
                    //encodingPropertiesToRetrieve.Add("System.Video.FrameRate");
                    IDictionary<string, object> encodingProperties = await file1.Properties.RetrievePropertiesAsync(encodingPropertiesToRetrieve);
                    frameRate = Convert.ToDouble((uint)encodingProperties["System.Video.FrameRate"]) / 1000.0; // Default returns frame rate as frames/1000 seconds. 

                    // Debugging code 
                    //System.Diagnostics.Debug.WriteLine("FILE HEIGHT: " + fileHeight + "\nFILE WIDTH: " + fileWidth);
                    VideoPlaybackCanvas.Height = fileHeight;
                    VideoPlaybackCanvas.Width = fileWidth;

                    if (mediaPlayer1 == null) // Create new mediaPlayer1 if null, otherwise just change the source 
                    {
                        mediaPlayer1 = new MediaPlayer();
                        mediaPlayer1.CommandManager.IsEnabled = false;
                        // MediaOpened is the right time to find the highest natural duration.
                        mediaPlayer1.TimelineController = timelineController;
                        mediaPlayer1.MediaOpened += MediaPlayer_MediaOpened;
                        mediaPlayer1.PlaybackSession.PositionChanged += MediaPlayerSession_LeftPositionChanged;
                        mediaPlayer1.SourceChanged += MediaPlayer_SourceChanged;

                    }
                    mediaPlayer1.Source = MediaSource.CreateFromStorageFile(file1);
                    mediaPlayer1.TimelineControllerPositionOffset = TimeSpan.Zero;
                    mediaPlayer1.IsMuted = true;
                }
                else // file is image 
                {
                    selectedImage = true;
                    ImageProperties imageProperties = await file1.Properties.GetImagePropertiesAsync();
                    fileHeight = imageProperties.Height;
                    fileWidth = imageProperties.Width;

                    VideoPlaybackCanvas.Height = fileHeight;
                    VideoPlaybackCanvas.Width = fileWidth;
                    // Disable buttons for video (Dada wanted the video-related buttons to be disabled if file was image) 
                    ToggleVideoButtons(false);

                    // Fix button hover
                    leftToolTip.Content = file1.Name;
                    ToolTipService.SetToolTip(SelectFile1, leftToolTip);
                }
                // Enable option to select 2nd file 
                if (SelectPTSFile1.Visibility == Visibility.Collapsed)
                {
                    SelectFile2.IsEnabled = true;
                }
            }
        }

        private async void SelectFile2_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mkv");
            picker.FileTypeFilter.Add(".avi");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".bmp");

            file2 = await picker.PickSingleFileAsync();

            if (file2 != null)
            {
                fileType2 = file2.FileType;
                if (fileType2 == ".mp4" || fileType2 == ".avi") // file is video 
                {
                    if (selectedImage)
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "First file selected was image, please select another image";
                        return;
                    }
                    VideoProperties videoProperties2 = await file2.Properties.GetVideoPropertiesAsync();

                    if (SelectPTSFile1.Visibility == Visibility.Collapsed)
                    {
                        // Generate PTS file using FC's application
                        await Generate_PTS(file2.Path, false);
                    }

                    // Get file properties 
                    duration2 = videoProperties2.Duration.TotalSeconds;

                    // Error checking - check if files are same size 
                    if (videoProperties2.Height != fileHeight || videoProperties2.Width != fileWidth)
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "Files must be same size";
                        return;
                    }
                    else
                    {
                        //List<string> encodingPropertiesToRetrieve = new List<string>();
                        //encodingPropertiesToRetrieve.Add("System.Video.FrameRate");
                        IDictionary<string, object> encodingProperties = await file2.Properties.RetrievePropertiesAsync(encodingPropertiesToRetrieve);
                        frameRate2 = Convert.ToDouble((uint)encodingProperties["System.Video.FrameRate"]) / 1000.0; // Default returns frame rate as frames/1000 seconds. 

                        // Debugging Notifications - can be used if someone is having a problem with the videos not playing 
                        //DebuggingNotifications_SourceChanged.Text = "";
                        //DebuggingNotifications_PositionChanged.Text = "";
                        //DebuggingNotifications_TimelineController.Text = "";
                        if (mediaPlayer2 == null) // Create new mediaPlayer2 if null 
                        {
                            mediaPlayer2 = new MediaPlayer();
                            mediaPlayer2.CommandManager.IsEnabled = false;
                            mediaPlayer2.MediaOpened += MediaPlayer_MediaOpened;
                            mediaPlayer2.TimelineController = timelineController;
                            mediaPlayer2.PlaybackSession.PositionChanged += MediaPlayerSession_RightPositionChanged;
                            mediaPlayer2.SourceChanged += MediaPlayer_SourceChanged;
                        }
                        mediaPlayer2.Source = MediaSource.CreateFromStorageFile(file2);
                        mediaPlayer2.TimelineControllerPositionOffset = TimeSpan.Zero;
                        mediaPlayer2.IsMuted = true;

                        // Bind media function
                        /*
                         * Need to call this function in case videos are different sizes from before 
                         */
                        BindMediaPlayersToUIElement(mediaPlayer1, mediaPlayer2, VideoPlaybackCanvas);
                        SelectNativeSize.IsEnabled = false;
                        SelectFullWidth.IsEnabled = true;
                        SelectFullScreen.IsEnabled = true;
                        setVideoSize(fileWidth / 2);

                        ToggleVideoButtons(true);

                        rightToolTip.Content = file2.Name;
                        ToolTipService.SetToolTip(SelectFile2, rightToolTip);
                    }
                }
                else // file is image 
                {
                    if (!selectedImage)
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "First file selected was video, please select another video";
                        return;
                    }
                    ImageProperties imageProperties = await file2.Properties.GetImagePropertiesAsync();
                   
                    if (imageProperties.Height != fileHeight || imageProperties.Width != fileWidth)
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "Files must be same size";
                        return;
                    }
                    BindImagesToUIElement(file1, file2, VideoPlaybackCanvas);
                    setVideoSize(fileWidth / 2);
                }
            }

        }

        // Maintain maxNaturalDurationForController so it is as long as the longest media source (this is called when MediaPlayer.Source is set) 
        private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            var naturalDurationForController = sender.PlaybackSession.NaturalDuration - sender.TimelineControllerPositionOffset;
            if (naturalDurationForController > maxNaturalDurationForController)
            {
                maxNaturalDurationForController = naturalDurationForController;
            }
        }

        // Pause the videos when playback reaches maximum duration 
        private void MediaTimelineController_PositionChanged(MediaTimelineController sender, object args)
        {
            //Debugging Notifications -can be used if someone is having a problem with the videos not playing
            //await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            //{
            //    DebuggingNotifications_TimelineController.Text = "TIMELINE CONTROLLER POSITION CHANGED";
            //});

            if (sender.Position > maxNaturalDurationForController)
            {
                sender.Pause();
            }
        }

        // Reset time positions when media source is changed
        private async void MediaPlayer_SourceChanged(MediaPlayer mediaPlayer, object args)
        {

            timelineController.Position = TimeSpan.Zero;
            mediaPlayer.TimelineController.Position = TimeSpan.Zero;
            mediaPlayer.TimelineControllerPositionOffset = TimeSpan.Zero;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => {
                // Debugging Notifications - can be used if someone is having a problem with the videos not playing
                // DebuggingNotifications_SourceChanged.Text = "MEDIA PLAYER SOURCE CHANGED";

                if (mediaPlayer.Equals(mediaPlayer1))
                {
                    leftToolTip.Content = file1.Name;
                    ToolTipService.SetToolTip(SelectFile1, leftToolTip);
                }
                else
                {
                    rightToolTip.Content = file2.Name;
                    ToolTipService.SetToolTip(SelectFile2, rightToolTip);
                }
            
            });
        }

        /*
         * Update info boxes when video position changes
         */
        private async void MediaPlayerSession_LeftPositionChanged(MediaPlaybackSession sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                UserNotifications.Visibility = Visibility.Collapsed;
                UserNotifications.Text = "";
                if (frameRate != 0 && ptsList1 != null)
                {
                    // Debugging Notifications - can be used if someone is having a problem with the videos not playing
                    // DebuggingNotifications_PositionChanged.Text = "LEFT MEDIA PLAYER POSITION CHANGED";
                    SetCurrentFrame();
                }
            });
        }

        /*
         * Update info boxes when video position changes
         */
        private async void MediaPlayerSession_RightPositionChanged(MediaPlaybackSession sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                UserNotifications.Visibility = Visibility.Collapsed;
                UserNotifications.Text = "";
                if (frameRate2 != 0 && ptsList2 != null)
                {
                    // Debugging Notifications - can be used if someone is having a problem with the videos not playing
                    // DebuggingNotifications_PositionChanged.Text = "RIGHT MEDIA PLAYER POSITION CHANGED";
                    SetCurrentFrame();
                }
            });
        }

        private void ToggleVideoButtons(bool toggle)
        {
            PauseResumeBothPlayers.IsEnabled = toggle;
            SeekBackward.IsEnabled = toggle;
            SeekForward.IsEnabled = toggle;
            MoveBackwardOneFrame.IsEnabled = toggle;
            MoveForwardOneFrame.IsEnabled = toggle;
            File1FrameInput.IsEnabled = toggle;
            File2FrameInput.IsEnabled = toggle;
            RestartVideos.IsEnabled = toggle;
        }

        private async void BindImagesToUIElement(StorageFile file1, StorageFile file2, UIElement uiElement)
        {
            // Get the backing composition visual for the passed in UIElement
            Visual hostVisual = ElementCompositionPreview.GetElementVisual(uiElement);

            // Get the compositor the visual belongs to
            Compositor compositor = hostVisual.Compositor;

            // Create a new container and make it a child of the element
            ContainerVisual container = compositor.CreateContainerVisual();
            ElementCompositionPreview.SetElementChildVisual(uiElement, container);

            // Create sprite visuals for the compositor 
            playerVisual1 = compositor.CreateSpriteVisual();
            playerVisual2 = compositor.CreateSpriteVisual();
            // Set the size of the sprite visuals
            videoSize1 = new Vector2((float)fileWidth, (float)fileHeight);
            videoSize2 = new Vector2((float)fileWidth, (float)fileHeight);
            playerVisual1.Size = videoSize1;
            playerVisual2.Size = videoSize2;

            // Create CompositionSurfaceBrush 
            CompositionSurfaceBrush imageSurfaceBrush1 = compositor.CreateSurfaceBrush();

            // Create LoadedImageSurface - represents a composition surface that an image 
            // We can assign the surface to the CompositionSurfaceBrush and it will show up once the image is loaded to the surface.
            fileStream1 = await file1.OpenAsync(Windows.Storage.FileAccessMode.Read);
            loadedImageSurface1 = LoadedImageSurface.StartLoadFromStream(fileStream1);
            imageSurfaceBrush1.Surface = loadedImageSurface1;
            imageSurfaceBrush1.Stretch = CompositionStretch.None;
            imageSurfaceBrush1.HorizontalAlignmentRatio = 0.0f;
            imageSurfaceBrush1.VerticalAlignmentRatio = 0.0f;

            playerVisual1.Brush = imageSurfaceBrush1;

            CompositionSurfaceBrush imageSurfaceBrush2 = compositor.CreateSurfaceBrush();
            fileStream2 = await file2.OpenAsync(Windows.Storage.FileAccessMode.Read);
            loadedImageSurface2 = LoadedImageSurface.StartLoadFromStream(fileStream2);

            imageSurfaceBrush2.Surface = loadedImageSurface2;
            imageSurfaceBrush2.Stretch = CompositionStretch.None;
            imageSurfaceBrush2.HorizontalAlignmentRatio = 0.0f;
            imageSurfaceBrush2.VerticalAlignmentRatio = 0.0f;

            playerVisual2.Brush = imageSurfaceBrush2;

            // Set the scale 
            DisplayInformation displayInformation = DisplayInformation.GetForCurrentView();
            double scaleFactor = displayInformation.RawPixelsPerViewPixel;
            // Set the scale 
            float sFactor = (float)(1 / scaleFactor);
            Vector3 scaleVector = new Vector3(sFactor, sFactor, sFactor);
            // commented the playerVisual scales, just set hostVisual scale
            hostVisual.Scale = scaleVector;

            // Set the canvas and sprite width
            VideoPlaybackCanvas.Width = playerVisual1.Size.X;
            VideoPlaybackCanvas.Height = playerVisual1.Size.Y;

            // Set the slider scale 
            Visual sliderVisual = ElementCompositionPreview.GetElementVisual(canvasSlider);
            sliderVisual.Scale = scaleVector;
            canvasSlider.Width = fileWidth;
            canvasSlider.Maximum = fileWidth;
            canvasSlider.Value = fileWidth / 2;

            // Insert the sprite visuals as children of the ContainerVisual
            container.Children.InsertAtTop(playerVisual2);
            container.Children.InsertAtTop(playerVisual1);
        }
        
        private void BindMediaPlayersToUIElement(MediaPlayer mediaPlayer1, MediaPlayer mediaPlayer2, UIElement uiElement)
        {
            // Get the backing composition visual for the passed in UIElement
            Visual hostVisual = ElementCompositionPreview.GetElementVisual(uiElement);

            // Get the compositor the visual belongs to
            Compositor compositor = hostVisual.Compositor;

            // Create a new container and make it a child of the element
            ContainerVisual container = compositor.CreateContainerVisual();
            ElementCompositionPreview.SetElementChildVisual(uiElement, container);

            // Add a new sprite visual to paint the player onto
            playerVisual2 = compositor.CreateSpriteVisual();
            playerVisual1 = compositor.CreateSpriteVisual();

            // Create a SpriteVisual for the 'white line'
            lineVisual = compositor.CreateSpriteVisual();
            lineVisual.Brush = compositor.CreateColorBrush(Windows.UI.Colors.White);
            lineVisual.Opacity = 1.0f;
            lineSize = new Vector2(3.0f, fileHeight);
            lineVisual.Size = lineSize;
            lineOffset = new Vector3(fileWidth / 2, 0.0f, 0.0f);
            lineVisual.Offset = lineOffset;

            //playerVisual.Size = uiElement.RenderSize.ToVector2();
            /*
             * This is setting the size of the SpriteVisual, which is what decides what
             * is ultimately shown to the user. For starting value, this would be half
             * of the overall view.
             * */
            videoSize1 = new Vector2((float)fileWidth, (float)fileHeight);
            videoSize2 = new Vector2((float)fileWidth, (float)fileHeight);
            playerVisual1.Size = videoSize1;
            playerVisual2.Size = videoSize2;

            // Set the scales 
            // get effective pixel dimensions of display/screen 
            DisplayInformation displayInformation = DisplayInformation.GetForCurrentView();
            //ResolutionScale resolutionScale = displayInformation.ResolutionScale;
            //String scaleValue = ((int)resolutionScale).ToString();
            //System.Diagnostics.Debug.WriteLine("RESOLUTION SCALE: " + scaleValue);
            //uint height = displayInformation.ScreenHeightInRawPixels;
            //uint width = displayInformation.ScreenWidthInRawPixels;
            //System.Diagnostics.Debug.WriteLine("SCALE FACTOR: " + scaleFactor);
            //System.Diagnostics.Debug.WriteLine("SCREEN HEIGHT: " + height + " WIDTH: " + width);
            //uint effectiveHeight = (uint)(height * (1 / scaleFactor));
            //uint effectiveWidth = (uint)(width * (1 / scaleFactor));
            //System.Diagnostics.Debug.WriteLine("EFFECTIVE SCREEN HEIGHT: " + effectiveHeight + "\nSCREEN WIDTH: " + effectiveWidth);

            double scaleFactor = displayInformation.RawPixelsPerViewPixel;
            // Set the scale 
            float sFactor = (float)(1 / scaleFactor);
            Vector3 scaleVector = new Vector3(sFactor, sFactor, sFactor);
            // commented the playerVisual scales, just set hostVisual scale
            hostVisual.Scale = scaleVector;

            // Set the canvas and sprite width
            double spriteWidth = playerVisual1.Size.X;
            double spriteHeight = playerVisual1.Size.Y;
            VideoPlaybackCanvas.Width = spriteWidth;
            VideoPlaybackCanvas.Height = spriteHeight;

            // Set the slider scale 
            Visual sliderVisual = ElementCompositionPreview.GetElementVisual(canvasSlider);
            sliderVisual.Scale = scaleVector;
            canvasSlider.Width = fileWidth;
            canvasSlider.Maximum = fileWidth;
            canvasSlider.Value = fileWidth / 2;

            //Canvas.SetTop(canvasSlider, spriteHeight / 2);
            //Canvas.SetZIndex(canvasSlider, 3);

            // end hack

            /*
             * This is how we tell media player of the maximum size we expect the video to be played back.
             * This would be fixed to the max size of the overall view.
             * */
            Vector2 playerSurfaceSize1 = new System.Numerics.Vector2(fileWidth, fileHeight);
            mediaPlayer1.SetSurfaceSize(new Windows.Foundation.Size(playerSurfaceSize1.X, playerSurfaceSize1.Y));
            Vector2 playerSurfaceSize2 = new System.Numerics.Vector2(fileWidth, fileHeight);
            mediaPlayer2.SetSurfaceSize(new Windows.Foundation.Size(playerSurfaceSize2.X, playerSurfaceSize2.Y));
            /// end hack

            // Get the player's surface
            var mediaSurface1 = mediaPlayer1.GetSurface(compositor);
            var compositionSurface1 = mediaSurface1.CompositionSurface;

            // Convert the surface to a brush used to paint the visual
            //Tarun prototype hack
            CompositionSurfaceBrush surfaceBrush1 = compositor.CreateSurfaceBrush(compositionSurface1);
            /*
             * Setting the Stretch & horizontal/vertical alignment is important to ensure that the video doesn't get scaled up/down
             * as the SpriteVisual is resized. This can only be done when creating and attaching the surface Brush.
             * Setting these three essentially affects the Scale property. We do not want to be changing the aspect of the displayed video.
             * */
            surfaceBrush1.Stretch = CompositionStretch.None;
            surfaceBrush1.HorizontalAlignmentRatio = 0.0f;
            surfaceBrush1.VerticalAlignmentRatio = 0.0f;
            playerVisual1.Brush = surfaceBrush1;
            ///  end hack
            // Get the player's surface
            var mediaSurface2 = mediaPlayer2.GetSurface(compositor);
            var compositionSurface2 = mediaSurface2.CompositionSurface;

            // Convert the surface to a brush used to paint the visual
            //Tarun prototype hack
            CompositionSurfaceBrush surfaceBrush2 = compositor.CreateSurfaceBrush(compositionSurface2);
            surfaceBrush2.Stretch = CompositionStretch.None;
            surfaceBrush2.HorizontalAlignmentRatio = 0.0f;
            surfaceBrush2.VerticalAlignmentRatio = 0.0f;
            playerVisual2.Brush = surfaceBrush2;
            /// end hack
            /// 
            container.Children.InsertAtTop(playerVisual2);
            container.Children.InsertAtTop(playerVisual1);
            container.Children.InsertAtTop(lineVisual);
        }

        private void ChangeScale_Click(object sender, RoutedEventArgs e)
        {
            double newWidth = fileWidth;
            double newHeight = fileHeight; 

            Button btn = sender as Button;
            string btnName = btn.Name;

            // Get height and width of the available window space for the video 
            var windowSize = Window.Current.Bounds; // Gives the effective pixels
            double effectiveWidth = windowSize.Width;
            // Get the y coordinate of the scrollviewer to subtract from the current window height (to get only the available height for the video space)
            var canvasScrollViewerVisual = CanvasScrollViewer.TransformToVisual(Window.Current.Content);
            var canvasScrollViewerCoordinate = canvasScrollViewerVisual.TransformPoint(new Windows.Foundation.Point(0, 0));
            double effectiveHeight = windowSize.Height - canvasScrollViewerCoordinate.Y;


            // Convert effective to real pixels 
            DisplayInformation displayInformation = DisplayInformation.GetForCurrentView();
            double rawPixelsPerViewPixels = displayInformation.RawPixelsPerViewPixel;
            double realWidth = effectiveWidth * rawPixelsPerViewPixels;
            double realHeight = effectiveHeight * rawPixelsPerViewPixels;
            
            // Get the scale factor for both directions
            double scaleHorizontal = realWidth / fileWidth;
            double scaleVertical = realHeight / fileHeight;

            switch (btnName)
            {
                // Already set the scale at the beginning (to account for effective pixels) 
                case "SelectNativeSize":
                    // Set width and height to native size 
                    newWidth = fileWidth;
                    newHeight = fileHeight;
                    // Enable other buttons and disable selected ones 
                    SelectNativeSize.IsEnabled = false;
                    SelectFullWidth.IsEnabled = true;
                    SelectFullScreen.IsEnabled = true;
                    break;
                case "SelectFullWidth":
                    // Scale horizontally 
                    newWidth = fileWidth * scaleHorizontal;
                    newHeight = fileHeight * scaleHorizontal;
                    // Enable other buttons and disable selected ones 
                    SelectFullWidth.IsEnabled = false;
                    SelectNativeSize.IsEnabled = true;
                    SelectFullScreen.IsEnabled = true;
                    break;
                case "SelectFullScreen":
                    // Check if height will fit if scaled horizontally 
                    if ((scaleHorizontal * fileHeight) <= realHeight)
                    {
                        // Video fits with horizontal scaling 
                        newWidth = scaleHorizontal * fileWidth;
                        newHeight = scaleHorizontal * fileHeight;
                    }
                    else // Scale the video vertically 
                    {
                        newWidth = scaleVertical * fileWidth;
                        newHeight = scaleVertical * fileHeight;
                    }
                    // Enable other buttons and disable selected ones 
                    SelectFullScreen.IsEnabled = false;
                    SelectNativeSize.IsEnabled = true;
                    SelectFullWidth.IsEnabled = true;
                    break;
            }

            // Change SpriteVisual, canvas, and surface sizes 
            Vector2 newDimensions = new Vector2((float)newWidth, (float)newHeight);
            // Update videoSize vectors 
            videoSize1.X = (float)newWidth;
            videoSize1.Y = (float)newHeight;
            videoSize2.X = (float)newWidth;
            videoSize2.Y = (float)newHeight;
            lineSize.Y = (float)newHeight;

            playerVisual1.Size = newDimensions;
            playerVisual2.Size = newDimensions;
            VideoPlaybackCanvas.Width = newWidth;
            VideoPlaybackCanvas.Height = newHeight;

            lineVisual.Size = lineSize;

            if (!selectedImage)
            {
                mediaPlayer1.SetSurfaceSize(new Windows.Foundation.Size(newDimensions.X, newDimensions.Y));
                mediaPlayer2.SetSurfaceSize(new Windows.Foundation.Size(newDimensions.X, newDimensions.Y));
            }
            else
            {
                loadedImageSurface1 = LoadedImageSurface.StartLoadFromStream(fileStream1, new Windows.Foundation.Size(newDimensions.X, newDimensions.Y));
                loadedImageSurface2 = LoadedImageSurface.StartLoadFromStream(fileStream2, new Windows.Foundation.Size(newDimensions.X, newDimensions.Y));
            }

            // Change the slider size 
            canvasSlider.Width = newWidth;
            canvasSlider.Maximum = newWidth;
            canvasSlider.Value = newWidth / 2;
        }

        private void setVideoSize(float newXValue)
        {
            videoSize1.X = newXValue;
            playerVisual1.Size = videoSize1;

            lineOffset.X = newXValue;
            lineVisual.Offset = lineOffset;
        }

        /*
         * Slider value changed 
         */
        private void VideoSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            Slider slider = sender as Slider;
            if (slider != null && playerVisual1 != null)
            {
                setVideoSize((float)slider.Value);
            }
        }

        private void PauseResume_Click(object sender, RoutedEventArgs e)
        {
            if (timelineController.State == MediaTimelineControllerState.Paused)
            {
                UserNotifications.Visibility = Visibility.Collapsed;
                UserNotifications.Text = "";
                timelineController.Resume();
                PauseResumeBothPlayers.Icon = new SymbolIcon(Symbol.Pause);
            }
            else if (timelineController.State == MediaTimelineControllerState.Running)
            {
                timelineController.Pause();
                MoveForwardOneFrame_Click(MoveForwardOneFrame, new RoutedEventArgs());
                PauseResumeBothPlayers.Icon = new SymbolIcon(Symbol.Play);
            }
        }

        /*
         * This method will get the correct frame number for each video according to the current time.
         * Assumes that videos are paused. 
         */
        private void SetCurrentFrame()
        {
            if (mediaPlayer1 != null)
            {
                // Current time position of left video 
                double timePositionLeftVideo = mediaPlayer1.TimelineController.Position.Add(mediaPlayer1.TimelineControllerPositionOffset).TotalSeconds;
                // Find the actual frame number given current time position
                int actualFrameLeft = FindCorrectFrame(timePositionLeftVideo, ref ptsList1);
                PTSIndex1 = actualFrameLeft - 1; // set left video index in PTS table (subtract one because frames start at 1 in the array) 
                // Update the information box 
                SetCurrentPositionText(actualFrameLeft.ToString(), timePositionLeftVideo, true);
            }
            if (mediaPlayer2 != null)
            { 
                // Repeat for right video 
                double timePositionRightVideo = mediaPlayer2.TimelineController.Position.Add(mediaPlayer2.TimelineControllerPositionOffset).TotalSeconds;
                int actualFrameRight = FindCorrectFrame(timePositionRightVideo, ref ptsList2);
                PTSIndex2 = actualFrameRight - 1;
                SetCurrentPositionText(actualFrameRight.ToString(), timePositionRightVideo, false);
            }
        }

        private void SetCurrentPositionText(string currFrame, double currTime, bool setLeft)
        {
            // Get rounded time
            double roundedTime = Math.Round(currTime, 5);
            if (setLeft)
            {
                LeftVidInfoFrameNum.Text = currFrame + "/" + numFrames1.ToString();
                // Round times 
                double durationRounded = Math.Round(duration1, 5);
                LeftVidInfoCurrentTime.Text = roundedTime.ToString() + "/" + durationRounded.ToString();
            }
            else
            {
                RightVidInfoFrameNum.Text = currFrame + "/" + numFrames2.ToString();
                // Round times 
                double durationRounded = Math.Round(duration2, 5);
                RightVidInfoCurrentTime.Text = roundedTime.ToString() + "/" + durationRounded.ToString();
            }
        }

        /*
         * Finds the actual frame number given the timestamp of either video. Using binary search. 
         */
        private int FindCorrectFrame(double timestamp, ref IList<string> ptsList)
        {
            int min = 0;
            int max = ptsList.Count - 1;
            int mid;
            double midVal;
            double midPlusOneVal; 
            while (min <= max)
            {
                mid = (min + max) / 2;
                midVal = Double.Parse(ptsList[mid]);
                if (mid + 1 > (ptsList.Count - 1))
                {
                    return max;
                }
                midPlusOneVal = Double.Parse(ptsList[mid + 1]);

                // in between current index and index + 1 
                if ((timestamp > midVal || (Math.Abs(timestamp - midVal) < 0.0001) ) && timestamp < midPlusOneVal)
                {
                    return ++mid; 
                }
                // less than current index 
                if (timestamp < midVal)
                {
                    max = mid - 1;
                }
                else 
                {
                    min = mid + 1; 
                }
            }
            return -1;
        }
        /*
         * Step backward one frame. 
         * Assumes that vieos are paused 
         */
        private void MoveBackwardOneFrame_Click(object sender, RoutedEventArgs e)
        {
            int newFrameLeft;
            int newFrameRight;
            UserNotifications.Visibility = Visibility.Collapsed;
            UserNotifications.Text = "";

            if ((mediaPlayer1.TimelineControllerPositionOffset != TimeSpan.Zero) || (mediaPlayer2.TimelineControllerPositionOffset != TimeSpan.Zero))
            {
                if (mediaPlayer1.TimelineControllerPositionOffset.TotalSeconds < mediaPlayer2.TimelineControllerPositionOffset.TotalSeconds)
                {
                    // Get current time for video with smaller offset 
                    double timePositionLeftVideo = mediaPlayer1.TimelineController.Position.TotalSeconds;
                    // Find the actual frame number given current time position
                    int actualFrameLeft = FindCorrectFrame(timePositionLeftVideo, ref ptsList1);
                    if (actualFrameLeft == 1)
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "Cannot step backward at this frame";
                        return;
                    }
                    PTSIndex1 = actualFrameLeft - 1;
                    // Get timestamp for next frame 
                    double nextFrameTimestampLeft = double.Parse(ptsList1[PTSIndex1]) - EPSILON_1;
                    newFrameLeft = actualFrameLeft - 1;

                    // Set the timeline controller 
                    timelineController.Position = TimeSpan.FromSeconds(nextFrameTimestampLeft);

                    // Get corresponding frame for the second media player 
                    double timePositionRightVideo = mediaPlayer2.PlaybackSession.Position.TotalSeconds;
                    newFrameRight = FindCorrectFrame(timePositionRightVideo, ref ptsList2);
                    PTSIndex2 = newFrameRight - 1;

                    // Update the info box
                    SetCurrentPositionText(newFrameLeft.ToString(), mediaPlayer1.PlaybackSession.Position.TotalSeconds, true);
                    SetCurrentPositionText(newFrameRight.ToString(), mediaPlayer2.PlaybackSession.Position.TotalSeconds, false);
                }
                else
                {
                    // Get current time position of right video 
                    double timePositionRightVideo = mediaPlayer2.TimelineController.Position.TotalSeconds;
                    int actualFrameRight = FindCorrectFrame(timePositionRightVideo, ref ptsList2);
                    if (actualFrameRight == 1)
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "Cannot step backward at this frame";
                        return;
                    }
                    PTSIndex2 = actualFrameRight - 1;
                    // Get timestamp for next frame 
                    double nextFrameTimestampRight = double.Parse(ptsList2[PTSIndex2]) - EPSILON_1;
                    newFrameRight = actualFrameRight - 1;

                    // Set the timeline controller 
                    timelineController.Position = TimeSpan.FromSeconds(nextFrameTimestampRight);

                    // Get corresponding frame for first media player 
                    double timePositionLeftVideo = mediaPlayer1.PlaybackSession.Position.TotalSeconds;
                    newFrameLeft = FindCorrectFrame(timePositionLeftVideo, ref ptsList1);
                    PTSIndex1 = newFrameLeft - 1;

                    // Update the info box
                    SetCurrentPositionText(newFrameLeft.ToString(), mediaPlayer1.PlaybackSession.Position.TotalSeconds, true);
                    SetCurrentPositionText(newFrameRight.ToString(), mediaPlayer2.PlaybackSession.Position.TotalSeconds, false);
                }
            }
            else
            {
                // Current time position of left video 
                double timePositionLeftVideo = mediaPlayer1.TimelineController.Position.TotalSeconds;
                // Find the actual frame number given current time position
                int actualFrameLeft = FindCorrectFrame(timePositionLeftVideo, ref ptsList1);
                if (actualFrameLeft == 1)
                {
                    UserNotifications.Visibility = Visibility.Visible;
                    UserNotifications.Text = "Cannot step backward at this frame";
                    return;
                }
                PTSIndex1 = actualFrameLeft - 1; // set left video index in PTS table (subtract one because frames start at 1 in the array) 
                // Get timestamp for previous frame (subtract 0.002)
                double nextFrameTimestampLeft = double.Parse(ptsList1[PTSIndex1]) - EPSILON_1;
                newFrameLeft = actualFrameLeft - 1;

                // Current time position of right video 
                double timePositionRightVideo = mediaPlayer2.TimelineController.Position.TotalSeconds;
                int actualFrameRight = FindCorrectFrame(timePositionRightVideo, ref ptsList2);
                if (actualFrameRight == 1)
                {
                    UserNotifications.Visibility = Visibility.Visible;
                    UserNotifications.Text = "Cannot step backward at this frame";
                    return;
                }
                PTSIndex2 = actualFrameRight - 1;
                // Get timestamp for previous frame
                double nextFrameTimestampRight = double.Parse(ptsList2[PTSIndex2]) - EPSILON_1;
                newFrameRight = actualFrameRight - 1;

                // Set the new position to the bigger timestamp 
                if (nextFrameTimestampLeft < nextFrameTimestampRight)
                {
                    timelineController.Position = TimeSpan.FromSeconds(nextFrameTimestampLeft);
                    newFrameRight = FindCorrectFrame(nextFrameTimestampLeft, ref ptsList2);
                }
                else
                {
                    timelineController.Position = TimeSpan.FromSeconds(nextFrameTimestampRight);
                    newFrameLeft = FindCorrectFrame(nextFrameTimestampRight, ref ptsList1);
                }
                // Update the info box 
                SetCurrentPositionText((newFrameLeft).ToString(), mediaPlayer1.PlaybackSession.Position.TotalSeconds, true);
                SetCurrentPositionText((newFrameRight).ToString(), mediaPlayer2.PlaybackSession.Position.TotalSeconds, false);
            }
        }

        /*
         * Step forward one frame. 
         * Assumes that videos are paused
         */
        private void MoveForwardOneFrame_Click(object sender, RoutedEventArgs e)
        {
            int newFrameLeft;
            int newFrameRight;

            UserNotifications.Visibility = Visibility.Collapsed;
            UserNotifications.Text = "";
            // Offset 
            if ((mediaPlayer1.TimelineControllerPositionOffset != TimeSpan.Zero) || (mediaPlayer2.TimelineControllerPositionOffset != TimeSpan.Zero))
            {
                if (mediaPlayer1.TimelineControllerPositionOffset.TotalSeconds < mediaPlayer2.TimelineControllerPositionOffset.TotalSeconds)
                {
                    // Get current time for video with smaller offset 
                    double timePositionLeftVideo = mediaPlayer1.TimelineController.Position.TotalSeconds;
                    // Find the actual frame number given current time position
                    int actualFrameLeft = FindCorrectFrame(timePositionLeftVideo, ref ptsList1);
                    if (actualFrameLeft == numFrames1)
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "Cannot step forward at this frame";
                        return;
                    }
                    PTSIndex1 = actualFrameLeft - 1;
                    // Get timestamp for next frame 
                    double nextFrameTimestampLeft = double.Parse(ptsList1[PTSIndex1 + 1]) + EPSILON_1;
                    newFrameLeft = actualFrameLeft + 1;

                    // Set the timeline controller 
                    timelineController.Position = TimeSpan.FromSeconds(nextFrameTimestampLeft);
                    double timePositionRightVideo = mediaPlayer2.PlaybackSession.Position.TotalSeconds;
                    // Get corresponding frame for the second media player 
                    newFrameRight = FindCorrectFrame(timePositionRightVideo, ref ptsList2);
                    PTSIndex2 = newFrameRight - 1;
                    // Update the info box
                    SetCurrentPositionText(newFrameLeft.ToString(), mediaPlayer1.PlaybackSession.Position.TotalSeconds, true);
                    SetCurrentPositionText(newFrameRight.ToString(), mediaPlayer2.PlaybackSession.Position.TotalSeconds, false);
                }
                // get timestamp for next frame 
                // set timelineController 
                // find the new time for second media player 
                // get corresponding frame 
                // set info boxes 
                else
                {
                    // Get current time position of right video 
                    double timePositionRightVideo = mediaPlayer2.TimelineController.Position.TotalSeconds;
                    int actualFrameRight = FindCorrectFrame(timePositionRightVideo, ref ptsList2);
                    if (actualFrameRight == numFrames2)
                    {
                        UserNotifications.Visibility = Visibility.Visible;
                        UserNotifications.Text = "Cannot step forward at this frame";
                        return;
                    }
                    PTSIndex2 = actualFrameRight - 1;
                    // Get timestamp for next frame 
                    double nextFrameTimestampRight = double.Parse(ptsList2[PTSIndex2 + 1]) + EPSILON_1;
                    newFrameRight = actualFrameRight + 1;

                    // Set the timeline controller 
                    timelineController.Position = TimeSpan.FromSeconds(nextFrameTimestampRight);
                    double timePositionLeftVideo = mediaPlayer1.PlaybackSession.Position.TotalSeconds;
                    // Get corresponding frame for first media player 
                    newFrameLeft = FindCorrectFrame(timePositionLeftVideo, ref ptsList1);
                    PTSIndex1 = newFrameLeft - 1;
                    // Update the info box
                    SetCurrentPositionText(newFrameLeft.ToString(), mediaPlayer1.PlaybackSession.Position.TotalSeconds, true);
                    SetCurrentPositionText(newFrameRight.ToString(), mediaPlayer2.PlaybackSession.Position.TotalSeconds, false);
                }

            }
            else
            {
                // Current time position of left video 
                double timePositionLeftVideo = mediaPlayer1.TimelineController.Position.TotalSeconds;
                // Find the actual frame number given current time position
                int actualFrameLeft = FindCorrectFrame(timePositionLeftVideo, ref ptsList1);
                if (actualFrameLeft == numFrames1)
                {
                    UserNotifications.Visibility = Visibility.Visible;
                    UserNotifications.Text = "Cannot step forward at this frame";
                    return;
                }
                PTSIndex1 = actualFrameLeft - 1; // set left video index in PTS table (subtract one because frames start at 1 in the array) 

                // Get timestamp for next frame 
                double nextFrameTimestampLeft = double.Parse(ptsList1[PTSIndex1 + 1]) + EPSILON_1;
                newFrameLeft = actualFrameLeft + 1;

                // Current time position of right video 
                double timePositionRightVideo = mediaPlayer2.TimelineController.Position.TotalSeconds;
                int actualFrameRight = FindCorrectFrame(timePositionRightVideo, ref ptsList2);
                if (actualFrameLeft == numFrames2)
                {
                    UserNotifications.Visibility = Visibility.Visible;
                    UserNotifications.Text = "Cannot step forward at this frame";
                    return;
                }
                PTSIndex2 = actualFrameRight - 1;
                double nextFrameTimestampRight = double.Parse(ptsList2[PTSIndex2 + 1]) + EPSILON_1;
                newFrameRight = actualFrameRight + 1;

                // Set the new position to the smaller timestamp 
                if (nextFrameTimestampLeft > nextFrameTimestampRight)
                {
                    timelineController.Position = TimeSpan.FromSeconds(nextFrameTimestampLeft);
                    /* This code is for videos with slightly different frame rates */
                    // Get actual frame number for right video with left's timestamp 
                    newFrameRight = FindCorrectFrame(nextFrameTimestampLeft, ref ptsList2);
                }
                else
                {
                    timelineController.Position = TimeSpan.FromSeconds(nextFrameTimestampRight);
                    newFrameLeft = FindCorrectFrame(nextFrameTimestampRight, ref ptsList1);
                }
                // Update the info box 
                SetCurrentPositionText((newFrameLeft).ToString(), mediaPlayer1.PlaybackSession.Position.TotalSeconds, true);
                SetCurrentPositionText((newFrameRight).ToString(), mediaPlayer2.PlaybackSession.Position.TotalSeconds, false);
            }
        }

        private void SeekBackward_Click(object sender, RoutedEventArgs e)
        {
            // Get current time position for both videos
            double currTimestamp1 = mediaPlayer1.PlaybackSession.Position.TotalSeconds;
            double currTimestamp2 = mediaPlayer2.PlaybackSession.Position.TotalSeconds;
            // Get the frame number associated with these times
            int frame1 = FindCorrectFrame(currTimestamp1, ref ptsList1);
            int frame2 = FindCorrectFrame(currTimestamp2, ref ptsList2);
            int newFrame;
            // Choose smaller frame to seek with 
            // Seek 10% backwards
            if (frame1 <= frame2) 
            {
                newFrame = (int)(frame1 - 0.1 * (numFrames1));
            }
            else
            {
                newFrame = (int)(frame2 - 0.1 * (numFrames2));
            }
            // Call Seek function            
            SeekFrame(newFrame);
        }
        
        private void SeekForward_Click(object sender, RoutedEventArgs e)
        {
            // Get current time position for both videos
            double currTimestamp1 = mediaPlayer1.PlaybackSession.Position.TotalSeconds;
            double currTimestamp2 = mediaPlayer2.PlaybackSession.Position.TotalSeconds;
            // Get the frame number associated with these times
            int frame1 = FindCorrectFrame(currTimestamp1, ref ptsList1);
            int frame2 = FindCorrectFrame(currTimestamp2, ref ptsList2);
            int newFrame;
            // Choose smaller frame to seek with 
            // Seek 10% backwards
            if (frame1 <= frame2)
            {
                newFrame = (int)(frame1 + 0.1 * (numFrames1));
            }
            else
            {
                newFrame = (int)(frame2 + 0.1 * (numFrames2));
            }
            // Call Seek function            
            SeekFrame(newFrame);
        }

        private void SeekFrame(int inputFrame)
        {
            double newTimestampLeft;
            double newTimestampRight;
            int newFrameLeft;
            int newFrameRight;

            // Check if frame is in bound
            if (UserNotifications != null && (inputFrame < 1 || inputFrame > numFrames1 || inputFrame > numFrames2))
            {
                UserNotifications.Visibility = Visibility.Visible;
                UserNotifications.Text = "Cannot seek, frame is out of bounds";
                return;
            }
            else if (UserNotifications != null)
            {
                UserNotifications.Visibility = Visibility.Collapsed;
                UserNotifications.Text = "";
            }

            // If there is offset set (for alignment), then seek with the video with smaller timestamp 
            if (inputFrame > 0 && ((mediaPlayer1.TimelineControllerPositionOffset != TimeSpan.Zero) || (mediaPlayer2.TimelineControllerPositionOffset != TimeSpan.Zero)))
            {
                // Seek using mediaPlayer1 since offset is less than 2's offset
                if (mediaPlayer1.TimelineControllerPositionOffset.TotalSeconds < mediaPlayer2.TimelineControllerPositionOffset.TotalSeconds)
                {
                    if (inputFrame >= 1 && inputFrame <= numFrames1)
                    {
                        newFrameLeft = inputFrame;
                        // Get timestamp for this frame (for first media player) 
                        newTimestampLeft = double.Parse(ptsList1[inputFrame - 1]) + EPSILON_1;
                        // Set the timeline controller to this position. Doing this SHOULD set the mediaPlayers' times as well. 
                        timelineController.Position = TimeSpan.FromSeconds(newTimestampLeft);
                        // Find the new time for the second media player. This should have the offset included in it 
                        newTimestampRight = mediaPlayer2.PlaybackSession.Position.TotalSeconds;
                        // Get corresponding frame for the second media player 
                        newFrameRight = FindCorrectFrame(newTimestampRight, ref ptsList2);
                        PTSIndex2 = newFrameRight - 1;
                        // Update the info box
                        SetCurrentPositionText(newFrameLeft.ToString(), mediaPlayer1.PlaybackSession.Position.TotalSeconds, true);
                        SetCurrentPositionText(newFrameRight.ToString(), mediaPlayer2.PlaybackSession.Position.TotalSeconds, false);
                    }
                }
                else // Seek using mediaPlayer 2 
                {
                    if (inputFrame >= 1 && inputFrame <= numFrames2)
                    {
                        newFrameRight = inputFrame;
                        // Get timestamp for this frame (for second media player) 
                        newTimestampRight = double.Parse(ptsList2[inputFrame - 1]) + EPSILON_1;
                        timelineController.Position = TimeSpan.FromSeconds(newTimestampRight);
                        // Find the new time for the first media player 
                        newTimestampLeft = mediaPlayer1.PlaybackSession.Position.TotalSeconds;
                        newFrameLeft = FindCorrectFrame(newTimestampLeft, ref ptsList1);
                        PTSIndex1 = newFrameLeft - 1;
                        // Update the info box 
                        SetCurrentPositionText(newFrameLeft.ToString(), mediaPlayer1.PlaybackSession.Position.TotalSeconds, true);
                        SetCurrentPositionText(newFrameRight.ToString(), mediaPlayer2.PlaybackSession.Position.TotalSeconds, false);
                    }
                }
            }
            // No offset set, do seek normally 
            else
            {
                if (inputFrame >= 1 && (inputFrame <= numFrames1 || inputFrame <= numFrames2))
                {
                    newTimestampLeft = double.Parse(ptsList1[inputFrame - 1]) + EPSILON_1;
                    newTimestampRight = double.Parse(ptsList2[inputFrame - 1]) + EPSILON_1;

                    // Seek to timestamp that is greater, 
                    if (newTimestampLeft > newTimestampRight)
                    {
                        timelineController.Position = TimeSpan.FromSeconds(newTimestampLeft);
                        /* This code is for videos with slightly different frame rates */
                        newFrameLeft = inputFrame; // Left video will be at the inputted frame number 
                        PTSIndex1 = inputFrame - 1;
                        // Get actual frame number for right video with left's timestamp 
                        newFrameRight = FindCorrectFrame(newTimestampLeft, ref ptsList2);
                        PTSIndex2 = newFrameRight - 1;
                    }
                    else
                    {
                        timelineController.Position = TimeSpan.FromSeconds(newTimestampRight);
                        /* This code is for videos with slightly different frame rates */
                        newFrameRight = inputFrame; // Right video will be at the inputted frame number 
                        PTSIndex2 = inputFrame - 1;
                        // Get actual frame number for left video with right's timestamp 
                        newFrameLeft = FindCorrectFrame(newTimestampRight, ref ptsList1);
                        PTSIndex1 = newFrameLeft - 1;
                    }
                    // Update the info box 
                    SetCurrentPositionText(newFrameLeft.ToString(), mediaPlayer1.PlaybackSession.Position.TotalSeconds, true);
                    SetCurrentPositionText(newFrameRight.ToString(), mediaPlayer2.PlaybackSession.Position.TotalSeconds, false);
                }
            }
        }
        /*
         * For seeking to a frame number, we have to check the timestamp in both tables (for the case where the frame rates are slightly different). 
         * If the timestamps are different due to variable frame rate, choose the smaller timestamp. We then have to display the actual frame number 
         * for the video with the larger timestamp.
         * ValueChanged="SeekInput_Changed"
         */
        private void SeekInput_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
            int inputFrame = (int)e.NewValue;
            SeekFrame(inputFrame);
            SeekInput.Value = 0;
        }

        /*
         * For alignment.
         * Changes the left video frame number to the frame user entered 
         */
        private void File1FrameInput_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
            int newValue = (int)e.NewValue;
            if (newValue > 0 && newValue <= numFrames1)
            {
                UserNotifications.Visibility = Visibility.Collapsed;
                UserNotifications.Text = "";
                // get the PTS for input frame 
                if (ptsList1 != null)
                {
                    double timestamp = double.Parse(ptsList1[newValue - 1]) + EPSILON_1;
                    mediaPlayer1.TimelineControllerPositionOffset = TimeSpan.FromSeconds(timestamp);
                    // Update info box 
                    LeftVidInfoFrameNum.Text = newValue.ToString() + "/" + numFrames1;
                    // Add the timeline controller position + offset position to get the current time that the media player is at
                    LeftVidInfoCurrentTime.Text = mediaPlayer1.TimelineController.Position.Add(mediaPlayer1.TimelineControllerPositionOffset).TotalSeconds.ToString() + "/" + duration1.ToString();
                }
                // else notify user that no PTS was uploaded
                else
                {
                    UserNotifications.Visibility = Visibility.Visible;
                    UserNotifications.Text = "No PTS File found";
                    return;
                }
                //sender.Value = 0;
            }
        }

        /* 
         * For alignment.
         * Changes the right video frame number to the frame user entered
         */
        private void File2FrameInput_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
            int newValue = (int)e.NewValue;
            if (newValue > 0 && newValue <= numFrames2)
            {
                UserNotifications.Visibility = Visibility.Collapsed;
                UserNotifications.Text = "";
                // get the PTS for input frame 
                if (ptsList2 != null)
                {
                    double timestamp = double.Parse(ptsList2[newValue - 1]) + EPSILON_1;
                    mediaPlayer2.TimelineControllerPositionOffset = TimeSpan.FromSeconds(timestamp);
                    // Update info box 
                    RightVidInfoFrameNum.Text = newValue.ToString() + "/" + numFrames2;
                    // Add the timeline controller position + offset position to get the current time that the media player is at
                    RightVidInfoCurrentTime.Text = mediaPlayer2.TimelineController.Position.Add(mediaPlayer2.TimelineControllerPositionOffset).TotalSeconds.ToString() + "/" + duration2.ToString();
                }
                // else notify user that no PTS was uploaded
                else
                {
                    UserNotifications.Visibility = Visibility.Visible;
                    UserNotifications.Text = "No PTS File found";
                    return;
                }
                //sender.Value = 0;
            }
        }

        private void RestartVideo_Click(object sender, RoutedEventArgs e)
        {
            UserNotifications.Visibility = Visibility.Collapsed;
            UserNotifications.Text = "";
            timelineController.Position = TimeSpan.Zero;
            mediaPlayer1.TimelineControllerPositionOffset = TimeSpan.Zero;
            mediaPlayer2.TimelineControllerPositionOffset = TimeSpan.Zero;
            LeftVidInfoFrameNum.Text = "1/" + numFrames1.ToString();
            RightVidInfoFrameNum.Text = "1/" + numFrames2.ToString();
            PauseResumeBothPlayers.Icon = new SymbolIcon(Symbol.Play);
        }

        private void CoreWindow_KeyDown(CoreWindow sender, KeyEventArgs e)
        {
            var key = e.VirtualKey;
            if (!(key == Windows.System.VirtualKey.Number0 || key == Windows.System.VirtualKey.Number1 || key == Windows.System.VirtualKey.Number2 || key == Windows.System.VirtualKey.Number3 || key == Windows.System.VirtualKey.Number4 || key == Windows.System.VirtualKey.Number5 || key == Windows.System.VirtualKey.Number6 || key == Windows.System.VirtualKey.Number7 || key == Windows.System.VirtualKey.Number9 || key == Windows.System.VirtualKey.Enter))
            {
                File1FrameInput.IsEnabled = false;
                File2FrameInput.IsEnabled = false;
            }
            RoutedEventArgs eventArgs = new RoutedEventArgs();

            if (IsCtrlKeyPressed())
            {
                switch (key)
                {
                    case Windows.System.VirtualKey.P:
                        PrintScreen_Click(PrintScreen, eventArgs);
                        break;
                }
                File1FrameInput.IsEnabled = true;
                File2FrameInput.IsEnabled = true;
            }
            else
            {
                switch (key)
                {
                    case Windows.System.VirtualKey.Space:
                        PauseResume_Click(PauseResumeBothPlayers, eventArgs);
                        break;
                    case Windows.System.VirtualKey.P:
                        PauseResume_Click(PauseResumeBothPlayers, eventArgs);
                        break;
                    case Windows.System.VirtualKey.F:
                        MoveForwardOneFrame_Click(MoveForwardOneFrame, eventArgs);
                        break;
                    case Windows.System.VirtualKey.B:
                        MoveBackwardOneFrame_Click(MoveBackwardOneFrame, eventArgs);
                        break;
                    case Windows.System.VirtualKey.R:
                        RestartVideo_Click(RestartVideos, eventArgs);
                        break;
                    case Windows.System.VirtualKey.D:
                        SeekForward_Click(SeekForward, eventArgs);
                        break;
                    case Windows.System.VirtualKey.V:
                        SeekBackward_Click(SeekBackward, eventArgs);
                        break;
                }
            }
            if (!(key == Windows.System.VirtualKey.Number0 || key == Windows.System.VirtualKey.Number1 || key == Windows.System.VirtualKey.Number2 || key == Windows.System.VirtualKey.Number3 || key == Windows.System.VirtualKey.Number4 || key == Windows.System.VirtualKey.Number5 || key == Windows.System.VirtualKey.Number6 || key == Windows.System.VirtualKey.Number7 || key == Windows.System.VirtualKey.Number9 || key == Windows.System.VirtualKey.Enter))
            {
                File1FrameInput.IsEnabled = true;
                File2FrameInput.IsEnabled = true;
            }
        }

        /*
         * Screenshot VideoPlaybackCanvas -- this needs to be fixed. (stopped working after fixing the scaling) 
         * 
         * Some links I used to create this code 
         * https://stackoverflow.com/questions/41354024/uwp-save-grid-as-png
         * RenderTargetBitmap: https://docs.microsoft.com/en-us/uwp/api/Windows.UI.Xaml.Media.Imaging.RenderTargetBitmap?redirectedfrom=MSDN&view=winrt-19041
         * GetPixelsAsync: https://docs.microsoft.com/en-us/uwp/api/windows.ui.xaml.media.imaging.rendertargetbitmap.getpixelsasync?view=winrt-19041#remarks
         * SetPixelData: https://docs.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapencoder.setpixeldata?view=winrt-19041#Windows_Graphics_Imaging_BitmapEncoder_SetPixelData_Windows_Graphics_Imaging_BitmapPixelFormat_Windows_Graphics_Imaging_BitmapAlphaMode_System_UInt32_System_UInt32_System_Double_System_Double_System_Byte___
         */
        private async void PrintScreen_Click(object sender, RoutedEventArgs e)
        {
            // use RenderTargetBitmap to render a UIElement (such as your page)
            RenderTargetBitmap renderTarget = new RenderTargetBitmap();
            await renderTarget.RenderAsync(VideoPlaybackCanvas);
            //RenderedImage.Source = renderTarget;
            var pixelBuffer = await renderTarget.GetPixelsAsync();
            var dataReader = Windows.Storage.Streams.DataReader.FromBuffer(pixelBuffer);
            var pixels = new byte[pixelBuffer.Length];
            dataReader.ReadBytes(pixels);

            //use a BitmapEncoder to encode the RenderTargetBitmap's pixels to a jpg or png to save out
            FileSavePicker fileSavePicker = new FileSavePicker();
            //fileSavePicker.FileTypeChoices.Add("JPEG files", new List<string>() {".jpg"});
            fileSavePicker.FileTypeChoices.Add("PNG files", new List<string>() { ".png" });

            StorageFile outputFile = await fileSavePicker.PickSaveFileAsync();

            if (outputFile != null)
            {
                using (IRandomAccessStream stream = await outputFile.OpenAsync(FileAccessMode.ReadWrite))
                {
                    uint height = (uint)(fileHeight + 2 * (int)canvasSlider.ActualHeight);
                    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8,
                                         BitmapAlphaMode.Premultiplied,
                                         (uint)renderTarget.PixelWidth,
                                         height,
                                         DisplayInformation.GetForCurrentView().RawDpiX,
                                         DisplayInformation.GetForCurrentView().RawDpiY,
                                         pixels);
                    await encoder.FlushAsync();
                }
            }
        }

        private static bool IsCtrlKeyPressed()
        {
            var ctrlState = CoreWindow.GetForCurrentThread().GetKeyState(Windows.System.VirtualKey.Control);
            return (ctrlState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        }

    }
    
    // Added this class to help prevent pressing Space bar from triggering Button click events. The Space bar is now only used for Pausing/Playing the video.
    public class MyButton : Button
    {
        protected override void OnProcessKeyboardAccelerators(Windows.UI.Xaml.Input.ProcessKeyboardAcceleratorEventArgs args)
        {
            if (args.Key == Windows.System.VirtualKey.Space)
            {
                args.Handled = true;
            }
            base.OnProcessKeyboardAccelerators(args);
        }
    }
}