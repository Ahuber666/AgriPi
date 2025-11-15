using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ObjectDetectionApp.Models;
using ObjectDetectionApp.Services;
using ObjectDetectionApp.Utilities;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Media;

using WpfSize = System.Windows.Size;
using WinRtSize = Windows.Foundation.Size;

namespace ObjectDetectionApp;

public partial class MainWindow : Window
{
    private ObjectDetectionService? _detectionService;
    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _frameReader;
    private CancellationTokenSource? _processingCancellation;
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private bool _isStopping;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryInitializeDetector();
    }

    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (_detectionService is null)
        {
            StatusText.Text = "Model not loaded";
            return;
        }

        StartButton.IsEnabled = false;
        StatusText.Text = "Starting camera...";

        try
        {
            await InitializeCameraAsync();
            StopButton.IsEnabled = true;
            StatusText.Text = "Camera running";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Camera error: {ex.Message}";
            StartButton.IsEnabled = true;
        }
    }

    private async void OnStopClicked(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = true;
        StatusText.Text = "Stopping camera...";
        await ShutdownCameraAsync();
        StatusText.Text = "Camera stopped";
    }

    private async void TryInitializeDetector()
    {
        var assetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
        var modelPath = Path.Combine(assetsPath, "model.onnx");
        var labelsPath = Path.Combine(assetsPath, "labels.txt");

        try
        {
            _detectionService = new ObjectDetectionService(modelPath, labelsPath, confidenceThreshold: 0.5f);
            StatusText.Text = "Model loaded. Click Start to begin.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Model error: {ex.Message}";
            StartButton.IsEnabled = false;
        }
    }

    private async Task InitializeCameraAsync()
    {
        _processingCancellation?.Cancel();
        _processingCancellation = new CancellationTokenSource();

        _mediaCapture = new MediaCapture();
        var settings = new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl
        };

        await _mediaCapture.InitializeAsync(settings);
        var source = _mediaCapture.FrameSources.Values
            .FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview || s.Info.MediaStreamType == MediaStreamType.VideoRecord)
            ?? throw new InvalidOperationException("No suitable video source found.");

        var format = source.SupportedFormats
            .OrderByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
            .FirstOrDefault();

        if (format is not null)
        {
            await source.SetFormatAsync(format);
        }

        _frameReader = await _mediaCapture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _frameReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _frameReader.FrameArrived += OnFrameArrived;
        var status = await _frameReader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            throw new InvalidOperationException($"Unable to start frame reader: {status}");
        }
    }

    private async Task ShutdownCameraAsync()
    {
        if (_isStopping)
        {
            return;
        }

        _isStopping = true;
        try
        {
            _processingCancellation?.Cancel();
            _processingCancellation?.Dispose();
            _processingCancellation = null;

            if (_frameReader is not null)
            {
                _frameReader.FrameArrived -= OnFrameArrived;
                await _frameReader.StopAsync();
                _frameReader.Dispose();
                _frameReader = null;
            }

            _mediaCapture?.Dispose();
            _mediaCapture = null;
        }
        finally
        {
            _isStopping = false;
        }
    }

    private async void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_detectionService is null || _processingCancellation?.IsCancellationRequested == true)
        {
            return;
        }

        if (!_processingGate.Wait(0))
        {
            return;
        }

        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            using var videoFrame = frame?.VideoMediaFrame?.GetVideoFrame();
            if (videoFrame?.SoftwareBitmap is null)
            {
                return;
            }

            SoftwareBitmap? bitmapCopy = null;
            try
            {
                bitmapCopy = SoftwareBitmap.Copy(videoFrame.SoftwareBitmap);
            }
            catch
            {
                try
                {
                    bitmapCopy = SoftwareBitmap.Convert(videoFrame.SoftwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => StatusText.Text = $"Frame copy error: {ex.Message}");
                }
            }

            if (bitmapCopy is null)
            {
                return;
            }

            await ProcessFrameAsync(bitmapCopy, _processingCancellation!.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => StatusText.Text = $"Processing error: {ex.Message}");
        }
        finally
        {
            _processingGate.Release();
        }
    }

    private async Task ProcessFrameAsync(SoftwareBitmap bitmap, CancellationToken token)
    {
        if (_detectionService is null)
        {
            bitmap.Dispose();
            return;
        }

        SoftwareBitmap? convertedBitmap = null;
        try
        {
            var workingBitmap = bitmap;
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                convertedBitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                workingBitmap = convertedBitmap;
            }

            var frameSize = new WinRtSize(workingBitmap.PixelWidth, workingBitmap.PixelHeight);
            var detections = await _detectionService.EvaluateAsync(workingBitmap, frameSize, token).ConfigureAwait(false);
            var pixels = workingBitmap.ToBgra8Bytes();
            var wpfFrameSize = new WpfSize(frameSize.Width, frameSize.Height);

            await Dispatcher.InvokeAsync(() =>
            {
                PreviewImage.Source = pixels.ToWriteableBitmap((int)wpfFrameSize.Width, (int)wpfFrameSize.Height);
                DrawDetections(detections, wpfFrameSize);
            });
        }
        finally
        {
            convertedBitmap?.Dispose();
            bitmap.Dispose();
        }
    }

    private void DrawDetections(IReadOnlyCollection<DetectionResult> detections, WpfSize frameSize)
    {
        OverlayCanvas.Children.Clear();

        double displayWidth = PreviewImage.ActualWidth;
        double displayHeight = PreviewImage.ActualHeight;

        if (displayWidth <= 0 || displayHeight <= 0)
        {
            if (PreviewImage.Source is BitmapSource source)
            {
                displayWidth = source.PixelWidth;
                displayHeight = source.PixelHeight;
            }
            else
            {
                displayWidth = frameSize.Width;
                displayHeight = frameSize.Height;
            }
        }

        OverlayCanvas.Width = displayWidth;
        OverlayCanvas.Height = displayHeight;

        double scaleX = displayWidth / frameSize.Width;
        double scaleY = displayHeight / frameSize.Height;

        foreach (var detection in detections)
        {
            var rectangle = new System.Windows.Shapes.Rectangle
            {
                Width = detection.BoundingBox.Width * scaleX,
                Height = detection.BoundingBox.Height * scaleY,
                Stroke = Brushes.Lime,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(40, 0, 255, 0))
            };

            Canvas.SetLeft(rectangle, detection.BoundingBox.X * scaleX);
            Canvas.SetTop(rectangle, detection.BoundingBox.Y * scaleY);
            OverlayCanvas.Children.Add(rectangle);

            var label = new TextBlock
            {
                Text = $"{detection.Label} ({detection.Confidence:P0})",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                Padding = new Thickness(4, 2, 4, 2),
                FontWeight = FontWeights.Bold
            };

            Canvas.SetLeft(label, detection.BoundingBox.X * scaleX);
            Canvas.SetTop(label, Math.Max(0, detection.BoundingBox.Y * scaleY - label.FontSize - 4));
            OverlayCanvas.Children.Add(label);
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        await ShutdownCameraAsync();
        if (_detectionService is not null)
        {
            await _detectionService.DisposeAsync();
        }
    }
}
