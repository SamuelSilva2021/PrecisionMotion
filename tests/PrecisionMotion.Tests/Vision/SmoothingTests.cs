using FluentAssertions;
using PrecisionMotion.Core.Vision.Smoothing;
using Xunit;

namespace PrecisionMotion.Tests.Vision;

public class SmoothingTests
{
    [Fact]
    public void ExponentialMovingAverageSmoother_FollowsMathematicalFormula()
    {
        // Arrange
        float alpha = 0.5f;
        double timeout = 1.0;
        var smoother = new ExponentialMovingAverageSmoother(alpha, timeout);

        var p1 = (X: 100f, Y: 100f);
        var p2 = (X: 200f, Y: 200f);
        var p3 = (X: 300f, Y: 300f);

        // Act & Assert
        // Passo 1: Primeiro ponto deve ser retornado sem alteração
        var s1 = smoother.Smooth(p1, timestampSeconds: 0.1);
        s1.X.Should().Be(100f);
        s1.Y.Should().Be(100f);

        // Passo 2: S2 = 0.5 * 200 + 0.5 * 100 = 150
        var s2 = smoother.Smooth(p2, timestampSeconds: 0.2);
        s2.X.Should().BeApproximately(150f, 0.001f);
        s2.Y.Should().BeApproximately(150f, 0.001f);

        // Passo 3: S3 = 0.5 * 300 + 0.5 * 150 = 225
        var s3 = smoother.Smooth(p3, timestampSeconds: 0.3);
        s3.X.Should().BeApproximately(225f, 0.001f);
        s3.Y.Should().BeApproximately(225f, 0.001f);
    }

    [Fact]
    public void ExponentialMovingAverageSmoother_ResetsOnTimeout()
    {
        // Arrange
        float alpha = 0.5f;
        double timeout = 0.25; // 250ms
        var smoother = new ExponentialMovingAverageSmoother(alpha, timeout);

        smoother.Smooth((100f, 100f), timestampSeconds: 1.0);
        smoother.Smooth((200f, 200f), timestampSeconds: 1.1); // Suavizado para 150

        // Act: Salto temporal superior ao timeout (> 250ms)
        var resetPoint = smoother.Smooth((500f, 500f), timestampSeconds: 1.5);

        // Assert: Deve descartar o histórico antigo e adotar o novo ponto imediatamente
        resetPoint.X.Should().Be(500f);
        resetPoint.Y.Should().Be(500f);
    }

    [Fact]
    public void ExponentialMovingAverageSmoother_ExplicitReset_ClearsState()
    {
        // Arrange
        var smoother = new ExponentialMovingAverageSmoother(0.5f, 1.0);
        smoother.Smooth((100f, 100f), timestampSeconds: 0.1);

        // Act
        smoother.Reset();
        var next = smoother.Smooth((300f, 300f), timestampSeconds: 0.2);

        // Assert
        next.X.Should().Be(300f);
        next.Y.Should().Be(300f);
    }
}
