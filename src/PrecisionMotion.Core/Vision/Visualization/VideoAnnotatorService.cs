using System.Buffers;
using System.Diagnostics;
using OpenCvSharp;
using PrecisionMotion.Core.Vision.Abstractions;
using PrecisionMotion.Core.Vision.Mathematics;
using PrecisionMotion.Core.Vision.Models;
using PrecisionMotion.Core.Vision.Pipeline;
using PrecisionMotion.Core.Vision.Selection;
using PrecisionMotion.Core.Vision.Smoothing;

namespace PrecisionMotion.Core.Vision.Visualization;

public record VideoAnnotatorProgress(
    int ProcessedFrames,
    int TotalFrames,
    double ProgressPercentage,
    double CurrentFps,
    double AverageLatencyMs,
    int TotalDetections,
    int BodyDetections,
    int HeadDetections
);

public record VideoAnnotatorResult(
    int ProcessedFrames,
    int TotalFrames,
    TimeSpan ElapsedTime,
    double AverageFps,
    double AverageLatencyMs,
    int TotalDetections,
    int BodyDetections,
    int HeadDetections,
    string OutputPath
);

public sealed class VideoAnnotatorService
{
    private readonly ITargetDetector _detector;
    private readonly VisionPipelineConfig _config;

    public VideoAnnotatorService(ITargetDetector detector, VisionPipelineConfig? config = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _config = config ?? VisionPipelineConfig.Default;
    }

    public async Task<int> RenderAnnotatedVideoAsync(
        string inputVideoPath,
        string outputVideoPath,
        int maxFrames = int.MaxValue,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var progressAdapter = progress != null 
            ? new Progress<VideoAnnotatorProgress>(p => progress.Report(p.ProgressPercentage))
            : null;

        var result = await RenderAnnotatedVideoDetailedAsync(
            inputVideoPath, 
            outputVideoPath, 
            maxFrames, 
            progressAdapter, 
            cancellationToken).ConfigureAwait(false);

        return result.ProcessedFrames;
    }

