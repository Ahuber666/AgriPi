using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AI.MachineLearning;
using ObjectDetectionApp.Models;
using ObjectDetectionApp.Utilities;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media;

namespace ObjectDetectionApp.Services;

public sealed class ObjectDetectionService : IAsyncDisposable
{
    private readonly LearningModelSession _session;
    private readonly string[] _labels;
    private readonly float _confidenceThreshold;
    private readonly int _inputWidth;
    private readonly int _inputHeight;

    public ObjectDetectionService(string modelPath, string labelsPath, float confidenceThreshold = 0.5f)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Unable to locate ONNX model.", modelPath);
        }

        if (!File.Exists(labelsPath))
        {
            throw new FileNotFoundException("Unable to locate labels file.", labelsPath);
        }

        _labels = File.ReadAllLines(labelsPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToArray();

        var model = LearningModel.LoadFromFilePath(modelPath);
        var device = new LearningModelDevice(LearningModelDeviceKind.Default);
        _session = new LearningModelSession(model, device);

        var inputFeature = model.InputFeatures.First() as TensorFeatureDescriptor;
        if (inputFeature is null || inputFeature.Shape.Count < 4)
        {
            throw new InvalidDataException("Unexpected model input tensor shape.");
        }

        _inputHeight = (int)inputFeature.Shape[^2];
        _inputWidth = (int)inputFeature.Shape[^1];
        _confidenceThreshold = confidenceThreshold;
    }

    public async Task<IReadOnlyList<DetectionResult>> EvaluateAsync(SoftwareBitmap bitmap, Size renderSize, CancellationToken cancellationToken)
    {
        using var resizedBitmap = await ResizeBitmapAsync(bitmap, _inputWidth, _inputHeight, cancellationToken).ConfigureAwait(false);

        var inputTensor = CreateInputTensor(resizedBitmap);

        var binding = new LearningModelBinding(_session);
        LearningModelEvaluationResult? results = null;
        TensorFloat? boxesTensor = null;
        TensorFloat? scoresTensor = null;
        IReadOnlyList<int>? labelData = null;
        List<DetectionResult>? detections = null;

        try
        {
            binding.Bind(_session.Model.InputFeatures[0].Name, inputTensor);

            results = await Task.Run(() => _session.Evaluate(binding, "ObjectDetectionSession"), cancellationToken).ConfigureAwait(false);

            boxesTensor = GetTensorFloat(results, "boxes") ?? GetFirstTensorFloat(results, t => t.Shape.Count >= 3 && t.Shape[^1] == 4)
                ?? throw new InvalidDataException("Unable to locate bounding box tensor in model output.");
            scoresTensor = GetTensorFloat(results, "scores") ?? GetFirstTensorFloat(results, t => t.Shape.Count >= 2 && t.Shape[^1] == boxesTensor.Shape[^2])
                ?? throw new InvalidDataException("Unable to locate score tensor in model output.");
            labelData = GetLabels(results, boxesTensor.Shape[^2]);

            var boxes = CopyTensorData(boxesTensor);
            var scores = CopyTensorData(scoresTensor);

            var detectionResults = new List<DetectionResult>();
            int boxCount = boxes.Length / 4;

            for (int i = 0; i < boxCount; i++)
            {
                float score = scores[i];
                if (score < _confidenceThreshold)
                {
                    continue;
                }

                float xMin = boxes[i * 4 + 0];
                float yMin = boxes[i * 4 + 1];
                float xMax = boxes[i * 4 + 2];
                float yMax = boxes[i * 4 + 3];

                var rect = new System.Windows.Rect(
                    xMin * renderSize.Width,
                    yMin * renderSize.Height,
                    Math.Max(0, (xMax - xMin) * renderSize.Width),
                    Math.Max(0, (yMax - yMin) * renderSize.Height));

                string label = ResolveLabel(labelData, i);

                detectionResults.Add(new DetectionResult(rect, label, score));
            }

            detections = detectionResults;
        }
        finally
        {
            DisposeWinRtObject(scoresTensor);
            DisposeWinRtObject(boxesTensor);
            DisposeWinRtObject(results);
            DisposeWinRtObject(binding);
            DisposeWinRtObject(inputTensor);
        }

        return detections ?? Array.Empty<DetectionResult>();
    }

    public ValueTask DisposeAsync()
    {
        _session.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<SoftwareBitmap> ResizeBitmapAsync(SoftwareBitmap source, int width, int height, CancellationToken cancellationToken)
    {
        var converted = source;
        bool createdNew = false;
        if (converted.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || converted.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            converted = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            createdNew = true;
        }

        var dest = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        using var input = VideoFrame.CreateWithSoftwareBitmap(converted);
        using var output = VideoFrame.CreateWithSoftwareBitmap(dest);
        await input.CopyToAsync(output).AsTask(cancellationToken).ConfigureAwait(false);
        if (createdNew)
        {
            converted.Dispose();
        }

        return dest;
    }

    private TensorFloat CreateInputTensor(SoftwareBitmap bitmap)
    {
        var buffer = bitmap.ToBgra8Bytes();
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int channelSize = width * height;
        var data = new float[3 * channelSize];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int pixelIndex = (y * width + x) * 4;
                byte b = buffer[pixelIndex + 0];
                byte g = buffer[pixelIndex + 1];
                byte r = buffer[pixelIndex + 2];

                float normalizedR = r / 255f;
                float normalizedG = g / 255f;
                float normalizedB = b / 255f;

                int offset = y * width + x;
                data[offset] = normalizedR;
                data[channelSize + offset] = normalizedG;
                data[channelSize * 2 + offset] = normalizedB;
            }
        }

        var shape = new long[] { 1, 3, height, width };
        return TensorFloat.CreateFromArray(shape, data);
    }

    private static TensorFloat? GetTensorFloat(LearningModelEvaluationResult results, string name)
    {
        return results.Outputs.TryGetValue(name, out var value) ? value as TensorFloat : null;
    }

    private static TensorFloat? GetFirstTensorFloat(LearningModelEvaluationResult results, Func<TensorFloat, bool> predicate)
    {
        foreach (var output in results.Outputs.Values)
        {
            if (output is TensorFloat tensor && predicate(tensor))
            {
                return tensor;
            }
        }

        return null;
    }

    private IReadOnlyList<int>? GetLabels(LearningModelEvaluationResult results, long expectedCount)
    {
        if (results.Outputs.TryGetValue("labels", out var labelsObj))
        {
            var labels = ExtractLabels(labelsObj, expectedCount: null);
            if (labels is not null)
            {
                return labels;
            }
        }

        foreach (var output in results.Outputs.Values)
        {
            var labels = ExtractLabels(output, expectedCount);
            if (labels is not null)
            {
                return labels;
            }
        }

        return null;
    }

    private string ResolveLabel(IReadOnlyList<int>? labels, int index)
    {
        if (labels is not null && index < labels.Count)
        {
            var labelIndex = labels[index];
            if (labelIndex >= 0 && labelIndex < _labels.Length)
            {
                return _labels[labelIndex];
            }
        }

        return "Unknown";
    }

    private static IReadOnlyList<int>? ExtractLabels(object candidate, long? expectedCount)
    {
        switch (candidate)
        {
            case TensorInt64Bit tensorInt when expectedCount is null || tensorInt.Shape[^1] == expectedCount:
                try
                {
                    return tensorInt.GetAsVectorView().Select(v => (int)v).ToArray();
                }
                finally
                {
                    DisposeWinRtObject(tensorInt);
                }
            case TensorFloat tensorFloat when expectedCount is null || tensorFloat.Shape[^1] == expectedCount:
                try
                {
                    return tensorFloat.GetAsVectorView().Select(v => (int)Math.Round(v)).ToArray();
                }
                finally
                {
                    DisposeWinRtObject(tensorFloat);
                }
        }

        return null;
    }

    private static float[] CopyTensorData(TensorFloat tensor)
    {
        var view = tensor.GetAsVectorView();
        var data = new float[view.Count];
        for (int i = 0; i < view.Count; i++)
        {
            data[i] = view[i];
        }

        return data;
    }

    private static void DisposeWinRtObject(object? instance)
    {
        switch (instance)
        {
            case null:
                return;
        }

        if (instance is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
                // Suppress cleanup failures to avoid masking the original error.
            }
        }

        if (instance is IClosable closable)
        {
            try
            {
                closable.Close();
            }
            catch (ObjectDisposedException)
            {
                // Ignore objects that were already disposed.
            }
            catch (Exception)
            {
                // Suppress cleanup failures to avoid masking the original error.
            }
        }
    }
}
