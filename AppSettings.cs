using System.Text.Json;
using System.Windows.Input;

namespace RiftSwitch;

enum AppTheme { System, Dark, Light }

sealed class AppSettings
{
    static string FilePath => Path.Combine(AccountStore.Dir, "settings.json");

    /// <summary>Open League after switching (otherwise just the Riot Client).</summary>
    public bool LaunchLeague { get; set; } = true;
    /// <summary>Closing the window keeps RiftSwitch running in the tray.</summary>
    public bool CloseToTray { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool TrayTipShown { get; set; }
    /// <summary>Slim one-line rows instead of full cards.</summary>
    public bool Compact { get; set; }

    // What account cards show (all on by default).
    public bool ShowRecent { get; set; } = true;
    public bool ShowDecay { get; set; } = true;
    public bool ShowPenalty { get; set; } = true;
    public bool ShowStreak { get; set; } = true;
    public bool ShowLpDelta { get; set; } = true;
    public bool ShowSpark { get; set; } = true;
    public bool ShowAge { get; set; } = true;

    /// <summary>Copies the card options to <see cref="CardOptions"/>, which the cards read.</summary>
    public void ApplyCardOptions()
    {
        CardOptions.Recent = ShowRecent; CardOptions.Decay = ShowDecay; CardOptions.Penalty = ShowPenalty;
        CardOptions.Streak = ShowStreak; CardOptions.LpDelta = ShowLpDelta; CardOptions.Spark = ShowSpark; CardOptions.Age = ShowAge;
    }

    // Global shortcut for the quick switcher. Default: Ctrl+Alt+A.
    public uint HotkeyModifiers { get; set; } = Native.MOD_CONTROL | Native.MOD_ALT;
    public uint HotkeyKey { get; set; } = (uint)KeyInterop.VirtualKeyFromKey(Key.A);

    public string HotkeyText => FormatHotkey(HotkeyModifiers, HotkeyKey);

    public static string FormatHotkey(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Native.MOD_WIN) != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            Key.OemTilde => "`",
            Key.Space => "Space",
            _ => key.ToString(),
        });
        return string.Join(" + ", parts);
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AccountStore.Dir);
            AccountStore.WriteDurably(FilePath, System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })));
        }
        catch { }
    }
}
