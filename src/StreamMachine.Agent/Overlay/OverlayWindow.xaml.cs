using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using StreamMachine.Core;

namespace StreamMachine.Agent.Overlay;

public sealed class LevelBrush : IValueConverter
{
    static readonly Brush Ok = Frozen("#3DDC97"), Warn = Frozen("#FFC24B"), Bad = Frozen("#FF5C7A"), Info = Frozen("#8FA3FF");
    static Brush Frozen(string c) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); b.Freeze(); return b; }
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch { Level.Ok => Ok, Level.Warn => Warn, Level.Bad => Bad, _ => Info };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public partial class OverlayWindow : Window
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    static readonly IntPtr HWND_TOPMOST = new(-1);

    IntPtr _previousForeground;
    public OverlayModel Model { get; }
    public bool IsOpen { get; private set; }

    public OverlayWindow(OverlayModel model)
    {
        Model = model;
        DataContext = model;
        InitializeComponent();
        // Keep the selected setting on screen while the list scrolls.
        model.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SettingsModel.Index)) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (SettingsList.ItemContainerGenerator.ContainerFromIndex(model.Settings.Index) is FrameworkElement fe)
                    fe.BringIntoView(new Rect(0, -60, fe.ActualWidth, fe.ActualHeight + 120));
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        SourceInitialized += (_, _) =>
        {
            // Tool window: never in Alt+Tab.
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80); // GWL_EXSTYLE |= WS_EX_TOOLWINDOW
        };
    }

    void CoverPrimaryScreen()
    {
        // SystemParameters are already in WPF units for the primary screen's DPI.
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    public void Open()
    {
        _previousForeground = ForegroundWindow.Current;
        CoverPrimaryScreen();
        if (!IsVisible) Show();
        IsOpen = true;
        var h = new WindowInteropHelper(this).Handle;
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, 0x1 | 0x2 | 0x40); // NOSIZE | NOMOVE | SHOWWINDOW
        var focused = ForegroundWindow.Force(h);
        Activate();
        Log.Info("overlay", "opened", ("focus", focused));

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var grow = new DoubleAnimation(0.975, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Root.BeginAnimation(OpacityProperty, fade);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    public void Dismiss(bool restoreFocus)
    {
        if (!IsOpen) return;
        IsOpen = false;
        Hide();
        Log.Info("overlay", "closed");
        // Hand focus back to what was in front (the stream), so its controller input resumes.
        if (restoreFocus && _previousForeground != IntPtr.Zero) ForegroundWindow.Force(_previousForeground);
    }

    /// <summary>Renders the overlay exactly as drawn (1920x1080 design size) to a PNG, for the render test.</summary>
    public void SaveRender(string path, int width = 1920, int height = 1080)
    {
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 1;
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RootScale.ScaleX = RootScale.ScaleY = 1;
        Root.Measure(new Size(1920, 1080));
        Root.Arrange(new Rect(0, 0, 1920, 1080));
        Root.UpdateLayout();
        var rtb = new RenderTargetBitmap(width, height, 96 * width / 1920.0, 96 * height / 1080.0, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 1920, 1080)); // what the TV shows behind it
            dc.DrawRectangle(new VisualBrush(Root), null, new Rect(0, 0, 1920, 1080));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
