using FluentAssertions;
using OpenCvSharp;
using PrecisionMotion.Core.Vision.Sources;
using Xunit;

namespace PrecisionMotion.Tests.Vision;

public class VideoFileFrameSourceTests
{
    [Fact]
    public void VideoFileFrameSource_StreamsFramesAccurately()
    {
        string tempVideoPath = Path.Combine(Path.GetTempPath(), "pm_test_video_" + Guid.NewGuid().ToString("N") + ".avi");

        try
        {
            // Cria um vídeo de teste de 5 frames (100x100, 30 FPS)
            int width = 100;
            int height = 100;
            int totalTestFrames = 5;

            using (var writer = new VideoWriter(tempVideoPath, FourCC.FromString("MJPG"), 30.0, new Size(width, height)))
            {
                for (int i = 0; i < totalTestFrames; i++)
                {
                    using var frame = new Mat(height, width, MatType.CV_8UC3, new Scalar(i * 30, i * 30, i * 30));
                    writer.Write(frame);
                }
            }

            // Act: Consome os frames via VideoFileFrameSource
            using var source = new VideoFileFrameSource(tempVideoPath, loop: false);

            source.Width.Should().Be(width);
            source.Height.Should().Be(height);
            source.Fps.Should().BeApproximately(30.0, 1.0);

            byte[] buffer = new byte[width * height * 3];
            int readCount = 0;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (source.TryGetNextFrame(buffer, out int w, out int h, cts.Token))
            {
                w.Should().Be(width);
                h.Should().Be(height);
                readCount++;
            }

            readCount.Should().Be(totalTestFrames);
        }
        finally
        {
            if (File.Exists(tempVideoPath))
            {
                File.Delete(tempVideoPath);
            }
        }
    }
}
