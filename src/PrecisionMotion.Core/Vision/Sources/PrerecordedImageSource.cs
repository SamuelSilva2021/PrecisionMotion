using OpenCvSharp;
using PrecisionMotion.Core.Vision.Abstractions;

namespace PrecisionMotion.Core.Vision.Sources;

public sealed class PrerecordedImageSource : IFrameSource
{
    private readonly IReadOnlyList<string> _imagePaths;
    private readonly bool _loop;
    private int _currentIndex;
    private bool _disposed;

    public PrerecordedImageSource(IReadOnlyList<string> imagePaths, bool loop = false)
    {
        _imagePaths = imagePaths ?? throw new ArgumentNullException(nameof(imagePaths));
        _loop = loop;
    }

    public bool TryGetNextFrame(Span<byte> destinationBuffer, out int width, out int height, CancellationToken cancellationToken)
    {
        width = 0;
        height = 0;

        if (_disposed || cancellationToken.IsCancellationRequested || _imagePaths.Count == 0)
        {
            return false;
        }

        if (_currentIndex >= _imagePaths.Count)
        {
            if (!_loop)
            {
                return false;
            }
            _currentIndex = 0;
        }

        string imagePath = _imagePaths[_currentIndex++];
        if (!File.Exists(imagePath))
        {
            return false;
        }

        using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (mat.Empty())
        {
            return false;
        }

        width = mat.Width;
        height = mat.Height;

        int totalBytes = width * height * 3;
        if (destinationBuffer.Length < totalBytes)
        {
            return false;
        }

        unsafe
        {
            var matSpan = new ReadOnlySpan<byte>(mat.DataPointer, totalBytes);
            matSpan.CopyTo(destinationBuffer);
        }

        return true;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
