using PrecisionMotion.Core.Vision.Abstractions;
using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Detection;

public sealed class MockTargetDetector : ITargetDetector
{
    private readonly List<DetectedTarget> _configuredTargets = new();
    private Func<ReadOnlyMemory<byte>, int, int, IReadOnlyList<DetectedTarget>>? _customDetectionRule;
    private bool _disposed;

    public MockTargetDetector()
    {
    }

    public MockTargetDetector(IEnumerable<DetectedTarget> initialTargets)
    {
        _configuredTargets.AddRange(initialTargets);
    }

    public void SetTargets(IEnumerable<DetectedTarget> targets)
    {
        _configuredTargets.Clear();
        _configuredTargets.AddRange(targets);
    }

    public void AddTarget(DetectedTarget target)
    {
        _configuredTargets.Add(target);
    }

    public void ClearTargets()
    {
        _configuredTargets.Clear();
    }

    public void SetCustomDetectionRule(Func<ReadOnlyMemory<byte>, int, int, IReadOnlyList<DetectedTarget>> rule)
    {
        _customDetectionRule = rule;
    }

    public ValueTask<IReadOnlyList<DetectedTarget>> DetectAsync(
        ReadOnlyMemory<byte> frameData, 
        int width, 
        int height, 
        CancellationToken cancellationToken)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult<IReadOnlyList<DetectedTarget>>(Array.Empty<DetectedTarget>());
        }

        if (_customDetectionRule != null)
        {
            var results = _customDetectionRule(frameData, width, height);
            return ValueTask.FromResult(results);
        }

        IReadOnlyList<DetectedTarget> targets = _configuredTargets.ToArray();
        return ValueTask.FromResult(targets);
    }

    public void Dispose()
    {
        _disposed = true;
        _configuredTargets.Clear();
    }
}
