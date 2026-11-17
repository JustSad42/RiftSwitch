using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RiftSwitch;

static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const uint ASFW_ANY = 0xFFFFFFFF;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    /// <summary>
    /// Brings our window to the front even when Windows' focus-stealing protection would block it,
    /// by briefly sharing input state with the current foreground window's thread.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint myThread = GetCurrentThreadId();
        if (fg != IntPtr.Zero && fgThread != myThread && AttachThreadInput(myThread, fgThread, true))
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            AttachThreadInput(myThread, fgThread, false);
        }
        else SetForegroundWindow(hwnd);
    }

    public static void ForceForeground(Window w) => ForceForeground(new WindowInteropHelper(w).Handle);

    /// <summary>Work area (in WPF units) of the monitor the mouse is on.</summary>
    public static Rect CursorWorkArea(Visual? v)
    {
        GetCursorPos(out var p);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromPoint(p, 2 /* nearest */), ref mi)) return SystemParameters.WorkArea;
        double scale = 1;
        if (v != null && PresentationSource.FromVisual(v)?.CompositionTarget is { } ct) scale = ct.TransformToDevice.M11;
        else scale = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? new Window()).DpiScaleX;
        var r = mi.rcWork;
        return new Rect(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);
    }

    public static void SetDarkTitleBar(IntPtr hwnd, bool dark) => SetDwm(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, dark ? 1 : 0);

    public static void SetDwm(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    [DllImport("psapi.dll", EntryPoint = "EmptyWorkingSet")]
    static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    /// <summary>Hands unused memory pages back to Windows.</summary>
    public static void TrimWorkingSet() => EmptyWorkingSet(GetCurrentProcess());
}
