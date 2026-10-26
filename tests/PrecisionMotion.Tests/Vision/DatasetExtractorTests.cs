using FluentAssertions;
using OpenCvSharp;
using PrecisionMotion.Core.Vision.Detection;
using PrecisionMotion.Core.Vision.Evaluation;
using PrecisionMotion.Core.Vision.Models;
using PrecisionMotion.Core.Vision.Pipeline;
using Xunit;

namespace PrecisionMotion.Tests.Vision;

public class DatasetExtractorTests
{
    [Fact]
    public async Task ExtractDatasetFromVideoAsync_GeneratesImagesLabelsAndYaml()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm_dataset_test_" + Guid.NewGuid().ToString("N"));
        string tempVideo = Path.Combine(tempDir, "test_video.avi");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Cria vídeo de 20 frames
            int w = 200;
            int h = 200;
            using (var writer = new VideoWriter(tempVideo, FourCC.FromString("MJPG"), 30.0, new Size(w, h)))
            {
                for (int i = 0; i < 20; i++)
                {
                    using var f = new Mat(h, w, MatType.CV_8UC3, new Scalar(i * 10, i * 10, i * 10));
                    writer.Write(f);
                }
            }

            var mockDetector = new MockTargetDetector(new[]
            {
                new DetectedTarget(new BoundingBox(50, 50, 40, 80), 0.90f, 0)
            });

            var config = new VisionPipelineConfig
            {
                FrameWidth = w,
                FrameHeight = h,
                MinimumConfidence = 0.50f
            };

            var extractor = new DatasetExtractor(mockDetector, config);
            string outputDir = Path.Combine(tempDir, "extracted");

            int extractedCount = await extractor.ExtractDatasetFromVideoAsync(
                tempVideo,
                outputDir,
                sampleIntervalFrames: 5,
                onlySaveFramesWithDetections: true,
                maxFramesToSave: 10);

            extractedCount.Should().BeGreaterThan(0);

            // Verifica estrutura de diretórios gerada
            File.Exists(Path.Combine(outputDir, "data.yaml")).Should().BeTrue();
            Directory.GetFiles(Path.Combine(outputDir, "images", "train")).Length.Should().Be(extractedCount);
            Directory.GetFiles(Path.Combine(outputDir, "labels", "train")).Length.Should().Be(extractedCount);
            Directory.GetFiles(Path.Combine(outputDir, "debug_annotated")).Length.Should().Be(extractedCount);
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
