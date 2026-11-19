using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RiftSwitch;

public partial class MainWindow : Window
{
    readonly App _app;
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    Account? _editing;
    string _editColor = "";
    bool _recordingHotkey;
    bool _quitting;
    readonly ICollectionView _view;
    string? _filterTag;
    // Drag to reorder
    Point _pressAt;
    Account? _pressed;
    bool _dragging, _justDragged;

    internal MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Backdrop.Attach(this);

        _view = System.Windows.Data.CollectionViewSource.GetDefaultView(_app.Store.Accounts);
        _view.Filter = o => _filterTag == null || o is Account a && a.Tags.Contains(_filterTag, StringComparer.OrdinalIgnoreCase);
        AccountList.ItemsSource = _view;
        _app.Store.Accounts.CollectionChanged += (_, _) => { UpdateEmpty(); if (!_dragging) RebuildFilters(); };
        ApplyListMode();
        RebuildFilters();
        _app.Switcher.StateChanged += UpdateState;
        _app.Switcher.Progress += step => BusyStep.Text = step;
        Updater.StatusChanged += () => Dispatcher.BeginInvoke(UpdateUpdateInfo);

        _toastTimer.Tick += (_, _) => HideToast();
        _clock.Tick += (_, _) => { foreach (var a in _app.Store.Accounts) a.RefreshTimes(); };
        _clock.Start();

        LaunchToggle.IsChecked = _app.Settings.LaunchLeague;
        ShowHotkeyHint();
        UpdateEmpty();
        UpdateState();
        PreviewKeyDown += OnKey;
#if DEBUG
        // Screenshot hook for testing: open a page straight away.
        Loaded += (_, _) =>
        {
            switch (Environment.GetEnvironmentVariable("RIFTSWITCH_OPEN"))
            {
                case "settings": OpenSettings_Click(this, new RoutedEventArgs()); break;
                case "settings-cards":
                    OpenSettings_Click(this, new RoutedEventArgs());
                    Dispatcher.BeginInvoke(() => CardOptionsPanel.BringIntoView(new Rect(0, 0, 10, 600)),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    break;
                case "edit" when _app.Store.Accounts.Count > 0: OpenEditor(_app.Store.Accounts[0]); break;
                case "busy" when _app.Store.Accounts.Count > 1:
                    _app.Store.Accounts[1].IsBusy = true;
                    BusyAvatar.Content = _app.Store.Accounts[1];
                    BusyTitle.Text = $"Switching to {_app.Store.Accounts[1].DisplayName}";
                    BusyStep.Text = "Closing Riot Client…";
                    Fade(BusyOverlay, true);
                    break;
                case "toast": ShowToast(new(ResultKind.Success, "Switched to Main", "League is starting.")); break;
            }
        };
#endif
    }

