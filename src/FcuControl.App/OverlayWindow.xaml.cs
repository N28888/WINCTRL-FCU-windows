using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FcuControl.App;

public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _hideTimer;

    public OverlayWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _hideTimer.Tick += (_, _) => BeginFadeOut();
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    public void ShowMessage(OverlayMessage message)
    {
        OverlayTitle.Text = message.Title;
        OverlayDetail.Text = message.Detail;
        OverlayProgress.Visibility = message.Percent.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (message.Percent.HasValue) OverlayProgress.Value = message.Percent.Value;

        var screen = Forms.Screen.FromHandle(NativeMethods.GetForegroundWindow());
        Left = screen.WorkingArea.Left + (screen.WorkingArea.Width - Width) / 2d;
        Top = screen.WorkingArea.Bottom - 150;
        if (!IsVisible) Show();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void BeginFadeOut()
    {
        _hideTimer.Stop();
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        animation.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, animation);
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLong(handle, NativeMethods.GwlExStyle, style | NativeMethods.WsExTransparent | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate);
    }

    private static class NativeMethods
    {
        internal const int GwlExStyle = -20;
        internal const int WsExTransparent = 0x20;
        internal const int WsExToolWindow = 0x80;
        internal const int WsExNoActivate = 0x08000000;
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(IntPtr handle, int index, int newStyle);
    }
}

