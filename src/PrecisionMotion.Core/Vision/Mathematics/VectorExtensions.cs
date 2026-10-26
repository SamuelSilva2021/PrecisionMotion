namespace PrecisionMotion.Core.Vision.Mathematics;

public static class VectorExtensions
{
    public static float DistanceTo(this (float X, float Y) source, (float X, float Y) target)
    {
        float dx = target.X - source.X;
        float dy = target.Y - source.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public static float Length(this (float X, float Y) vector)
    {
        return MathF.Sqrt(vector.X * vector.X + vector.Y * vector.Y);
    }

    public static (float X, float Y) Normalize(this (float X, float Y) vector)
    {
        float length = vector.Length();
        if (length <= float.Epsilon)
        {
            return (0f, 0f);
        }

        return (vector.X / length, vector.Y / length);
    }

    public static (float X, float Y) Subtract(this (float X, float Y) a, (float X, float Y) b)
    {
        return (a.X - b.X, a.Y - b.Y);
    }

    public static (float X, float Y) Add(this (float X, float Y) a, (float X, float Y) b)
    {
        return (a.X + b.X, a.Y + b.Y);
    }
}
