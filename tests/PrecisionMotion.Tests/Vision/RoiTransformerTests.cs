using FluentAssertions;
using PrecisionMotion.Core.Models;
using PrecisionMotion.Core.Vision.Mathematics;
using PrecisionMotion.Core.Vision.Models;
using Xunit;

namespace PrecisionMotion.Tests.Vision;

public class RoiTransformerTests
{
    [Fact]
    public void CreateCentered_ProducesCorrectCenteredCoordinates()
    {
        // 1920x1080 com ROI 640x640
        // X = (1920 - 640) / 2 = 640
        // Y = (1080 - 640) / 2 = 220
        var roi = RoiTransformer.CreateCentered(1920, 1080, 640, 640);

        roi.Bounds.X.Should().Be(640);
        roi.Bounds.Y.Should().Be(220);
        roi.Bounds.Width.Should().Be(640);
        roi.Bounds.Height.Should().Be(640);
    }

    [Fact]
    public void ToGlobal_And_ToLocal_TranslateCorrectly()
    {
        var roi = new RoiTransformer(new ScreenRegion(100, 200, 400, 400));
        var localBox = new BoundingBox(10, 20, 50, 100);

        var globalBox = roi.ToGlobal(localBox);
        globalBox.X.Should().Be(110);
        globalBox.Y.Should().Be(220);
        globalBox.Width.Should().Be(50);
        globalBox.Height.Should().Be(100);

        var convertedBack = roi.ToLocal(globalBox);
        convertedBack.X.Should().Be(localBox.X);
        convertedBack.Y.Should().Be(localBox.Y);
    }

    [Fact]
    public void ExtractRoi_CopiesCorrectSliceFromSourceSpan()
    {
        int srcWidth = 10;
        int srcHeight = 10;
        int channels = 1;
        byte[] srcData = new byte[srcWidth * srcHeight * channels];

        // Preenche com padrão: valor = y * 10 + x
        for (int y = 0; y < srcHeight; y++)
        {
            for (int x = 0; x < srcWidth; x++)
            {
                srcData[y * srcWidth + x] = (byte)(y * 10 + x);
            }
        }

        // Extrai ROI de 2x2 com origem em (3, 4)
        var roi = new RoiTransformer(new ScreenRegion(3, 4, 2, 2));
        byte[] dstBuffer = new byte[4];

        bool success = roi.ExtractRoi(srcData, srcWidth, srcHeight, channels, dstBuffer);

        success.Should().BeTrue();
        dstBuffer[0].Should().Be(43); // y=4, x=3
        dstBuffer[1].Should().Be(44); // y=4, x=4
        dstBuffer[2].Should().Be(53); // y=5, x=3
        dstBuffer[3].Should().Be(54); // y=5, x=4
    }
}
