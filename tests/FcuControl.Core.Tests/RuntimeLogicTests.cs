using FcuControl.Core;

namespace FcuControl.Core.Tests;

public sealed class RuntimeLogicTests
{
    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(42, 42)]
    [InlineData(101, 100)]
    public void ClampPercent_UsesTheValidRange(int input, int expected)
    {
        Assert.Equal(expected, ValueMath.ClampPercent(input));
    }

    [Theory]
    [InlineData(95, 10, 100)]
    [InlineData(5, -10, 0)]
    [InlineData(40, 5, 45)]
    public void AdjustPercent_AppliesDeltaAndClamps(int current, int delta, int expected)
    {
        Assert.Equal(expected, ValueMath.AdjustPercent(current, delta));
    }

    [Fact]
    public void BrightnessPlan_PreservesRelativeDifferencesAndClamps()
    {
        MonitorSnapshot[] monitors =
        [
            new("a", "内屏", MonitorBackend.Wmi, true, true, 95, "OK"),
            new("b", "外屏", MonitorBackend.DdcCi, false, true, 40, "OK"),
            new("c", "不支持", MonitorBackend.Unsupported, false, false, null, "NO")
        ];

        var result = ValueMath.PlanBrightnessChanges(monitors, new HashSet<string> { "a", "b", "c" }, 10);

        Assert.Collection(result,
            change => { Assert.Equal("a", change.MonitorId); Assert.Equal(100, change.Target); },
            change => { Assert.Equal("b", change.MonitorId); Assert.Equal(50, change.Target); });
    }

    [Fact]
    public void ConflictStateMachine_HoldsYieldForTheResumeDelay()
    {
        var machine = new ConflictStateMachine(TimeSpan.FromSeconds(2));
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(RuntimeMode.Yielded, machine.Evaluate(false, true, true, false, now));
        Assert.Equal(RuntimeMode.Yielded, machine.Evaluate(false, false, false, false, now.AddSeconds(1)));
        Assert.Equal(RuntimeMode.WaitingForDevice, machine.Evaluate(false, false, false, false, now.AddSeconds(2.1)));
    }

    [Fact]
    public void ManualPauseAlwaysHasPriority()
    {
        var machine = new ConflictStateMachine();
        var mode = machine.Evaluate(true, true, true, true, DateTimeOffset.UtcNow);
        Assert.Equal(RuntimeMode.ManualPaused, mode);
    }

    [Fact]
    public void DefaultsContainEveryOutputTarget()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Equal(Enum.GetValues<OutputTargetKind>().Length, settings.OutputBindings.Select(binding => binding.Target).Distinct().Count());
        Assert.Contains(settings.OutputBindings, binding =>
            binding.Target == OutputTargetKind.Altitude && binding.Source == OutputSourceKind.FcuLcdBrightness);
        Assert.Contains(settings.OutputBindings, binding =>
            binding.Target == OutputTargetKind.VerticalSpeed && binding.Source == OutputSourceKind.FcuBacklightBrightness);
        Assert.Contains(settings.ConflictProcesses, value => value.Equals("SimAppPro", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CoalescingQueueKeepsOnlyTheLatestTargetForEachMonitor()
    {
        var queue = new CoalescingTargetQueue();
        queue.Enqueue("monitor-a", 10);
        queue.Enqueue("MONITOR-A", 35);
        queue.Enqueue("monitor-b", 70);

        var keys = queue.DrainKeys();

        Assert.Equal(2, keys.Count);
        Assert.True(queue.TryTakeLatest("monitor-a", out var first));
        Assert.Equal(35, first);
        Assert.True(queue.TryTakeLatest("monitor-b", out var second));
        Assert.Equal(70, second);
    }

    [Fact]
    public void DisconnectedDeviceTransitionsBackToRetryableWaitingState()
    {
        var machine = new ConflictStateMachine();
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(RuntimeMode.DeviceError, machine.Evaluate(false, false, false, true, now));
        Assert.Equal(RuntimeMode.WaitingForDevice, machine.Evaluate(false, false, false, false, now.AddSeconds(1)));
        Assert.Equal(RuntimeMode.Active, machine.Evaluate(false, false, true, false, now.AddSeconds(2)));
    }
}
