using System.Management;
using System.Runtime.InteropServices;
using FcuControl.Core;

namespace FcuControl.App.Services;

public sealed class BrightnessService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MonitorAccessor> _accessors = new(StringComparer.OrdinalIgnoreCase);
    private readonly CoalescingTargetQueue _writeQueue = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _writerLoop;

    public BrightnessService()
    {
        _writerLoop = Task.Run(() => WriterLoopAsync(_cancellation.Token));
    }

    public event Action<IReadOnlyList<MonitorSnapshot>>? Changed;

    public IReadOnlyList<MonitorSnapshot> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return _accessors.Values
                    .Select(accessor => accessor.Snapshot)
                    .OrderByDescending(snapshot => snapshot.IsInternal)
                    .ThenBy(snapshot => snapshot.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }
        }
    }

    public async Task RefreshAsync()
    {
        var discovered = await Task.Run(DiscoverMonitors).ConfigureAwait(false);
        List<MonitorAccessor> old;
        lock (_gate)
        {
            old = _accessors.Values.ToList();
            _accessors.Clear();
            foreach (var accessor in discovered)
            {
                _accessors[accessor.Snapshot.Id] = accessor;
            }
        }

        foreach (var accessor in old)
        {
            accessor.Dispose();
        }

        RaiseChanged();
    }

    public IReadOnlyList<BrightnessChange> AdjustSelected(ISet<string> enabledMonitorIds, int delta)
    {
        List<BrightnessChange> changes = [];
        lock (_gate)
        {
            foreach (var accessor in _accessors.Values)
            {
                var snapshot = accessor.Snapshot;
                if (!enabledMonitorIds.Contains(snapshot.Id) || !snapshot.IsControllable || !snapshot.Brightness.HasValue)
                {
                    continue;
                }

                var target = ValueMath.AdjustPercent(snapshot.Brightness.Value, delta);
                if (target == snapshot.Brightness.Value)
                {
                    continue;
                }

                changes.Add(new BrightnessChange(snapshot.Id, snapshot.Name, snapshot.Brightness.Value, target));
                accessor.Snapshot = snapshot with { Brightness = target, Status = "等待写入" };
                _writeQueue.Enqueue(snapshot.Id, target);
            }
        }

        if (changes.Count > 0)
        {
            RaiseChanged();
        }
        return changes;
    }

    public int? GetBrightness(string? monitorId)
    {
        if (string.IsNullOrWhiteSpace(monitorId)) return null;
        lock (_gate)
        {
            return _accessors.TryGetValue(monitorId, out var accessor) ? accessor.Snapshot.Brightness : null;
        }
    }

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        while (await _writeQueue.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ids = _writeQueue.DrainKeys();

            await Task.Delay(45, cancellationToken).ConfigureAwait(false);
            ids.UnionWith(_writeQueue.DrainKeys());

            foreach (var id in ids)
            {
                if (!_writeQueue.TryTakeLatest(id, out var target)) continue;
                bool success;
                lock (_gate)
                {
                    success = _accessors.TryGetValue(id, out var accessor) && accessor.TrySet(target);
                    if (accessor is not null)
                    {
                        accessor.Snapshot = accessor.Snapshot with
                        {
                            Brightness = success ? target : accessor.Snapshot.Brightness,
                            Status = success ? "可控制" : "写入失败"
                        };
                    }
                }
                RaiseChanged();
            }
        }
    }

    private List<MonitorAccessor> DiscoverMonitors()
    {
        var results = new List<MonitorAccessor>();
        var identities = ReadMonitorIdentities();
        var internalInstanceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var brightnessSearcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM WmiMonitorBrightness WHERE Active=True");
            foreach (ManagementObject brightnessObject in brightnessSearcher.Get())
            {
                var instanceName = Convert.ToString(brightnessObject["InstanceName"]) ?? string.Empty;
                var current = Convert.ToInt32(brightnessObject["CurrentBrightness"]);
                internalInstanceNames.Add(instanceName);
                var identity = identities.FirstOrDefault(item => InstanceMatches(item.InstanceName, instanceName));
                var id = identity?.StableId ?? $"WMI:{instanceName}";
                var name = identity?.DisplayName ?? "内置显示器";
                results.Add(new MonitorAccessor(
                    new MonitorSnapshot(id, name, MonitorBackend.Wmi, true, true, current, "可控制"),
                    target => SetWmiBrightness(instanceName, target)));
            }
        }
        catch (Exception exception)
        {
            results.Add(new MonitorAccessor(
                new MonitorSnapshot("WMI:unavailable", "内置显示器", MonitorBackend.Unsupported, true, false, null,
                    $"WMI 不可用：{exception.Message}"),
                _ => false));
        }

        var externalIdentities = new Queue<MonitorIdentity>(identities.Where(identity =>
            internalInstanceNames.All(internalName => !InstanceMatches(identity.InstanceName, internalName))));
        var logicalMonitors = new List<IntPtr>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (monitor, _, _, _) => { logicalMonitors.Add(monitor); return true; }, IntPtr.Zero);

        var physicalIndex = 0;
        foreach (var logicalMonitor in logicalMonitors)
        {
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(logicalMonitor, out var count) || count == 0)
            {
                continue;
            }

            var physical = new NativeMethods.PhysicalMonitor[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(logicalMonitor, count, physical))
            {
                continue;
            }

            foreach (var item in physical)
            {
                physicalIndex++;
                var identity = externalIdentities.Count > 0 ? externalIdentities.Dequeue() : null;
                var description = string.IsNullOrWhiteSpace(item.Description) ? $"外接显示器 {physicalIndex}" : item.Description.Trim();
                var id = identity?.StableId ?? $"DDC:{Sanitize(description)}:{physicalIndex}";
                var name = identity?.DisplayName ?? description;
                var capabilitiesOk = NativeMethods.GetMonitorCapabilities(item.Handle, out var capabilities, out _);
                var brightnessOk = NativeMethods.GetMonitorBrightness(item.Handle, out var minimum, out var current, out var maximum);
                var controllable = brightnessOk && (!capabilitiesOk || (capabilities & NativeMethods.MonitorCapabilityBrightness) != 0);

                if (!controllable && identity is null && internalInstanceNames.Count > 0 && logicalMonitors.Count == 1)
                {
                    NativeMethods.DestroyPhysicalMonitor(item.Handle);
                    continue;
                }

                if (controllable)
                {
                    var normalized = Normalize(current, minimum, maximum);
                    results.Add(new MonitorAccessor(
                        new MonitorSnapshot(id, name, MonitorBackend.DdcCi, false, true, normalized, "可控制"),
                        target => NativeMethods.SetMonitorBrightness(item.Handle, Denormalize(target, minimum, maximum)),
                        item.Handle));
                }
                else
                {
                    results.Add(new MonitorAccessor(
                        new MonitorSnapshot(id, name, MonitorBackend.Unsupported, false, false, null, "DDC/CI 亮度不可用"),
                        _ => false,
                        item.Handle));
                }
            }
        }

        return results;
    }

    private static List<MonitorIdentity> ReadMonitorIdentities()
    {
        var result = new List<MonitorIdentity>();
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM WmiMonitorID WHERE Active=True");
            foreach (ManagementObject item in searcher.Get())
            {
                var instance = Convert.ToString(item["InstanceName"]) ?? string.Empty;
                var manufacturer = DecodeUShortString(item["ManufacturerName"] as ushort[]);
                var product = DecodeUShortString(item["ProductCodeID"] as ushort[]);
                var serial = DecodeUShortString(item["SerialNumberID"] as ushort[]);
                var stableId = $"EDID:{manufacturer}:{product}:{serial}:{instance}";
                var displayName = string.Join(' ', new[] { manufacturer, product }.Where(value => !string.IsNullOrWhiteSpace(value)));
                result.Add(new MonitorIdentity(instance, stableId, string.IsNullOrWhiteSpace(displayName) ? "显示器" : displayName));
            }
        }
        catch
        {
            // DDC enumeration can still work without EDID metadata.
        }
        return result;
    }

    private static bool SetWmiBrightness(string instanceName, int target)
    {
        try
        {
            using var methodsSearcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject methodObject in methodsSearcher.Get())
            {
                var candidate = Convert.ToString(methodObject["InstanceName"]) ?? string.Empty;
                if (!InstanceMatches(candidate, instanceName)) continue;
                methodObject.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(target, 0, 100)]);
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private void RaiseChanged() => Changed?.Invoke(Snapshots);

    private static string DecodeUShortString(ushort[]? values) =>
        values is null ? string.Empty : new string(values.Where(value => value != 0).Select(value => (char)value).ToArray()).Trim();

    private static bool InstanceMatches(string left, string right) =>
        string.Equals(NormalizeInstance(left), NormalizeInstance(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeInstance(string value) => value.Replace("_0", string.Empty, StringComparison.OrdinalIgnoreCase);
    private static string Sanitize(string value) => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
    private static int Normalize(uint value, uint minimum, uint maximum) => maximum <= minimum ? 0 : (int)Math.Round((value - minimum) * 100d / (maximum - minimum));
    private static uint Denormalize(int value, uint minimum, uint maximum) => maximum <= minimum ? minimum : minimum + (uint)Math.Round(Math.Clamp(value, 0, 100) * (maximum - minimum) / 100d);

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _writeQueue.Complete();
        try { await _writerLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        lock (_gate)
        {
            foreach (var accessor in _accessors.Values) accessor.Dispose();
            _accessors.Clear();
        }
        _cancellation.Dispose();
    }

    private sealed record MonitorIdentity(string InstanceName, string StableId, string DisplayName);

    private sealed class MonitorAccessor : IDisposable
    {
        private readonly Func<int, bool> _setter;
        private readonly IntPtr _physicalHandle;

        public MonitorAccessor(MonitorSnapshot snapshot, Func<int, bool> setter, IntPtr physicalHandle = default)
        {
            Snapshot = snapshot;
            _setter = setter;
            _physicalHandle = physicalHandle;
        }

        public MonitorSnapshot Snapshot { get; set; }
        public bool TrySet(int target)
        {
            try { return _setter(target); } catch { return false; }
        }

        public void Dispose()
        {
            if (_physicalHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyPhysicalMonitor(_physicalHandle);
            }
        }
    }

    private static class NativeMethods
    {
        internal const uint MonitorCapabilityBrightness = 0x00000002;
        internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PhysicalMonitor
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] physicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyPhysicalMonitor(IntPtr monitor);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorCapabilities(IntPtr monitor, out uint capabilities, out uint colorTemperatures);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
    }
}
