using System.Buffers;
using System.Diagnostics;
using PrecisionMotion.Core.Vision.Abstractions;
using PrecisionMotion.Core.Vision.Mathematics;
using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Pipeline;

public sealed class VisionPipeline : IAsyncDisposable, IDisposable
{
    private readonly IFrameSource _frameSource;
    private readonly ITargetDetector _detector;
    private readonly ITargetSelector _selector;
    private readonly ISmoother _smoother;
    private readonly CoordinateCalculator _calculator;
    private readonly VisionPipelineConfig _config;
    private readonly RoiTransformer? _roiTransformer;

    private Task? _processingTask;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private long _totalFramesProcessed;
    private readonly Stopwatch _fpsStopwatch = new();

    public event Action<TargetSpatialData?, (float X, float Y)?, VisionMetrics>? OnFrameProcessed;

    public VisionPipeline(
        IFrameSource frameSource,
        ITargetDetector detector,
        ITargetSelector selector,
        ISmoother smoother,
        CoordinateCalculator calculator,
        VisionPipelineConfig config)
    {
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _smoother = smoother ?? throw new ArgumentNullException(nameof(smoother));
        _calculator = calculator ?? throw new ArgumentNullException(nameof(calculator));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        if (_config.UseRoi)
        {
            _roiTransformer = RoiTransformer.CreateCentered(
                _config.FrameWidth, 
                _config.FrameHeight, 
                _config.RoiWidth, 
                _config.RoiHeight);
        }
    }

    public void Start()
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _fpsStopwatch.Restart();
        _totalFramesProcessed = 0;
        _processingTask = Task.Run(() => ProcessingLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            if (_processingTask != null)
            {
                try
                {
                    await _processingTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Loop cancelado normalmente
                }
            }
            _cts.Dispose();
            _cts = null;
        }
    }

    public async ValueTask<(TargetSpatialData? SelectedTarget, (float X, float Y)? SmoothedPoint, VisionMetrics Metrics)> ProcessSingleFrameAsync(CancellationToken cancellationToken)
    {
        int bufferSize = _config.FrameWidth * _config.FrameHeight * 3;
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        var totalWatch = Stopwatch.StartNew();
        var captureWatch = Stopwatch.StartNew();

        try
        {
            bool frameAcquired = _frameSource.TryGetNextFrame(rentedBuffer, out int width, out int height, cancellationToken);
            captureWatch.Stop();

            if (!frameAcquired || width <= 0 || height <= 0)
            {
                totalWatch.Stop();
                var emptyMetrics = new VisionMetrics(
                    CaptureLatencyMs: captureWatch.Elapsed.TotalMilliseconds,
                    InferenceLatencyMs: 0,
                    ProcessingLatencyMs: 0,
                    TotalLatencyMs: totalWatch.Elapsed.TotalMilliseconds,
                    FramesPerSecond: CalculateFps());

                OnFrameProcessed?.Invoke(null, null, emptyMetrics);
                return (null, null, emptyMetrics);
            }

            int frameBytes = width * height * 3;

            var inferWatch = Stopwatch.StartNew();
            IReadOnlyList<DetectedTarget> detectedList;

            if (_config.UseRoi && _roiTransformer != null)
            {
                int roiBytes = _roiTransformer.Bounds.Width * _roiTransformer.Bounds.Height * 3;
                byte[] rentedRoi = ArrayPool<byte>.Shared.Rent(roiBytes);
                try
                {
                    _roiTransformer.ExtractRoi(rentedBuffer.AsSpan(0, frameBytes), width, height, 3, rentedRoi.AsSpan(0, roiBytes));
                    var roiMemory = new ReadOnlyMemory<byte>(rentedRoi, 0, roiBytes);

                    var roiDetections = await _detector.DetectAsync(
                        roiMemory, 
                        _roiTransformer.Bounds.Width, 
                        _roiTransformer.Bounds.Height, 
                        cancellationToken).ConfigureAwait(false);

                    var globalDetections = new List<DetectedTarget>(roiDetections.Count);
                    for (int i = 0; i < roiDetections.Count; i++)
                    {
                        globalDetections.Add(_roiTransformer.ToGlobal(roiDetections[i]));
                    }
                    detectedList = globalDetections;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rentedRoi);
                }
            }
            else
            {
                var frameMemory = new ReadOnlyMemory<byte>(rentedBuffer, 0, frameBytes);
                detectedList = await _detector.DetectAsync(frameMemory, width, height, cancellationToken).ConfigureAwait(false);
            }

            inferWatch.Stop();

            var procWatch = Stopwatch.StartNew();

            TargetSpatialData? selectedSpatial = null;
            (float X, float Y)? smoothedHead = null;

            if (detectedList.Count > 0)
            {
                Span<TargetSpatialData> spatialSpan = stackalloc TargetSpatialData[Math.Min(detectedList.Count, 64)];
                TargetSpatialData[]? rentedSpatial = null;

                if (detectedList.Count > 64)
                {
                    rentedSpatial = ArrayPool<TargetSpatialData>.Shared.Rent(detectedList.Count);
                    spatialSpan = rentedSpatial.AsSpan(0, detectedList.Count);
                }

                try
                {
                    for (int i = 0; i < detectedList.Count; i++)
                    {
                        spatialSpan[i] = _calculator.Calculate(detectedList[i], _config.ReferencePoint);
                    }

                    selectedSpatial = _selector.SelectTarget(spatialSpan.Slice(0, detectedList.Count), _config.ReferencePoint, _config.MinimumConfidence);

                    if (selectedSpatial.HasValue)
                    {
                        double timestamp = totalWatch.Elapsed.TotalSeconds;
                        smoothedHead = _smoother.Smooth(selectedSpatial.Value.EstimatedHead, timestamp);
                    }
                }
                finally
                {
                    if (rentedSpatial != null)
                    {
                        ArrayPool<TargetSpatialData>.Shared.Return(rentedSpatial);
                    }
                }
            }

            procWatch.Stop();
            totalWatch.Stop();

            _totalFramesProcessed++;
            var metrics = new VisionMetrics(
                CaptureLatencyMs: captureWatch.Elapsed.TotalMilliseconds,
                InferenceLatencyMs: inferWatch.Elapsed.TotalMilliseconds,
                ProcessingLatencyMs: procWatch.Elapsed.TotalMilliseconds,
                TotalLatencyMs: totalWatch.Elapsed.TotalMilliseconds,
                FramesPerSecond: CalculateFps());

            OnFrameProcessed?.Invoke(selectedSpatial, smoothedHead, metrics);
            return (selectedSpatial, smoothedHead, metrics);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private async Task ProcessingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessSingleFrameAsync(cancellationToken).ConfigureAwait(false);

                if (_config.TargetFps > 0)
                {
                    int delayMs = Math.Max(1, 1000 / _config.TargetFps);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Tratamento resiliente de erro em loop contínuo
            }
        }
    }

    private double CalculateFps()
    {
        double elapsedSec = _fpsStopwatch.Elapsed.TotalSeconds;
        return elapsedSec > 0 ? _totalFramesProcessed / elapsedSec : 0.0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        _frameSource.Dispose();
        _detector.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        Dispose();
    }
}
