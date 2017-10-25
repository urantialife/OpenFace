﻿///////////////////////////////////////////////////////////////////////////////
// Copyright (C) 2017, Carnegie Mellon University and University of Cambridge,
// all rights reserved.
//
// ACADEMIC OR NON-PROFIT ORGANIZATION NONCOMMERCIAL RESEARCH USE ONLY
//
// BY USING OR DOWNLOADING THE SOFTWARE, YOU ARE AGREEING TO THE TERMS OF THIS LICENSE AGREEMENT.  
// IF YOU DO NOT AGREE WITH THESE TERMS, YOU MAY NOT USE OR DOWNLOAD THE SOFTWARE.
//
// License can be found in OpenFace-license.txt

//     * Any publications arising from the use of this software, including but
//       not limited to academic journal and conference publications, technical
//       reports and manuals, must cite at least one of the following works:
//
//       OpenFace: an open source facial behavior analysis toolkit
//       Tadas Baltrušaitis, Peter Robinson, and Louis-Philippe Morency
//       in IEEE Winter Conference on Applications of Computer Vision, 2016  
//
//       Rendering of Eyes for Eye-Shape Registration and Gaze Estimation
//       Erroll Wood, Tadas Baltrušaitis, Xucong Zhang, Yusuke Sugano, Peter Robinson, and Andreas Bulling 
//       in IEEE International. Conference on Computer Vision (ICCV),  2015 
//
//       Cross-dataset learning and person-speci?c normalisation for automatic Action Unit detection
//       Tadas Baltrušaitis, Marwa Mahmoud, and Peter Robinson 
//       in Facial Expression Recognition and Analysis Challenge, 
//       IEEE International Conference on Automatic Face and Gesture Recognition, 2015 
//
//       Constrained Local Neural Fields for robust facial landmark detection in the wild.
//       Tadas Baltrušaitis, Peter Robinson, and Louis-Philippe Morency. 
//       in IEEE Int. Conference on Computer Vision Workshops, 300 Faces in-the-Wild Challenge, 2013.    
//
///////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.IO;
using Microsoft.Win32;

