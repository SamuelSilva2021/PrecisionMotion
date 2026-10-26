namespace PrecisionMotion.Core.Vision.Abstractions;

public interface ISmoother
{
    (float X, float Y) Smooth((float X, float Y) currentPoint, double timestampSeconds);
    void Reset();
}
