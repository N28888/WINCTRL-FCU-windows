using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using FcuControl.Core;
using Microsoft.Win32;

namespace FcuControl.App;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly OverlayWindow _overlay = new();
    private readonly ObservableCollection<MappingRow> _mappingRows = [];
    private readonly ObservableCollection<MonitorRow> _monitorRows = [];
    private readonly ObservableCollection<OutputRow> _outputRows = [];
    private readonly ObservableCollection<AudioSwitchRow> _audioSwitchRows = [];
    private readonly ObservableCollection<ApplicationLaunchRow> _applicationLaunchRows = [];
    private bool _updatingOutputRows;
    private bool _updatingAudioSwitchRows;

    public MainWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
        MappingGrid.ItemsSource = _mappingRows;
        MonitorGrid.ItemsSource = _monitorRows;
        OutputGrid.ItemsSource = _outputRows;
        AudioSwitchGrid.ItemsSource = _audioSwitchRows;
        ApplicationLaunchGrid.ItemsSource = _applicationLaunchRows;

        foreach (var action in Enum.GetValues<AppAction>())
        {
            _mappingRows.Add(new MappingRow(action, ActionNames.Get(action)));
        }

        ConflictProcessesBox.Text = string.Join(Environment.NewLine, controller.Settings.ConflictProcesses);
        VolumeStepBox.Text = controller.Settings.VolumeStepPercent.ToString();
        BrightnessStepBox.Text = controller.Settings.BrightnessStepPercent.ToString();
        BacklightBox.Text = controller.Settings.BacklightBrightness.ToString();
        LcdBrightnessBox.Text = controller.Settings.LcdBrightness.ToString();
        LedBrightnessBox.Text = controller.Settings.LedBrightness.ToString();
        HardwareBrightnessStepBox.Text = controller.Settings.HardwareBrightnessStepPercent.ToString();

        controller.StateChanged += ControllerOnStateChanged;
        controller.MappingsChanged += RefreshMappings;
        controller.MonitorsChanged += ControllerOnMonitorsChanged;
        controller.AudioDevicesChanged += _ => Dispatcher.BeginInvoke(RefreshAudioSwitchRows);
        controller.AudioSwitchBindingsChanged += () => Dispatcher.BeginInvoke(RefreshAudioSwitchRows);
        controller.ApplicationLaunchBindingsChanged += () => Dispatcher.BeginInvoke(RefreshApplicationLaunchRows);
        controller.HardwareBrightnessChanged += () => Dispatcher.BeginInvoke(RefreshHardwareBrightnessFields);
        controller.DiagnosticReceived += AddDiagnostic;
        controller.OverlayRequested += message => Dispatcher.BeginInvoke(() => _overlay.ShowMessage(message));

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }
        };
        Closing += MainWindow_Closing;
        Loaded += (_, _) => RefreshAll();
    }

    public bool AllowClose { get; set; }

    public void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RefreshAll()
    {
        RefreshState();
        RefreshMappings();
        RefreshMonitors(_controller.Monitors);
        RefreshOutputRows();
        RefreshAudioSwitchRows();
        RefreshApplicationLaunchRows();
    }

    private void ControllerOnStateChanged()
    {
        Dispatcher.BeginInvoke(RefreshState);
    }

    private void RefreshState()
    {
        StatusText.Text = _controller.StatusText;
        DeviceStatusText.Text = _controller.HidConnected ? "已连接 WINCTRL 32 FCU" : _controller.StatusText;
        var audio = _controller.AudioSnapshot;
        AudioStatusText.Text = audio.Muted
            ? $"{_controller.DefaultAudioDeviceName} · {audio.Volume}% · 已静音"
            : $"{_controller.DefaultAudioDeviceName} · {audio.Volume}%";
        CurrentAudioDeviceText.Text = $"当前默认：{_controller.DefaultAudioDeviceName}";
        ConflictStatusText.Text = _controller.ConflictMatches.Count > 0
            ? string.Join(", ", _controller.ConflictMatches)
            : "未检测到冲突程序";
        PauseButton.Content = _controller.Settings.ManualPaused ? "恢复" : "暂停";
    }

    private void RefreshMappings()
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var row in _mappingRows)
            {
                row.ControlId = _controller.GetBinding(row.Action) ?? "未绑定";
            }
        });
    }

    private void ControllerOnMonitorsChanged(IReadOnlyList<MonitorSnapshot> monitors)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshMonitors(monitors);
            RefreshOutputRows();
        });
    }

    private void RefreshMonitors(IReadOnlyList<MonitorSnapshot> monitors)
    {
        _monitorRows.Clear();
        foreach (var monitor in monitors)
        {
            _monitorRows.Add(new MonitorRow(
                monitor.Id,
                monitor.Name,
                monitor.Backend switch { MonitorBackend.Wmi => "WMI", MonitorBackend.DdcCi => "DDC/CI", _ => "不支持" },
                monitor.Brightness?.ToString() ?? "—",
                monitor.Status,
                monitor.IsControllable,
                _controller.IsMonitorEnabled(monitor.Id)));
        }
    }

    private void RefreshOutputRows()
    {
        _updatingOutputRows = true;
        try
        {
            _outputRows.Clear();
            var monitors = _controller.Monitors.Select(monitor => new MonitorOption(monitor.Id, monitor.Name)).ToArray();
            foreach (var target in Enum.GetValues<OutputTargetKind>())
            {
                var binding = _controller.Settings.OutputBindings.First(item => item.Target == target);
                _outputRows.Add(new OutputRow(target, OutputNames.Get(target), binding.Source, binding.MonitorId, monitors));
            }
        }
        finally
        {
            _updatingOutputRows = false;
        }
    }

    private void RefreshAudioSwitchRows()
    {
        _updatingAudioSwitchRows = true;
        try
        {
            _audioSwitchRows.Clear();
            var devices = _controller.AudioDevices.ToList();
            foreach (var binding in _controller.Settings.AudioDeviceSwitchBindings)
            {
                var options = devices.Select(device => new AudioDeviceOption(
                    device.Id,
                    device.IsDefault ? $"{device.Name}（当前默认）" : device.Name)).ToList();
                if (!string.IsNullOrWhiteSpace(binding.DeviceId) &&
                    options.All(option => !string.Equals(option.Id, binding.DeviceId, StringComparison.OrdinalIgnoreCase)))
                {
                    options.Add(new AudioDeviceOption(binding.DeviceId, "已保存的设备（当前不可用）"));
                }

                _audioSwitchRows.Add(new AudioSwitchRow(binding, options));
            }
        }
        finally
        {
            _updatingAudioSwitchRows = false;
        }
    }

    private void RefreshApplicationLaunchRows()
    {
        _applicationLaunchRows.Clear();
        foreach (var binding in _controller.Settings.ApplicationLaunchBindings)
        {
            _applicationLaunchRows.Add(new ApplicationLaunchRow(binding));
        }
    }

    private void RefreshHardwareBrightnessFields()
    {
        BacklightBox.Text = _controller.Settings.BacklightBrightness.ToString();
        LcdBrightnessBox.Text = _controller.Settings.LcdBrightness.ToString();
        LedBrightnessBox.Text = _controller.Settings.LedBrightness.ToString();
        HardwareBrightnessStepBox.Text = _controller.Settings.HardwareBrightnessStepPercent.ToString();
    }

    private void AddDiagnostic(string message)
    {
        DiagnosticList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} {message}");
        while (DiagnosticList.Items.Count > 100) DiagnosticList.Items.RemoveAt(DiagnosticList.Items.Count - 1);
    }

    private void LearnButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MappingRow row)
        {
            _controller.BeginLearning(row.Action);
            _overlay.ShowMessage(new OverlayMessage("等待 FCU 输入", $"请操作：{row.ActionName}"));
        }
    }

    private void ClearBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MappingRow row)
        {
            _controller.ClearBinding(row.Action);
        }
    }

    private void AddAudioSwitchButton_Click(object sender, RoutedEventArgs e) => _controller.AddAudioSwitchBinding();

    private void RefreshAudioDevicesButton_Click(object sender, RoutedEventArgs e) => _controller.RefreshAudioDevices();

    private void LearnAudioSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AudioSwitchRow row) return;
        _controller.BeginAudioSwitchLearning(row.BindingId);
        _overlay.ShowMessage(new OverlayMessage("等待 FCU 输入", "请按下用于切换该音频设备的 FCU 按键"));
    }

    private void DeleteAudioSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AudioSwitchRow row)
        {
            _controller.RemoveAudioSwitchBinding(row.BindingId);
        }
    }

    private void AudioSwitchDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAudioSwitchRows) return;
        if (sender is ComboBox { DataContext: AudioSwitchRow row } comboBox)
        {
            row.SelectedDeviceId = comboBox.SelectedValue as string;
            _controller.UpdateAudioSwitchBinding(row.BindingId, row.SelectedDeviceId, row.SelectedLedTarget);
        }
    }

    private void AudioSwitchLed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAudioSwitchRows) return;
        if (sender is ComboBox { DataContext: AudioSwitchRow row } comboBox)
        {
            row.SelectedLedOption = comboBox.SelectedItem as LedOption;
            _controller.UpdateAudioSwitchBinding(row.BindingId, row.SelectedDeviceId, row.SelectedLedTarget);
        }
    }

    private void AddApplicationLaunchButton_Click(object sender, RoutedEventArgs e) =>
        _controller.AddApplicationLaunchBinding();

    private void BrowseApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ApplicationLaunchRow row) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择要由 FCU 启动的软件",
            Filter = "应用程序或快捷方式 (*.exe;*.lnk)|*.exe;*.lnk",
            CheckFileExists = true,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(row.ExecutablePath) && File.Exists(row.ExecutablePath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(row.ExecutablePath);
            dialog.FileName = Path.GetFileName(row.ExecutablePath);
        }

        if (dialog.ShowDialog(this) == true)
        {
            _controller.UpdateApplicationLaunchBinding(row.BindingId, dialog.FileName);
        }
    }

    private void LearnApplicationLaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ApplicationLaunchRow row) return;
        _controller.BeginApplicationLaunchLearning(row.BindingId);
        _overlay.ShowMessage(new OverlayMessage("等待 FCU 输入", "请按下用于启动该软件的 FCU 按键"));
    }

    private void DeleteApplicationLaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ApplicationLaunchRow row)
        {
            _controller.RemoveApplicationLaunchBinding(row.BindingId);
        }
    }

    private void MonitorEnabled_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as CheckBox)?.DataContext is MonitorRow row)
        {
            _controller.SetMonitorEnabled(row.Id, row.Enabled);
        }
    }

    private void OutputSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingOutputRows) return;
        if (sender is ComboBox { DataContext: OutputRow row, SelectedValue: OutputSourceKind selectedSource })
        {
            row.SelectedSource = selectedSource;
            row.NotifySourceChanged();
            _controller.UpdateOutputBinding(row.Target, selectedSource, row.SelectedMonitorId);
        }
    }

    private void OutputMonitor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingOutputRows) return;
        if (sender is ComboBox { DataContext: OutputRow row } comboBox && row.NeedsMonitor)
        {
            var selectedMonitorId = comboBox.SelectedValue as string;
            row.SelectedMonitorId = selectedMonitorId;
            _controller.UpdateOutputBinding(row.Target, row.SelectedSource, selectedMonitorId);
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e) => _controller.ToggleManualPause();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await _controller.RefreshMonitorsAsync();
        RefreshAll();
    }

    private void SaveStepsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(VolumeStepBox.Text, out var volume) || !int.TryParse(BrightnessStepBox.Text, out var brightness))
        {
            MessageBox.Show("请输入 1–20 的整数步长。", "步长设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _controller.UpdateSteps(volume, brightness);
        VolumeStepBox.Text = _controller.Settings.VolumeStepPercent.ToString();
        BrightnessStepBox.Text = _controller.Settings.BrightnessStepPercent.ToString();
    }

    private void SaveConflictProcessesButton_Click(object sender, RoutedEventArgs e)
    {
        _controller.UpdateConflictProcesses(ConflictProcessesBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        ConflictProcessesBox.Text = string.Join(Environment.NewLine, _controller.Settings.ConflictProcesses);
    }

    private void SaveHardwareBrightnessButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(BacklightBox.Text, out var backlight) ||
            !int.TryParse(LcdBrightnessBox.Text, out var lcd) ||
            !int.TryParse(LedBrightnessBox.Text, out var led) ||
            !int.TryParse(HardwareBrightnessStepBox.Text, out var step))
        {
            MessageBox.Show("硬件亮度必须是 0–100 的整数，旋钮步长必须是 1–20 的整数。", "FCU 输出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _controller.UpdateHardwareBrightness(backlight, lcd, led, step);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }

    private sealed class MappingRow : NotifyBase
    {
        private string _controlId = "未绑定";
        public MappingRow(AppAction action, string actionName) { Action = action; ActionName = actionName; }
        public AppAction Action { get; }
        public string ActionName { get; }
        public string ControlId { get => _controlId; set => SetField(ref _controlId, value); }
    }

    private sealed record MonitorOption(string Id, string Name);
    private sealed record SourceOption(OutputSourceKind Kind, string Label);
    private sealed record AudioDeviceOption(string Id, string Name);
    private sealed record LedOption(OutputTargetKind? Target, string Name);

    private sealed class ApplicationLaunchRow
    {
        public ApplicationLaunchRow(ApplicationLaunchBinding binding)
        {
            BindingId = binding.BindingId;
            ControlId = string.IsNullOrWhiteSpace(binding.ControlId) ? "未绑定" : binding.ControlId;
            ExecutablePath = binding.ExecutablePath;
            ApplicationName = string.IsNullOrWhiteSpace(binding.ExecutablePath)
                ? "尚未选择软件"
                : Path.GetFileNameWithoutExtension(binding.ExecutablePath);
        }

        public string BindingId { get; }
        public string ControlId { get; }
        public string ExecutablePath { get; }
        public string ApplicationName { get; }
    }

    private sealed class AudioSwitchRow : NotifyBase
    {
        private string? _selectedDeviceId;
        private LedOption? _selectedLedOption;

        public AudioSwitchRow(AudioDeviceSwitchBinding binding, IEnumerable<AudioDeviceOption> devices)
        {
            BindingId = binding.BindingId;
            ControlId = string.IsNullOrWhiteSpace(binding.ControlId) ? "未绑定" : binding.ControlId;
            _selectedDeviceId = string.IsNullOrWhiteSpace(binding.DeviceId) ? null : binding.DeviceId;
            DeviceOptions = new ObservableCollection<AudioDeviceOption>(devices);
            LedOptions = new ObservableCollection<LedOption>(
            [
                new(null, "不联动"),
                new(OutputTargetKind.LocLed, "LOC LED"),
                new(OutputTargetKind.Ap1Led, "AP1 LED"),
                new(OutputTargetKind.Ap2Led, "AP2 LED"),
                new(OutputTargetKind.AthrLed, "A/THR LED"),
                new(OutputTargetKind.ExpedLed, "EXPED LED"),
                new(OutputTargetKind.ApprLed, "APPR LED")
            ]);
            _selectedLedOption = LedOptions.First(option => option.Target == binding.LedTarget);
        }

        public string BindingId { get; }
        public string ControlId { get; }
        public ObservableCollection<AudioDeviceOption> DeviceOptions { get; }
        public ObservableCollection<LedOption> LedOptions { get; }
        public string? SelectedDeviceId { get => _selectedDeviceId; set => SetField(ref _selectedDeviceId, value); }
        public LedOption? SelectedLedOption { get => _selectedLedOption; set => SetField(ref _selectedLedOption, value); }
        public OutputTargetKind? SelectedLedTarget => SelectedLedOption?.Target;
    }

    private sealed class OutputRow : NotifyBase
    {
        private OutputSourceKind _selectedSource;
        private string? _selectedMonitorId;

        public OutputRow(OutputTargetKind target, string targetName, OutputSourceKind source, string? monitorId, IEnumerable<MonitorOption> monitors)
        {
            Target = target;
            TargetName = targetName;
            _selectedSource = source;
            _selectedMonitorId = monitorId;
            MonitorOptions = new ObservableCollection<MonitorOption>(monitors);
            var numeric = target is OutputTargetKind.Speed or OutputTargetKind.Heading or OutputTargetKind.Altitude or OutputTargetKind.VerticalSpeed;
            SourceOptions = new ObservableCollection<SourceOption>(numeric
                ? [new(OutputSourceKind.Blank, "留空"), new(OutputSourceKind.MasterVolume, "系统音量"), new(OutputSourceKind.MonitorBrightness, "显示器亮度"),
                   new(OutputSourceKind.FcuLcdBrightness, "FCU LCD 亮度"), new(OutputSourceKind.FcuBacklightBrightness, "FCU 按键背光")]
                : [new(OutputSourceKind.ConstantOff, "关闭"), new(OutputSourceKind.ConstantOn, "常亮"), new(OutputSourceKind.Muted, "静音"),
                   new(OutputSourceKind.AppActive, "程序活动"), new(OutputSourceKind.Yielded, "已让位"), new(OutputSourceKind.DeviceError, "设备故障")]);
            if (SourceOptions.All(option => option.Kind != source)) _selectedSource = SourceOptions[0].Kind;
        }

        public OutputTargetKind Target { get; }
        public string TargetName { get; }
        public ObservableCollection<SourceOption> SourceOptions { get; }
        public ObservableCollection<MonitorOption> MonitorOptions { get; }
        public OutputSourceKind SelectedSource { get => _selectedSource; set { if (SetField(ref _selectedSource, value)) OnPropertyChanged(nameof(NeedsMonitor)); } }
        public string? SelectedMonitorId { get => _selectedMonitorId; set => SetField(ref _selectedMonitorId, value); }
        public bool NeedsMonitor => SelectedSource == OutputSourceKind.MonitorBrightness;
        public void NotifySourceChanged() => OnPropertyChanged(nameof(NeedsMonitor));
    }

    private sealed class MonitorRow
    {
        public MonitorRow(string id, string name, string backend, string brightness, string status, bool controllable, bool enabled)
        {
            Id = id; Name = name; Backend = backend; Brightness = brightness; Status = status; Controllable = controllable; Enabled = enabled;
        }
        public string Id { get; }
        public string Name { get; }
        public string Backend { get; }
        public string Brightness { get; }
        public string Status { get; }
        public bool Controllable { get; }
        public bool Enabled { get; set; }
    }

    private abstract class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal static class OutputNames
{
    public static string Get(OutputTargetKind target) => target switch
    {
        OutputTargetKind.Speed => "SPD 数码窗",
        OutputTargetKind.Heading => "HDG 数码窗",
        OutputTargetKind.Altitude => "ALT 数码窗",
        OutputTargetKind.VerticalSpeed => "V/S 数码窗",
        OutputTargetKind.LocLed => "LOC LED",
        OutputTargetKind.Ap1Led => "AP1 LED",
        OutputTargetKind.Ap2Led => "AP2 LED",
        OutputTargetKind.AthrLed => "A/THR LED",
        OutputTargetKind.ExpedLed => "EXPED LED",
        OutputTargetKind.ApprLed => "APPR LED",
        _ => target.ToString()
    };
}
