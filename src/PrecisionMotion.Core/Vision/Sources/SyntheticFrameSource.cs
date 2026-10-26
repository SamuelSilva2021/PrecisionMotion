using PrecisionMotion.Core.Vision.Abstractions;

namespace PrecisionMotion.Core.Vision.Sources;

public sealed class SyntheticFrameSource : IFrameSource
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _channels;
    private readonly int _totalFrames;
    private int _currentFrame;
    private bool _disposed;

    public SyntheticFrameSource(int width = 640, int height = 640, int channels = 3, int totalFrames = int.MaxValue)
    {
        _width = width;
        _height = height;
        _channels = channels;
        _totalFrames = totalFrames;
    }

    public bool TryGetNextFrame(Span<byte> destinationBuffer, out int width, out int height, CancellationToken cancellationToken)
    {
        width = _width;
        height = _height;

        if (_disposed || cancellationToken.IsCancellationRequested || _currentFrame >= _totalFrames)
        {
            return false;
        }

        int requiredBytes = _width * _height * _channels;
        if (destinationBuffer.Length < requiredBytes)
        {
            return false;
        }

        // Preenche com fundo estático
        destinationBuffer.Slice(0, requiredBytes).Fill(32);

        // Gera um alvo geométrico procedural em movimento
        int targetSize = 40;
        int targetX = (_currentFrame * 5) % Math.Max(1, _width - targetSize);
        int targetY = (_currentFrame * 3) % Math.Max(1, _height - targetSize);

        for (int y = targetY; y < targetY + targetSize && y < _height; y++)
        {
            for (int x = targetX; x < targetX + targetSize && x < _width; x++)
            {
                int pixelOffset = (y * _width + x) * _channels;
                for (int c = 0; c < _channels; c++)
                {
                    destinationBuffer[pixelOffset + c] = 255;
                }
            }
        }

        _currentFrame++;
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
