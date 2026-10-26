using PrecisionMotion.Core.Models;
using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Mathematics;

public sealed class RoiTransformer
{
    public ScreenRegion Bounds { get; }

    public RoiTransformer(ScreenRegion bounds)
    {
        Bounds = bounds;
    }

    public static RoiTransformer CreateCentered(int screenWidth, int screenHeight, int roiWidth, int roiHeight)
    {
        int clampedW = Math.Min(screenWidth, roiWidth);
        int clampedH = Math.Min(screenHeight, roiHeight);

        int x = Math.Max(0, (screenWidth - clampedW) / 2);
        int y = Math.Max(0, (screenHeight - clampedH) / 2);

        return new RoiTransformer(new ScreenRegion(x, y, clampedW, clampedH));
    }

    public BoundingBox ToGlobal(BoundingBox localBox)
    {
        return new BoundingBox(
            localBox.X + Bounds.X,
            localBox.Y + Bounds.Y,
            localBox.Width,
            localBox.Height);
    }

    public DetectedTarget ToGlobal(DetectedTarget localTarget)
    {
        return new DetectedTarget(
            ToGlobal(localTarget.Box),
            localTarget.Confidence,
            localTarget.ClassId);
    }

    public (float X, float Y) ToGlobal((float X, float Y) localPoint)
    {
        return (localPoint.X + Bounds.X, localPoint.Y + Bounds.Y);
    }

    public BoundingBox ToLocal(BoundingBox globalBox)
    {
        return new BoundingBox(
            globalBox.X - Bounds.X,
            globalBox.Y - Bounds.Y,
            globalBox.Width,
            globalBox.Height);
    }

    public (float X, float Y) ToLocal((float X, float Y) globalPoint)
    {
        return (globalPoint.X - Bounds.X, globalPoint.Y - Bounds.Y);
    }

    public bool ExtractRoi(
        ReadOnlySpan<byte> srcFrame,
        int srcWidth,
        int srcHeight,
        int channels,
        Span<byte> dstBuffer)
    {
        int requiredBytes = Bounds.Width * Bounds.Height * channels;
        if (dstBuffer.Length < requiredBytes)
        {
            return false;
        }

        int rowBytes = Bounds.Width * channels;
        int srcRowStride = srcWidth * channels;

        for (int row = 0; row < Bounds.Height; row++)
        {
            int srcY = Bounds.Y + row;
            if (srcY >= srcHeight) break;

            int srcOffset = (srcY * srcRowStride) + (Bounds.X * channels);
            int dstOffset = row * rowBytes;

            if (srcOffset + rowBytes <= srcFrame.Length && dstOffset + rowBytes <= dstBuffer.Length)
            {
                srcFrame.Slice(srcOffset, rowBytes).CopyTo(dstBuffer.Slice(dstOffset, rowBytes));
            }
        }

        return true;
    }
}
