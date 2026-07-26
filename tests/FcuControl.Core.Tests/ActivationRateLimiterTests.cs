using FcuControl.Core;

namespace FcuControl.Core.Tests;

public sealed class ActivationRateLimiterTests
{
    [Fact]
    public void SameControlCanOnlyTriggerOnceWithinThirtyMilliseconds()
    {
        var limiter = new ActivationRateLimiter();
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAccept("mute-button", now));
        Assert.False(limiter.TryAccept("mute-button", now.AddMilliseconds(10)));
        Assert.False(limiter.TryAccept("MUTE-BUTTON", now.AddMilliseconds(29)));
        Assert.True(limiter.TryAccept("mute-button", now.AddMilliseconds(30)));
    }

    [Fact]
    public void DifferentControlsHaveIndependentWindows()
    {
        var limiter = new ActivationRateLimiter();
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAccept("knob-plus", now));
        Assert.True(limiter.TryAccept("knob-minus", now.AddMilliseconds(10)));
    }

    [Fact]
    public void ResetAllowsImmediateActivationAfterReconnect()
    {
        var limiter = new ActivationRateLimiter();
        var now = DateTimeOffset.UtcNow;
        Assert.True(limiter.TryAccept("button", now));

        limiter.Reset();

        Assert.True(limiter.TryAccept("button", now.AddMilliseconds(1)));
    }
}
