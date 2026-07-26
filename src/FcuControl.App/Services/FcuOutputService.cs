using FcuControl.Core;

namespace FcuControl.App.Services;

public sealed class FcuOutputService
{
    private static readonly OutputTargetKind[] NumericTargets =
    [
        OutputTargetKind.Speed,
        OutputTargetKind.Heading,
        OutputTargetKind.Altitude,
        OutputTargetKind.VerticalSpeed
    ];

    private static readonly IReadOnlyDictionary<OutputTargetKind, byte> LedChannels = new Dictionary<OutputTargetKind, byte>
    {
        [OutputTargetKind.LocLed] = 0x03,
        [OutputTargetKind.Ap1Led] = 0x05,
        [OutputTargetKind.Ap2Led] = 0x07,
        [OutputTargetKind.AthrLed] = 0x09,
        [OutputTargetKind.ExpedLed] = 0x0B,
        [OutputTargetKind.ApprLed] = 0x0D
    };

    private readonly HidFcuService _hid;
    private readonly Action<string>? _diagnostic;
    private readonly FcuOutputProtocol _protocol = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int?[]? _lastDisplayValues;
    private readonly Dictionary<OutputTargetKind, bool> _lastLeds = [];
    private bool _brightnessInitialized;

    public FcuOutputService(HidFcuService hid, Action<string>? diagnostic = null)
    {
        _hid = hid;
        _diagnostic = diagnostic;
    }

    public void ResetCache()
    {
        _lastDisplayValues = null;
        _lastLeds.Clear();
        _brightnessInitialized = false;
    }

    public async Task UpdateAsync(
        AppSettings settings,
        IReadOnlyList<MonitorSnapshot> monitors,
        int volume,
        bool muted,
        string? defaultAudioDeviceId,
        RuntimeMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!_hid.IsConnected || mode is RuntimeMode.Yielded or RuntimeMode.ManualPaused)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_brightnessInitialized)
            {
                await WriteBrightnessAsync(settings, cancellationToken).ConfigureAwait(false);
                _brightnessInitialized = true;
            }

            var numeric = new Dictionary<OutputTargetKind, int?>();
            foreach (var target in NumericTargets)
            {
                var binding = settings.OutputBindings.FirstOrDefault(item => item.Target == target);
                numeric[target] = ResolveNumeric(binding, settings, monitors, volume);
            }

            var displayValues = NumericTargets.Select(target => numeric[target]).ToArray();
            if (_lastDisplayValues is null || !_lastDisplayValues.AsSpan().SequenceEqual(displayValues))
            {
                var messages = _protocol.BuildDisplayMessages(numeric);
                var sent = true;
                for (var index = 0; index < messages.Count; index++)
                {
                    var result = await _hid.WriteAsync(messages[index], cancellationToken).ConfigureAwait(false);
                    if (!result) _diagnostic?.Invoke($"LCD HID {index + 1}/{messages.Count} FAIL");
                    sent &= result;
                }
                if (!sent)
                {
                    _diagnostic?.Invoke($"LCD 目标 SPD={Format(displayValues[0])} HDG={Format(displayValues[1])} " +
                                        $"ALT={Format(displayValues[2])} VS={Format(displayValues[3])} 发送失败");
                }
                if (sent) _lastDisplayValues = displayValues;
            }

            foreach (var led in LedChannels)
            {
                var binding = settings.OutputBindings.FirstOrDefault(item => item.Target == led.Key);
                var audioSwitchOwnsLed = settings.AudioDeviceSwitchBindings.Any(item => item.LedTarget == led.Key);
                var state = audioSwitchOwnsLed
                    ? AudioSwitchBindingRegistry.IsLedActive(settings.AudioDeviceSwitchBindings, led.Key, defaultAudioDeviceId)
                    : ResolveBoolean(binding, muted, mode);
                if (_lastLeds.TryGetValue(led.Key, out var previous) && previous == state) continue;
                var sent = await _hid.WriteAsync(FcuOutputProtocol.BuildLightMessage(led.Value, state), cancellationToken).ConfigureAwait(false);
                if (sent) _lastLeds[led.Key] = state;
                else _lastLeds.Remove(led.Key);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ShutdownHardwareAsync()
    {
        if (!_hid.IsConnected) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            var blank = new Dictionary<OutputTargetKind, int?>
            {
                [OutputTargetKind.Speed] = null,
                [OutputTargetKind.Heading] = null,
                [OutputTargetKind.Altitude] = null,
                [OutputTargetKind.VerticalSpeed] = null
            };
            foreach (var message in _protocol.BuildDisplayMessages(blank))
            {
                await _hid.WriteAsync(message, timeout.Token).ConfigureAwait(false);
            }
            foreach (var channel in LedChannels.Values)
            {
                await _hid.WriteAsync(FcuOutputProtocol.BuildLightMessage(channel, false), timeout.Token).ConfigureAwait(false);
            }
            foreach (var channel in new byte[] { 0x00, 0x1E, 0x01, 0x11 })
            {
                await _hid.WriteAsync(FcuOutputProtocol.BuildBrightnessMessage(channel, 0), timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Handoff must not wait on a device that is already unavailable.
        }
        finally
        {
            if (_gate.CurrentCount == 0) _gate.Release();
            ResetCache();
        }
    }

    private async Task WriteBrightnessAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _hid.WriteAsync(FcuOutputProtocol.BuildBrightnessMessage(0x00, settings.BacklightBrightness), cancellationToken).ConfigureAwait(false);
        await _hid.WriteAsync(FcuOutputProtocol.BuildBrightnessMessage(0x1E, settings.BacklightBrightness), cancellationToken).ConfigureAwait(false);
        await _hid.WriteAsync(FcuOutputProtocol.BuildBrightnessMessage(0x01, settings.LcdBrightness), cancellationToken).ConfigureAwait(false);
        await _hid.WriteAsync(FcuOutputProtocol.BuildBrightnessMessage(0x11, settings.LedBrightness), cancellationToken).ConfigureAwait(false);
    }

    private static int? ResolveNumeric(
        OutputBinding? binding,
        AppSettings settings,
        IReadOnlyList<MonitorSnapshot> monitors,
        int volume)
    {
        return binding?.Source switch
        {
            OutputSourceKind.MasterVolume => volume,
            OutputSourceKind.MonitorBrightness => monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.Id, binding.MonitorId, StringComparison.OrdinalIgnoreCase))?.Brightness ?? -1,
            OutputSourceKind.FcuLcdBrightness => settings.LcdBrightness,
            OutputSourceKind.FcuBacklightBrightness => settings.BacklightBrightness,
            _ => null
        };
    }

    private static bool ResolveBoolean(OutputBinding? binding, bool muted, RuntimeMode mode)
    {
        return binding?.Source switch
        {
            OutputSourceKind.Muted => muted,
            OutputSourceKind.AppActive => mode == RuntimeMode.Active,
            OutputSourceKind.Yielded => mode == RuntimeMode.Yielded,
            OutputSourceKind.DeviceError => mode == RuntimeMode.DeviceError,
            OutputSourceKind.ConstantOn => true,
            _ => false
        };
    }

    private static string Format(int? value) => value.HasValue ? value.Value.ToString() : "空";
}
