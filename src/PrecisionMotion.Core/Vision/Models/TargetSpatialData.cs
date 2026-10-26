namespace PrecisionMotion.Core.Vision.Models;

public readonly record struct TargetSpatialData(
    DetectedTarget Target,
    (float X, float Y) EstimatedHead,
    (float X, float Y) Center,
    (float X, float Y) RelativeDisplacement,
    (float X, float Y) NormalizedDisplacement,
    float DistanceToReference);
