using System.Runtime.InteropServices;
using FcuControl.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace FcuControl.App.Services;

public sealed class AudioService : IMMNotificationClient, IDisposable
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private IReadOnlyList<AudioDeviceSnapshot> _devices = [];
    private bool _disposed;
    private int _refreshQueued;
    private int _volumeNotificationQueued;
    private int _pendingNotificationVolume;
    private int _pendingNotificationMuted;

    public AudioService()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
        Refresh(raiseEvents: false);
    }

    public event Action<int, bool>? Changed;
    public event Action<IReadOnlyList<AudioDeviceSnapshot>>? DevicesChanged;

    public IReadOnlyList<AudioDeviceSnapshot> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices.ToArray();
            }
        }
    }

    public string? DefaultDeviceId
    {
        get
        {
            lock (_gate)
            {
                EnsureDeviceLocked();
                return SafeDeviceId(_device);
            }
        }
    }

    public string DefaultDeviceName
    {
        get
        {
            lock (_gate)
            {
                EnsureDeviceLocked();
                return _device is null ? Localization.Get("Audio.NoOutputDevice") : SafeName(_device);
            }
        }
    }

    public (int Volume, bool Muted) Snapshot
    {
        get
        {
            lock (_gate)
            {
                EnsureDeviceLocked();
                return SnapshotLocked();
            }
        }
    }

    public (int Volume, bool Muted) Adjust(int delta)
    {
        (int Volume, bool Muted) result;
        lock (_gate)
        {
            EnsureDeviceLocked();
            if (_device is null)
            {
                throw new InvalidOperationException(Localization.Get("Audio.NoDefaultOutput"));
            }

            var current = (int)Math.Round(_device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            var target = Math.Clamp(current + delta, 0, 100);
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = target / 100f;
            result = (target, _device.AudioEndpointVolume.Mute);
        }

        Changed?.Invoke(result.Volume, result.Muted);
        return result;
    }

    public (int Volume, bool Muted) ToggleMute()
    {
        (int Volume, bool Muted) result;
        lock (_gate)
        {
            EnsureDeviceLocked();
            if (_device is null)
            {
                throw new InvalidOperationException(Localization.Get("Audio.NoDefaultOutput"));
            }

            _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute;
            result = SnapshotLocked();
        }

        Changed?.Invoke(result.Volume, result.Muted);
        return result;
    }

    public AudioDeviceSnapshot SetDefaultOutputDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new InvalidOperationException(Localization.Get("Audio.SelectTarget"));
        }

        AudioDeviceSnapshot target;
        lock (_gate)
        {
            target = _devices.FirstOrDefault(device =>
                         string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException(Localization.Get("Audio.TargetUnavailable"));
        }

        AudioEndpointPolicy.SetDefaultEndpoint(target.Id);
        Refresh(raiseEvents: true);

        if (!string.Equals(DefaultDeviceId, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Localization.Get("Audio.SwitchFailed", target.Name));
        }

        return target with { IsDefault = true };
    }

    public void RefreshDevices() => Refresh(raiseEvents: true);

    private void Refresh(bool raiseEvents)
    {
        IReadOnlyList<AudioDeviceSnapshot> devices;
        (int Volume, bool Muted) snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            CloseDefaultDeviceLocked();
            devices = EnumerateDevicesLocked();
            OpenDefaultDeviceLocked();
            var defaultId = SafeDeviceId(_device);
            devices = devices.Select(device => device with
            {
                IsDefault = string.Equals(device.Id, defaultId, StringComparison.OrdinalIgnoreCase)
            }).ToArray();
            _devices = devices;
            snapshot = SnapshotLocked();
        }

        if (!raiseEvents) return;
        DevicesChanged?.Invoke(devices);
        Changed?.Invoke(snapshot.Volume, snapshot.Muted);
    }

    private IReadOnlyList<AudioDeviceSnapshot> EnumerateDevicesLocked()
    {
        var result = new List<AudioDeviceSnapshot>();
        try
        {
            var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var endpoint in endpoints)
            {
                using (endpoint)
                {
                    result.Add(new AudioDeviceSnapshot(endpoint.ID, SafeName(endpoint), false));
                }
            }
        }
        catch
        {
            // Audio devices can briefly disappear while Windows changes endpoints.
        }

        return result.OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void EnsureDeviceLocked()
    {
        try
        {
            if (_device is not null && _device.State == DeviceState.Active) return;
        }
        catch
        {
            // Reopen an endpoint whose COM identity became unavailable.
        }

        CloseDefaultDeviceLocked();
        OpenDefaultDeviceLocked();
        var defaultId = SafeDeviceId(_device);
        _devices = _devices.Select(device => device with
        {
            IsDefault = string.Equals(device.Id, defaultId, StringComparison.OrdinalIgnoreCase)
        }).ToArray();
    }

    private void CloseDefaultDeviceLocked()
    {
        if (_device is not null)
        {
            try { _device.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolumeOnVolumeNotification; }
            catch { }
            try { _device.Dispose(); }
            catch { }
            _device = null;
        }
    }

    private void OpenDefaultDeviceLocked()
    {
        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += AudioEndpointVolumeOnVolumeNotification;
        }
        catch
        {
            // A default output may not exist temporarily during device switching.
        }
    }

    private (int Volume, bool Muted) SnapshotLocked()
    {
        if (_device is null) return (0, false);
        return ((int)Math.Round(_device.AudioEndpointVolume.MasterVolumeLevelScalar * 100),
            _device.AudioEndpointVolume.Mute);
    }

    private static string SafeName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch { return device.ID; }
    }

    private static string? SafeDeviceId(MMDevice? device)
    {
        if (device is null) return null;
        try { return device.ID; }
        catch { return null; }
    }

    private void AudioEndpointVolumeOnVolumeNotification(AudioVolumeNotificationData data)
    {
        Volatile.Write(ref _pendingNotificationVolume, (int)Math.Round(data.MasterVolume * 100));
        Volatile.Write(ref _pendingNotificationMuted, data.Muted ? 1 : 0);
        if (_disposed || Interlocked.Exchange(ref _volumeNotificationQueued, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Never re-enter MMDevice while its COM notification callback is on
                // the stack. Doing so can dispose the endpoint inside its own callback
                // and deadlock both the callback thread and the WPF dispatcher.
                await Task.Delay(25).ConfigureAwait(false);
                Interlocked.Exchange(ref _volumeNotificationQueued, 0);
                if (_disposed) return;
                Changed?.Invoke(
                    Volatile.Read(ref _pendingNotificationVolume),
                    Volatile.Read(ref _pendingNotificationMuted) != 0);
            }
            catch
            {
                Interlocked.Exchange(ref _volumeNotificationQueued, 0);
            }
        });
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => QueueRefresh();
    public void OnDeviceAdded(string pwstrDeviceId) => QueueRefresh();
    public void OnDeviceRemoved(string deviceId) => QueueRefresh();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
        {
            QueueRefresh();
        }
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Setting the default endpoint emits a burst of property notifications.
        // Refreshing from this callback can recursively create another burst; device
        // add/remove/state/default callbacks and the manual refresh button cover the
        // changes that matter to this application.
    }

    private void QueueRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // IMMNotificationClient callbacks must return promptly. Waiting until
                // the callback unwinds also avoids re-entering MMDeviceEnumerator while
                // Windows is still committing the endpoint change.
                await Task.Delay(100).ConfigureAwait(false);
                Interlocked.Exchange(ref _refreshQueued, 0);
                Refresh(raiseEvents: true);
            }
            catch
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                // A later device notification or manual refresh will retry.
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _enumerator.UnregisterEndpointNotificationCallback(this);
        lock (_gate)
        {
            _disposed = true;
            CloseDefaultDeviceLocked();
        }
        _enumerator.Dispose();
    }
}

internal static class AudioEndpointPolicy
{
    public static void SetDefaultEndpoint(string deviceId)
    {
        var policy = (IPolicyConfig)Activator.CreateInstance(typeof(PolicyConfigClient))!;
        try
        {
            var firstFailure = 0;
            foreach (var role in Enum.GetValues<PolicyRole>())
            {
                var result = policy.SetDefaultEndpoint(deviceId, role);
                if (result < 0 && firstFailure == 0) firstFailure = result;
            }

            if (firstFailure < 0) Marshal.ThrowExceptionForHR(firstFailure);
        }
        finally
        {
            Marshal.FinalReleaseComObject(policy);
        }
    }

    private enum PolicyRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private sealed class PolicyConfigClient;

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, nint format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, nint period, nint minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int store, nint key, nint value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int store, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PolicyRole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
