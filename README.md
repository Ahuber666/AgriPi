# AgriPi Object Detection

A Windows desktop application built with WPF and Windows ML to run a Custom Vision object-detection model in real time from a webcam.

## Prerequisites

- Windows 10/11 with the .NET 7 SDK installed.
- A compatible webcam.
- An ONNX object-detection model exported from Azure Custom Vision (Compact domain recommended) and the accompanying `labels.txt` file.

## Project Structure

```
src/
  ObjectDetectionApp/
    App.xaml                # WPF application bootstrapper
    MainWindow.xaml         # UI definition with video preview and overlay canvas
    MainWindow.xaml.cs      # Camera capture, inference loop, and overlay rendering
    Models/DetectionResult.cs
    Services/ObjectDetectionService.cs
    Utilities/SoftwareBitmapExtensions.cs
```

Place your model assets in `src/ObjectDetectionApp/Assets/` as `model.onnx` and `labels.txt`. The app will automatically load them on startup.

## Running the App

1. Restore dependencies and build the project:
   ```powershell
   dotnet restore src/ObjectDetectionApp/ObjectDetectionApp.csproj
   dotnet build src/ObjectDetectionApp/ObjectDetectionApp.csproj
   ```
2. Run the application:
   ```powershell
   dotnet run --project src/ObjectDetectionApp/ObjectDetectionApp.csproj
   ```
3. Click **Start** to initialize the webcam and begin live detection. Use **Stop** to release the camera.

## Notes

- Inference runs asynchronously to keep the UI responsive. Bounding boxes are rendered with confidence labels above the video feed.
- The project uses `Microsoft.AI.MachineLearning` so it can take advantage of available hardware accelerators via Windows ML.
- For best performance, ensure your model’s input size matches the target device capabilities; resizing occurs automatically before inference.
