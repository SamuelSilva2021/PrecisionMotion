using System.Diagnostics;
using OpenCvSharp;
using PrecisionMotion.Core.Vision.Abstractions;
using PrecisionMotion.Core.Vision.Mathematics;
using PrecisionMotion.Core.Vision.Models;
using PrecisionMotion.Core.Vision.Visualization;

namespace PrecisionMotion.Core.Vision.Evaluation;

public sealed class OfflineEvaluationHarness
{
    private readonly ITargetDetector _detector;
    private readonly CoordinateCalculator? _calculator;
    private readonly float _iouMatchThreshold;
    private readonly float _minimumConfidence;
    private readonly RoiTransformer? _roiTransformer;

    public OfflineEvaluationHarness(
        ITargetDetector detector,
        CoordinateCalculator? calculator = null,
        float iouMatchThreshold = 0.50f,
        float minimumConfidence = 0.50f,
        RoiTransformer? roiTransformer = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _calculator = calculator;
        _iouMatchThreshold = iouMatchThreshold;
        _minimumConfidence = minimumConfidence;
        _roiTransformer = roiTransformer;
    }

    public async Task<EvaluationMetrics> EvaluateDatasetAsync(
        string datasetDirectory,
        string? outputAnnotatedDir = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(datasetDirectory))
        {
            throw new DirectoryNotFoundException($"Diretório de dataset não encontrado: {datasetDirectory}");
        }

        var extensions = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" };
        var imageFiles = extensions.SelectMany(ext => Directory.GetFiles(datasetDirectory, ext)).ToList();

        var metrics = new EvaluationMetrics
        {
            TotalFramesEvaluated = imageFiles.Count
        };

        var latencies = new List<double>(imageFiles.Count);
        double totalIoUSum = 0.0;
        int iouMatchesCount = 0;
        double totalHeadErrorSum = 0.0;
        int headMatchesCount = 0;

