using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RiftSwitch;

public partial class App : Application
{
    // A test copy (pointed at a scratch data folder) gets its own instance, so it never talks to the real one.
    static readonly string InstanceSuffix = Environment.GetEnvironmentVariable("RIFTSWITCH_DATA") != null ? ".Test" : "";
    static readonly string MutexName = @"Local\RiftSwitch.SingleInstance" + InstanceSuffix;
    static readonly string ShowEventName = @"Local\RiftSwitch.Show" + InstanceSuffix;

    // Theme colors: (dark, light). Most are see-through so they sit on the glass background.
    static readonly (string Key, string Dark, string Light)[] Palette =
    [
        ("Bg", "#23242A", "#F7F7F9"),
        ("Panel", "#12FFFFFF", "#A6FFFFFF"),
        ("Card", "#0DFFFFFF", "#8CFFFFFF"),
        ("Line", "#1AFFFFFF", "#17000000"),
        ("Fg", "#EEEEF2", "#18181C"),
        ("Muted", "#9C9EA8", "#5F616B"),
        ("Hover", "#10FFFFFF", "#0B000000"),
        ("Selected", "#1CFFFFFF", "#14000000"),
        ("Thumb", "#40FFFFFF", "#40000000"),
        ("Scrim", "#B3141519", "#CCF3F3F6"),
        ("Good", "#4FD1A5", "#0F8A66"),
        ("Warn", "#F2B84B", "#A86A0A"),
        ("Bad", "#F0606E", "#C62F3E"),
    ];
    static readonly (string Dark, string Light) GlassSurface = ("#B01A1B20", "#C4F3F3F6");
    static readonly (string Dark, string Light) AccentColor = ("#E3B95C", "#9A6A00");

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    HotkeyWindow? _hotkey;
    TrayIcon? _tray;
    QuickWindow? _quick;
    bool _dark = true;
    string? _appliedTheme;

    internal AccountStore Store { get; } = new();
    internal Switcher Switcher { get; private set; } = null!;
    internal AppSettings Settings { get; private set; } = new();
    internal MainWindow? MainWin { get; private set; }
    public bool IsDark => _dark;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        if (e.Args.Contains("--selftest"))
        {
            ApplyTheme();
            Dispatcher.BeginInvoke(async () => Shutdown(await SelfTest.RunAsync()));
            return;
        }
#endif

        // After an update, the new version waits for the old one to finish closing.
        int wait = Array.IndexOf(e.Args, "--wait-for");
        if (wait >= 0 && wait + 1 < e.Args.Length && int.TryParse(e.Args[wait + 1], out int oldPid))
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(15000); } catch { }

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: ask the running copy to show its window, then exit.
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            _mutex = null;
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() => { while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowMain); }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            Notify(new SwitchResult(ResultKind.Error, "Something went wrong", ex.Exception.Message));
        };

        Updater.CleanUp();
        Settings = AppSettings.Load();
        Settings.ApplyCardOptions();
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(ApplyTheme);

        Store.Load();
        if (Store.Recovered > 0 || Store.UsedBackup)
            Dispatcher.BeginInvoke(() => Notify(new SwitchResult(ResultKind.Info,
                Store.Recovered > 0 ? $"Recovered {Store.Recovered} account{(Store.Recovered == 1 ? "" : "s")}" : "Account list restored",
                "The account list was damaged (probably by a crash or power cut) and has been rebuilt from your saved sign-ins." +
                (Store.Recovered > 0 ? " Nicknames, notes and tags on those accounts couldn't be recovered." : ""))),
                DispatcherPriority.ApplicationIdle);
        Switcher = new Switcher(Store);
        Switcher.Notice += Notify;
        var launchToast = new LaunchToast();
        Switcher.LeagueStarting += acc => launchToast.ShowStarting(acc);
        Switcher.LeagueStarted += (_, ok) => launchToast.ShowDone(ok);
#if DEBUG
        if (Environment.GetEnvironmentVariable("RIFTSWITCH_OPEN") == "launch")
            Dispatcher.BeginInvoke(() => { if (Store.Accounts.Count > 0) launchToast.ShowStarting(Store.Accounts[^1]); },
                DispatcherPriority.ApplicationIdle);
