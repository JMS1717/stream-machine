using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Runtime.InteropServices;

namespace StreamMachine.Agent.Overlay;

/// <summary>A small click-through pill at the top of the screen while desktop mouse mode is on.</summary>
public sealed class MouseBadge : Window
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    readonly TextBlock _hint;

    public MouseBadge()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;

        var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        _hint = new TextBlock
        {
            Text = "A click   ·   B right-click   ·   right stick scroll   ·   Y keyboard   ·   hold View + Menu for the menu",
            FontSize = 17, Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xA6, 0xC8)), Margin = new Thickness(18, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = new LinearGradientBrush(Color.FromRgb(0x94, 0x79, 0xFF), Color.FromRgb(0x5B, 0x3B, 0xE6), 45),
            Child = new TextBlock { Text = "", FontFamily = icons, FontSize = 17, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        row.Children.Add(new TextBlock { Text = "Mouse mode", FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_hint);
        Content = new Border
        {
            CornerRadius = new CornerRadius(26), Padding = new Thickness(10, 9, 24, 9), Margin = new Thickness(24),
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x16, 0x14, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x94, 0x79, 0xFF)), BorderThickness = new Thickness(1.5),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.5 },
            Child = row,
        };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            // Click-through, never focused, not in Alt+Tab: WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x80000 | 0x80 | 0x08000000);
        };
        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => Place();
    }

    void Place()
    {
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = 0;
    }

    public void ShowBadge()
    {
        _hint.Visibility = Visibility.Visible;
        Show();
        Opacity = 1;
        // The hint fades after a few seconds; the pill itself stays as a reminder.
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        t.Tick += (_, _) => { t.Stop(); _hint.Visibility = Visibility.Collapsed; BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.75, TimeSpan.FromMilliseconds(400))); };
        t.Start();
    }
}
