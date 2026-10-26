using FluentAssertions;
using PrecisionMotion.Core.Vision.Mathematics;
using PrecisionMotion.Core.Vision.Models;
using PrecisionMotion.Core.Vision.Selection;
using Xunit;

namespace PrecisionMotion.Tests.Vision;

public class TargetSelectorTests
{
    private readonly (float X, float Y) _crosshair = (960f, 540f);
    private readonly CoordinateCalculator _calculator = new(1920, 1080, 0.16f);

    [Fact]
    public void ClosestToCrosshairSelector_SelectsTargetWithMinimumDistance()
    {
        // Arrange
        var selector = new ClosestToCrosshairSelector();

        var targets = new[]
        {
            new DetectedTarget(new BoundingBox(100, 100, 50, 100), 0.90f, 0), // Longe
            new DetectedTarget(new BoundingBox(900, 500, 50, 100), 0.85f, 0), // Mais próximo
            new DetectedTarget(new BoundingBox(500, 400, 50, 100), 0.99f, 0), // Médio
            new DetectedTarget(new BoundingBox(1500, 800, 50, 100), 0.70f, 0), // Longe
            new DetectedTarget(new BoundingBox(950, 600, 50, 100), 0.80f, 0)  // Médio
        };

        var spatialData = targets.Select(t => _calculator.Calculate(t, _crosshair)).ToArray();

        // Act
        var selected = selector.SelectTarget(spatialData, _crosshair, minimumConfidence: 0.50f);

        // Assert
        selected.Should().NotBeNull();
        float expectedMinDist = spatialData.Min(s => s.DistanceToReference);
        selected!.Value.DistanceToReference.Should().Be(expectedMinDist);
        selected.Value.Target.Box.X.Should().Be(900);
    }

    [Fact]
    public void ClosestToCrosshairSelector_ExcludesTargetsBelowMinimumConfidence()
    {
        // Arrange
        var selector = new ClosestToCrosshairSelector();

        // O alvo mais próximo possui confiança 0.40, abaixo do threshold de 0.60
        var targets = new[]
        {
            new DetectedTarget(new BoundingBox(950, 530, 50, 100), 0.40f, 0), // Muito próximo, baixa confiança
            new DetectedTarget(new BoundingBox(700, 500, 50, 100), 0.75f, 0)  // Mais distante, alta confiança
        };

        var spatialData = targets.Select(t => _calculator.Calculate(t, _crosshair)).ToArray();

        // Act
        var selected = selector.SelectTarget(spatialData, _crosshair, minimumConfidence: 0.60f);

        // Assert
        selected.Should().NotBeNull();
        selected!.Value.Target.Confidence.Should().Be(0.75f);
        selected.Value.Target.Box.X.Should().Be(700);
    }

    [Fact]
    public void ClosestToCrosshairSelector_ReturnsNull_WhenAllTargetsBelowConfidence()
    {
        // Arrange
        var selector = new ClosestToCrosshairSelector();

        var targets = new[]
        {
            new DetectedTarget(new BoundingBox(950, 530, 50, 100), 0.30f, 0),
            new DetectedTarget(new BoundingBox(700, 500, 50, 100), 0.45f, 0)
        };

        var spatialData = targets.Select(t => _calculator.Calculate(t, _crosshair)).ToArray();

        // Act
        var selected = selector.SelectTarget(spatialData, _crosshair, minimumConfidence: 0.50f);

        // Assert
        selected.Should().BeNull();
    }

    [Fact]
    public void HighestConfidenceSelector_SelectsHighestConfidenceTarget()
    {
        // Arrange
        var selector = new HighestConfidenceSelector();

        var targets = new[]
        {
            new DetectedTarget(new BoundingBox(950, 530, 50, 100), 0.65f, 0),
            new DetectedTarget(new BoundingBox(700, 500, 50, 100), 0.98f, 0),
            new DetectedTarget(new BoundingBox(800, 500, 50, 100), 0.85f, 0)
        };

        var spatialData = targets.Select(t => _calculator.Calculate(t, _crosshair)).ToArray();

        // Act
        var selected = selector.SelectTarget(spatialData, _crosshair, minimumConfidence: 0.50f);

        // Assert
        selected.Should().NotBeNull();
        selected!.Value.Target.Confidence.Should().Be(0.98f);
    }
}
