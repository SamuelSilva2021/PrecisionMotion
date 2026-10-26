namespace PrecisionMotion.Core.Vision.Models;

public readonly record struct BoundingBox(float X, float Y, float Width, float Height)
{
    public (float X, float Y) Center => (X + Width / 2f, Y + Height / 2f);
    public (float X, float Y) TopCenter => (X + Width / 2f, Y);
}
