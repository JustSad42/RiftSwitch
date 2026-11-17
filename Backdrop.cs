using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RiftSwitch;

/// <summary>
/// Windows 11 window look: frosted-glass background, rounded corners and the system shadow.
/// The window needs a WindowChrome with GlassFrameThickness="-1" and a transparent Background.
/// On older Windows it keeps a solid background (the "Surface" brush is opaque there).
/// </summary>
static class Backdrop
{
    /// <summary>Glass backgrounds need Windows 11 22H2 or newer.</summary>
    public static bool GlassSupported { get; } = Environment.OSVersion.Version.Build >= 22621;

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_MAINWINDOW = 2, DWMSBT_TRANSIENTWINDOW = 3;

    public static void Attach(Window window, bool acrylic = false) =>
        window.SourceInitialized += (_, _) => Apply(window, acrylic);

    public static void Apply(Window window) => Apply(window, window.Tag as string == "acrylic");

    public static void Apply(Window window, bool acrylic)
    {
        if (acrylic) window.Tag = "acrylic";
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.SetDarkTitleBar(hwnd, ((App)Application.Current).IsDark);
        Native.SetDwm(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        if (GlassSupported)
        {
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
            Native.SetDwm(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, acrylic ? DWMSBT_TRANSIENTWINDOW : DWMSBT_MAINWINDOW);
        }
    }
}
