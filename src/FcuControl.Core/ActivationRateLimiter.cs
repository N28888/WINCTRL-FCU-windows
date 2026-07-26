namespace FcuControl.Core;

public sealed class ActivationRateLimiter
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAccepted = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _minimumInterval;

    public ActivationRateLimiter(TimeSpan? minimumInterval = null)
    {
        _minimumInterval = minimumInterval ?? DefaultInterval;
    }

    public bool TryAccept(string controlId, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            if (_lastAccepted.TryGetValue(controlId, out var previous) &&
                timestamp - previous < _minimumInterval)
            {
                return false;
            }

            _lastAccepted[controlId] = timestamp;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _lastAccepted.Clear();
        }
    }
}
