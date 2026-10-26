using PrecisionMotion.Core.Vision.Abstractions;
using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Selection;

public sealed class ClosestToCrosshairSelector : ITargetSelector
{
    public TargetSpatialData? SelectTarget(
        ReadOnlySpan<TargetSpatialData> targets, 
        (float X, float Y) referencePoint, 
        float minimumConfidence)
    {
        TargetSpatialData? bestTarget = null;
        float minDistance = float.MaxValue;

        for (int i = 0; i < targets.Length; i++)
        {
            ref readonly var target = ref targets[i];

            if (target.Target.Confidence < minimumConfidence)
            {
                continue;
            }

            if (target.DistanceToReference < minDistance)
            {
                minDistance = target.DistanceToReference;
                bestTarget = target;
            }
        }

        return bestTarget;
    }
}