// Internal libraries
using OpenCVWrappers;
using CppInterop;
using CppInterop.LandmarkDetector;
using CameraInterop;
using FaceAnalyser_Interop;
using GazeAnalyser_Interop;
using System.Globalization;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace OpenFaceOffline
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        // Timing for measuring FPS
        #region High-Resolution Timing
        static DateTime startTime;
        static Stopwatch sw = new Stopwatch();

        static MainWindow()
        {
            startTime = DateTime.Now;
            sw.Start();
        }

        public static DateTime CurrentTime
        {
            get { return startTime + sw.Elapsed; }
        }
        #endregion

        // -----------------------------------------------------------------
        // Members
        // -----------------------------------------------------------------

        Thread processing_thread;

        // Some members for displaying the results
        private Capture capture;
        private WriteableBitmap latest_img;
        private WriteableBitmap latest_aligned_face;
        private WriteableBitmap latest_HOG_descriptor;

        // Managing the running of the analysis system
        private volatile bool thread_running;
        private volatile bool thread_paused = false;
        // Allows for going forward in time step by step
        // Useful for visualising things
        private volatile int skip_frames = 0;

        FpsTracker processing_fps = new FpsTracker();

        volatile bool detectionSucceeding = false;
        
        // For tracking
        FaceModelParameters face_model_params;
        CLNF clnf_model;
        FaceAnalyserManaged face_analyser;
        GazeAnalyserManaged gaze_analyser;

        // Recording parameters (default values)
        Recorder recorder;

        public bool RecordAligned { get; set; } = false; // Aligned face images
        public bool RecordHOG { get; set; } = false; // HOG features extracted from face images
        public bool Record2DLandmarks { get; set; } = true; // 2D locations of facial landmarks (in pixels)
        public bool Record3DLandmarks { get; set; } = true; // 3D locations of facial landmarks (in pixels)
        public bool RecordModelParameters { get; set; } = true; // Facial shape parameters (rigid and non-rigid geometry)
        public bool RecordPose { get; set; } = true; // Head pose (position and orientation)
        public bool RecordAUs { get; set; } = true; // Facial action units
        public bool RecordGaze { get; set; } = true; // Eye gaze

        // Visualisation options
        public bool ShowTrackedVideo { get; set; } = true; // Showing the actual tracking
        public bool ShowAppearance { get; set; } = true; // Showing appeaance features like HOG
        public bool ShowGeometry { get; set; } = true; // Showing geometry features, pose, gaze, and non-rigid
        public bool ShowAUs { get; set; } = true; // Showing Facial Action Units

        int image_output_size = 112;
        
        // Where the recording is done (by default in a record directory, from where the application executed)
        String record_root = "./record";

        // For AU prediction, if videos are long dynamic models should be used
        public bool DynamicAUModels { get; set; } = true;

        // Camera calibration parameters
        public double fx = -1, fy = -1, cx = -1, cy = -1;
        bool estimate_camera_parameters = true;

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this; // For WPF data binding

            // Set the icon
            Uri iconUri = new Uri("logo1.ico", UriKind.RelativeOrAbsolute);
            this.Icon = BitmapFrame.Create(iconUri);
            
            String root = AppDomain.CurrentDomain.BaseDirectory;

            face_model_params = new FaceModelParameters(root, false);
            clnf_model = new CLNF(face_model_params);
            face_analyser = new FaceAnalyserManaged(root, DynamicAUModels, image_output_size);

            gaze_analyser = new GazeAnalyserManaged();
        }

        // ----------------------------------------------------------
        // Actual work gets done here

        // The main function call for processing images or video files, TODO rename this as it is not a loop
        private void ProcessingLoop(String[] filenames, int cam_id = -1, int width = -1, int height = -1, bool multi_face = false)
        {
            SetupFeatureExtractionMode();

            thread_running = true;

            // Create the video capture and call the VideoLoop
            if (filenames != null)
            {
                face_model_params.optimiseForVideo();
                if (cam_id == -2)
                {
                    List<String> image_files_all = new List<string>();
                    foreach (string image_name in filenames)
                        image_files_all.Add(image_name);

                    // Loading an image sequence that represents a video                   
                    capture = new Capture(image_files_all);

                    if (capture.isOpened())
                    {
                        // Prepare recording if any based on the directory
                        String file_no_ext = System.IO.Path.GetDirectoryName(filenames[0]);
                        file_no_ext = System.IO.Path.GetFileName(file_no_ext);

                        // Start the actual processing and recording
                        FeatureExtractionLoop(file_no_ext);                        

                    }
                    else
                    {
                        string messageBoxText = "Failed to open an image";
                        string caption = "Not valid file";
                        MessageBoxButton button = MessageBoxButton.OK;
                        MessageBoxImage icon = MessageBoxImage.Warning;

                        // Display message box
                        MessageBox.Show(messageBoxText, caption, button, icon);
                    }
                }
                else if (cam_id == -3)
                {
                    // Process all the provided images
                    ProcessImages(filenames);

                }
                else
                {
                    face_model_params.optimiseForVideo();
                    // Loading a video file (or a number of them)
                    foreach (string filename in filenames)
                    {
                        if (!thread_running)
                        {
                            continue;
                        }

                        capture = new Capture(filename);

                        if (capture.isOpened())
                        {
                            String file_no_ext = System.IO.Path.GetFileNameWithoutExtension(filename);
                            
                            // Start the actual processing                        
                            FeatureExtractionLoop(file_no_ext);

                        }
                        else
                        {
                            string messageBoxText = "File is not a video or the codec is not supported.";
                            string caption = "Not valid file";
                            MessageBoxButton button = MessageBoxButton.OK;
                            MessageBoxImage icon = MessageBoxImage.Warning;

                            // Display message box
                            MessageBox.Show(messageBoxText, caption, button, icon);
                        }
                    }
                }
            }

            EndMode();

        }

        // 
        private void ProcessImages(string[] filenames)
        {
            // Turn off unneeded visualisations and recording settings
            bool TrackVid = ShowTrackedVideo; ShowTrackedVideo = true;
            bool ShowApp = ShowAppearance; ShowAppearance = false;
            bool ShowGeo = ShowGeometry; ShowGeometry = false;
            bool showAU = ShowAUs; ShowAUs = false;
            bool recAlign = RecordAligned;
            bool recHOG = RecordHOG;

            // Actually update the GUI accordingly
            Dispatcher.Invoke(DispatcherPriority.Render, new TimeSpan(0, 0, 0, 0, 2000), (Action)(() =>
            {
                VisualisationChange(null, null);
            }));

            face_model_params.optimiseForImages();
            // Loading an image file (or a number of them)
            foreach (string filename in filenames)
            {
                if (!thread_running)
                {
                    continue;
                }

                capture = new Capture(filename);

                if (capture.isOpened())
                {
                    // Start the actual processing                        
                    ProcessImage();

                }
                else
                {
                    string messageBoxText = "File is not an image or the decoder is not supported.";
                    string caption = "Not valid file";
                    MessageBoxButton button = MessageBoxButton.OK;
                    MessageBoxImage icon = MessageBoxImage.Warning;

                    // Display message box
                    MessageBox.Show(messageBoxText, caption, button, icon);
                }
            }

            // Clear image setup, restore the views
            ShowTrackedVideo = TrackVid;
            ShowAppearance = ShowApp;
            ShowGeometry = ShowGeo;
            ShowAUs = showAU;
            RecordHOG = recHOG;
            RecordAligned = recAlign;

            // Actually update the GUI accordingly
            Dispatcher.Invoke(DispatcherPriority.Render, new TimeSpan(0, 0, 0, 0, 2000), (Action)(() =>
            {
                VisualisationChange(null, null);
            }));
        }

        // Capturing and processing the video frame by frame
        private void ProcessImage()
        {
            Thread.CurrentThread.IsBackground = true;

            clnf_model.Reset();
            face_analyser.Reset();

            //////////////////////////////////////////////
            // CAPTURE FRAME AND DETECT LANDMARKS FOLLOWED BY THE REQUIRED IMAGE PROCESSING
            //////////////////////////////////////////////
            RawImage frame = null;
            double progress = -1;

            frame = new RawImage(capture.GetNextFrame(false));
            progress = capture.GetProgress();

            if (frame.Width == 0)
            {
                // This indicates that we reached the end of the video file
                return;
            }

            var grayFrame = new RawImage(capture.GetCurrentFrameGray());

            if (grayFrame == null)
            {
                Console.WriteLine("Gray is empty");
                return;
            }

            List<List<Tuple<double, double>>> landmark_detections = ProcessImage(clnf_model, face_model_params, frame, grayFrame);

            List<Point> landmark_points = new List<Point>();

            for (int i = 0; i < landmark_detections.Count; ++i)
            {

                List<Tuple<double, double>> landmarks = landmark_detections[i];
                foreach (var p in landmarks)
                {
                    landmark_points.Add(new Point(p.Item1, p.Item2));
                }
            }

            // Visualisation
            if (ShowTrackedVideo)
            {
                Dispatcher.Invoke(DispatcherPriority.Render, new TimeSpan(0, 0, 0, 0, 200), (Action)(() =>
                {
                    if (latest_img == null)
                    {
                        latest_img = frame.CreateWriteableBitmap();
                    }

                    frame.UpdateWriteableBitmap(latest_img);

                    video.Source = latest_img;
                    video.Confidence = 1;
                    video.FPS = processing_fps.GetFPS();
                    video.Progress = progress;

                    video.OverlayLines = new List<Tuple<Point, Point>>();

                    video.OverlayPoints = landmark_points;

                }));
            }
            latest_img = null;
        }


        // Capturing and processing the video frame by frame
        private void FeatureExtractionLoop(string output_file_name)
        {

            DateTime? startTime = CurrentTime;

            var lastFrameTime = CurrentTime;

            clnf_model.Reset();
            face_analyser.Reset();

            // If the camera calibration parameters are not set (indicated by -1), guesstimate them
            if(estimate_camera_parameters || fx == -1 || fy == -1 || cx == -1 || cy == -1)
            { 
                fx = 500.0 * (capture.width / 640.0);
                fy = 500.0 * (capture.height / 480.0);

                fx = (fx + fy) / 2.0;
                fy = fx;

                cx = capture.width / 2f;
                cy = capture.height / 2f;
            }

            // Setup the recorder first
            recorder = new Recorder(record_root, output_file_name, capture.width, capture.height, Record2DLandmarks, Record3DLandmarks, RecordModelParameters, RecordPose,
                RecordAUs, RecordGaze, RecordAligned, RecordHOG, clnf_model, face_analyser, fx, fy, cx, cy, DynamicAUModels);

            int frame_id = 0;

            double fps = capture.GetFPS();
            if (fps <= 0) fps = 30;

            while (thread_running)
            {
                //////////////////////////////////////////////
                // CAPTURE FRAME AND DETECT LANDMARKS FOLLOWED BY THE REQUIRED IMAGE PROCESSING
                //////////////////////////////////////////////
                RawImage frame = null;
                double progress = -1;

                frame = new RawImage(capture.GetNextFrame(false));
                progress = capture.GetProgress();

                if (frame.Width == 0)
                {
                    // This indicates that we reached the end of the video file
                    break;
                }

                lastFrameTime = CurrentTime;
                processing_fps.AddFrame();

                var grayFrame = new RawImage(capture.GetCurrentFrameGray());

                if (grayFrame == null)
                {
                    Console.WriteLine("Gray is empty");
                    continue;
                }

                detectionSucceeding = ProcessFrame(clnf_model, face_model_params, frame, grayFrame, fx, fy, cx, cy);

                // The face analysis step (for AUs and eye gaze)
                face_analyser.AddNextFrame(frame, clnf_model.CalculateAllLandmarks(), detectionSucceeding, false, ShowAppearance); // TODO change
                gaze_analyser.AddNextFrame(clnf_model, detectionSucceeding, fx, fy, cx, cy);

                recorder.RecordFrame(clnf_model, face_analyser, gaze_analyser, detectionSucceeding, frame_id + 1, ((double)frame_id) / fps);

                List<Tuple<double, double>> landmarks = clnf_model.CalculateVisibleLandmarks();

                VisualizeFeatures(frame, landmarks, fx, fy, cx, cy, progress);

                while (thread_running & thread_paused && skip_frames == 0)
                {
                    Thread.Sleep(10);
                }

                frame_id++;

                if (skip_frames > 0)
                    skip_frames--;

            }

            latest_img = null;
            skip_frames = 0;

            // Unpause if it's paused
            if (thread_paused)
            {
                Dispatcher.Invoke(DispatcherPriority.Render, new TimeSpan(0, 0, 0, 0, 200), (Action)(() =>
                {
                    PauseButton_Click(null, null);
                }));
            }

            recorder.FinishRecording(clnf_model, face_analyser);

        }

        // Replaying the features frame by frame
        private void FeatureVisualizationLoop(string input_feature_file, string input_video_file)
        {

            DateTime? startTime = CurrentTime;

            var lastFrameTime = CurrentTime;
           
            int frame_id = 0;

            double fps = capture.GetFPS();
            if (fps <= 0) fps = 30;

            while (thread_running)
            {
                //////////////////////////////////////////////
                // CAPTURE FRAME AND DETECT LANDMARKS FOLLOWED BY THE REQUIRED IMAGE PROCESSING
                //////////////////////////////////////////////
                RawImage frame = null;
                double progress = -1;

                frame = new RawImage(capture.GetNextFrame(false));
                progress = capture.GetProgress();

                if (frame.Width == 0)
                {
                    // This indicates that we reached the end of the video file
                    break;
                }

                // TODO stop button should actually clear the video
                lastFrameTime = CurrentTime;
                processing_fps.AddFrame();

                var grayFrame = new RawImage(capture.GetCurrentFrameGray());

                if (grayFrame == null)
                {
                    Console.WriteLine("Gray is empty");
                    continue;
                }

                detectionSucceeding = ProcessFrame(clnf_model, face_model_params, frame, grayFrame, fx, fy, cx, cy);

                // The face analysis step (for AUs)
                face_analyser.AddNextFrame(frame, clnf_model.CalculateAllLandmarks(), detectionSucceeding, false, ShowAppearance);

                // For gaze analysis
                gaze_analyser.AddNextFrame(clnf_model, detectionSucceeding, fx, fy, cx, cy);

                recorder.RecordFrame(clnf_model, face_analyser, gaze_analyser, detectionSucceeding, frame_id + 1, ((double)frame_id) / fps);

                List<Tuple<double, double>> landmarks = clnf_model.CalculateVisibleLandmarks();

                VisualizeFeatures(frame, landmarks, fx, fy, cx, cy, progress);
                

                while (thread_running & thread_paused && skip_frames == 0)
                {
                    Thread.Sleep(10);
                }

                frame_id++;

                if (skip_frames > 0)
                    skip_frames--;

            }

            latest_img = null;
            skip_frames = 0;

            // Unpause if it's paused
            if (thread_paused)
            {
                Dispatcher.Invoke(DispatcherPriority.Render, new TimeSpan(0, 0, 0, 0, 200), (Action)(() =>
                {
                    PauseButton_Click(null, null);
                }));
            }

            recorder.FinishRecording(clnf_model, face_analyser);

        }


        private void VisualizeFeatures(RawImage frame, List<Tuple<double, double>> landmarks, double fx, double fy, double cx, double cy, double progress)
        {
            List<Tuple<Point, Point>> lines = null;
            List<Tuple<double, double>> eye_landmarks = null;
            List<Tuple<Point, Point>> gaze_lines = null;
            Tuple<double, double> gaze_angle = new Tuple<double, double>(0, 0);

            List<double> pose = new List<double>();
            clnf_model.GetPose(pose, fx, fy, cx, cy);
            List<double> non_rigid_params = clnf_model.GetNonRigidParams();

            double confidence = (-clnf_model.GetConfidence()) / 2.0 + 0.5;

            if (confidence < 0)
                confidence = 0;
            else if (confidence > 1)
                confidence = 1;

            double scale = 0;

            if (detectionSucceeding)
            {
                
                eye_landmarks = clnf_model.CalculateVisibleEyeLandmarks();
                lines = clnf_model.CalculateBox((float)fx, (float)fy, (float)cx, (float)cy);

                scale = clnf_model.GetRigidParams()[0];

                gaze_lines = gaze_analyser.CalculateGazeLines(scale, (float)fx, (float)fy, (float)cx, (float)cy);
                gaze_angle = gaze_analyser.GetGazeAngle();
            }

            // Visualisation (as a separate function)
            Dispatcher.Invoke(DispatcherPriority.Render, new TimeSpan(0, 0, 0, 0, 200), (Action)(() =>
            {
                if (ShowAUs)
                {
                    var au_classes = face_analyser.GetCurrentAUsClass();
                    var au_regs = face_analyser.GetCurrentAUsReg();

                    auClassGraph.Update(au_classes);

                    var au_regs_scaled = new Dictionary<String, double>();
                    foreach (var au_reg in au_regs)
                    {
                        au_regs_scaled[au_reg.Key] = au_reg.Value / 5.0;
                        if (au_regs_scaled[au_reg.Key] < 0)
                            au_regs_scaled[au_reg.Key] = 0;

                        if (au_regs_scaled[au_reg.Key] > 1)
                            au_regs_scaled[au_reg.Key] = 1;
                    }
                    auRegGraph.Update(au_regs_scaled);
                }

                if (ShowGeometry)
                {
                    int yaw = (int)(pose[4] * 180 / Math.PI + 0.5);
                    int roll = (int)(pose[5] * 180 / Math.PI + 0.5);
                    int pitch = (int)(pose[3] * 180 / Math.PI + 0.5);

                    YawLabel.Content = yaw + "°";
                    RollLabel.Content = roll + "°";
                    PitchLabel.Content = pitch + "°";

                    XPoseLabel.Content = (int)pose[0] + " mm";
                    YPoseLabel.Content = (int)pose[1] + " mm";
                    ZPoseLabel.Content = (int)pose[2] + " mm";

                    nonRigidGraph.Update(non_rigid_params);

                    // Update eye gaze
                    String x_angle = String.Format("{0:F0}°", gaze_angle.Item1 * (180.0 / Math.PI));
                    String y_angle = String.Format("{0:F0}°", gaze_angle.Item2 * (180.0 / Math.PI));
                    GazeXLabel.Content = x_angle;
                    GazeYLabel.Content = y_angle;
                }

                if (ShowTrackedVideo)
                {
                    if (latest_img == null)
                    {
                        latest_img = frame.CreateWriteableBitmap();
                    }

                    frame.UpdateWriteableBitmap(latest_img);

                    video.Source = latest_img;
                    video.Confidence = confidence;
                    video.FPS = processing_fps.GetFPS();
                    video.Progress = progress;
                    video.FaceScale = scale;

                    if (!detectionSucceeding)
                    {
                        video.OverlayLines.Clear();
                        video.OverlayPoints.Clear();
                        video.OverlayEyePoints.Clear();
                        video.GazeLines.Clear();
                    }
                    else
                    {
                        video.OverlayLines = lines;

                        List<Point> landmark_points = new List<Point>();
                        foreach (var p in landmarks)
                        {
                            landmark_points.Add(new Point(p.Item1, p.Item2));
                        }

                        List<Point> eye_landmark_points = new List<Point>();
                        foreach (var p in eye_landmarks)
                        {
                            eye_landmark_points.Add(new Point(p.Item1, p.Item2));
                        }


                        video.OverlayPoints = landmark_points;
                        video.OverlayEyePoints = eye_landmark_points;
                        video.GazeLines = gaze_lines;
                    }
                }

                if (ShowAppearance)
                {
                    RawImage aligned_face = face_analyser.GetLatestAlignedFace();
                    RawImage hog_face = face_analyser.GetLatestHOGDescriptorVisualisation();

                    if (latest_aligned_face == null)
                    {
                        latest_aligned_face = aligned_face.CreateWriteableBitmap();
                        latest_HOG_descriptor = hog_face.CreateWriteableBitmap();
                    }

                    aligned_face.UpdateWriteableBitmap(latest_aligned_face);
                    hog_face.UpdateWriteableBitmap(latest_HOG_descriptor);

                    AlignedFace.Source = latest_aligned_face;
                    AlignedHOG.Source = latest_HOG_descriptor;
                }
            }));


        }

        private void StopTracking()
        {
            // First complete the running of the thread
            if (processing_thread != null)
            {
                // Tell the other thread to finish
                thread_running = false;
                processing_thread.Join();
            }
        }



        // ----------------------------------------------------------
        // Interacting with landmark detection and face analysis

        private bool ProcessFrame(CLNF clnf_model, FaceModelParameters clnf_params, RawImage frame, RawImage grayscale_frame, double fx, double fy, double cx, double cy)
        {
            detectionSucceeding = clnf_model.DetectLandmarksInVideo(grayscale_frame, clnf_params);
            return detectionSucceeding;

        }

        private List<List<Tuple<double, double>>> ProcessImage(CLNF clnf_model, FaceModelParameters clnf_params, RawImage frame, RawImage grayscale_frame)
        {
            List<List<Tuple<double, double>>> landmark_detections = clnf_model.DetectMultiFaceLandmarksInImage(grayscale_frame, clnf_params);
            return landmark_detections;

        }


        // ----------------------------------------------------------
        // Mode handling (image, video)
        // ----------------------------------------------------------

        // Disable GUI components that should not be active during processing
        private void SetupFeatureExtractionMode()
        {
            Dispatcher.Invoke((Action)(() =>
            {
                SettingsMenu.IsEnabled = false;
                RecordingMenu.IsEnabled = false;
                AUSetting.IsEnabled = false;

                PauseButton.IsEnabled = true;
                StopButton.IsEnabled = true;
                NextFiveFramesButton.IsEnabled = false;
                NextFrameButton.IsEnabled = false;
            }));
        }

        // When the processing is done re-enable the components
        private void EndMode()
        {
            Dispatcher.Invoke(DispatcherPriority.Render, new TimeSpan(0, 0, 0, 1, 0), (Action)(() =>
            {

                SettingsMenu.IsEnabled = true;
                RecordingMenu.IsEnabled = true;
                AUSetting.IsEnabled = true;

                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                NextFiveFramesButton.IsEnabled = false;
                NextFrameButton.IsEnabled = false;

                // Clean up the interface itself
                video.Source = null;
                
                auClassGraph.Update(new Dictionary<string, double>());
                auRegGraph.Update(new Dictionary<string, double>());
                YawLabel.Content = "0°";
                RollLabel.Content = "0°";
                PitchLabel.Content = "0°";

                XPoseLabel.Content = "0 mm";
                YPoseLabel.Content = "0 mm";
                ZPoseLabel.Content = "0 mm";

                nonRigidGraph.Update(new List<double>());

                GazeXLabel.Content = "0°";
                GazeYLabel.Content = "0°";
                
                AlignedFace.Source = null;
                AlignedHOG.Source = null;

            }));
        }

        // ----------------------------------------------------------
        // Opening Videos/Images
        // ----------------------------------------------------------

        private void videoFileOpenClick(object sender, RoutedEventArgs e)
        {
            new Thread(() => openVideoFile()).Start();
        }

        private void openVideoFile()
        {
            StopTracking();

            Dispatcher.Invoke(DispatcherPriority.Render, new TimeSpan(0, 0, 0, 2, 0), (Action)(() =>
            {
                var d = new OpenFileDialog();
                d.Multiselect = true;
                d.Filter = "Video files|*.avi;*.wmv;*.mov;*.mpg;*.mpeg;*.mp4";

                if (d.ShowDialog(this) == true)
                {

                    string[] video_files = d.FileNames;

                    processing_thread = new Thread(() => ProcessingLoop(video_files));
                    processing_thread.Start();

                }
            }));
        }


        private void imageFileOpenClick(object sender, RoutedEventArgs e)
        {
            new Thread(() => imageOpen()).Start();
        }

        private void imageOpen()
        {
            StopTracking();

            Dispatcher.Invoke(DispatcherPriority.Render, new TimeSpan(0, 0, 0, 2, 0), (Action)(() =>
            {
                var d = new OpenFileDialog();
                d.Multiselect = true;
                d.Filter = "Image files|*.jpg;*.jpeg;*.bmp;*.png;*.gif";

                if (d.ShowDialog(this) == true)
                {

                    string[] image_files = d.FileNames;

                    processing_thread = new Thread(() => ProcessingLoop(image_files, -3));
                    processing_thread.Start();

                }
            }));
        }

        private void imageSequenceFileOpenClick(object sender, RoutedEventArgs e)
        {
            new Thread(() => imageSequenceOpen()).Start();
        }

        private void imageSequenceOpen()
        {
            StopTracking();

            Dispatcher.Invoke(DispatcherPriority.Render, new TimeSpan(0, 0, 0, 2, 0), (Action)(() =>
            {
                var d = new OpenFileDialog();
                d.Multiselect = true;
                d.Filter = "Image files|*.jpg;*.jpeg;*.bmp;*.png;*.gif";

                if (d.ShowDialog(this) == true)
                {

                    string[] image_files = d.FileNames;

                    processing_thread = new Thread(() => ProcessingLoop(image_files, -2));
                    processing_thread.Start();

                }
            }));
        }

        // --------------------------------------------------------
        // Button handling
        // --------------------------------------------------------

        // Cleanup stuff when closing the window
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (processing_thread != null)
            {
                // Stop capture and tracking
                thread_running = false;
                processing_thread.Join();

                capture.Dispose();
            }
            face_analyser.Dispose();
        }

        // Stopping the tracking
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (processing_thread != null)
            {
                // Stop capture and tracking
                thread_paused = false;
                thread_running = false;
                // Let the processing thread finish
                processing_thread.Join();

                // Clean up the interface
                EndMode();
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (processing_thread != null)
            {
                // Stop capture and tracking                
                thread_paused = !thread_paused;

                NextFrameButton.IsEnabled = thread_paused;
                NextFiveFramesButton.IsEnabled = thread_paused;

                if (thread_paused)
                {
                    PauseButton.Content = "Resume";
                }
                else
                {
                    PauseButton.Content = "Pause";
                }
            }
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender.Equals(NextFrameButton))
            {
                skip_frames += 1;
            }
            else if (sender.Equals(NextFiveFramesButton))
            {
                skip_frames += 5;
            }
        }


        private void VisualisationChange(object sender, RoutedEventArgs e)
        {
            // Collapsing or restoring the windows here
            if (!ShowTrackedVideo)
            {
                VideoBorder.Visibility = System.Windows.Visibility.Collapsed;
                MainGrid.ColumnDefinitions[0].Width = new GridLength(0, GridUnitType.Star);
            }
            else
            {
                VideoBorder.Visibility = System.Windows.Visibility.Visible;
                MainGrid.ColumnDefinitions[0].Width = new GridLength(2.1, GridUnitType.Star);
            }

            if (!ShowAppearance)
            {
                AppearanceBorder.Visibility = System.Windows.Visibility.Collapsed;
                MainGrid.ColumnDefinitions[1].Width = new GridLength(0, GridUnitType.Star);
            }
            else
            {
                AppearanceBorder.Visibility = System.Windows.Visibility.Visible;
                MainGrid.ColumnDefinitions[1].Width = new GridLength(0.8, GridUnitType.Star);
            }

            // Collapsing or restoring the windows here
            if (!ShowGeometry)
            {
                GeometryBorder.Visibility = System.Windows.Visibility.Collapsed;
                MainGrid.ColumnDefinitions[2].Width = new GridLength(0, GridUnitType.Star);
            }
            else
            {
                GeometryBorder.Visibility = System.Windows.Visibility.Visible;
                MainGrid.ColumnDefinitions[2].Width = new GridLength(1.0, GridUnitType.Star);
            }

            // Collapsing or restoring the windows here
            if (!ShowAUs)
            {
                ActionUnitBorder.Visibility = System.Windows.Visibility.Collapsed;
                MainGrid.ColumnDefinitions[3].Width = new GridLength(0, GridUnitType.Star);
            }
            else
            {
                ActionUnitBorder.Visibility = System.Windows.Visibility.Visible;
                MainGrid.ColumnDefinitions[3].Width = new GridLength(1.6, GridUnitType.Star);
            }

        }

        private void UseDynamicModelsCheckBox_Click(object sender, RoutedEventArgs e)
        {
            // Change the face analyser, this should be safe as the model is only allowed to change when not running
            String root = AppDomain.CurrentDomain.BaseDirectory;
            face_analyser = new FaceAnalyserManaged(root, DynamicAUModels, image_output_size);
        }

        private void setOutputImageSize_Click(object sender, RoutedEventArgs e)
        {

            NumberEntryWindow number_entry_window = new NumberEntryWindow(image_output_size);
            number_entry_window.Icon = this.Icon;

            number_entry_window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            if (number_entry_window.ShowDialog() == true)
            {
                image_output_size = number_entry_window.OutputInt;
                String root = AppDomain.CurrentDomain.BaseDirectory;
                face_analyser = new FaceAnalyserManaged(root, DynamicAUModels, image_output_size);

            }
        }

        private void setCameraParameters_Click(object sender, RoutedEventArgs e)
        {
            CameraParametersEntry camera_params_entry_window = new CameraParametersEntry(fx, fy, cx, cy);
            camera_params_entry_window.Icon = this.Icon;

            camera_params_entry_window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            if (camera_params_entry_window.ShowDialog() == true)
            {
                fx = camera_params_entry_window.Fx;
                fy = camera_params_entry_window.Fy;
                cx = camera_params_entry_window.Cx;
                cy = camera_params_entry_window.Cy;
                if(fx == -1 || fy == -1 || cx == -1 || cy == -1)
                {
                    estimate_camera_parameters = true;
                }
                else
                {
                    estimate_camera_parameters = false;
                }
            }
        }

        private void OutputLocationItem_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new CommonOpenFileDialog();
            dlg.Title = "Select output directory";
            dlg.IsFolderPicker = true;
            dlg.AllowNonFileSystemItems = false;
            dlg.EnsureFileExists = true;
            dlg.EnsurePathExists = true;
            dlg.EnsureReadOnly = false;
            dlg.EnsureValidNames = true;
            dlg.Multiselect = false;
            dlg.ShowPlacesList = true;

            if (dlg.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var folder = dlg.FileName;
                record_root = folder;
            }
        }

    }
}