    public async Task<VideoAnnotatorResult> RenderAnnotatedVideoDetailedAsync(
        string inputVideoPath,
        string outputVideoPath,
        int maxFrames = int.MaxValue,
        IProgress<VideoAnnotatorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputVideoPath))
        {
            throw new FileNotFoundException("Vídeo de entrada não encontrado", inputVideoPath);
        }

        using var capture = new VideoCapture(inputVideoPath);
        if (!capture.IsOpened())
        {
            throw new InvalidOperationException($"Não foi possível inicializar VideoCapture para: {inputVideoPath}");
        }

        int width = capture.FrameWidth;
        int height = capture.FrameHeight;
        double fps = capture.Fps > 0 ? capture.Fps : 30.0;
        int totalVideoFrames = (int)capture.FrameCount;
        int framesToProcess = Math.Min(totalVideoFrames > 0 ? totalVideoFrames : maxFrames, maxFrames);

        string? outDir = Path.GetDirectoryName(outputVideoPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var fourcc = FourCC.FromString("MJPG");
        using var writer = new VideoWriter(outputVideoPath, fourcc, fps, new Size(width, height));

        if (!writer.IsOpened())
        {
            throw new InvalidOperationException($"Não foi possível inicializar VideoWriter para: {outputVideoPath}");
        }

        var tracker = new OfflineDetectionTracker();
        var selector = new ClosestToCrosshairSelector();
        var smoother = new ExponentialMovingAverageSmoother(_config.SmoothingAlpha, _config.SmoothingTimeoutSeconds);
        var calculator = new CoordinateCalculator(width, height, _config.HeadRatio);

        RoiTransformer? roiTransformer = null;
        if (_config.UseRoi)
        {
            roiTransformer = RoiTransformer.CreateCentered(width, height, _config.RoiWidth, _config.RoiHeight);
        }

        int frameBytes = width * height * 3;
        byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(frameBytes);

        int processedFrames = 0;
        int totalDetections = 0;
        int bodyDetections = 0;
        int headDetections = 0;
        double accumulatedLatencyMs = 0;

        var overallStopwatch = Stopwatch.StartNew();
        var fpsStopwatch = Stopwatch.StartNew();

        try
        {
            using var frameMat = new Mat();

            while (processedFrames < framesToProcess && !cancellationToken.IsCancellationRequested)
            {
                if (!capture.Read(frameMat) || frameMat.Empty())
                {
                    break;
                }

                var frameWatch = Stopwatch.StartNew();

                // Copia bytes de BGR para o buffer
                unsafe
                {
                    new ReadOnlySpan<byte>(frameMat.DataPointer, frameBytes).CopyTo(frameBuffer);
                }

                IReadOnlyList<DetectedTarget> detectedList;
                var inferWatch = Stopwatch.StartNew();

                if (_config.UseRoi && roiTransformer != null)
                {
                    int roiBytes = roiTransformer.Bounds.Width * roiTransformer.Bounds.Height * 3;
                    byte[] roiBuffer = ArrayPool<byte>.Shared.Rent(roiBytes);
                    try
                    {
                        roiTransformer.ExtractRoi(frameBuffer.AsSpan(0, frameBytes), width, height, 3, roiBuffer.AsSpan(0, roiBytes));
                        var roiMemory = new ReadOnlyMemory<byte>(roiBuffer, 0, roiBytes);

                        var roiDetections = await _detector.DetectAsync(
                            roiMemory, 
                            roiTransformer.Bounds.Width, 
                            roiTransformer.Bounds.Height, 
                            cancellationToken).ConfigureAwait(false);

                        var globalDetections = new List<DetectedTarget>(roiDetections.Count);
                        for (int d = 0; d < roiDetections.Count; d++)
                        {
                            globalDetections.Add(roiTransformer.ToGlobal(roiDetections[d]));
                        }
                        detectedList = globalDetections;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(roiBuffer);
                    }
                }
                else
                {
                    var memory = new ReadOnlyMemory<byte>(frameBuffer, 0, frameBytes);
                    detectedList = await _detector.DetectAsync(memory, width, height, cancellationToken).ConfigureAwait(false);
                }

                inferWatch.Stop();

                // Contabilidade de classes
                for (int t = 0; t < detectedList.Count; t++)
                {
                    totalDetections++;
                    if (detectedList[t].ClassId == 1) headDetections++;
                    else bodyDetections++;
                }

                // Seleção e cálculo de cabeça
                TargetSpatialData? selectedSpatial = null;
                (float X, float Y)? smoothedHead = null;

                if (detectedList.Count > 0)
                {
                    TargetSpatialData[] rentedSpatial = ArrayPool<TargetSpatialData>.Shared.Rent(detectedList.Count);

                    try
                    {
                        for (int s = 0; s < detectedList.Count; s++)
                        {
                            rentedSpatial[s] = calculator.Calculate(detectedList[s], _config.ReferencePoint);
                        }

                        selectedSpatial = selector.SelectTarget(rentedSpatial.AsSpan(0, detectedList.Count), _config.ReferencePoint, _config.MinimumConfidence);
                        if (selectedSpatial.HasValue)
                        {
                            double timestamp = overallStopwatch.Elapsed.TotalSeconds;
                            smoothedHead = smoother.Smooth(selectedSpatial.Value.EstimatedHead, timestamp);
                        }
                    }
                    finally
                    {
                        ArrayPool<TargetSpatialData>.Shared.Return(rentedSpatial);
                    }
                }
                else
                {
                    smoother.Reset();
                }

                frameWatch.Stop();
                accumulatedLatencyMs += inferWatch.Elapsed.TotalMilliseconds;

                var metrics = new VisionMetrics(
                    CaptureLatencyMs: 0,
                    InferenceLatencyMs: inferWatch.Elapsed.TotalMilliseconds,
                    ProcessingLatencyMs: frameWatch.Elapsed.TotalMilliseconds - inferWatch.Elapsed.TotalMilliseconds,
                    TotalLatencyMs: frameWatch.Elapsed.TotalMilliseconds,
                    FramesPerSecond: (processedFrames + 1) / Math.Max(0.001, fpsStopwatch.Elapsed.TotalSeconds));

                Rect? roiRect = _config.UseRoi && roiTransformer != null
                    ? new Rect(roiTransformer.Bounds.X, roiTransformer.Bounds.Y, roiTransformer.Bounds.Width, roiTransformer.Bounds.Height)
                    : null;

                using var annotated = ImageAnnotator.AnnotateFrame(
                    frameMat,
                    detectedList,
                    smoothedHead ?? selectedSpatial?.EstimatedHead,
                    _config.ReferencePoint,
                    metrics,
                    roiRect);

                var tracks = tracker.Update(detectedList);
                foreach (var track in tracks)
                {
                    var box = track.Detection.Box;
                    int x = Math.Clamp((int)box.X, 0, width - 1);
                    int y = Math.Clamp((int)box.Y - 5, 18, height - 1);
                    Cv2.PutText(annotated, $"ID {track.Id} | {track.Detection.Confidence:P0} | frames {track.ObservedFrames}",
                        new Point(x, y), HersheyFonts.HersheySimplex, 0.5, new Scalar(255, 255, 0), 2);
                }

                writer.Write(annotated);
                processedFrames++;

                if (progress != null && (processedFrames % 2 == 0 || processedFrames == framesToProcess))
                {
                    double avgLat = accumulatedLatencyMs / Math.Max(1, processedFrames);
                    double currentFps = processedFrames / Math.Max(0.001, fpsStopwatch.Elapsed.TotalSeconds);
                    double pct = framesToProcess > 0 ? (double)processedFrames / framesToProcess : 0;

                    progress.Report(new VideoAnnotatorProgress(
                        ProcessedFrames: processedFrames,
                        TotalFrames: framesToProcess,
                        ProgressPercentage: pct,
                        CurrentFps: currentFps,
                        AverageLatencyMs: avgLat,
                        TotalDetections: totalDetections,
                        BodyDetections: bodyDetections,
                        HeadDetections: headDetections));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frameBuffer);
        }

        overallStopwatch.Stop();
        double finalAvgLatency = accumulatedLatencyMs / Math.Max(1, processedFrames);
        double finalAvgFps = processedFrames / Math.Max(0.001, overallStopwatch.Elapsed.TotalSeconds);

        return new VideoAnnotatorResult(
            ProcessedFrames: processedFrames,
            TotalFrames: framesToProcess,
            ElapsedTime: overallStopwatch.Elapsed,
            AverageFps: finalAvgFps,
            AverageLatencyMs: finalAvgLatency,
            TotalDetections: totalDetections,
            BodyDetections: bodyDetections,
            HeadDetections: headDetections,
            OutputPath: outputVideoPath);
    }
}

