using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace RiftSwitch;

/// <summary>Notification-area icon. Left click opens the quick switcher; the menu lists accounts too.</summary>
sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly App _app;

    public TrayIcon(App app)
    {
        _app = app;
        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Opening += (_, _) => Build(menu);
        Build(menu);

        _icon = new WinForms.NotifyIcon
        {
            Icon = AppIcon(),
            Text = "RiftSwitch",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) _app.ShowQuick(); };
        _icon.BalloonTipClicked += (_, _) => _app.ShowMain();
    }

    void Build(WinForms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var bold = new Drawing.Font(WinForms.Control.DefaultFont, Drawing.FontStyle.Bold);

        if (Updater.Ready is { } v)
        {
            var update = new WinForms.ToolStripMenuItem($"Restart to update to {v.ToString(3)}") { Font = bold };
            update.Click += (_, _) => Updater.ApplyAndRestart();
            menu.Items.Add(update);
            menu.Items.Add(new WinForms.ToolStripSeparator());
        }

        foreach (var acc in _app.Store.Accounts)
        {
            var text = acc.ShowRiotId ? $"{acc.DisplayName}   ({acc.RiotId})" : acc.DisplayName;
            if (acc.Slot is >= 1 and <= 9) text = $"{acc.Slot}.  {text}";
            var item = new WinForms.ToolStripMenuItem(text) { Checked = acc.IsActive, Enabled = !_app.Switcher.Busy };
            var a = acc;
            item.Click += (_, _) => _app.SwitchTo(a);
            menu.Items.Add(item);
        }
        if (_app.Store.Accounts.Count > 0) menu.Items.Add(new WinForms.ToolStripSeparator());

        menu.Items.Add("Add account…", null, (_, _) => { _app.ShowMain(); _app.MainWin?.StartAdd(); });
        menu.Items.Add("Open RiftSwitch", null, (_, _) => _app.ShowMain());

        var startup = new WinForms.ToolStripMenuItem("Start with Windows") { Checked = AutoStart.IsEnabled, Enabled = !AutoStart.IsDevBuild };
        startup.Click += (_, _) => AutoStart.Set(!AutoStart.IsEnabled);
        menu.Items.Add(startup);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit RiftSwitch", null, (_, _) => _app.Quit());
    }

    public void Balloon(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(4000, title, text == "" ? " " : text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.None);

    static Drawing.Icon AppIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (info != null) return new Drawing.Icon(info.Stream, WinForms.SystemInformation.SmallIconSize);
        }
        catch { }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
