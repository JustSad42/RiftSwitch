using System.Windows.Interop;

namespace RiftSwitch;

/// <summary>Invisible message-only window that receives the global hotkey.</summary>
sealed class HotkeyWindow : IDisposable
{
    const int HotkeyId = 1;
    readonly HwndSource _source;

    public event Action? Pressed;

    public HotkeyWindow()
    {
        _source = new HwndSource(new HwndSourceParameters("RiftSwitchHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    public bool Register(uint modifiers, uint vk)
    {
        Unregister();
        return Native.RegisterHotKey(_source.Handle, HotkeyId, modifiers | Native.MOD_NOREPEAT, vk);
    }

    public void Unregister() => Native.UnregisterHotKey(_source.Handle, HotkeyId);

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }
}
