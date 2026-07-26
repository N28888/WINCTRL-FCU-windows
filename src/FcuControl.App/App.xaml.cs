using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FcuControl.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private AppController? _controller;
    private MainWindow? _mainWindow;
    private OverlayWindow? _overlay;
    private TrayIconService? _tray;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "Local\\FcuControl-WinCtrl32-BB10", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(Localization.Get("App.AlreadyRunning"), Localization.Get("App.ShortTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            _controller?.Logger.Error("UI 未处理异常", args.Exception);
            MessageBox.Show(Localization.Get("App.UnhandledError", args.Exception.Message), Localization.Get("App.ShortTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _controller = new AppController(Dispatcher);
        _overlay = new OverlayWindow();
        _controller.OverlayRequested += ShowOverlay;
        _tray = new TrayIconService(_controller, Dispatcher, ShowMainWindow, ExitApplication);
        ShowMainWindow();
        _ = _controller.StartAsync();
    }

    private void ShowMainWindow()
    {
        if (_controller is null) return;
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(_controller, ShowOverlay);
            _mainWindow.Closed += MainWindowOnClosed;
            MainWindow = _mainWindow;
        }

        _mainWindow.ShowAndActivate();
    }

    private void ShowOverlay(OverlayMessage message) =>
        Dispatcher.BeginInvoke(() => _overlay?.ShowMessage(message));

    private void MainWindowOnClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.Closed -= MainWindowOnClosed;
        if (ReferenceEquals(_mainWindow, window)) _mainWindow = null;
        if (ReferenceEquals(MainWindow, window)) MainWindow = null!;

        if (!_exiting)
        {
            Dispatcher.BeginInvoke(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using var process = Process.GetCurrentProcess();
                NativeMethods.K32EmptyWorkingSet(process.Handle);
            }, DispatcherPriority.ApplicationIdle);
        }
    }

    private async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        _tray?.Dispose();
        if (_controller is not null)
        {
            _controller.OverlayRequested -= ShowOverlay;
            await _controller.DisposeAsync();
        }

        _mainWindow?.Close();
        _overlay?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern bool K32EmptyWorkingSet(IntPtr process);
    }
}
