using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PrecisionMotion.Core.Vision.Abstractions;

namespace PrecisionMotion.UI;

public sealed class DesktopFrameSource : IFrameSource
{
    private readonly Rectangle _bounds;
    private bool _disposed;

    public int Width => _bounds.Width;
    public int Height => _bounds.Height;

    public DesktopFrameSource()
    {
        _bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("Nenhum monitor principal encontrado.");
    }

    public bool TryGetNextFrame(Span<byte> destinationBuffer, out int width, out int height, CancellationToken cancellationToken)
    {
        width = Width;
        height = Height;
        if (_disposed || cancellationToken.IsCancellationRequested) return false;

        int bytes = checked(width * height * 3);
        if (destinationBuffer.Length < bytes) throw new ArgumentException("Buffer insuficiente.", nameof(destinationBuffer));

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(_bounds.Left, _bounds.Top, 0, 0, bitmap.Size);

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] row = new byte[width * 3];
            for (int y = 0; y < height; y++)
            {
                IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(rowPtr, row, 0, row.Length);
                row.AsSpan().CopyTo(destinationBuffer.Slice(y * row.Length, row.Length));
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return true;
    }

    public void Dispose() => _disposed = true;
}
