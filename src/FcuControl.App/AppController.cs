using System.Collections.ObjectModel;
using System.Windows.Threading;
using FcuControl.App.Infrastructure;
using FcuControl.App.Services;
using FcuControl.Core;

namespace FcuControl.App;

public sealed record OverlayMessage(string Title, string Detail, int? Percent = null, bool IsMuted = false);

public sealed class AppController : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _settingsStore;
    private readonly BindingRegistry _bindings;
    private readonly AudioSwitchBindingRegistry _audioSwitchBindings;
    private readonly ApplicationLaunchBindingRegistry _applicationLaunchBindings;
    private readonly HidFcuService _hid = new();
    private readonly AudioService _audio = new();
    private readonly BrightnessService _brightness = new();
    private readonly ApplicationLauncher _applicationLauncher = new();
    private readonly ConflictProcessMonitor _conflictMonitor;
    private readonly ConflictStateMachine _stateMachine = new();
    private readonly FcuOutputService _output;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _outputUpdateGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Task? _lifecycleLoop;
    private AppAction? _learningAction;
    private string? _learningAudioSwitchBindingId;
    private string? _learningApplicationLaunchBindingId;
    private bool _conflictPresent;
    private IReadOnlyList<string> _conflictMatches = [];
    private bool _deviceError;
    private bool _started;
    private int _outputUpdatePending;

    public AppController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        var baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FcuControl");
        Logger = new FileLogger(baseDirectory);
        _settingsStore = new SettingsStore(Logger);
        Settings = _settingsStore.Load();
        Localization.SetLanguage(Settings.Language);
        StatusText = Localization.Get("Status.Starting");
        _bindings = new BindingRegistry(Settings.InputBindings);
        _audioSwitchBindings = new AudioSwitchBindingRegistry(Settings.AudioDeviceSwitchBindings);
        _applicationLaunchBindings = new ApplicationLaunchBindingRegistry(Settings.ApplicationLaunchBindings);
        _conflictMonitor = new ConflictProcessMonitor(() => Settings.ConflictProcesses);
        _output = new FcuOutputService(_hid, Logger.Info);

        _hid.ControlActivated += HidOnControlActivated;
        _hid.ConnectionChanged += HidOnConnectionChanged;
        _hid.Diagnostic += message =>
        {
            // RAW/DROP reports are much faster than the UI can render while a knob
            // is spun. Accepted inputs are already published as ACT below.
            if (!message.StartsWith("OUT FAIL", StringComparison.Ordinal)) return;
            Logger.Warn(message);
            _dispatcher.BeginInvoke(() => DiagnosticReceived?.Invoke(message));
        };
        _audio.Changed += AudioOnChanged;
        _audio.DevicesChanged += AudioDevicesOnChanged;
        _brightness.Changed += monitors => _dispatcher.BeginInvoke(() =>
        {
            MonitorsChanged?.Invoke(monitors);
            _ = UpdateHardwareOutputAsync();
        });
        _conflictMonitor.Changed += ConflictMonitorOnChanged;
    }

    public FileLogger Logger { get; }
    public AppSettings Settings { get; }
    public RuntimeMode Mode { get; private set; } = RuntimeMode.WaitingForDevice;
    public string StatusText { get; private set; } = string.Empty;
    public IReadOnlyList<string> ConflictMatches => _conflictMatches;
    public bool HidConnected => _hid.IsConnected;
    public IReadOnlyList<MonitorSnapshot> Monitors => _brightness.Snapshots;
    public (int Volume, bool Muted) AudioSnapshot => _audio.Snapshot;
    public IReadOnlyList<AudioDeviceSnapshot> AudioDevices => _audio.Devices;
    public string? DefaultAudioDeviceId => _audio.DefaultDeviceId;
    public string DefaultAudioDeviceName => _audio.DefaultDeviceName;
    public AppAction? LearningAction => _learningAction;

    public event Action? StateChanged;
    public event Action? MappingsChanged;
    public event Action<IReadOnlyList<MonitorSnapshot>>? MonitorsChanged;
    public event Action<IReadOnlyList<AudioDeviceSnapshot>>? AudioDevicesChanged;
    public event Action? AudioSwitchBindingsChanged;
    public event Action? ApplicationLaunchBindingsChanged;
    public event Action? HardwareBrightnessChanged;
    public event Action<string>? DiagnosticReceived;
    public event Action<OverlayMessage>? OverlayRequested;

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        Logger.Info("FCU 控制器启动");

        var firstMonitorSetup = Settings.BrightnessTargets.Count == 0;
        await _brightness.RefreshAsync().ConfigureAwait(false);
        ReconcileMonitorSettings(firstMonitorSetup);
        SaveSettings();

        var scan = _conflictMonitor.ScanNow();
        _conflictPresent = scan.Present;
        _conflictMatches = scan.Matches;
        _conflictMonitor.Start();
        _lifecycleLoop = Task.Run(() => LifecycleLoopAsync(_cancellation.Token));
        await PublishStateAsync().ConfigureAwait(false);
    }

    public void BeginLearning(AppAction action)
    {
        _learningAudioSwitchBindingId = null;
        _learningApplicationLaunchBindingId = null;
        _learningAction = action;
        StatusText = Localization.Get("Learning.Action", ActionNames.Get(action));
        StateChanged?.Invoke();
    }

    public void CancelLearning()
    {
        _learningAction = null;
        _learningAudioSwitchBindingId = null;
        _learningApplicationLaunchBindingId = null;
        StateChanged?.Invoke();
    }

    public void ClearBinding(AppAction action)
    {
        if (_bindings.Clear(action))
        {
            SaveSettings();
            MappingsChanged?.Invoke();
        }
    }

    public string? GetBinding(AppAction action) =>
        Settings.InputBindings.FirstOrDefault(binding => binding.Action == action)?.ControlId;

    public string AddAudioSwitchBinding()
    {
        var binding = _audioSwitchBindings.Add();
        SaveSettings();
        AudioSwitchBindingsChanged?.Invoke();
        return binding.BindingId;
    }

    public void RemoveAudioSwitchBinding(string bindingId)
    {
        if (!_audioSwitchBindings.Remove(bindingId)) return;
        if (string.Equals(_learningAudioSwitchBindingId, bindingId, StringComparison.OrdinalIgnoreCase))
        {
            _learningAudioSwitchBindingId = null;
        }
        SaveSettings();
        _output.ResetCache();
        AudioSwitchBindingsChanged?.Invoke();
        _ = UpdateHardwareOutputAsync();
    }

    public void UpdateAudioSwitchBinding(string bindingId, string? deviceId, OutputTargetKind? ledTarget)
    {
        // WPF ComboBox raises SelectionChanged while a row is being created. If the
        // value did not actually change, rebuilding the grid here would create an
        // endless SelectionChanged -> rebuild cycle on the UI dispatcher.
        if (!_audioSwitchBindings.Update(bindingId, deviceId, ledTarget)) return;
        SaveSettings();
        _output.ResetCache();
        AudioSwitchBindingsChanged?.Invoke();
        _ = UpdateHardwareOutputAsync();
    }

    public void BeginAudioSwitchLearning(string bindingId)
    {
        var binding = _audioSwitchBindings.Find(bindingId) ?? throw new InvalidOperationException(Localization.Get("Error.AudioBindingNotFound"));
        _learningAction = null;
        _learningApplicationLaunchBindingId = null;
        _learningAudioSwitchBindingId = binding.BindingId;
        var device = _audio.Devices.FirstOrDefault(item =>
            string.Equals(item.Id, binding.DeviceId, StringComparison.OrdinalIgnoreCase));
        StatusText = Localization.Get("Learning.AudioSwitch", device?.Name ?? Localization.Get("State.SelectedAudioDevice"));
        StateChanged?.Invoke();
    }

    public void RefreshAudioDevices() => _audio.RefreshDevices();

    public string AddApplicationLaunchBinding()
    {
        var binding = _applicationLaunchBindings.Add();
        SaveSettings();
        ApplicationLaunchBindingsChanged?.Invoke();
        return binding.BindingId;
    }

    public void RemoveApplicationLaunchBinding(string bindingId)
    {
        if (!_applicationLaunchBindings.Remove(bindingId)) return;
        if (string.Equals(_learningApplicationLaunchBindingId, bindingId, StringComparison.OrdinalIgnoreCase))
        {
            _learningApplicationLaunchBindingId = null;
        }
        SaveSettings();
        ApplicationLaunchBindingsChanged?.Invoke();
    }

    public void UpdateApplicationLaunchBinding(string bindingId, string? executablePath)
    {
        _applicationLaunchBindings.UpdatePath(bindingId, executablePath);
        SaveSettings();
        ApplicationLaunchBindingsChanged?.Invoke();
    }

    public void BeginApplicationLaunchLearning(string bindingId)
    {
        var binding = _applicationLaunchBindings.Find(bindingId) ?? throw new InvalidOperationException(Localization.Get("Error.ApplicationBindingNotFound"));
        _learningAction = null;
        _learningAudioSwitchBindingId = null;
        _learningApplicationLaunchBindingId = binding.BindingId;
        var name = string.IsNullOrWhiteSpace(binding.ExecutablePath)
            ? Localization.Get("State.SelectedApplication")
            : Path.GetFileNameWithoutExtension(binding.ExecutablePath);
        StatusText = Localization.Get("Learning.ApplicationLaunch", name);
        StateChanged?.Invoke();
    }

    public void SetManualPaused(bool paused)
    {
        Settings.ManualPaused = paused;
        SaveSettings();
        Logger.Info(paused ? "用户手动暂停" : "用户恢复控制");
        _ = EvaluateLifecycleAsync();
    }

    public void ToggleManualPause() => SetManualPaused(!Settings.ManualPaused);

    public void UpdateLanguage(string language)
    {
        Settings.Language = language == Localization.English ? Localization.English : Localization.Chinese;
        Localization.SetLanguage(Settings.Language);
        SaveSettings();
        StatusText = BuildLearningStatus() ?? BuildStatusText();
        StateChanged?.Invoke();
    }

    public async Task RefreshMonitorsAsync()
    {
        await _brightness.RefreshAsync().ConfigureAwait(false);
        ReconcileMonitorSettings(firstSetup: false);
        SaveSettings();
    }

    public void SetMonitorEnabled(string monitorId, bool enabled)
    {
        var setting = Settings.BrightnessTargets.FirstOrDefault(item =>
            string.Equals(item.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase));
        if (setting is null)
        {
            setting = new BrightnessTargetSetting
            {
                MonitorId = monitorId,
                Order = Settings.BrightnessTargets.Count,
                Enabled = enabled
            };
            Settings.BrightnessTargets.Add(setting);
        }
        else
        {
            setting.Enabled = enabled;
        }
        SaveSettings();
        _ = UpdateHardwareOutputAsync();
    }

    public bool IsMonitorEnabled(string monitorId) =>
        Settings.BrightnessTargets.FirstOrDefault(item =>
            string.Equals(item.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase))?.Enabled ?? false;

    public void UpdateSteps(int volumeStep, int brightnessStep)
    {
        Settings.VolumeStepPercent = Math.Clamp(volumeStep, 1, 20);
        Settings.BrightnessStepPercent = Math.Clamp(brightnessStep, 1, 20);
        SaveSettings();
    }

    public void UpdateConflictProcesses(IEnumerable<string> names)
    {
        Settings.ConflictProcesses = names
            .Select(ProcessName.Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SaveSettings();
    }

    public void UpdateOutputBinding(OutputTargetKind target, OutputSourceKind source, string? monitorId)
    {
        var binding = Settings.OutputBindings.First(item => item.Target == target);
        var normalizedMonitorId = source == OutputSourceKind.MonitorBrightness ? monitorId : null;
        if (binding.Source == source &&
            string.Equals(binding.MonitorId, normalizedMonitorId, StringComparison.OrdinalIgnoreCase)) return;
        binding.Source = source;
        binding.MonitorId = normalizedMonitorId;
        SaveSettings();
        _output.ResetCache();
        _ = UpdateHardwareOutputAsync();
    }

    public void UpdateHardwareBrightness(int backlight, int lcd, int led, int step)
    {
        Settings.BacklightBrightness = Math.Clamp(backlight, 0, 100);
        Settings.LcdBrightness = Math.Clamp(lcd, 0, 100);
        Settings.LedBrightness = Math.Clamp(led, 0, 100);
        Settings.HardwareBrightnessStepPercent = Math.Clamp(step, 1, 20);
        SaveSettings();
        _output.ResetCache();
        HardwareBrightnessChanged?.Invoke();
        _ = UpdateHardwareOutputAsync();
    }

    private async Task LifecycleLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await EvaluateLifecycleAsync().ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EvaluateLifecycleAsync()
    {
        if (!await _lifecycleGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
        var mode = _stateMachine.Evaluate(Settings.ManualPaused, _conflictPresent, _hid.IsConnected, _deviceError, DateTimeOffset.Now);
        var shouldRelease = mode is RuntimeMode.Yielded or RuntimeMode.ManualPaused;

        if (shouldRelease && _hid.IsConnected)
        {
            await _output.ShutdownHardwareAsync().ConfigureAwait(false);
            await _hid.DisconnectAsync(mode == RuntimeMode.Yielded ? "已让位给飞行软件" : "已手动暂停").ConfigureAwait(false);
        }
        else if (!shouldRelease && !_hid.IsConnected)
        {
            var connected = await _hid.ConnectAsync().ConfigureAwait(false);
            if (connected)
            {
                _deviceError = false;
                _output.ResetCache();
                mode = RuntimeMode.Active;
                Mode = RuntimeMode.Active;
                await UpdateHardwareOutputAsync().ConfigureAwait(false);
            }
        }

        Mode = _stateMachine.Evaluate(Settings.ManualPaused, _conflictPresent, _hid.IsConnected, _deviceError, DateTimeOffset.Now);
        StatusText = BuildStatusText();
        await PublishStateAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void HidOnControlActivated(HidControlActivation activation)
    {
        _dispatcher.BeginInvoke(async () =>
        {
            DiagnosticReceived?.Invoke($"ACT {activation.ControlId}");
            if (_learningAction.HasValue)
            {
                var action = _learningAction.Value;
                var clearedAudioBindings = false;
                foreach (var binding in Settings.AudioDeviceSwitchBindings.Where(binding =>
                             string.Equals(binding.ControlId, activation.ControlId, StringComparison.OrdinalIgnoreCase)))
                {
                    binding.ControlId = string.Empty;
                    clearedAudioBindings = true;
                }
                var clearedApplicationBindings = false;
                foreach (var binding in Settings.ApplicationLaunchBindings.Where(binding =>
                             string.Equals(binding.ControlId, activation.ControlId, StringComparison.OrdinalIgnoreCase)))
                {
                    binding.ControlId = string.Empty;
                    clearedApplicationBindings = true;
                }
                _bindings.Bind(activation.ControlId, activation.Trigger, action);
                _learningAction = null;
                SaveSettings();
                MappingsChanged?.Invoke();
                if (clearedAudioBindings) AudioSwitchBindingsChanged?.Invoke();
                if (clearedApplicationBindings) ApplicationLaunchBindingsChanged?.Invoke();
                OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.BindingComplete"),
                    $"{ActionNames.Get(action)} ← {activation.ControlId}"));
                return;
            }

            if (_learningAudioSwitchBindingId is not null)
            {
                var bindingId = _learningAudioSwitchBindingId;
                _learningAudioSwitchBindingId = null;
                _audioSwitchBindings.AssignControl(bindingId, activation.ControlId, activation.Trigger);
                var removedInputBinding = Settings.InputBindings.RemoveAll(binding =>
                    binding.Trigger == activation.Trigger &&
                    string.Equals(binding.ControlId, activation.ControlId, StringComparison.OrdinalIgnoreCase)) > 0;
                var clearedApplicationBindings = false;
                foreach (var binding in Settings.ApplicationLaunchBindings.Where(binding =>
                             string.Equals(binding.ControlId, activation.ControlId, StringComparison.OrdinalIgnoreCase)))
                {
                    binding.ControlId = string.Empty;
                    clearedApplicationBindings = true;
                }
                SaveSettings();
                if (removedInputBinding) MappingsChanged?.Invoke();
                if (clearedApplicationBindings) ApplicationLaunchBindingsChanged?.Invoke();
                AudioSwitchBindingsChanged?.Invoke();
                OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.BindingComplete"),
                    Localization.Get("Overlay.AudioSwitchBound", activation.ControlId)));
                return;
            }

            if (_learningApplicationLaunchBindingId is not null)
            {
                var bindingId = _learningApplicationLaunchBindingId;
                _learningApplicationLaunchBindingId = null;
                _applicationLaunchBindings.AssignControl(bindingId, activation.ControlId, activation.Trigger);
                var removedInputBinding = Settings.InputBindings.RemoveAll(binding =>
                    binding.Trigger == activation.Trigger &&
                    string.Equals(binding.ControlId, activation.ControlId, StringComparison.OrdinalIgnoreCase)) > 0;
                var clearedAudioBindings = false;
                foreach (var binding in Settings.AudioDeviceSwitchBindings.Where(binding =>
                             string.Equals(binding.ControlId, activation.ControlId, StringComparison.OrdinalIgnoreCase)))
                {
                    binding.ControlId = string.Empty;
                    clearedAudioBindings = true;
                }
                SaveSettings();
                if (removedInputBinding) MappingsChanged?.Invoke();
                if (clearedAudioBindings) AudioSwitchBindingsChanged?.Invoke();
                ApplicationLaunchBindingsChanged?.Invoke();
                OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.BindingComplete"),
                    Localization.Get("Overlay.ApplicationLaunchBound", activation.ControlId)));
                return;
            }

            if (Mode != RuntimeMode.Active) return;
            var audioSwitch = _audioSwitchBindings.Resolve(activation.ControlId, activation.Trigger);
            if (audioSwitch is not null)
            {
                await ExecuteAudioSwitchAsync(audioSwitch);
                return;
            }
            var applicationLaunch = _applicationLaunchBindings.Resolve(activation.ControlId, activation.Trigger);
            if (applicationLaunch is not null)
            {
                ExecuteApplicationLaunch(applicationLaunch);
                return;
            }
            var actionToRun = _bindings.Resolve(activation.ControlId, activation.Trigger);
            if (actionToRun.HasValue)
            {
                await ExecuteActionAsync(actionToRun.Value);
            }
        });
    }

    private void ExecuteApplicationLaunch(ApplicationLaunchBinding binding)
    {
        try
        {
            var name = _applicationLauncher.Launch(binding.ExecutablePath);
            Logger.Info($"已通过 FCU 启动软件：{binding.ExecutablePath}");
            OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.ApplicationLaunched"), name));
        }
        catch (Exception exception)
        {
            Logger.Error("启动软件失败", exception);
            OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.ApplicationLaunchFailed"), exception.Message));
        }
    }

    private async Task ExecuteAudioSwitchAsync(AudioDeviceSwitchBinding binding)
    {
        try
        {
            var target = _audio.SetDefaultOutputDevice(binding.DeviceId);
            Logger.Info($"默认音频输出已切换：{target.Name}");
            var audio = _audio.Snapshot;
            OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.AudioSwitched"), $"{target.Name} · {audio.Volume}%", audio.Volume, audio.Muted));
            await UpdateHardwareOutputAsync();
        }
        catch (Exception exception)
        {
            Logger.Error("切换默认音频输出失败", exception);
            OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.AudioSwitchFailed"), exception.Message));
        }
    }

    private async Task ExecuteActionAsync(AppAction action)
    {
        try
        {
            switch (action)
            {
                case AppAction.VolumeUp:
                {
                    var result = _audio.Adjust(Settings.VolumeStepPercent);
                    OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.SystemVolume"), result.Muted ? Localization.Get("State.Muted") : $"{result.Volume}%", result.Volume, result.Muted));
                    break;
                }
                case AppAction.VolumeDown:
                {
                    var result = _audio.Adjust(-Settings.VolumeStepPercent);
                    OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.SystemVolume"), result.Muted ? Localization.Get("State.Muted") : $"{result.Volume}%", result.Volume, result.Muted));
                    break;
                }
                case AppAction.ToggleMute:
                {
                    var result = _audio.ToggleMute();
                    OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.SystemVolume"), result.Muted ? Localization.Get("State.Muted") : $"{result.Volume}%", result.Volume, result.Muted));
                    break;
                }
                case AppAction.BrightnessUp:
                    AdjustBrightness(Settings.BrightnessStepPercent);
                    break;
                case AppAction.BrightnessDown:
                    AdjustBrightness(-Settings.BrightnessStepPercent);
                    break;
                case AppAction.FcuLcdBrightnessUp:
                    AdjustFcuHardwareBrightness(adjustLcd: true, Settings.HardwareBrightnessStepPercent);
                    break;
                case AppAction.FcuLcdBrightnessDown:
                    AdjustFcuHardwareBrightness(adjustLcd: true, -Settings.HardwareBrightnessStepPercent);
                    break;
                case AppAction.FcuBacklightBrightnessUp:
                    AdjustFcuHardwareBrightness(adjustLcd: false, Settings.HardwareBrightnessStepPercent);
                    break;
                case AppAction.FcuBacklightBrightnessDown:
                    AdjustFcuHardwareBrightness(adjustLcd: false, -Settings.HardwareBrightnessStepPercent);
                    break;
                case AppAction.ToggleManualPause:
                    ToggleManualPause();
                    break;
            }
            await UpdateHardwareOutputAsync();
        }
        catch (Exception exception)
        {
            Logger.Error($"执行动作 {action} 失败", exception);
            OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.ActionFailed"), exception.Message));
        }
    }

    private void AdjustBrightness(int delta)
    {
        var enabled = Settings.BrightnessTargets.Where(item => item.Enabled)
            .Select(item => item.MonitorId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = _brightness.AdjustSelected(enabled, delta);
        var detail = changes.Count == 0
            ? Localization.Get("Overlay.NoControllableMonitor")
            : string.Join("  ·  ", changes.Select(change => $"{change.MonitorName} {change.Target}%"));
        OverlayRequested?.Invoke(new OverlayMessage(Localization.Get("Overlay.MonitorBrightness"), detail,
            changes.Count == 1 ? changes[0].Target : null));
    }

    private void AdjustFcuHardwareBrightness(bool adjustLcd, int delta)
    {
        var current = adjustLcd ? Settings.LcdBrightness : Settings.BacklightBrightness;
        var target = ValueMath.AdjustPercent(current, delta);
        if (adjustLcd) Settings.LcdBrightness = target;
        else Settings.BacklightBrightness = target;
        SaveSettings();
        _output.ResetCache();
        HardwareBrightnessChanged?.Invoke();
        OverlayRequested?.Invoke(new OverlayMessage(
            Localization.Get(adjustLcd ? "Source.FcuLcdBrightness" : "Source.FcuBacklightBrightness"),
            $"{target}%",
            target));
    }

    private void AudioOnChanged(int volume, bool muted)
    {
        _ = UpdateHardwareOutputAsync();
        _dispatcher.BeginInvoke(() => StateChanged?.Invoke());
    }

    private void AudioDevicesOnChanged(IReadOnlyList<AudioDeviceSnapshot> devices)
    {
        _ = UpdateHardwareOutputAsync();
        _dispatcher.BeginInvoke(() =>
        {
            AudioDevicesChanged?.Invoke(devices);
            StateChanged?.Invoke();
        });
    }

    private void HidOnConnectionChanged(bool connected, string message)
    {
        Logger.Info(message);
        if (!connected && message.Contains("读取中断", StringComparison.OrdinalIgnoreCase))
        {
            _deviceError = true;
        }
        _dispatcher.BeginInvoke(() =>
        {
            StatusText = connected
                ? Localization.Get("State.Connected", _hid.DeviceName)
                : Localization.Get(_deviceError ? "State.DeviceError" : "State.Waiting");
            StateChanged?.Invoke();
        });
    }

    private void ConflictMonitorOnChanged(bool present, IReadOnlyList<string> matches)
    {
        _conflictPresent = present;
        _conflictMatches = matches;
        Logger.Info(present ? $"检测到让位程序：{string.Join(", ", matches)}" : "让位程序已退出，等待恢复");
        _ = EvaluateLifecycleAsync();
    }

    private async Task UpdateHardwareOutputAsync()
    {
        if (!_hid.IsConnected) return;
        Interlocked.Exchange(ref _outputUpdatePending, 1);
        if (!await _outputUpdateGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            do
            {
                Interlocked.Exchange(ref _outputUpdatePending, 0);
                if (!_hid.IsConnected) return;
                var audio = _audio.Snapshot;
                await _output.UpdateAsync(Settings, _brightness.Snapshots, audio.Volume, audio.Muted,
                        _audio.DefaultDeviceId, Mode, _cancellation.Token)
                    .ConfigureAwait(false);
            }
            while (Volatile.Read(ref _outputUpdatePending) != 0);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Logger.Error("更新 FCU 输出失败", exception);
        }
        finally
        {
            _outputUpdateGate.Release();
            if (Volatile.Read(ref _outputUpdatePending) != 0 && !_cancellation.IsCancellationRequested)
            {
                _ = UpdateHardwareOutputAsync();
            }
        }
    }

    private void ReconcileMonitorSettings(bool firstSetup)
    {
        var monitors = _brightness.Snapshots;
        foreach (var monitor in monitors)
        {
            if (Settings.BrightnessTargets.All(item => !string.Equals(item.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase)))
            {
                Settings.BrightnessTargets.Add(new BrightnessTargetSetting
                {
                    MonitorId = monitor.Id,
                    Enabled = monitor.IsControllable,
                    Order = Settings.BrightnessTargets.Count
                });
            }
        }

        if (!firstSetup) return;
        var selected = Settings.BrightnessTargets.Where(item => item.Enabled)
            .OrderBy(item => item.Order)
            .Select(item => item.MonitorId)
            .ToArray();
        if (selected.Length > 0)
        {
            var heading = Settings.OutputBindings.First(item => item.Target == OutputTargetKind.Heading);
            heading.Source = OutputSourceKind.MonitorBrightness;
            heading.MonitorId = selected[0];
        }
    }

    private string BuildStatusText()
    {
        return Mode switch
        {
            RuntimeMode.Active => Localization.Get("State.Connected", _hid.DeviceName),
            RuntimeMode.WaitingForDevice => Localization.Get("State.Waiting"),
            RuntimeMode.Yielded => _conflictMatches.Count > 0
                ? Localization.Get("State.Yielded", string.Join(", ", _conflictMatches))
                : Localization.Get("State.WaitingRelease"),
            RuntimeMode.ManualPaused => Localization.Get("State.Paused"),
            RuntimeMode.DeviceError => Localization.Get("State.DeviceError"),
            _ => Localization.Get("State.Unknown")
        };
    }

    private string? BuildLearningStatus()
    {
        if (_learningAction.HasValue)
        {
            return Localization.Get("Learning.Action", ActionNames.Get(_learningAction.Value));
        }
        if (_learningAudioSwitchBindingId is not null)
        {
            var binding = _audioSwitchBindings.Find(_learningAudioSwitchBindingId);
            var device = binding is null ? null : _audio.Devices.FirstOrDefault(item =>
                string.Equals(item.Id, binding.DeviceId, StringComparison.OrdinalIgnoreCase));
            return Localization.Get("Learning.AudioSwitch", device?.Name ?? Localization.Get("State.SelectedAudioDevice"));
        }
        if (_learningApplicationLaunchBindingId is not null)
        {
            var binding = _applicationLaunchBindings.Find(_learningApplicationLaunchBindingId);
            var name = string.IsNullOrWhiteSpace(binding?.ExecutablePath)
                ? Localization.Get("State.SelectedApplication")
                : Path.GetFileNameWithoutExtension(binding.ExecutablePath);
            return Localization.Get("Learning.ApplicationLaunch", name);
        }
        return null;
    }

    private Task PublishStateAsync()
    {
        return _dispatcher.InvokeAsync(() => StateChanged?.Invoke()).Task;
    }

    private void SaveSettings()
    {
        try { _settingsStore.Save(Settings); }
        catch (Exception exception) { Logger.Error("保存设置失败", exception); }
    }

    public async ValueTask DisposeAsync()
    {
        Logger.Info("FCU 控制器退出");
        _cancellation.Cancel();
        if (_lifecycleLoop is not null)
        {
            try { await _lifecycleLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        await _conflictMonitor.DisposeAsync();
        await _output.ShutdownHardwareAsync();
        await _hid.DisposeAsync();
        await _brightness.DisposeAsync();
        _audio.Dispose();
        _outputUpdateGate.Dispose();
        _lifecycleGate.Dispose();
        _cancellation.Dispose();
    }
}

public static class ActionNames
{
    public static string Get(AppAction action) => action switch
    {
        AppAction.VolumeUp => Localization.Get("Action.VolumeUp"),
        AppAction.VolumeDown => Localization.Get("Action.VolumeDown"),
        AppAction.ToggleMute => Localization.Get("Action.ToggleMute"),
        AppAction.BrightnessUp => Localization.Get("Action.BrightnessUp"),
        AppAction.BrightnessDown => Localization.Get("Action.BrightnessDown"),
        AppAction.FcuLcdBrightnessUp => Localization.Get("Action.FcuLcdBrightnessUp"),
        AppAction.FcuLcdBrightnessDown => Localization.Get("Action.FcuLcdBrightnessDown"),
        AppAction.FcuBacklightBrightnessUp => Localization.Get("Action.FcuBacklightBrightnessUp"),
        AppAction.FcuBacklightBrightnessDown => Localization.Get("Action.FcuBacklightBrightnessDown"),
        AppAction.ToggleManualPause => Localization.Get("Action.ToggleManualPause"),
        _ => action.ToString()
    };
}
