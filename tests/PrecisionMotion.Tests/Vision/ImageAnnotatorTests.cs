using FluentAssertions;
using OpenCvSharp;
using PrecisionMotion.Core.Vision.Models;
using PrecisionMotion.Core.Vision.Visualization;
using Xunit;

namespace PrecisionMotion.Tests.Vision;

public class ImageAnnotatorTests
{
    [Fact]
    public void AnnotateFrame_RendersAnnotationsWithoutModifyingOriginal()
    {
        using var original = new Mat(400, 400, MatType.CV_8UC3, new Scalar(0, 0, 0));

        var targets = new[]
        {
            new DetectedTarget(new BoundingBox(100, 100, 50, 100), 0.90f, 0)
        };

        var metrics = new VisionMetrics(
            CaptureLatencyMs: 1.0,
            InferenceLatencyMs: 3.5,
            ProcessingLatencyMs: 0.2,
            TotalLatencyMs: 4.7,
            FramesPerSecond: 120.0);

        using var annotated = ImageAnnotator.AnnotateFrame(
            original,
            targets,
            estimatedHead: (125f, 116f),
            referencePoint: (200f, 200f),
            metrics: metrics,
            roiRect: new Rect(50, 50, 300, 300));

        annotated.Should().NotBeNull();
        annotated.Width.Should().Be(400);
        annotated.Height.Should().Be(400);

        // O original ainda deve ser totalmente preto
        Cv2.CountNonZero(original.CvtColor(ColorConversionCodes.BGR2GRAY)).Should().Be(0);

        // O anotado deve conter pixels coloridos desenhados (caixa, texto, telemetria)
        Cv2.CountNonZero(annotated.CvtColor(ColorConversionCodes.BGR2GRAY)).Should().BeGreaterThan(0);
    }
}
