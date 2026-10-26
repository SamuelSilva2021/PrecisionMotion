using FluentAssertions;
using OpenCvSharp;
using PrecisionMotion.Core.Vision.Detection;
using PrecisionMotion.Core.Vision.Models;
using PrecisionMotion.Core.Vision.Pipeline;
using PrecisionMotion.Core.Vision.Visualization;
using Xunit;

namespace PrecisionMotion.Tests.Vision;

public class VideoAnnotatorServiceTests
{
    [Fact]
    public async Task RenderAnnotatedVideoAsync_ProducesValidVideoFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm_video_annot_test_" + Guid.NewGuid().ToString("N"));
        string tempInput = Path.Combine(tempDir, "input.avi");
        string tempOutput = Path.Combine(tempDir, "output_annotated.avi");
        Directory.CreateDirectory(tempDir);

        try
        {
            int w = 200;
            int h = 200;
            using (var writer = new VideoWriter(tempInput, FourCC.FromString("MJPG"), 30.0, new Size(w, h)))
            {
                for (int i = 0; i < 10; i++)
                {
                    using var f = new Mat(h, w, MatType.CV_8UC3, new Scalar(30, 30, 30));
                    writer.Write(f);
                }
            }

            var mockDetector = new MockTargetDetector(new[]
            {
                new DetectedTarget(new BoundingBox(50, 50, 40, 80), 0.95f, 0)
            });

            var config = new VisionPipelineConfig
            {
                FrameWidth = w,
                FrameHeight = h,
                MinimumConfidence = 0.50f
            };

            var service = new VideoAnnotatorService(mockDetector, config);

            int renderedCount = await service.RenderAnnotatedVideoAsync(tempInput, tempOutput, maxFrames: 5);

            renderedCount.Should().Be(5);
            File.Exists(tempOutput).Should().BeTrue();
            new FileInfo(tempOutput).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
