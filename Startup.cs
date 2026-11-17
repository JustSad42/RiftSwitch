using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RiftSwitch;

/// <summary>"Start with Windows" via the current user's Run key.</summary>
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "RiftSwitch";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
        else k.DeleteValue(Name, false);
    }

    /// <summary>A dev build in bin\ shouldn't register itself anywhere.</summary>
    public static bool IsDevBuild =>
        Environment.ProcessPath?.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>RiftSwitch's entry in the Start menu (current user only).</summary>
static class StartMenu
{
    static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RiftSwitch.lnk");

    public static bool Exists => File.Exists(LinkPath);

    public static void Create()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(LinkPath);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "RiftSwitch: switch Riot accounts in one click";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    public static void Remove()
    {
        if (File.Exists(LinkPath)) File.Delete(LinkPath);
    }
}
