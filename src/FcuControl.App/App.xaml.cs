using System.Threading;
using System.Windows;

namespace FcuControl.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private AppController? _controller;
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;

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
        _mainWindow = new MainWindow(_controller);
        MainWindow = _mainWindow;
        _tray = new TrayIconService(_controller, _mainWindow, ExitApplication);
        _mainWindow.Show();
        _ = _controller.StartAsync();
    }

    private async void ExitApplication()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
        }

        _tray?.Dispose();
        if (_controller is not null)
        {
            await _controller.DisposeAsync();
        }

        _mainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