#endif

        MainWin = new MainWindow(this);
        _quick = new QuickWindow(this);
        _tray = new TrayIcon(this);

        _hotkey = new HotkeyWindow();
        _hotkey.Pressed += () => _quick.Toggle();
        if (!RegisterHotkey())
            Dispatcher.BeginInvoke(() => Notify(new SwitchResult(ResultKind.Warning, $"{Settings.HotkeyText} is taken",
                "Another app uses that shortcut. Pick a different one in RiftSwitch's settings.")), DispatcherPriority.ApplicationIdle);

        if (!AutoStart.IsDevBuild && !StartMenu.Exists) try { StartMenu.Create(); } catch { }

        Switcher.Start();

        if (e.Args.Contains("--updated"))
            Dispatcher.BeginInvoke(() => _tray.Balloon("RiftSwitch updated", $"Now on version {Updater.CurrentText}."),
                DispatcherPriority.ApplicationIdle);

        // Look for updates shortly after starting, then every 6 hours. Installs when idle.
        var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(6);
            if (!Settings.AutoUpdate) return;
            await Updater.CheckAsync();
            TryApplyUpdate();
        };
        updateTimer.Start();

        if (!e.Args.Contains("--background")) ShowMain();
    }

    /// <summary>Installs a downloaded update when nothing is open and no switch is running.</summary>
    internal void TryApplyUpdate()
    {
        if (Updater.Ready == null || Switcher.Busy || MainWin?.IsVisible == true || _quick?.IsVisible == true) return;
        Updater.ApplyAndRestart();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex != null)
        {
            _hotkey?.Dispose();
            _tray?.Dispose();
            _mutex.ReleaseMutex();
        }
        base.OnExit(e);
    }

    internal void Quit()
    {
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    // ---------- windows ----------

    internal void ShowMain()
    {
        _quick?.HideNow();
        MainWin?.ShowAndActivate();
    }

    internal void ShowQuick() => _quick?.ShowCentered();

    /// <summary>Switches to an account from anywhere (main window, quick switcher, tray).</summary>
    internal async void SwitchTo(Account acc)
    {
        var result = await Switcher.SwitchAsync(acc, Settings.LaunchLeague);
        Notify(result);
    }

    /// <summary>Shows a result in the main window if it's open, else as a tray notification.</summary>
    internal void Notify(SwitchResult r)
    {
        if (MainWin?.IsVisible == true && MainWin.WindowState != WindowState.Minimized) MainWin.ShowToast(r);
        else if (_quick?.IsVisible == true) _quick.ShowResult(r);
        else _tray?.Balloon(r.Title, r.Message, r.Kind is ResultKind.Warning or ResultKind.Error);
    }

    internal void TrayTip()
    {
        if (Settings.TrayTipShown) return;
        Settings.TrayTipShown = true;
        Settings.Save();
        _tray?.Balloon("RiftSwitch is still running", $"Press {Settings.HotkeyText} or click the tray icon to switch accounts.");
    }

    // ---------- hotkey ----------

    internal bool RegisterHotkey() => _hotkey?.Register(Settings.HotkeyModifiers, Settings.HotkeyKey) ?? false;
    internal bool TryRegisterHotkey(uint mods, uint vk) => _hotkey?.Register(mods, vk) ?? false;
    internal void SuspendHotkey() => _hotkey?.Unregister();

    internal void SaveSettings()
    {
        Settings.Save();
        _appliedTheme = null;
        ApplyTheme();
    }

    // ---------- theme ----------

    internal void ApplyTheme()
    {
        bool dark = Settings.Theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemIsDark(),
        };
        var key = dark.ToString();
        if (_appliedTheme == key) return;
        _appliedTheme = key;
        _dark = dark;

        foreach (var (name, d, l) in Palette) SetBrush(name, Hex(dark ? d : l));
        SetBrush("Surface", Backdrop.GlassSupported ? Hex(dark ? GlassSurface.Dark : GlassSurface.Light) : Hex(dark ? Palette[0].Dark : Palette[0].Light));
        var accent = Hex(dark ? AccentColor.Dark : AccentColor.Light);
        SetBrush("Accent", accent);
        SetBrush("AccentSoft", Color.FromArgb(dark ? (byte)0x33 : (byte)0x26, accent.R, accent.G, accent.B));
        SetBrush("OnAccent", dark ? Hex("#1A1408") : Colors.White);
        foreach (var (name, d, l) in new[] { ("Good", Palette[10].Dark, Palette[10].Light), ("Warn", Palette[11].Dark, Palette[11].Light), ("Bad", Palette[12].Dark, Palette[12].Light) })
        {
            var c = Hex(dark ? d : l);
            SetBrush(name + "Soft", Color.FromArgb(0x2A, c.R, c.G, c.B));
        }
        foreach (Window w in Windows) Backdrop.Apply(w);
    }

    void SetBrush(string key, Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        Resources[key] = brush;
    }

    static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v == 0;
        }
        catch { return true; }
    }
}
