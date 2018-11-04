using System;
using System.Linq;
using AVFoundation;
using CoreAnimation;
using CoreFoundation;
using CoreGraphics;
using CoreImage;
using CoreMedia;
using UIKit;

namespace Vision.FaceLandmarksDemo
{
    public partial class ViewController : UIViewController
    {
        readonly AVCaptureSession _captureSession = new AVCaptureSession();
        readonly CAShapeLayer _shapeLayer = new CAShapeLayer();

        AVCaptureVideoPreviewLayer _videoPreview;

        protected ViewController(IntPtr handle) : base(handle)
        {
            // Note: this .ctor should not contain any initialization logic.
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            // Perform any additional setup after loading the view, typically from a nib.

            _videoPreview = new AVCaptureVideoPreviewLayer(_captureSession);

            ConfigureDeviceAndStart();
        }

        public override void ViewDidAppear(bool animated)
        {
            base.ViewDidAppear(animated);

            View.Layer.AddSublayer(_videoPreview);

            // needs to filp coordinate system for Vision
            _shapeLayer.AffineTransform = CGAffineTransform.MakeScale(1, -1);

            View.Layer.AddSublayer(_shapeLayer);
        }

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();

            _videoPreview.Frame = View.Frame;
            _shapeLayer.Frame = View.Frame;
        }

        public AVCaptureDevice GetDevice()
        {
            var videoDeviceDiscoverySession = AVCaptureDeviceDiscoverySession.Create(new AVCaptureDeviceType[] { AVCaptureDeviceType.BuiltInWideAngleCamera }, AVMediaType.Video, AVCaptureDevicePosition.Front);
            return videoDeviceDiscoverySession.Devices.FirstOrDefault();
        }

