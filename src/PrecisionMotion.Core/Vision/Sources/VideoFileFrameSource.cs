using OpenCvSharp;
using PrecisionMotion.Core.Vision.Abstractions;

namespace PrecisionMotion.Core.Vision.Sources;

public sealed class VideoFileFrameSource : IFrameSource
{
    private readonly VideoCapture _capture;
    private readonly bool _loop;
    private readonly double _fps;
    private bool _disposed;

    public double Fps => _fps;
    public int FrameCount => (int)_capture.FrameCount;
    public int Width => _capture.FrameWidth;
    public int Height => _capture.FrameHeight;

    public VideoFileFrameSource(string videoPath, bool loop = false)
    {
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("Arquivo de vídeo não encontrado", videoPath);
        }

        _capture = new VideoCapture(videoPath);
        if (!_capture.IsOpened())
        {
            throw new InvalidOperationException($"Não foi possível abrir o arquivo de vídeo: {videoPath}");
        }

        _fps = _capture.Fps > 0 ? _capture.Fps : 60.0;
        _loop = loop;
    }

    public bool TryGetNextFrame(Span<byte> destinationBuffer, out int width, out int height, CancellationToken cancellationToken)
    {
        width = 0;
        height = 0;

        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        using var frame = new Mat();
        if (!_capture.Read(frame) || frame.Empty())
        {
            if (_loop)
            {
                _capture.Set(VideoCaptureProperties.PosFrames, 0);
                if (!_capture.Read(frame) || frame.Empty())
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        width = frame.Width;
        height = frame.Height;
        int totalBytes = width * height * 3;

        if (destinationBuffer.Length < totalBytes)
        {
            return false;
        }

        unsafe
        {
            new ReadOnlySpan<byte>(frame.DataPointer, totalBytes).CopyTo(destinationBuffer);
        }

        return true;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _capture.Dispose();
            _disposed = true;
        }
    }
}
