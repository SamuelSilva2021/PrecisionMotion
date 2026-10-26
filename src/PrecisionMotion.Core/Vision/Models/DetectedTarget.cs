namespace PrecisionMotion.Core.Vision.Models;

public readonly record struct DetectedTarget(
    BoundingBox Box,
    float Confidence,
    int ClassId);