        public void ConfigureDeviceAndStart()
        {
            var device = GetDevice();

            if (device == null)
            {
                return;
            }

            try
            {
                if (device.LockForConfiguration(out var error))
                {
                    if (device.IsFocusModeSupported(AVCaptureFocusMode.ContinuousAutoFocus))
                    {
                        device.FocusMode = AVCaptureFocusMode.ContinuousAutoFocus;
                    }

                    device.UnlockForConfiguration();
                }

                // Configure Input
                var input = AVCaptureDeviceInput.FromDevice(device, out var error2);
                _captureSession.AddInput(input);

                // Configure Output
                var videoOutput = new AVCaptureVideoDataOutput();
                var settings = new AVVideoSettingsUncompressed()
                {
                    PixelFormatType = CoreVideo.CVPixelFormatType.CV32BGRA
                };
                videoOutput.WeakVideoSettings = settings.Dictionary;
                videoOutput.AlwaysDiscardsLateVideoFrames = true;

                var videoCaptureQueue = new DispatchQueue("Video Queue");
                videoOutput.SetSampleBufferDelegateQueue(new OutputRecorder(View, _shapeLayer), videoCaptureQueue);

                if (_captureSession.CanAddOutput(videoOutput))
                {
                    _captureSession.AddOutput(videoOutput);
                }

                // Start session
                _captureSession.StartRunning();
            }
            catch (Exception e)
            {
                Console.Write(e);
            }
        }
    }

    public class OutputRecorder : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        readonly UIView _view;
        CAShapeLayer _shapeLayer;

        public OutputRecorder(UIView view, CAShapeLayer shapeLayer)
        {
            _shapeLayer = shapeLayer;
            _view = view;
        }

        public override void DidOutputSampleBuffer(AVCaptureOutput captureOutput, CMSampleBuffer sampleBuffer, AVCaptureConnection connection)
        {
            using (var pixelBuffer = sampleBuffer.GetImageBuffer())
            using (var ciImage = new CIImage(pixelBuffer))
            using (var imageWithOrientation = ciImage.CreateByApplyingOrientation(ImageIO.CGImagePropertyOrientation.LeftMirrored))
            {
                DetectFaces(imageWithOrientation);
            }

            sampleBuffer.Dispose();
        }

        VNSequenceRequestHandler _sequenceRequestHandler = new VNSequenceRequestHandler();
        VNDetectFaceLandmarksRequest _detectFaceLandmarksRequest;

        void DetectFaces(CIImage imageWithOrientation)
        {
            if (_detectFaceLandmarksRequest == null)
            {
                _detectFaceLandmarksRequest = new VNDetectFaceLandmarksRequest((request, error) =>
                {
                    RemoveSublayers(_shapeLayer);

                    if (error == null)
                    {
                        var results = request.GetResults<VNFaceObservation>();

                        foreach (var result in results)
                        {
                            if (result.Landmarks != null)
                            {
                                var boundingBox = result.BoundingBox;
                                var scaledBouncingBox = Scale(boundingBox, _view.Bounds.Size);

                                InvokeOnMainThread(() =>
                                {
                                    DrawLandmark(result.Landmarks.FaceContour, scaledBouncingBox, false, UIColor.White);

                                    DrawLandmark(result.Landmarks.LeftEye, scaledBouncingBox, true, UIColor.Green);
                                    DrawLandmark(result.Landmarks.RightEye, scaledBouncingBox, true, UIColor.Green);

                                    DrawLandmark(result.Landmarks.Nose, scaledBouncingBox, true, UIColor.Blue);
                                    DrawLandmark(result.Landmarks.NoseCrest, scaledBouncingBox, false, UIColor.Blue);

                                    DrawLandmark(result.Landmarks.InnerLips, scaledBouncingBox, true, UIColor.Yellow);
                                    DrawLandmark(result.Landmarks.OuterLips, scaledBouncingBox, true, UIColor.Yellow);

                                    DrawLandmark(result.Landmarks.LeftEyebrow, scaledBouncingBox, false, UIColor.Blue);
                                    DrawLandmark(result.Landmarks.RightEyebrow, scaledBouncingBox, false, UIColor.Blue);
                                });
                            }
                        }
                    }
                    else
                    {
                        throw new Exception(error.LocalizedDescription);
                    }
                });
            }

            _sequenceRequestHandler.Perform(new[] { _detectFaceLandmarksRequest }, imageWithOrientation, out var requestHandlerError);
            if (requestHandlerError != null)
            {
                throw new Exception(requestHandlerError.LocalizedDescription);
            }
        }

        static void RemoveSublayers(CAShapeLayer layer)
        {
            if (layer.Sublayers?.Any() == true)
            {
                var sublayers = layer.Sublayers?.ToList();
                foreach (var sublayer in sublayers)
                {
                    sublayer.RemoveFromSuperLayer();
                }
            }
        }

        void DrawLandmark(VNFaceLandmarkRegion2D feature, CGRect scaledBouncingBox, bool closed, UIColor color)
        {
            if (feature == null)
            {
                return;
            }

            var mappedPoints = feature.NormalizedPoints.Select(o => new CGPoint(x: o.X * scaledBouncingBox.Width + scaledBouncingBox.X, y: o.Y * scaledBouncingBox.Height + scaledBouncingBox.Y));

            using (var newLayer = new CAShapeLayer())
            {
                newLayer.Frame = _view.Frame;
                newLayer.StrokeColor = color.CGColor;
                newLayer.LineWidth = 2;
                newLayer.FillColor = UIColor.Clear.CGColor;


                using (UIBezierPath path = new UIBezierPath())
                {
                    path.MoveTo(mappedPoints.First());
                    foreach (var point in mappedPoints.Skip(1))
                    {
                        path.AddLineTo(point);
                    }

                    if (closed)
                    {
                        path.AddLineTo(mappedPoints.First());
                    }

                    newLayer.Path = path.CGPath;
                }

                _shapeLayer.AddSublayer(newLayer);
            }
        }

        static CGRect Scale(CGRect source, CGSize dest)
        {
            return new CGRect
            {
                X = source.X * dest.Width,
                Y = source.Y * dest.Height,
                Width = source.Width * dest.Width,
                Height = source.Height * dest.Height
            };
        }
    }
}