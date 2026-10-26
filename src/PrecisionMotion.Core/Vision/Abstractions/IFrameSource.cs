namespace PrecisionMotion.Core.Vision.Abstractions;

public interface IFrameSource : IDisposable
{
    /// <summary>
    /// Copia o próximo frame para o buffer fornecido sem alocações no heap.
    /// Retorna false quando não houver mais frames ou caso seja cancelado.
    /// </summary>
    bool TryGetNextFrame(Span<byte> destinationBuffer, out int width, out int height, CancellationToken cancellationToken);
}
