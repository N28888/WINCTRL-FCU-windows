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
    private readonly Action<OverlayMessage> _showOverlay;
    private readonly ObservableCollection<MappingRow> _mappingRows = [];
    private readonly ObservableCollection<MonitorRow> _monitorRows = [];
    private readonly ObservableCollection<OutputRow> _outputRows = [];
    private readonly ObservableCollection<AudioSwitchRow> _audioSwitchRows = [];
    private readonly ObservableCollection<ApplicationLaunchRow> _applicationLaunchRows = [];
    private bool _updatingOutputRows;
    private bool _updatingAudioSwitchRows;

    public MainWindow(AppController controller, Action<OverlayMessage> showOverlay)
    {
        _controller = controller;
        _showOverlay = showOverlay;
        Localization.SetLanguage(controller.Settings.Language);
        InitializeComponent();
        MappingGrid.ItemsSource = _mappingRows;
        MonitorGrid.ItemsSource = _monitorRows;
        OutputGrid.ItemsSource = _outputRows;
        AudioSwitchGrid.ItemsSource = _audioSwitchRows;
        ApplicationLaunchGrid.ItemsSource = _applicationLaunchRows;

        LanguageBox.ItemsSource = new[]
        {
            new LanguageOption(Localization.Chinese, "中文"),
            new LanguageOption(Localization.English, "English")
        };
        LanguageBox.SelectedValue = controller.Settings.Language;
        RefreshLocalizedRows();

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
        controller.AudioDevicesChanged += ControllerOnAudioDevicesChanged;
        controller.AudioSwitchBindingsChanged += ControllerOnAudioSwitchBindingsChanged;
        controller.ApplicationLaunchBindingsChanged += ControllerOnApplicationLaunchBindingsChanged;
        controller.HardwareBrightnessChanged += ControllerOnHardwareBrightnessChanged;
        controller.DiagnosticReceived += AddDiagnostic;

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                Close();
            }
        };
        Closed += MainWindow_Closed;
        Loaded += (_, _) => RefreshAll();
    }

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

    private void RefreshLocalizedRows()
    {
        _mappingRows.Clear();
        foreach (var action in Enum.GetValues<AppAction>())
        {
            _mappingRows.Add(new MappingRow(action, ActionNames.Get(action)));
        }
    }

    private void ControllerOnStateChanged()
    {
        Dispatcher.BeginInvoke(RefreshState);
    }

    private void RefreshState()
    {
        StatusText.Text = _controller.StatusText;
        DeviceStatusText.Text = _controller.HidConnected ? Localization.Get("State.ConnectedFcu") : _controller.StatusText;
        var audio = _controller.AudioSnapshot;
        AudioStatusText.Text = audio.Muted
            ? $"{_controller.DefaultAudioDeviceName} · {audio.Volume}% · {Localization.Get("State.Muted")}"
            : $"{_controller.DefaultAudioDeviceName} · {audio.Volume}%";
        CurrentAudioDeviceText.Text = Localization.Get("State.CurrentDefault", _controller.DefaultAudioDeviceName);
        ConflictStatusText.Text = _controller.ConflictMatches.Count > 0
            ? string.Join(", ", _controller.ConflictMatches)
            : Localization.Get("State.NoConflict");
        PauseButton.Content = Localization.Get(_controller.Settings.ManualPaused ? "Button.Resume" : "Button.Pause");
    }

    private void RefreshMappings()
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var row in _mappingRows)
            {
                row.ControlId = _controller.GetBinding(row.Action) ?? Localization.Get("State.Unbound");
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

    private void ControllerOnAudioDevicesChanged(IReadOnlyList<AudioDeviceSnapshot> _) =>
        Dispatcher.BeginInvoke(RefreshAudioSwitchRows);

    private void ControllerOnAudioSwitchBindingsChanged() =>
        Dispatcher.BeginInvoke(RefreshAudioSwitchRows);

    private void ControllerOnApplicationLaunchBindingsChanged() =>
        Dispatcher.BeginInvoke(RefreshApplicationLaunchRows);

    private void ControllerOnHardwareBrightnessChanged() =>
        Dispatcher.BeginInvoke(RefreshHardwareBrightnessFields);

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _controller.StateChanged -= ControllerOnStateChanged;
        _controller.MappingsChanged -= RefreshMappings;
        _controller.MonitorsChanged -= ControllerOnMonitorsChanged;
        _controller.AudioDevicesChanged -= ControllerOnAudioDevicesChanged;
        _controller.AudioSwitchBindingsChanged -= ControllerOnAudioSwitchBindingsChanged;
        _controller.ApplicationLaunchBindingsChanged -= ControllerOnApplicationLaunchBindingsChanged;
        _controller.HardwareBrightnessChanged -= ControllerOnHardwareBrightnessChanged;
        _controller.DiagnosticReceived -= AddDiagnostic;
    }

    private void RefreshMonitors(IReadOnlyList<MonitorSnapshot> monitors)
    {
        _monitorRows.Clear();
        foreach (var monitor in monitors)
        {
            _monitorRows.Add(new MonitorRow(
                monitor.Id,
                LocalizeMonitorName(monitor.Name),
                monitor.Backend switch { MonitorBackend.Wmi => "WMI", MonitorBackend.DdcCi => "DDC/CI", _ => Localization.Get("State.Unsupported") },
                monitor.Brightness?.ToString() ?? "—",
                LocalizeMonitorStatus(monitor.Status),
                monitor.IsControllable,
                _controller.IsMonitorEnabled(monitor.Id)));
        }
    }

    private static string LocalizeMonitorName(string name)
    {
        if (name == "内置显示器") return Localization.Get("Monitor.Internal");
        if (name == "显示器") return Localization.Get("Monitor.Generic");
        const string prefix = "外接显示器 ";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            ? Localization.Get("Monitor.External", name[prefix.Length..])
            : name;
    }

    private static string LocalizeMonitorStatus(string status)
    {
        return status switch
        {
            "等待写入" => Localization.Get("Monitor.Pending"),
            "可控制" => Localization.Get("Monitor.Controllable"),
            "写入失败" => Localization.Get("Monitor.WriteFailed"),
            "DDC/CI 亮度不可用" => Localization.Get("Monitor.DdcUnavailable"),
            _ when status.StartsWith("WMI 不可用：", StringComparison.Ordinal) =>
                Localization.Get("Monitor.WmiUnavailable", status[8..]),
            _ => status
        };
    }

    private void RefreshOutputRows()
    {
        _updatingOutputRows = true;
        try
        {
            _outputRows.Clear();
            var monitors = _controller.Monitors.Select(monitor => new MonitorOption(monitor.Id, LocalizeMonitorName(monitor.Name))).ToArray();
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
                    device.IsDefault ? Localization.Get("State.CurrentDefaultSuffix", device.Name) : device.Name)).ToList();
                if (!string.IsNullOrWhiteSpace(binding.DeviceId) &&
                    options.All(option => !string.Equals(option.Id, binding.DeviceId, StringComparison.OrdinalIgnoreCase)))
                {
                    options.Add(new AudioDeviceOption(binding.DeviceId, Localization.Get("State.SavedDeviceUnavailable")));
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
            _showOverlay(new OverlayMessage(Localization.Get("Overlay.WaitingInput"),
                Localization.Get("Overlay.UseAction", row.ActionName)));
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
        _showOverlay(new OverlayMessage(Localization.Get("Overlay.WaitingInput"),
            Localization.Get("Overlay.PressAudioSwitch")));
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
            Title = Localization.Get("Dialog.ChooseApplication"),
            Filter = Localization.Get("Dialog.ApplicationFilter"),
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
        _showOverlay(new OverlayMessage(Localization.Get("Overlay.WaitingInput"),
            Localization.Get("Overlay.PressApplicationLaunch")));
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

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedValue is not string language || language == _controller.Settings.Language) return;
        _controller.UpdateLanguage(language);
        RefreshLocalizedRows();
        RefreshAll();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await _controller.RefreshMonitorsAsync();
        RefreshAll();
    }

    private void SaveStepsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(VolumeStepBox.Text, out var volume) || !int.TryParse(BrightnessStepBox.Text, out var brightness))
        {
            MessageBox.Show(Localization.Get("Validation.Step"), Localization.Get("Group.AdjustmentSteps"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(Localization.Get("Validation.HardwareBrightness"), Localization.Get("Tab.Output"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _controller.UpdateHardwareBrightness(backlight, lcd, led, step);
    }

    private sealed class MappingRow : NotifyBase
    {
        private string _controlId = Localization.Get("State.Unbound");
        public MappingRow(AppAction action, string actionName) { Action = action; ActionName = actionName; }
        public AppAction Action { get; }
        public string ActionName { get; }
        public string ControlId { get => _controlId; set => SetField(ref _controlId, value); }
    }

    private sealed record MonitorOption(string Id, string Name);
    private sealed record LanguageOption(string Code, string Name);
    private sealed record SourceOption(OutputSourceKind Kind, string Label);
    private sealed record AudioDeviceOption(string Id, string Name);
    private sealed record LedOption(OutputTargetKind? Target, string Name);

    private sealed class ApplicationLaunchRow
    {
        public ApplicationLaunchRow(ApplicationLaunchBinding binding)
        {
            BindingId = binding.BindingId;
            ControlId = string.IsNullOrWhiteSpace(binding.ControlId) ? Localization.Get("State.Unbound") : binding.ControlId;
            ExecutablePath = binding.ExecutablePath;
            ApplicationName = string.IsNullOrWhiteSpace(binding.ExecutablePath)
                ? Localization.Get("State.NoApplication")
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
            ControlId = string.IsNullOrWhiteSpace(binding.ControlId) ? Localization.Get("State.Unbound") : binding.ControlId;
            _selectedDeviceId = string.IsNullOrWhiteSpace(binding.DeviceId) ? null : binding.DeviceId;
            DeviceOptions = new ObservableCollection<AudioDeviceOption>(devices);
            LedOptions = new ObservableCollection<LedOption>(
            [
                new(null, Localization.Get("State.NoLinkedLed")),
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
                ? [new(OutputSourceKind.Blank, Localization.Get("Source.Blank")), new(OutputSourceKind.MasterVolume, Localization.Get("Source.MasterVolume")), new(OutputSourceKind.MonitorBrightness, Localization.Get("Source.MonitorBrightness")),
                   new(OutputSourceKind.FcuLcdBrightness, Localization.Get("Source.FcuLcdBrightness")), new(OutputSourceKind.FcuBacklightBrightness, Localization.Get("Source.FcuBacklightBrightness"))]
                : [new(OutputSourceKind.ConstantOff, Localization.Get("Source.ConstantOff")), new(OutputSourceKind.ConstantOn, Localization.Get("Source.ConstantOn")), new(OutputSourceKind.Muted, Localization.Get("Source.Muted")),
                   new(OutputSourceKind.AppActive, Localization.Get("Source.AppActive")), new(OutputSourceKind.Yielded, Localization.Get("Source.Yielded")), new(OutputSourceKind.DeviceError, Localization.Get("Source.DeviceError"))]);
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
        OutputTargetKind.Speed => Localization.Get("Output.Speed"),
        OutputTargetKind.Heading => Localization.Get("Output.Heading"),
        OutputTargetKind.Altitude => Localization.Get("Output.Altitude"),
        OutputTargetKind.VerticalSpeed => Localization.Get("Output.VerticalSpeed"),
        OutputTargetKind.LocLed => "LOC LED",
        OutputTargetKind.Ap1Led => "AP1 LED",
        OutputTargetKind.Ap2Led => "AP2 LED",
        OutputTargetKind.AthrLed => "A/THR LED",
        OutputTargetKind.ExpedLed => "EXPED LED",
        OutputTargetKind.ApprLed => "APPR LED",
        _ => target.ToString()
    };
}
