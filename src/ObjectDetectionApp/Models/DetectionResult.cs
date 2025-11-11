using System.Windows;

namespace ObjectDetectionApp.Models;

public sealed record DetectionResult(Rect BoundingBox, string Label, float Confidence);
