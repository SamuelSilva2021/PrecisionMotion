namespace PrecisionMotion.Core.Vision.Pipeline;

public sealed class VisionPipelineConfig
{
    public int FrameWidth { get; set; } = 1920;
    public int FrameHeight { get; set; } = 1080;
    public (float X, float Y) ReferencePoint { get; set; } = (960f, 540f);
    public float HeadRatio { get; set; } = 0.16f;
    public float MinimumConfidence { get; set; } = 0.50f;
    public float SmoothingAlpha { get; set; } = 0.45f;
    public double SmoothingTimeoutSeconds { get; set; } = 0.25;
    public int TargetFps { get; set; } = 60;

    public static VisionPipelineConfig Default => new();
}
