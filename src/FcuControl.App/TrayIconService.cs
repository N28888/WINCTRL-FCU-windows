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

    public TrayIconService(AppController controller, MainWindow window, Action exit)
    {
        _controller = controller;
        _window = window;
        _statusItem = new Forms.ToolStripMenuItem("正在启动") { Enabled = false };
        _pauseItem = new Forms.ToolStripMenuItem("暂停", null, (_, _) => controller.ToggleManualPause());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("打开主窗口", null, (_, _) => window.Dispatcher.BeginInvoke(window.ShowAndActivate)));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => window.Dispatcher.BeginInvoke(exit)));

        _ownedIcon = LoadApplicationIcon();
        _icon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon ?? Drawing.SystemIcons.Application,
            Text = "WINCTRL 32 FCU 控制器",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => window.Dispatcher.BeginInvoke(window.ShowAndActivate);
        controller.StateChanged += Update;
        Update();
    }

    private void Update()
    {
        _window.Dispatcher.BeginInvoke(() =>
        {
            _statusItem.Text = _controller.StatusText;
            _pauseItem.Text = _controller.Settings.ManualPaused ? "恢复" : "暂停";
            _icon.Text = _controller.StatusText.Length > 63 ? _controller.StatusText[..63] : _controller.StatusText;
        });
    }

    public void Dispose()
    {
        _controller.StateChanged -= Update;
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
