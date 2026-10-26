using PrecisionMotion.Core.Vision.Abstractions;

namespace PrecisionMotion.Core.Vision.Smoothing;

public sealed class ExponentialMovingAverageSmoother : ISmoother
{
    private readonly float _alpha;
    private readonly double _timeoutSeconds;
    private (float X, float Y)? _lastPoint;
    private double _lastTimestamp;

    public ExponentialMovingAverageSmoother(float alpha = 0.45f, double timeoutSeconds = 0.25)
    {
        _alpha = Math.Clamp(alpha, 0.01f, 1.0f);
        _timeoutSeconds = timeoutSeconds;
    }

    public (float X, float Y) Smooth((float X, float Y) currentPoint, double timestampSeconds)
    {
        if (_lastPoint is null || (timestampSeconds - _lastTimestamp) > _timeoutSeconds)
        {
            _lastPoint = currentPoint;
            _lastTimestamp = timestampSeconds;
            return currentPoint;
        }

        float smoothX = (_alpha * currentPoint.X) + ((1.0f - _alpha) * _lastPoint.Value.X);
        float smoothY = (_alpha * currentPoint.Y) + ((1.0f - _alpha) * _lastPoint.Value.Y);

        _lastPoint = (smoothX, smoothY);
        _lastTimestamp = timestampSeconds;

        return _lastPoint.Value;
    }

    public void Reset()
    {
        _lastPoint = null;
        _lastTimestamp = 0;
    }
}
