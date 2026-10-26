using FluentAssertions;
using PrecisionMotion.Core.Vision.Mathematics;
using PrecisionMotion.Core.Vision.Models;
using Xunit;

namespace PrecisionMotion.Tests.Vision;

public class CoordinateTransformTests
{
    private const int FrameWidth = 1920;
    private const int FrameHeight = 1080;
    private readonly (float X, float Y) _crosshair = (FrameWidth / 2f, FrameHeight / 2f); // (960, 540)

    [Fact]
    public void Calculate_TargetAtCenter_ProducesZeroRelativeDisplacement()
    {
        // Arrange
        float headRatio = 0.16f;
        var calculator = new CoordinateCalculator(FrameWidth, FrameHeight, headRatio);

        // Caixa posicionada de modo que o ponto da cabeça (headX, headY) caia exatamente no centro (960, 540)
        float boxWidth = 100f;
        float boxHeight = 200f;
        float boxX = _crosshair.X - (boxWidth / 2f); // 960 - 50 = 910
        float boxY = _crosshair.Y - (boxHeight * headRatio); // 540 - 32 = 508

        var target = new DetectedTarget(
            Box: new BoundingBox(boxX, boxY, boxWidth, boxHeight),
            Confidence: 0.95f,
            ClassId: 0);

        // Act
        var spatialData = calculator.Calculate(target, _crosshair);

        // Assert
        spatialData.EstimatedHead.X.Should().BeApproximately(_crosshair.X, 0.001f);
        spatialData.EstimatedHead.Y.Should().BeApproximately(_crosshair.Y, 0.001f);
        spatialData.RelativeDisplacement.X.Should().BeApproximately(0f, 0.001f);
        spatialData.RelativeDisplacement.Y.Should().BeApproximately(0f, 0.001f);
        spatialData.NormalizedDisplacement.X.Should().BeApproximately(0f, 0.001f);
        spatialData.NormalizedDisplacement.Y.Should().BeApproximately(0f, 0.001f);
        spatialData.DistanceToReference.Should().BeApproximately(0f, 0.001f);
    }

    [Theory]
    [InlineData(1100f, 600f, 1, 1)]   // Quadrante 4 (+X, +Y: inferior direito)
    [InlineData(800f, 600f, -1, 1)]   // Quadrante 3 (-X, +Y: inferior esquerdo)
    [InlineData(800f, 400f, -1, -1)]  // Quadrante 2 (-X, -Y: superior esquerdo)
    [InlineData(1100f, 400f, 1, -1)]  // Quadrante 1 (+X, -Y: superior direito)
    public void Calculate_TargetsInFourQuadrants_ProduceCorrectSigns(
        float headCenterX, float headCenterY, int expectedSignX, int expectedSignY)
    {
        // Arrange
        float headRatio = 0.20f;
        var calculator = new CoordinateCalculator(FrameWidth, FrameHeight, headRatio);

        float boxWidth = 100f;
        float boxHeight = 200f;
        float boxX = headCenterX - (boxWidth / 2f);
        float boxY = headCenterY - (boxHeight * headRatio);

        var target = new DetectedTarget(
            Box: new BoundingBox(boxX, boxY, boxWidth, boxHeight),
            Confidence: 0.90f,
            ClassId: 0);

        // Act
        var spatialData = calculator.Calculate(target, _crosshair);

        // Assert
        MathF.Sign(spatialData.RelativeDisplacement.X).Should().Be(expectedSignX);
        MathF.Sign(spatialData.RelativeDisplacement.Y).Should().Be(expectedSignY);
        MathF.Sign(spatialData.NormalizedDisplacement.X).Should().Be(expectedSignX);
        MathF.Sign(spatialData.NormalizedDisplacement.Y).Should().Be(expectedSignY);
    }

    [Fact]
    public void Calculate_HeadPosition_IsWidthInvariantAndLinearlyDependentOnHeight()
    {
        // Arrange
        float headRatio = 0.16f;
        var calculator = new CoordinateCalculator(FrameWidth, FrameHeight, headRatio);

        float startX = 200f;
        float startY = 150f;
        float height = 300f;

        var boxNarrow = new DetectedTarget(new BoundingBox(startX, startY, 50f, height), 0.9f, 0);
        var boxWide = new DetectedTarget(new BoundingBox(startX, startY, 200f, height), 0.9f, 0);

        // Act
        var spatialNarrow = calculator.Calculate(boxNarrow, _crosshair);
        var spatialWide = calculator.Calculate(boxWide, _crosshair);

        // Assert: A coordenada Y da cabeça deve ser idêntica independentemente da largura da caixa
        spatialNarrow.EstimatedHead.Y.Should().Be(spatialWide.EstimatedHead.Y);
        spatialNarrow.EstimatedHead.Y.Should().BeApproximately(startY + (height * headRatio), 0.001f);
    }
}
