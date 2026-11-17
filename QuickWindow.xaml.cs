using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RiftSwitch;

/// <summary>Spotlight-style account picker opened by the global shortcut or the tray icon.</summary>
public partial class QuickWindow : Window
{
    readonly App _app;
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.2) };
    bool _switching;

    internal QuickWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Backdrop.Attach(this, acrylic: true);
        Deactivated += (_, _) => { if (!_switching) HideNow(); };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideNow(); };
        _app.Switcher.Progress += step => { if (IsVisible && _switching) SetStatus(step, spinning: true); };
        PreviewKeyDown += OnKey;
    }

    internal void Toggle()
    {
        if (IsVisible) HideNow();
        else ShowCentered();
    }

    internal void ShowCentered()
    {
        _hideTimer.Stop();
        _switching = _app.Switcher.Busy;
        Search.Text = "";
        Refresh();
        Status.Visibility = _switching ? Visibility.Visible : Visibility.Collapsed;
        Hints.Visibility = OpenLink.Visibility = _switching ? Visibility.Collapsed : Visibility.Visible;
        if (_switching) SetStatus("Switching…", spinning: true);

        // Upper third of the screen the mouse is on, like Windows search.
        var area = Native.CursorWorkArea(_app.MainWin);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + area.Height * 0.22;
        Opacity = 0;
        Show();
        Activate();
        Native.ForceForeground(this);
        Search.Focus();

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        Pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    internal void HideNow()
    {
        if (!IsVisible) return;
        _hideTimer.Stop();
        Hide();
        _app.TryApplyUpdate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true; // reused, never closed
        HideNow();
    }

    void Refresh()
    {
        var q = Search.Text.Trim();
        var items = _app.Store.Accounts.Where(a => q == "" ||
            a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            a.RiotId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            a.Note.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            a.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
            a.Region.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        List.ItemsSource = items;
        // Start on the first account that isn't the one already signed in.
        List.SelectedItem = items.FirstOrDefault(a => !a.IsActive) ?? items.FirstOrDefault();
        SearchHint.Visibility = Search.Text == "" ? Visibility.Visible : Visibility.Collapsed;

        bool none = items.Count == 0;
        Empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = _app.Store.Accounts.Count == 0 ? "No accounts yet" : "No matches";
        EmptyText.Text = _app.Store.Accounts.Count == 0 ? "Open RiftSwitch to add one." : "Try a nickname, Riot ID, tag or region.";
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    void Search_KeyDown(object sender, KeyEventArgs e)
    {
        // Number keys switch only while the search box is empty (so they can still be typed in a search).
        if (Search.Text == "" && Keyboard.Modifiers == ModifierKeys.None &&
            e.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
        {
            int n = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
            if (n < _app.Store.Accounts.Count) Pick(_app.Store.Accounts[n]);
            e.Handled = true;
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: HideNow(); e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter:
                if (List.SelectedItem is Account a) Pick(a);
                e.Handled = true;
                break;
        }
    }

    void Move(int delta)
    {
        int count = List.Items.Count;
        if (count == 0) return;
        List.SelectedIndex = (List.SelectedIndex + delta + count) % count;
        List.ScrollIntoView(List.SelectedItem);
    }

    void List_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem { DataContext: Account a })
            Pick(a);
    }

    async void Pick(Account a)
    {
        if (_switching) return;
        _switching = true;
        Hints.Visibility = OpenLink.Visibility = Visibility.Collapsed;
        Status.Visibility = Visibility.Visible;
        SetStatus($"Switching to {a.DisplayName}…", spinning: true);

        var result = await _app.Switcher.SwitchAsync(a, _app.Settings.LaunchLeague);
        _switching = false;
        _app.Notify(result);
    }

    /// <summary>Shows a finished switch in the footer, then closes.</summary>
    internal void ShowResult(SwitchResult r)
    {
        Status.Visibility = Visibility.Visible;
        Hints.Visibility = OpenLink.Visibility = Visibility.Collapsed;
        var (key, glyph) = r.Kind switch
        {
            ResultKind.Success => ("Good", ""),
            ResultKind.Warning => ("Warn", ""),
            ResultKind.Error => ("Bad", ""),
            _ => ("Accent", ""),
        };
        StatusIcon.Text = glyph;
        StatusIcon.Foreground = (Brush)FindResource(key);
        SetStatus(r.Message == "" ? r.Title : $"{r.Title}. {r.Message}", spinning: false);
        StatusText.ToolTip = r.Message;
        _hideTimer.Interval = TimeSpan.FromSeconds(r.Kind == ResultKind.Success ? 1.6 : 6);
        _hideTimer.Start();
    }

    void SetStatus(string text, bool spinning)
    {
        StatusText.Text = text;
        StatusSpin.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = spinning ? Visibility.Collapsed : Visibility.Visible;
    }

    void Open_Click(object sender, RoutedEventArgs e) => _app.ShowMain();
}
