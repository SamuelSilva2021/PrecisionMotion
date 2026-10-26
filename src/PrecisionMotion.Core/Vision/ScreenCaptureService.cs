using System.Runtime.InteropServices;
using OpenCvSharp;
using PrecisionMotion.Core.Interop;
using PrecisionMotion.Core.Models;

namespace PrecisionMotion.Core.Vision;

public sealed class ScreenCaptureService : IDisposable
{
    public static Mat? CaptureRegion(ScreenRegion region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            return null;

        IntPtr hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return null;

        IntPtr hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, region.Width, region.Height);
        IntPtr hOld = NativeMethods.SelectObject(hdcMem, hBitmap);

        try
        {
            bool success = NativeMethods.BitBlt(
                hdcMem, 0, 0, region.Width, region.Height,
                hdcScreen, region.X, region.Y,
                NativeMethods.SRCCOPY);

            if (!success) return null;

            // Extract bitmap bits into Mat
            var mat = new Mat(region.Height, region.Width, MatType.CV_8UC4);
            var bmpInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = region.Width,
                    biHeight = -region.Height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };

            GetDIBits(hdcMem, hBitmap, 0, (uint)region.Height, mat.Data, ref bmpInfo, 0);

            // Convert to grayscale
            var gray = new Mat();
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGRA2GRAY);
            mat.Dispose();

            return gray;
        }
        finally
        {
            NativeMethods.SelectObject(hdcMem, hOld);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(hdcMem);
            NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, IntPtr pixels, ref BITMAPINFO bmi, uint usage);

    public void Dispose() { }
}
