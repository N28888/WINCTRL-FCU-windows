namespace FcuControl.Core;

public static class ValueMath
{
    public static int ClampPercent(int value) => Math.Clamp(value, 0, 100);

    public static int AdjustPercent(int current, int delta) => ClampPercent(current + delta);

    public static IReadOnlyList<BrightnessChange> PlanBrightnessChanges(
        IEnumerable<MonitorSnapshot> monitors,
        ISet<string> enabledIds,
        int delta)
    {
        return monitors
            .Where(monitor =>
                enabledIds.Contains(monitor.Id) &&
                monitor.IsControllable &&
                monitor.Brightness.HasValue)
            .Select(monitor => new BrightnessChange(
                monitor.Id,
                monitor.Name,
                monitor.Brightness!.Value,
                AdjustPercent(monitor.Brightness.Value, delta)))
            .ToArray();
    }
}

public sealed class ConflictStateMachine
{
    private readonly TimeSpan _resumeDelay;
    private DateTimeOffset? _lastConflictSeen;

    public ConflictStateMachine(TimeSpan? resumeDelay = null)
    {
        _resumeDelay = resumeDelay ?? TimeSpan.FromSeconds(2);
    }

    public RuntimeMode Evaluate(
        bool manualPaused,
        bool conflictPresent,
        bool deviceConnected,
        bool deviceError,
        DateTimeOffset now)
    {
        if (manualPaused)
        {
            return RuntimeMode.ManualPaused;
        }

        if (conflictPresent)
        {
            _lastConflictSeen = now;
            return RuntimeMode.Yielded;
        }

        if (_lastConflictSeen.HasValue && now - _lastConflictSeen.Value < _resumeDelay)
        {
            return RuntimeMode.Yielded;
        }

        if (deviceError)
        {
            return RuntimeMode.DeviceError;
        }

        return deviceConnected ? RuntimeMode.Active : RuntimeMode.WaitingForDevice;
    }
}
