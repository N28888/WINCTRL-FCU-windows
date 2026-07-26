using System.Diagnostics;

namespace FcuControl.App.Services;

public sealed class ConflictProcessMonitor : IAsyncDisposable
{
    private readonly Func<IReadOnlyCollection<string>> _configuredNames;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;
    private bool _lastPresent;
    private string[] _lastMatches = [];

    public ConflictProcessMonitor(Func<IReadOnlyCollection<string>> configuredNames)
    {
        _configuredNames = configuredNames;
    }

    public event Action<bool, IReadOnlyList<string>>? Changed;

    public void Start()
    {
        _loop ??= Task.Run(() => MonitorLoopAsync(_cancellation.Token));
    }

    public (bool Present, IReadOnlyList<string> Matches) ScanNow()
    {
        var configured = _configuredNames()
            .Select(Infrastructure.ProcessName.Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (configured.Contains(process.ProcessName))
                {
                    matches.Add(process.ProcessName);
                }
            }
            catch
            {
                // Processes can exit between enumeration and property access.
            }
            finally
            {
                process.Dispose();
            }
        }

        return (matches.Count > 0, matches.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = ScanNow();
            if (result.Present != _lastPresent || !result.Matches.SequenceEqual(_lastMatches, StringComparer.OrdinalIgnoreCase))
            {
                _lastPresent = result.Present;
                _lastMatches = result.Matches.ToArray();
                Changed?.Invoke(result.Present, result.Matches);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cancellation.Dispose();
    }
}