    internal void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Native.ForceForeground(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_quitting) return;
        if (_app.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            _app.TrayTip();
            _app.TryApplyUpdate();
            Dispatcher.BeginInvoke(Native.TrimWorkingSet, DispatcherPriority.ApplicationIdle);
        }
        else
        {
            _quitting = true;
            _app.Quit();
        }
    }

    void UpdateEmpty()
    {
        bool empty = _app.Store.Accounts.Count == 0;
        EmptyState.Visibility = empty && !_app.Switcher.AddingAccount ? Visibility.Visible : Visibility.Collapsed;
        ListScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    void UpdateState()
    {
        var s = _app.Switcher;
        AddBanner.Visibility = s.AddingAccount ? Visibility.Visible : Visibility.Collapsed;
        PersistBanner.Visibility = s.NotPersistent && !s.AddingAccount ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmpty();

        if (s.Busy && BusyOverlay.Visibility != Visibility.Visible)
        {
            var target = _app.Store.Accounts.FirstOrDefault(a => a.IsBusy);
            BusyAvatar.Content = target;
            BusyAvatar.Visibility = target != null ? Visibility.Visible : Visibility.Collapsed;
            BusyTitle.Text = target != null ? $"Switching to {target.DisplayName}" : "Getting the sign-in screen ready";
            BusyStep.Text = "";
            Fade(BusyOverlay, true);
        }
        else if (!s.Busy && BusyOverlay.Visibility == Visibility.Visible) Fade(BusyOverlay, false);
    }

    void ShowHotkeyHint()
    {
        HotkeyKeys.ItemsSource = _app.Settings.HotkeyText.Split(" + ");
    }

    // ---------- account actions ----------

    static Account? AccountOf(object sender) => (sender as FrameworkElement)?.DataContext as Account;

    void Card_Click(object sender, MouseButtonEventArgs e)
    {
        _pressed = null;
        if (_justDragged) { _justDragged = false; return; } // the press was the start of a drag, not a click
        if (AccountOf(sender) is { } a) _app.SwitchTo(a);
    }

    // ---------- drag to reorder ----------

    void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = AccountOf(sender);
        _pressAt = e.GetPosition(this);
        _justDragged = false;
    }

    void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || _dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _pressAt;
        if (Math.Abs(d.Y) < 6 && Math.Abs(d.X) < 6) return;

        var acc = _pressed;
        _dragging = true;
        acc.IsDragging = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(Account), acc), DragDropEffects.Move); }
        finally
        {
            acc.IsDragging = false;
            _dragging = false;
            _justDragged = true;
            _pressed = null;
            _app.Store.Save();
            Dispatcher.BeginInvoke(() => _justDragged = false, DispatcherPriority.Input);
        }
    }

    void Card_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(typeof(Account)) is not Account dragged || AccountOf(sender) is not { } over) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        if (over == dragged) return;
        // Live reorder: the dragged card takes the place of the one under the mouse.
        _app.Store.MoveTo(dragged, _app.Store.Accounts.IndexOf(over));
    }

    void Card_Drop(object sender, DragEventArgs e) => e.Handled = true;

    // ---------- list mode & tag filter ----------

    void ApplyListMode() =>
        AccountList.ItemTemplate = (DataTemplate)FindResource(_app.Settings.Compact ? "AccountRow" : "AccountCard");

    void RebuildFilters()
    {
        var tags = _app.Store.AllTags();
        if (_filterTag != null && !tags.Contains(_filterTag, StringComparer.OrdinalIgnoreCase)) _filterTag = null;
        FilterChips.Children.Clear();
        FilterBar.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (tags.Count == 0) { _view.Refresh(); return; }

        RadioButton Chip(string text, string? tag)
        {
            var rb = new RadioButton
            {
                Content = text, Style = (Style)FindResource("Chip"), GroupName = "filter",
                IsChecked = string.Equals(_filterTag, tag, StringComparison.OrdinalIgnoreCase),
                Padding = new Thickness(11, 4, 11, 4),
            };
            rb.Checked += (_, _) => { _filterTag = tag; _view.Refresh(); UpdateEmpty(); };
            return rb;
        }
        FilterChips.Children.Add(Chip("All", null));
        foreach (var t in tags)
            FilterChips.Children.Add(Chip($"{t}  {_app.Store.Accounts.Count(a => a.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))}", t));
        _view.Refresh();
    }

    void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (AccountOf(sender) is { } a) _app.SwitchTo(a);
    }

    void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (AccountOf(sender) is { } a) ShowMenu((FrameworkElement)sender, a);
        e.Handled = true;
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        if (AccountOf(sender) is { } a) ShowMenu((FrameworkElement)sender, a);
    }

    void ShowMenu(FrameworkElement anchor, Account a)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        MenuItem Item(string header, string glyph, Action click, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, Icon = new TextBlock { Text = glyph }, IsEnabled = enabled };
            mi.Click += (_, _) => click();
            return mi;
        }
        int index = _app.Store.Accounts.IndexOf(a);
        if (!a.IsActive) menu.Items.Add(Item("Switch", "", () => _app.SwitchTo(a), !_app.Switcher.Busy));
        menu.Items.Add(Item("Edit nickname, color, note", "", () => OpenEditor(a)));
        menu.Items.Add(Item("Copy Riot ID", "", () => { try { Clipboard.SetText(a.RiotId); ShowToast(new(ResultKind.Success, "Riot ID copied", a.RiotId)); } catch { } }));
        if (a.GameName != "" && a.Platform != "")
        {
            void Link(string name, string url) => menu.Items.Add(Item($"Open on {name}", "",
                () => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }));
            menu.Items.Add(new Separator());
            Link("op.gg", a.OpGg);
            Link("u.gg", a.UGg);
            Link("League of Graphs", a.LeagueOfGraphs);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Move up", "", () => _app.Store.Move(a, -1), index > 0));
        menu.Items.Add(Item("Move down", "", () => _app.Store.Move(a, 1), index < _app.Store.Accounts.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove", "", () => { OpenEditor(a); RemoveAsk_Click(this, new RoutedEventArgs()); }));
        menu.IsOpen = true;
    }

    internal async void StartAdd()
    {
        var result = await _app.Switcher.BeginAddAccountAsync();
        if (result.Kind != ResultKind.Info) ShowToast(result);
    }

    void Add_Click(object sender, RoutedEventArgs e) => StartAdd();

    void CancelAdd_Click(object sender, RoutedEventArgs e) => _app.Switcher.CancelAdd();

    void LaunchToggle_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.LaunchLeague = LaunchToggle.IsChecked == true;
        _app.Settings.Save();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_recordingHotkey) return;
        if (e.Key == Key.Escape)
        {
            if (EditOverlay.Visibility == Visibility.Visible) CloseEditor();
            else if (SettingsPage.Visibility == Visibility.Visible) CloseSettings();
            else Close();
            e.Handled = true;
        }
        // 1-9 switch straight away, like the quick switcher.
        else if (EditOverlay.Visibility != Visibility.Visible && SettingsPage.Visibility != Visibility.Visible &&
                 Keyboard.Modifiers == ModifierKeys.None && e.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
        {
            int n = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
            if (n < _app.Store.Accounts.Count) _app.SwitchTo(_app.Store.Accounts[n]);
            e.Handled = true;
        }
    }

    // ---------- editor ----------

    void OpenEditor(Account a)
    {
        _editing = a;
        EditAvatar.Content = a;
        EditRiotId.Text = a.Region != "" ? $"{a.RiotId}  ·  {a.Region}" : a.RiotId;
        EditLabel.Text = a.Label;
        EditNote.Text = a.Note;
        _editColor = a.Color;
        EditColors.Items.Clear();
        foreach (var hex in Account.Palette)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                GroupName = "swatch",
                IsChecked = string.Equals(hex, a.Color, StringComparison.OrdinalIgnoreCase),
                Tag = hex,
            };
            rb.Checked += (s, _) => _editColor = (string)((RadioButton)s).Tag;
            EditColors.Items.Add(rb);
        }
        EditTags.Text = string.Join(", ", a.Tags);
        BuildTagSuggestions();
        EditButtons.Visibility = Visibility.Visible;
        RemoveConfirm.Visibility = Visibility.Collapsed;

        EditOverlay.Visibility = Visibility.Visible;
        EditOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        EditSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(320, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        EditLabel.Focus();
        EditLabel.SelectAll();
    }

    static List<string> ParseTags(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0).Select(t => t.Length > 20 ? t[..20] : t)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();

    /// <summary>Tags used on other accounts, one click to add.</summary>
    void BuildTagSuggestions()
    {
        TagSuggestions.Children.Clear();
        var current = ParseTags(EditTags.Text);
        foreach (var tag in _app.Store.AllTags().Where(t => !current.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            var b = new Button { Style = (Style)FindResource("SecondaryButton"), Padding = new Thickness(9, 2, 9, 3), FontSize = 11.5,
                                 Margin = new Thickness(0, 0, 5, 4), Content = "+ " + tag, Focusable = false };
            b.Click += (_, _) =>
            {
                var list = ParseTags(EditTags.Text);
                list.Add(tag);
                EditTags.Text = string.Join(", ", list);
                BuildTagSuggestions();
            };
            TagSuggestions.Children.Add(b);
        }
        TagSuggestions.Visibility = TagSuggestions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void CloseEditor()
    {
        var anim = new DoubleAnimation(0, 380, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) => { EditOverlay.Visibility = Visibility.Collapsed; _editing = null; };
        EditSlide.BeginAnimation(TranslateTransform.YProperty, anim);
        EditOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)));
    }

    void EditSave_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is { } a)
        {
            a.Label = EditLabel.Text.Trim();
            a.Note = EditNote.Text.Trim();
            a.Tags = ParseTags(EditTags.Text);
            RebuildFilters();
            if (_editColor != "") a.Color = _editColor;
            _app.Store.Save();
        }
        CloseEditor();
    }

    void EditField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EditSave_Click(sender, e); e.Handled = true; }
    }

    void EditCancel_Click(object sender, RoutedEventArgs e) => CloseEditor();
    void EditScrim_Click(object sender, MouseButtonEventArgs e) => CloseEditor();
    void Sheet_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void RemoveAsk_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } a) return;
        RemoveText.Text = $"Remove {a.DisplayName}? Its saved sign-in is deleted from this PC. Your Riot account isn't affected.";
        EditButtons.Visibility = Visibility.Collapsed;
        RemoveConfirm.Visibility = Visibility.Visible;
    }

    void RemoveCancel_Click(object sender, RoutedEventArgs e)
    {
        EditButtons.Visibility = Visibility.Visible;
        RemoveConfirm.Visibility = Visibility.Collapsed;
    }

    void RemoveConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is { } a)
        {
            _app.Store.Remove(a);
            ShowToast(new(ResultKind.Info, $"Removed {a.DisplayName}"));
        }
        CloseEditor();
    }

    // ---------- settings ----------

    // One switch per card extra: title, hint, read, write.
    static readonly (string Title, string Hint, Func<AppSettings, bool> Get, Action<AppSettings, bool> Set)[] CardOptionRows =
    [
        ("Recent games", "Champion and win/loss of the last five games.", s => s.ShowRecent, (s, v) => s.ShowRecent = v),
        ("Decay countdown", "Days until solo rank decays (Diamond and above).", s => s.ShowDecay, (s, v) => s.ShowDecay = v),
        ("Penalties", "Queue lockout, low priority queue, ranked restrictions, dodge timer.", s => s.ShowPenalty, (s, v) => s.ShowPenalty = v),
        ("Win/loss streak", "Wins or losses in a row.", s => s.ShowStreak, (s, v) => s.ShowStreak = v),
        ("LP this session", "LP gained or lost since you signed in.", s => s.ShowLpDelta, (s, v) => s.ShowLpDelta = v),
        ("LP history graph", "Small line of solo rank over time.", s => s.ShowSpark, (s, v) => s.ShowSpark = v),
        ("Sign-in age", "When each saved sign-in was last refreshed.", s => s.ShowAge, (s, v) => s.ShowAge = v),
    ];

    void BuildCardOptions()
    {
        CardOptionsPanel.Children.Clear();
        foreach (var row in CardOptionRows)
        {
            var toggle = new CheckBox
            {
                Style = (Style)CardOptionsPanel.FindResource("Toggle"), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, IsChecked = row.Get(_app.Settings),
            };
            toggle.Click += (_, _) =>
            {
                row.Set(_app.Settings, toggle.IsChecked == true);
                _app.Settings.ApplyCardOptions();
                foreach (var a in _app.Store.Accounts) a.ChangedAll();
                _app.SaveSettings();
            };
            var text = new StackPanel { Margin = new Thickness(0, 0, 56, 0) };
            text.Children.Add(new TextBlock { Text = row.Title, Style = (Style)CardOptionsPanel.FindResource("RowTitle") });
            text.Children.Add(new TextBlock { Text = row.Hint, Style = (Style)CardOptionsPanel.FindResource("RowHint") });
            var grid = new Grid();
            grid.Children.Add(text);
            grid.Children.Add(toggle);
            CardOptionsPanel.Children.Add(new Border { Style = (Style)CardOptionsPanel.FindResource("Row"), Child = grid });
        }
    }

    void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings;
        BuildCardOptions();
        SetLaunch.IsChecked = s.LaunchLeague;
        SetTray.IsChecked = s.CloseToTray;
        SetCompact.IsChecked = s.Compact;
        SetUpdate.IsChecked = s.AutoUpdate;
        SetStartup.IsChecked = AutoStart.IsEnabled;
        SetStartup.IsEnabled = !AutoStart.IsDevBuild;
        ThemeSystem.IsChecked = s.Theme == AppTheme.System;
        ThemeDark.IsChecked = s.Theme == AppTheme.Dark;
        ThemeLight.IsChecked = s.Theme == AppTheme.Light;
        HotkeyButton.Content = s.HotkeyText;
        HotkeyError.Visibility = Visibility.Collapsed;
        UpdateUpdateInfo();

        SettingsPage.Visibility = Visibility.Visible;
        SettingsSlide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(ActualWidth * 0.4, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        SettingsPage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    void CloseSettings()
    {
        StopRecording();
        var anim = new DoubleAnimation(0, ActualWidth * 0.4, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) => SettingsPage.Visibility = Visibility.Collapsed;
        SettingsSlide.BeginAnimation(TranslateTransform.XProperty, anim);
        SettingsPage.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)));
        LaunchToggle.IsChecked = _app.Settings.LaunchLeague;
    }

    void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();

    void SettingToggle_Click(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings;
        s.LaunchLeague = SetLaunch.IsChecked == true;
        s.CloseToTray = SetTray.IsChecked == true;
        s.AutoUpdate = SetUpdate.IsChecked == true;
        if (s.Compact != (SetCompact.IsChecked == true))
        {
            s.Compact = SetCompact.IsChecked == true;
            ApplyListMode();
        }
        if (sender == SetStartup)
            try { AutoStart.Set(SetStartup.IsChecked == true); } catch { SetStartup.IsChecked = AutoStart.IsEnabled; }
        _app.SaveSettings();
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.Theme = ThemeDark.IsChecked == true ? AppTheme.Dark : ThemeLight.IsChecked == true ? AppTheme.Light : AppTheme.System;
        _app.SaveSettings();
    }

    void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingHotkey) { StopRecording(); return; }
        _recordingHotkey = true;
        _app.SuspendHotkey();
        HotkeyButton.Content = "Press keys…";
        HotkeyError.Visibility = Visibility.Collapsed;
        HotkeyButton.Focus();
    }

    void HotkeyButton_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { StopRecording(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        uint mods = 0;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) mods |= Native.MOD_CONTROL;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= Native.MOD_ALT;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= Native.MOD_SHIFT;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= Native.MOD_WIN;
        if (mods == 0 || mods == Native.MOD_SHIFT)
        {
            HotkeyError.Text = "Use at least Ctrl, Alt or Win with the key.";
            HotkeyError.Visibility = Visibility.Visible;
            return;
        }
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!_app.TryRegisterHotkey(mods, vk))
        {
            HotkeyError.Text = $"{AppSettings.FormatHotkey(mods, vk)} is used by another app. Try another.";
            HotkeyError.Visibility = Visibility.Visible;
            return;
        }
        _app.Settings.HotkeyModifiers = mods;
        _app.Settings.HotkeyKey = vk;
        _app.Settings.Save();
        _recordingHotkey = false;
        HotkeyButton.Content = _app.Settings.HotkeyText;
        ShowHotkeyHint();
    }

    void HotkeyButton_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => StopRecording();

    void StopRecording()
    {
        if (!_recordingHotkey) return;
        _recordingHotkey = false;
        _app.RegisterHotkey();
        HotkeyButton.Content = _app.Settings.HotkeyText;
    }

    void UpdateUpdateInfo()
    {
        VersionText.Text = $"RiftSwitch {Updater.CurrentText}";
        UpdateStatus.Text = Updater.Status != "" ? Updater.Status : "Updates come from GitHub.";
        UpdateButton.Content = Updater.Ready != null ? "Restart now" : "Check now";
    }

    async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Ready != null) { Updater.ApplyAndRestart(showWindow: true); return; }
        UpdateButton.IsEnabled = false;
        await Updater.CheckAsync(manual: true);
        UpdateButton.IsEnabled = true;
        UpdateUpdateInfo();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AccountStore.Dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AccountStore.Dir}\"") { UseShellExecute = true });
    }

    // ---------- window buttons ----------

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- toast & fades ----------

    internal void ShowToast(SwitchResult r)
    {
        var (brush, glyph) = r.Kind switch
        {
            ResultKind.Success => ("Good", ""),
            ResultKind.Warning => ("Warn", ""),
            ResultKind.Error => ("Bad", ""),
            _ => ("Accent", ""),
        };
        ToastDot.Fill = (Brush)FindResource(brush + "Soft");
        ToastIcon.Foreground = (Brush)FindResource(brush);
        ToastIcon.Text = glyph;
        ToastTitle.Text = r.Title;
        ToastText.Text = r.Message;
        ToastText.Visibility = r.Message == "" ? Visibility.Collapsed : Visibility.Visible;

        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        ToastSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(r.Kind is ResultKind.Warning or ResultKind.Error ? 9 : 4.5);
        _toastTimer.Start();
    }

    void HideToast()
    {
        _toastTimer.Stop();
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        anim.Completed += (_, _) => { if (Toast.Opacity == 0) Toast.Visibility = Visibility.Collapsed; };
        Toast.BeginAnimation(OpacityProperty, anim);
    }

    void Toast_Click(object sender, MouseButtonEventArgs e) => HideToast();

    static void Fade(UIElement el, bool show)
    {
        if (show) el.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 160 : 220));
        if (!show) anim.Completed += (_, _) => { if (el.Opacity == 0) el.Visibility = Visibility.Collapsed; };
        el.BeginAnimation(OpacityProperty, anim);
    }
}
