using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FcuControl.App;

public sealed class TrayIconService : IDisposable
{
    private readonly AppController _controller;
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon? _ownedIcon;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Forms.ToolStripMenuItem _openItem;
    private readonly Forms.ToolStripMenuItem _exitItem;

    public TrayIconService(AppController controller, MainWindow window, Action exit)
    {
        _controller = controller;
        _window = window;
        _statusItem = new Forms.ToolStripMenuItem(Localization.Get("Status.Starting")) { Enabled = false };
        _pauseItem = new Forms.ToolStripMenuItem(Localization.Get("Button.Pause"), null, (_, _) => controller.ToggleManualPause());
        _openItem = new Forms.ToolStripMenuItem(Localization.Get("Tray.Open"), null, (_, _) => window.Dispatcher.BeginInvoke(window.ShowAndActivate));
        _exitItem = new Forms.ToolStripMenuItem(Localization.Get("Tray.Exit"), null, (_, _) => window.Dispatcher.BeginInvoke(exit));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _ownedIcon = LoadApplicationIcon();
        _icon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon ?? Drawing.SystemIcons.Application,
            Text = Localization.Get("App.Title"),
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => window.Dispatcher.BeginInvoke(window.ShowAndActivate);
        controller.StateChanged += Update;
        Localization.LanguageChanged += Update;
        Update();
    }

    private void Update()
    {
        _window.Dispatcher.BeginInvoke(() =>
        {
            _statusItem.Text = _controller.StatusText;
            _pauseItem.Text = Localization.Get(_controller.Settings.ManualPaused ? "Button.Resume" : "Button.Pause");
            _openItem.Text = Localization.Get("Tray.Open");
            _exitItem.Text = Localization.Get("Tray.Exit");
            _icon.Text = _controller.StatusText.Length > 63 ? _controller.StatusText[..63] : _controller.StatusText;
        });
    }

    public void Dispose()
    {
        _controller.StateChanged -= Update;
        Localization.LanguageChanged -= Update;
        _icon.Visible = false;
        _icon.Dispose();
        _ownedIcon?.Dispose();
    }

    private static Drawing.Icon? LoadApplicationIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath)) return null;
            using var extracted = Drawing.Icon.ExtractAssociatedIcon(executablePath);
            return extracted is null ? null : (Drawing.Icon)extracted.Clone();
        }
        catch
        {
            return null;
        }
    }
}
