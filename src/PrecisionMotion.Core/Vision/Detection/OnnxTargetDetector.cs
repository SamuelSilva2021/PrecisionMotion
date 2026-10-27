using System.Buffers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PrecisionMotion.Core.Vision.Abstractions;
using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Detection;

public sealed class OnnxTargetDetector : ITargetDetector
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _modelWidth;
    private readonly int _modelHeight;
    private readonly float _confidenceThreshold;
    private readonly float _iouThreshold;
    private readonly HashSet<int>? _allowedClassIds;
    private bool _disposed;

    public OnnxTargetDetector(
        string modelPath,
        int modelWidth = 640,
        int modelHeight = 640,
        float confidenceThreshold = 0.5f,
        float iouThreshold = 0.45f,
        IEnumerable<int>? allowedClassIds = null,
        SessionOptions? options = null)
    {
        _modelWidth = modelWidth;
        _modelHeight = modelHeight;
        _confidenceThreshold = confidenceThreshold;
        _iouThreshold = iouThreshold;
        _allowedClassIds = allowedClassIds != null ? new HashSet<int>(allowedClassIds) : null;

        var opt = options ?? new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

        _session = new InferenceSession(modelPath, opt);
        _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? "images";
    }

    public ValueTask<IReadOnlyList<DetectedTarget>> DetectAsync(
        ReadOnlyMemory<byte> frameData, 
        int width, 
        int height, 
        CancellationToken cancellationToken)
    {
        if (_disposed || cancellationToken.IsCancellationRequested || frameData.IsEmpty)
        {
            return ValueTask.FromResult<IReadOnlyList<DetectedTarget>>(Array.Empty<DetectedTarget>());
        }

        int tensorSize = 1 * 3 * _modelHeight * _modelWidth;
        float[] tensorBuffer = ArrayPool<float>.Shared.Rent(tensorSize);

        try
        {
            PreprocessFrame(frameData.Span, width, height, tensorBuffer);

            var dimensions = new[] { 1, 3, _modelHeight, _modelWidth };
            var tensor = new DenseTensor<float>(tensorBuffer.AsMemory(0, tensorSize), dimensions);
            var inputs = new List<NamedOnnxValue>(1)
            {
                NamedOnnxValue.CreateFromTensor(_inputName, tensor)
            };

            using var results = _session.Run(inputs);
            var output = results.FirstOrDefault()?.AsTensor<float>();

            if (output == null)
            {
                return ValueTask.FromResult<IReadOnlyList<DetectedTarget>>(Array.Empty<DetectedTarget>());
            }

            var targets = ParseDetections(output, width, height);
            return ValueTask.FromResult<IReadOnlyList<DetectedTarget>>(targets);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(tensorBuffer);
        }
    }

    private void PreprocessFrame(ReadOnlySpan<byte> frameSpan, int srcWidth, int srcHeight, Span<float> tensorSpan)
    {
        // Converte e normaliza com resize bilinear simplificado ou nearest neighbor para [1, 3, H, W]
        float scale = Math.Min((float)_modelWidth / srcWidth, (float)_modelHeight / srcHeight);
        int resizedWidth = Math.Clamp((int)Math.Round(srcWidth * scale), 1, _modelWidth);
        int resizedHeight = Math.Clamp((int)Math.Round(srcHeight * scale), 1, _modelHeight);
        int padX = (_modelWidth - resizedWidth) / 2;
        int padY = (_modelHeight - resizedHeight) / 2;

        int channelSize = _modelHeight * _modelWidth;
        int rOffset = 0;
        int gOffset = channelSize;
        int bOffset = channelSize * 2;

        tensorSpan.Slice(0, channelSize * 3).Fill(114f / 255f);

        for (int y = 0; y < resizedHeight; y++)
        {
            int srcY = Math.Min((int)(y / scale), srcHeight - 1);
            int rowOffset = srcY * srcWidth * 3;

            for (int x = 0; x < resizedWidth; x++)
            {
                int srcX = Math.Min((int)(x / scale), srcWidth - 1);
                int pixelOffset = rowOffset + srcX * 3;
                int tensorIndex = (y + padY) * _modelWidth + x + padX;

                if (pixelOffset + 2 >= frameSpan.Length)
                    continue;

                tensorSpan[rOffset + tensorIndex] = frameSpan[pixelOffset + 2] / 255f;
                tensorSpan[gOffset + tensorIndex] = frameSpan[pixelOffset + 1] / 255f;
                tensorSpan[bOffset + tensorIndex] = frameSpan[pixelOffset] / 255f;
            }
        }
    }

    private List<DetectedTarget> ParseDetections(Tensor<float> output, int origWidth, int origHeight)
    {
        var detections = new List<DetectedTarget>();
        var dims = output.Dimensions;

        // Suporta formatos comuns de saída YOLO: [1, num_features, num_proposals] ou [1, num_proposals, num_features]
        if (dims.Length == 3)
        {
            int dim1 = dims[1];
            int dim2 = dims[2];

            bool transposed = dim1 < dim2 && dim1 >= 5; // [1, 84, 8400]
            int numProposals = transposed ? dim2 : dim1;
            int numFeatures = transposed ? dim1 : dim2;

            for (int i = 0; i < numProposals; i++)
            {
                float cx, cy, w, h;
                float maxClassScore = 0f;
                int bestClassId = 0;

                if (transposed)
                {
                    cx = output[0, 0, i];
                    cy = output[0, 1, i];
                    w = output[0, 2, i];
                    h = output[0, 3, i];

                    for (int c = 4; c < numFeatures; c++)
                    {
                        float score = output[0, c, i];
                        if (score > maxClassScore)
                        {
                            maxClassScore = score;
                            bestClassId = c - 4;
                        }
                    }
                }
                else
                {
                    cx = output[0, i, 0];
                    cy = output[0, i, 1];
                    w = output[0, i, 2];
                    h = output[0, i, 3];

                    for (int c = 4; c < numFeatures; c++)
                    {
                        float score = output[0, i, c];
                        if (score > maxClassScore)
                        {
                            maxClassScore = score;
                            bestClassId = c - 4;
                        }
                    }
                }

                if (maxClassScore >= _confidenceThreshold)
                {
                    if (_allowedClassIds != null && !_allowedClassIds.Contains(bestClassId))
                    {
                        continue;
                    }

                    // Escala de volta para dimensões da imagem original
                    float scale = Math.Min((float)_modelWidth / origWidth, (float)_modelHeight / origHeight);
                    int resizedWidth = Math.Clamp((int)Math.Round(origWidth * scale), 1, _modelWidth);
                    int resizedHeight = Math.Clamp((int)Math.Round(origHeight * scale), 1, _modelHeight);
                    int padX = (_modelWidth - resizedWidth) / 2;
                    int padY = (_modelHeight - resizedHeight) / 2;

                    float left = Math.Clamp((cx - w / 2f - padX) / scale, 0, origWidth);
                    float top = Math.Clamp((cy - h / 2f - padY) / scale, 0, origHeight);
                    float right = Math.Clamp((cx + w / 2f - padX) / scale, 0, origWidth);
                    float bottom = Math.Clamp((cy + h / 2f - padY) / scale, 0, origHeight);

                    if (right <= left || bottom <= top)
                        continue;

                    var box = new BoundingBox(left, top, right - left, bottom - top);
                    detections.Add(new DetectedTarget(box, maxClassScore, bestClassId));
                }
            }
        }

        return ApplyNms(detections, _iouThreshold);
    }

    private static List<DetectedTarget> ApplyNms(List<DetectedTarget> targets, float iouThreshold)
    {
        if (targets.Count <= 1) return targets;

        targets.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        var selected = new List<DetectedTarget>(targets.Count);
        Span<bool> suppressed = stackalloc bool[Math.Min(targets.Count, 256)];
        bool[]? rentedSuppressed = null;

        if (targets.Count > 256)
        {
            rentedSuppressed = ArrayPool<bool>.Shared.Rent(targets.Count);
            rentedSuppressed.AsSpan(0, targets.Count).Clear();
            suppressed = rentedSuppressed.AsSpan(0, targets.Count);
        }

        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (suppressed[i]) continue;

                var current = targets[i];
                selected.Add(current);

                for (int j = i + 1; j < targets.Count; j++)
                {
                    if (suppressed[j]) continue;

                    if (CalculateIou(current.Box, targets[j].Box) > iouThreshold)
                    {
                        suppressed[j] = true;
                    }
                }
            }
        }
        finally
        {
            if (rentedSuppressed != null)
            {
                ArrayPool<bool>.Shared.Return(rentedSuppressed);
            }
        }

        return selected;
    }

    private static float CalculateIou(BoundingBox a, BoundingBox b)
    {
        float x1 = Math.Max(a.X, b.X);
        float y1 = Math.Max(a.Y, b.Y);
        float x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        float y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        float intersectionArea = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float areaA = a.Width * a.Height;
        float areaB = b.Width * b.Height;
        float unionArea = areaA + areaB - intersectionArea;

        return unionArea > 0 ? intersectionArea / unionArea : 0f;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _session.Dispose();
            _disposed = true;
        }
    }
}
