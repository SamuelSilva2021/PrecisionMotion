using FluentAssertions;
using PrecisionMotion.Core.Vision.Detection;
using PrecisionMotion.Core.Vision.Mathematics;
using PrecisionMotion.Core.Vision.Models;
using PrecisionMotion.Core.Vision.Pipeline;
using PrecisionMotion.Core.Vision.Selection;
using PrecisionMotion.Core.Vision.Smoothing;
using PrecisionMotion.Core.Vision.Sources;
using Xunit;

namespace PrecisionMotion.Tests.Vision;

public class PipelineIntegrationTests
{
    [Fact]
    public async Task ProcessSingleFrameAsync_WithSyntheticSourceAndMockDetector_ComputesMetricsAndTarget()
    {
        // Arrange
        var config = new VisionPipelineConfig
        {
            FrameWidth = 640,
            FrameHeight = 480,
            ReferencePoint = (320f, 240f),
            HeadRatio = 0.16f,
            MinimumConfidence = 0.60f,
            SmoothingAlpha = 0.5f,
            SmoothingTimeoutSeconds = 0.25
        };

        using var frameSource = new SyntheticFrameSource(config.FrameWidth, config.FrameHeight, channels: 3, totalFrames: 10);
        using var mockDetector = new MockTargetDetector(new[]
        {
            new DetectedTarget(new BoundingBox(300, 200, 40, 80), 0.92f, ClassId: 0)
        });

        var selector = new ClosestToCrosshairSelector();
        var smoother = new ExponentialMovingAverageSmoother(config.SmoothingAlpha, config.SmoothingTimeoutSeconds);
        var calculator = new CoordinateCalculator(config.FrameWidth, config.FrameHeight, config.HeadRatio);

        await using var pipeline = new VisionPipeline(frameSource, mockDetector, selector, smoother, calculator, config);

        bool callbackInvoked = false;
        pipeline.OnFrameProcessed += (target, smoothed, metrics) =>
        {
            callbackInvoked = true;
            target.Should().NotBeNull();
            target!.Value.Target.Confidence.Should().Be(0.92f);
            smoothed.Should().NotBeNull();
            metrics.TotalLatencyMs.Should().BeGreaterThanOrEqualTo(0);
        };

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await pipeline.ProcessSingleFrameAsync(cts.Token);

        // Assert
        callbackInvoked.Should().BeTrue();
        result.SelectedTarget.Should().NotBeNull();
        result.SmoothedPoint.Should().NotBeNull();
        result.Metrics.TotalLatencyMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Pipeline_StartsAndStopsCleanly_WithContinuousProcessing()
    {
        // Arrange
        var config = new VisionPipelineConfig
        {
            FrameWidth = 320,
            FrameHeight = 240,
            ReferencePoint = (160f, 120f),
            TargetFps = 120
        };

        using var frameSource = new SyntheticFrameSource(config.FrameWidth, config.FrameHeight, channels: 3, totalFrames: 1000);
        using var mockDetector = new MockTargetDetector();

        var selector = new ClosestToCrosshairSelector();
        var smoother = new ExponentialMovingAverageSmoother();
        var calculator = new CoordinateCalculator(config.FrameWidth, config.FrameHeight);

        await using var pipeline = new VisionPipeline(frameSource, mockDetector, selector, smoother, calculator, config);

        int processedCount = 0;
        pipeline.OnFrameProcessed += (_, _, _) =>
        {
            Interlocked.Increment(ref processedCount);
        };

        // Act
        pipeline.Start();
        await Task.Delay(100);
        await pipeline.StopAsync();

        // Assert
        processedCount.Should().BeGreaterThan(0);
    }
}
