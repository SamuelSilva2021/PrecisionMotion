using FluentAssertions;
using OpenCvSharp;
using PrecisionMotion.Core.Vision.Detection;
using PrecisionMotion.Core.Vision.Evaluation;
using PrecisionMotion.Core.Vision.Models;
using Xunit;

namespace PrecisionMotion.Tests.Vision;

public class EvaluationHarnessTests
{
    [Fact]
    public void ParseYoloFormat_ParsesNormalizedCoordinatesCorrectly()
    {
        // Linha YOLO: class_id x_center y_center width height
        // 0 0.5 0.5 0.2 0.4
        // Em 1000x1000:
        // width = 200, height = 400
        // x = 500 - 100 = 400, y = 500 - 200 = 300
        string content = "0 0.5 0.5 0.2 0.4";
        var parsed = GroundTruthTarget.ParseYoloFormat(content, 1000, 1000);

        parsed.Should().HaveCount(1);
        parsed[0].ClassId.Should().Be(0);
        parsed[0].Box.X.Should().Be(400f);
        parsed[0].Box.Y.Should().Be(300f);
        parsed[0].Box.Width.Should().Be(200f);
        parsed[0].Box.Height.Should().Be(400f);
    }

    [Fact]
    public void CalculateIou_ComputesCorrectIntersectionOverUnion()
    {
        // Duas caixas idênticas de 100x100 -> IoU = 1.0
        var b1 = new BoundingBox(10, 10, 100, 100);
        var b2 = new BoundingBox(10, 10, 100, 100);
        OfflineEvaluationHarness.CalculateIou(b1, b2).Should().Be(1.0f);

        // Duas caixas sem sobreposição -> IoU = 0.0
        var b3 = new BoundingBox(200, 200, 50, 50);
        OfflineEvaluationHarness.CalculateIou(b1, b3).Should().Be(0.0f);

        // Caixa b1 [0, 0, 10, 10] e b4 [5, 0, 10, 10]
        // Interseção: 5x10 = 50. União: 100 + 100 - 50 = 150. IoU = 50 / 150 = 1/3 ~ 0.333
        var boxA = new BoundingBox(0, 0, 10, 10);
        var boxB = new BoundingBox(5, 0, 10, 10);
        OfflineEvaluationHarness.CalculateIou(boxA, boxB).Should().BeApproximately(50f / 150f, 0.001f);
    }

    [Fact]
    public async Task OfflineEvaluationHarness_EvaluatesDatasetAndComputesMetrics()
    {
        // Cria diretório temporário para simular dataset
        string tempDir = Path.Combine(Path.GetTempPath(), "pm_eval_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Cria imagem de teste sintética 100x100
            string imgPath = Path.Combine(tempDir, "frame_001.png");
            using (var mat = new Mat(100, 100, MatType.CV_8UC3, new Scalar(50, 50, 50)))
            {
                Cv2.ImWrite(imgPath, mat);
            }

            // Cria anotação YOLO correspondente: alvo em 50,50 com 20x40
            string labelPath = Path.Combine(tempDir, "frame_001.txt");
            await File.WriteAllTextAsync(labelPath, "0 0.5 0.5 0.2 0.4");

            // Configura mock detector para retornar a caixa detectada correta
            var detectedBox = new BoundingBox(40, 30, 20, 40); // Perfeito
            var mockDetector = new MockTargetDetector(new[]
            {
                new DetectedTarget(detectedBox, 0.95f, 0)
            });

            var harness = new OfflineEvaluationHarness(mockDetector, iouMatchThreshold: 0.50f, minimumConfidence: 0.50f);
            var metrics = await harness.EvaluateDatasetAsync(tempDir);

            // Assert
            metrics.TotalFramesEvaluated.Should().Be(1);
            metrics.TotalGroundTruthTargets.Should().Be(1);
            metrics.TruePositives.Should().Be(1);
            metrics.FalsePositives.Should().Be(0);
            metrics.FalseNegatives.Should().Be(0);
            metrics.Precision.Should().Be(1.0);
            metrics.Recall.Should().Be(1.0);
            metrics.F1Score.Should().Be(1.0);
            metrics.MeanIoU.Should().BeApproximately(1.0, 0.001);

            string report = metrics.GenerateMarkdownReport();
            report.Should().Contain("100,00%");
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
