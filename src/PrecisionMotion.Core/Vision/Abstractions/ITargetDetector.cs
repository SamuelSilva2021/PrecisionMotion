using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Abstractions;

public interface ITargetDetector : IDisposable
{
    /// <summary>
    /// Executa a inferência sobre os bytes do frame fornecido.
    /// </summary>
    ValueTask<IReadOnlyList<DetectedTarget>> DetectAsync(
        ReadOnlyMemory<byte> frameData, 
        int width, 
        int height, 
        CancellationToken cancellationToken);
}
