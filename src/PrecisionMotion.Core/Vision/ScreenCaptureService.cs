using System.Runtime.InteropServices;
using OpenCvSharp;
using PrecisionMotion.Core.Interop;
using PrecisionMotion.Core.Models;

namespace PrecisionMotion.Core.Vision;

/// <summary>
/// Captura de regiões da tela via GDI. Os objetos GDI (DC da tela, DC de memória e DIB section) são
/// mantidos por thread e reaproveitados entre capturas: recriá-los a cada leitura era caro e gerava
/// churn de handles GDI dentro do processo do jogo-alvo da captura.
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    private sealed class CaptureContext
    {
        public IntPtr ScreenDc;
        public IntPtr MemDc;
        public IntPtr Bitmap;
        public IntPtr OldBitmap;
        public IntPtr Bits;
        public int Width;
        public int Height;

        public void EnsureCapacity(int width, int height)
        {
            if (Bitmap != IntPtr.Zero && width <= Width && height <= Height)
                return;

            ReleaseBitmap();
            // Cresce com folga para não recriar a cada região ligeiramente maior.
            Width = Math.Max(width, Width);
            Height = Math.Max(height, Height);
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = Width,
                    biHeight = -Height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };
            Bitmap = NativeMethods.CreateDIBSection(ScreenDc, ref info, 0, out Bits, IntPtr.Zero, 0);
            if (Bitmap != IntPtr.Zero)
                OldBitmap = NativeMethods.SelectObject(MemDc, Bitmap);
        }

        private void ReleaseBitmap()
        {
            if (Bitmap == IntPtr.Zero) return;
            NativeMethods.SelectObject(MemDc, OldBitmap);
            NativeMethods.DeleteObject(Bitmap);
            Bitmap = IntPtr.Zero;
            Bits = IntPtr.Zero;
        }

        public void Release()
        {
            ReleaseBitmap();
            if (MemDc != IntPtr.Zero) NativeMethods.DeleteDC(MemDc);
            if (ScreenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, ScreenDc);
            MemDc = ScreenDc = IntPtr.Zero;
        }
    }

    private static long s_captureCount;
    private static long s_captureTicks;

    public static long CaptureCount => Interlocked.Read(ref s_captureCount);

    public static double MeanCaptureMs =>
        CaptureCount == 0 ? 0.0 : Interlocked.Read(ref s_captureTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / CaptureCount;

    [ThreadStatic] private static CaptureContext? t_context;

    private static CaptureContext? GetContext()
    {
        if (t_context != null)
            return t_context;

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        if (memDc == IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        return t_context = new CaptureContext { ScreenDc = screenDc, MemDc = memDc };
    }

    /// <summary>Libera os objetos GDI da thread atual; chamar ao encerrar uma thread de captura.</summary>
    public static void ReleaseThreadResources()
    {
        t_context?.Release();
        t_context = null;
    }

    public static Mat? CaptureRegion(ScreenRegion region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            return null;

        var ctx = GetContext();
        if (ctx == null) return null;

        ctx.EnsureCapacity(region.Width, region.Height);
        if (ctx.Bitmap == IntPtr.Zero) return null;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        // Sem CAPTUREBLT: janelas em camadas (overlays) ficam de fora e a captura é bem mais barata.
        if (!NativeMethods.BitBlt(ctx.MemDc, 0, 0, region.Width, region.Height,
                ctx.ScreenDc, region.X, region.Y, NativeMethods.SRCCOPY))
            return null;

        // Lê direto da DIB section (stride = largura da section) e converte para cinza já na saída.
        using var bgra = Mat.FromPixelData(region.Height, region.Width, MatType.CV_8UC4, ctx.Bits, (long)ctx.Width * 4);
        var gray = new Mat();
        Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
        Interlocked.Increment(ref s_captureCount);
        Interlocked.Add(ref s_captureTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        return gray;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
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
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    public void Dispose() { }
}
