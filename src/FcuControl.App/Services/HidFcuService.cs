using FcuControl.Core;
using HidSharp;
using HidSharp.Reports;
using HidSharp.Reports.Input;

namespace FcuControl.App.Services;

public sealed class HidFcuService : IAsyncDisposable
{
    public const int VendorId = 0x4098;
    public const int ProductId = 0xBB10;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ActivationRateLimiter _activationRateLimiter = new();
    private CancellationTokenSource? _readCancellation;
    private Task? _readLoop;
    private Task? _heartbeatLoop;
    private HidStream? _stream;
    private bool _disposed;
    private DateTimeOffset _acceptActivationsAfter;

    public event Action<HidControlActivation>? ControlActivated;
    public event Action<string>? Diagnostic;
    public event Action<bool, string>? ConnectionChanged;

    public bool IsConnected => _stream is not null;
    public string DeviceName { get; private set; } = "WINCTRL 32 FCU";

    public async Task<bool> ConnectAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _stream is not null)
            {
                return _stream is not null;
            }

            var device = DeviceList.Local.GetHidDeviceOrNull(VendorId, ProductId);
            if (device is null)
            {
                ConnectionChanged?.Invoke(false, "未找到 WINCTRL 32 FCU");
                return false;
            }

            try
            {
                DeviceName = SafeGetName(device);
                var stream = device.Open();
                stream.ReadTimeout = Timeout.Infinite;
                stream.WriteTimeout = 250;
                _stream = stream;
                _acceptActivationsAfter = DateTimeOffset.UtcNow.AddMilliseconds(500);
                _activationRateLimiter.Reset();
                _readCancellation = new CancellationTokenSource();
                _readLoop = Task.Run(() => ReadLoop(device, stream, _readCancellation.Token));
                _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_readCancellation.Token));
                ConnectionChanged?.Invoke(true, $"已连接 {DeviceName}");
                return true;
            }
            catch (Exception exception)
            {
                ConnectionChanged?.Invoke(false, $"FCU 打开失败：{exception.Message}");
                return false;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task DisconnectAsync(string reason)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _readCancellation?.Cancel();
            _stream?.Dispose();
            _stream = null;
            if (_readLoop is not null)
            {
                try { await _readLoop.WaitAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false); }
                catch { }
            }
            if (_heartbeatLoop is not null)
            {
                try { await _heartbeatLoop.WaitAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false); }
                catch { }
            }
            _readLoop = null;
            _heartbeatLoop = null;
            _readCancellation?.Dispose();
            _readCancellation = null;
            ConnectionChanged?.Invoke(false, reason);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> WriteAsync(byte[] report, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = _stream;
            if (stream is null)
            {
                return false;
            }

            // Keep the proven synchronous HID transport, but never execute it on the
            // WPF dispatcher: a stalled device write must not freeze the whole UI.
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => stream.Write(report, 0, report.Length), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke($"OUT FAIL {exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await WriteAsync(FcuOutputProtocol.BuildHeartbeatMessage(), cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false);
                await WriteAsync(FcuOutputProtocol.BuildHeartbeatMessage(), cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(2550), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal disconnect, handoff, or shutdown.
        }
    }

    private void ReadLoop(HidDevice device, HidStream stream, CancellationToken cancellationToken)
    {
        try
        {
            var descriptor = device.GetReportDescriptor();
            var receiver = descriptor.CreateHidDeviceInputReceiver();
            var parsers = descriptor.DeviceItems
                .Select(item => item.CreateDeviceItemInputParser())
                .ToArray();
            var initializedParsers = new HashSet<DeviceItemInputParser>();
            var buffer = new byte[Math.Max(1, descriptor.MaxInputReportLength)];
            receiver.Start(stream);

            while (!cancellationToken.IsCancellationRequested && receiver.IsRunning)
            {
                if (!receiver.WaitHandle.WaitOne(200))
                {
                    continue;
                }

                while (receiver.TryRead(buffer, 0, out var report))
                {
                    Diagnostic?.Invoke($"RAW {Convert.ToHexString(buffer.AsSpan(0, report.Length))}");
                    foreach (var parser in parsers)
                    {
                        if (!parser.TryParseReport(buffer, 0, report))
                        {
                            continue;
                        }

                        var isBaseline = initializedParsers.Add(parser);
                        while (parser.HasChanged)
                        {
                            var index = parser.GetNextChangedIndex();
                            if (index < 0) break;
                            if (!isBaseline)
                            {
                                HandleValueChange(parser, index, report.ReportID);
                            }
                        }
                    }
                }
            }

            if (!cancellationToken.IsCancellationRequested &&
                ReferenceEquals(Interlocked.CompareExchange(ref _stream, null, stream), stream))
            {
                stream.Dispose();
                ConnectionChanged?.Invoke(false, "FCU 输入接收已停止，正在重新连接");
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _stream, null, stream), stream))
            {
                stream.Dispose();
            }
            ConnectionChanged?.Invoke(false, $"FCU 读取中断：{exception.Message}");
        }
    }

    private void HandleValueChange(DeviceItemInputParser parser, int index, byte reportId)
    {
        var value = parser.GetValue(index);
        var previous = parser.GetPreviousValue(index);
        var logical = value.GetLogicalValue();
        var previousLogical = previous.GetLogicalValue();
        var usage = value.Usages.FirstOrDefault();
        var item = value.DataItem;
        string? direction = null;
        var activate = false;

        if (item.IsRelative || item.ExpectedUsageType == ExpectedUsageType.UpDown)
        {
            if (logical != 0)
            {
                direction = logical > 0 ? "+" : "-";
                activate = true;
            }
        }
        else if (item.IsBoolean ||
                 item.ExpectedUsageType is ExpectedUsageType.PushButton or ExpectedUsageType.ToggleButton or ExpectedUsageType.OneShot)
        {
            activate = logical != 0 && previousLogical == 0;
        }

        var controlId = $"R{reportId:X2}:U{usage:X8}:I{index:D3}{direction}";
        Diagnostic?.Invoke($"{controlId} {previousLogical}->{logical}");
        var timestamp = DateTimeOffset.UtcNow;
        if (activate && timestamp >= _acceptActivationsAfter)
        {
            if (!_activationRateLimiter.TryAccept(controlId, timestamp))
            {
                Diagnostic?.Invoke($"DROP {controlId}（30ms 内重复输入）");
                return;
            }

            ControlActivated?.Invoke(new HidControlActivation(controlId, InputTrigger.Press, timestamp));
        }
    }

    private static string SafeGetName(HidDevice device)
    {
        try { return device.GetFriendlyName(); } catch { }
        return "WINCTRL 32 FCU";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await DisconnectAsync("程序退出").ConfigureAwait(false);
        _disposed = true;
        _lifecycleGate.Dispose();
        _writeGate.Dispose();
    }
}
