using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FcuControl.Core;

public sealed class CoalescingTargetQueue
{
    private readonly ConcurrentDictionary<string, int> _latestTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _keys = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public void Enqueue(string key, int target)
    {
        _latestTargets[key] = target;
        _keys.Writer.TryWrite(key);
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _keys.Reader.WaitToReadAsync(cancellationToken);

    public HashSet<string> DrainKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_keys.Reader.TryRead(out var key)) keys.Add(key);
        return keys;
    }

    public bool TryTakeLatest(string key, out int target) => _latestTargets.TryRemove(key, out target);

    public void Complete() => _keys.Writer.TryComplete();
}