        foreach (var imgPath in imageFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var mat = Cv2.ImRead(imgPath, ImreadModes.Color);
            if (mat.Empty()) continue;

            int width = mat.Width;
            int height = mat.Height;

            // Carrega anotações Ground Truth (mesmo nome com extensão .txt)
            string labelPath = Path.ChangeExtension(imgPath, ".txt");
            var groundTruths = new List<GroundTruthTarget>();
            if (File.Exists(labelPath))
            {
                string txtContent = await File.ReadAllTextAsync(labelPath, cancellationToken).ConfigureAwait(false);
                groundTruths = GroundTruthTarget.ParseYoloFormat(txtContent, width, height);
            }

            metrics.TotalGroundTruthTargets += groundTruths.Count;

            // Executa inferência
            int totalBytes = width * height * 3;
            var frameBytes = new byte[totalBytes];
            unsafe
            {
                new ReadOnlySpan<byte>(mat.DataPointer, totalBytes).CopyTo(frameBytes);
            }

            var watch = Stopwatch.StartNew();
            IReadOnlyList<DetectedTarget> detectedList;

            if (_roiTransformer != null)
            {
                int roiBytes = _roiTransformer.Bounds.Width * _roiTransformer.Bounds.Height * 3;
                var roiBuffer = new byte[roiBytes];
                _roiTransformer.ExtractRoi(frameBytes, width, height, 3, roiBuffer);

                var roiDetections = await _detector.DetectAsync(roiBuffer, _roiTransformer.Bounds.Width, _roiTransformer.Bounds.Height, cancellationToken).ConfigureAwait(false);

                var globalDetections = new List<DetectedTarget>(roiDetections.Count);
                for (int i = 0; i < roiDetections.Count; i++)
                {
                    globalDetections.Add(_roiTransformer.ToGlobal(roiDetections[i]));
                }
                detectedList = globalDetections;
            }
            else
            {
                detectedList = await _detector.DetectAsync(frameBytes, width, height, cancellationToken).ConfigureAwait(false);
            }

            watch.Stop();
            latencies.Add(watch.Elapsed.TotalMilliseconds);

            // Filtra por confiança mínima
            var validPredictions = detectedList.Where(p => p.Confidence >= _minimumConfidence).ToList();

            // Avalia correspondências IoU entre Predições e Ground Truths
            var matchedGt = new bool[groundTruths.Count];
            var matchedPred = new bool[validPredictions.Count];

            for (int p = 0; p < validPredictions.Count; p++)
            {
                float bestIou = 0f;
                int bestGtIdx = -1;

                for (int g = 0; g < groundTruths.Count; g++)
                {
                    if (matchedGt[g]) continue;

                    float iou = CalculateIou(validPredictions[p].Box, groundTruths[g].Box);
                    if (iou > bestIou && iou >= _iouMatchThreshold)
                    {
                        bestIou = iou;
                        bestGtIdx = g;
                    }
                }

                if (bestGtIdx >= 0)
                {
                    matchedGt[bestGtIdx] = true;
                    matchedPred[p] = true;
                    metrics.TruePositives++;
                    totalIoUSum += bestIou;
                    iouMatchesCount++;

                    // Se houver calculadora, mede erro de cabeça
                    if (_calculator != null)
                    {
                        var refPt = (width / 2f, height / 2f);
                        var predSpatial = _calculator.Calculate(validPredictions[p], refPt);
                        var gtHead = groundTruths[bestGtIdx].HeadPoint ?? (groundTruths[bestGtIdx].Box.X + (groundTruths[bestGtIdx].Box.Width / 2f), groundTruths[bestGtIdx].Box.Y + (groundTruths[bestGtIdx].Box.Height * 0.16f));

                        float dx = predSpatial.EstimatedHead.X - gtHead.X;
                        float dy = predSpatial.EstimatedHead.Y - gtHead.Y;
                        totalHeadErrorSum += MathF.Sqrt(dx * dx + dy * dy);
                        headMatchesCount++;
                    }
                }
                else
                {
                    metrics.FalsePositives++;
                }
            }

            // Ground truths que não foram detectados são Falsos Negativos
            for (int g = 0; g < groundTruths.Count; g++)
            {
                if (!matchedGt[g])
                {
                    metrics.FalseNegatives++;
                }
            }

            // Opcionalmente salva imagem anotada
            if (!string.IsNullOrEmpty(outputAnnotatedDir))
            {
                string outPath = Path.Combine(outputAnnotatedDir, Path.GetFileName(imgPath));
                var roiRect = _roiTransformer != null 
                    ? new Rect(_roiTransformer.Bounds.X, _roiTransformer.Bounds.Y, _roiTransformer.Bounds.Width, _roiTransformer.Bounds.Height) 
                    : (Rect?)null;

                ImageAnnotator.SaveAnnotatedImage(
                    imgPath,
                    outPath,
                    validPredictions,
                    metrics: new VisionMetrics(0, watch.Elapsed.TotalMilliseconds, 0, watch.Elapsed.TotalMilliseconds, 1000.0 / Math.Max(1.0, watch.Elapsed.TotalMilliseconds)),
                    roiRect: roiRect);
            }
        }

        metrics.MeanIoU = iouMatchesCount > 0 ? totalIoUSum / iouMatchesCount : 0.0;
        metrics.MeanHeadErrorPixels = headMatchesCount > 0 ? totalHeadErrorSum / headMatchesCount : 0.0;

        if (latencies.Count > 0)
        {
            metrics.AverageInferenceLatencyMs = latencies.Average();
            latencies.Sort();
            int p95Index = (int)Math.Ceiling(latencies.Count * 0.95) - 1;
            metrics.P95InferenceLatencyMs = latencies[Math.Clamp(p95Index, 0, latencies.Count - 1)];
        }

        return metrics;
    }

    public static float CalculateIou(BoundingBox a, BoundingBox b)
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
}
