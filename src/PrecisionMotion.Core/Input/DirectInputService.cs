using PrecisionMotion.Core.Interop;

namespace PrecisionMotion.Core.Input;

public sealed class DirectInputService : IDisposable
{
    private bool _periodActive;

    public DirectInputService()
    {
        try
        {
            if (NativeMethods.TimeBeginPeriod(1) == 0)
                _periodActive = true;
        }
        catch { }
    }

    public static bool IsKeyPressed(int vKey)
    {
        return (NativeMethods.GetAsyncKeyState(vKey) & 0x8000) != 0;
    }

    public static bool WasKeyPressedSinceLastQuery(int vKey, out bool isCurrentlyDown)
    {
        short state = NativeMethods.GetAsyncKeyState(vKey);
        isCurrentlyDown = (state & 0x8000) != 0;
        return (state & 0x0001) != 0;
    }

    public static void MoveRelative(int dx, int dy)
    {
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_MOVE, dx, dy, 0, UIntPtr.Zero);
    }

    public void Dispose()
    {
        if (_periodActive)
        {
            NativeMethods.TimeEndPeriod(1);
            _periodActive = false;
        }
    }
}
