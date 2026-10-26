using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Abstractions;

public interface ITargetSelector
{
    TargetSpatialData? SelectTarget(
        ReadOnlySpan<TargetSpatialData> targets, 
        (float X, float Y) referencePoint, 
        float minimumConfidence);
}
