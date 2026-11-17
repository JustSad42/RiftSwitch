using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RiftSwitch;

/// <summary>
/// Small always-on-top pill at the top of the screen while Riot prepares League after Play was pressed
/// ("Starting League…"), then "League is open". It never takes focus, so it doesn't fight the Riot Client.
/// </summary>
sealed class LaunchToast : Window
{
    readonly ContentPresenter _avatar = new() { Width = 30, Height = 30 };
    readonly TextBlock _title = new() { FontSize = 13.5, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _sub = new() { FontSize = 12, Margin = new Thickness(0, 1, 0, 0) };
    readonly System.Windows.Shapes.Path _spinner = new();
    readonly TextBlock _check = new() { Text = "", FontSize = 14, Visibility = Visibility.Collapsed,
                                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer _hide = new();
    readonly DispatcherTimer _elapsed = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _since;

    public LaunchToast()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        Title = "RiftSwitch launch";
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        _avatar.ContentTemplate = (DataTemplate)Application.Current.FindResource("Avatar");
        _spinner.Style = (Style)Application.Current.FindResource("Spinner");
        _spinner.Width = _spinner.Height = 16;
        _check.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        _check.SetResourceReference(TextBlock.ForegroundProperty, "Good");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var status = new Grid { Width = 20, Height = 20, Margin = new Thickness(14, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(_spinner);
        status.Children.Add(_check);

        var text = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_sub);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_avatar);
        row.Children.Add(text);
        row.Children.Add(status);

        var pill = new Border
        {
            CornerRadius = new CornerRadius(22), Padding = new Thickness(8, 7, 14, 7), BorderThickness = new Thickness(1),
            Margin = new Thickness(18), Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black },
        };
        pill.SetResourceReference(Border.BackgroundProperty, "Bg");
        pill.SetResourceReference(Border.BorderBrushProperty, "Line");
        Content = pill;

        _hide.Tick += (_, _) => { _hide.Stop(); FadeOut(); };
        _elapsed.Tick += (_, _) => _sub.Text = $"Riot takes about 10 seconds · {(int)(DateTime.Now - _since).TotalSeconds}s";
        SourceInitialized += (_, _) =>
        {
            // Never steal focus or appear in Alt+Tab.
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
    }

    public void ShowStarting(Account acc)
    {
        _hide.Stop();
        _avatar.Content = acc;
        _title.Text = $"Starting League on {acc.DisplayName}";
        _since = DateTime.Now;
        _sub.Text = "Riot takes about 10 seconds";
        _spinner.Visibility = Visibility.Visible;
        _check.Visibility = Visibility.Collapsed;
        _elapsed.Start();

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        UpdateLayout();
        var area = Native.CursorWorkArea(this);
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 6;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    public void ShowDone(bool ok)
    {
        if (!IsVisible) return;
        _elapsed.Stop();
        _spinner.Visibility = Visibility.Collapsed;
        _check.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        _title.Text = ok ? "League is open" : "League didn't start";
        _sub.Text = ok ? "Good luck on the Rift" : "Press Play in the Riot Client";
        _hide.Interval = TimeSpan.FromSeconds(ok ? 2 : 6);
        _hide.Start();
    }

    void FadeOut()
    {
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        anim.Completed += (_, _) => { if (Opacity == 0) Hide(); };
        BeginAnimation(OpacityProperty, anim);
    }
}
