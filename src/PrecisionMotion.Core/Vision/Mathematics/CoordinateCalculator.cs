using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Mathematics;

public sealed class CoordinateCalculator
{
    private readonly float _frameWidth;
    private readonly float _frameHeight;
    private readonly float _headRatio;

    public CoordinateCalculator(int frameWidth, int frameHeight, float headRatio = 0.16f)
    {
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        _headRatio = Math.Clamp(headRatio, 0.05f, 0.40f);
    }

    public TargetSpatialData Calculate(DetectedTarget target, (float X, float Y) referencePoint)
    {
        var box = target.Box;
        var center = box.Center;

        // Estimativa anatômica da cabeça: eixo X centralizado na caixa, eixo Y no terço superior
        float headX = box.X + (box.Width / 2f);
        float headY = box.Y + (box.Height * _headRatio);
        var headPoint = (X: headX, Y: headY);

        // Deslocamento relativo em pixels em relação à referência (ex: centro do visor)
        float deltaX = headX - referencePoint.X;
        float deltaY = headY - referencePoint.Y;

        // Normalização em escala [-1.0, 1.0]
        float normX = _frameWidth > 0 ? deltaX / (_frameWidth / 2f) : 0f;
        float normY = _frameHeight > 0 ? deltaY / (_frameHeight / 2f) : 0f;

        float distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);

        return new TargetSpatialData(
            Target: target,
            EstimatedHead: headPoint,
            Center: center,
            RelativeDisplacement: (deltaX, deltaY),
            NormalizedDisplacement: (normX, normY),
            DistanceToReference: distance);
    }
}
