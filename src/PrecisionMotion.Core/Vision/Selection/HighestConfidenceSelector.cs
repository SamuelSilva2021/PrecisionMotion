using PrecisionMotion.Core.Vision.Abstractions;
using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Selection;

public sealed class HighestConfidenceSelector : ITargetSelector
{
    public TargetSpatialData? SelectTarget(
        ReadOnlySpan<TargetSpatialData> targets, 
        (float X, float Y) referencePoint, 
        float minimumConfidence)
    {
        TargetSpatialData? bestTarget = null;
        float maxConfidence = float.MinValue;

        for (int i = 0; i < targets.Length; i++)
        {
            ref readonly var target = ref targets[i];

            if (target.Target.Confidence < minimumConfidence)
            {
                continue;
            }

            if (target.Target.Confidence > maxConfidence)
            {
                maxConfidence = target.Target.Confidence;
                bestTarget = target;
            }
            else if (Math.Abs(target.Target.Confidence - maxConfidence) < float.Epsilon &&
                     bestTarget.HasValue &&
                     target.DistanceToReference < bestTarget.Value.DistanceToReference)
            {
                bestTarget = target;
            }
        }

        return bestTarget;
    }
}
