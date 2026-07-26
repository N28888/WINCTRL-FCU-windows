using FcuControl.Core;

namespace FcuControl.Core.Tests;

public sealed class AudioSwitchBindingRegistryTests
{
    [Fact]
    public void AssignControl_ReassignsAControlToOnlyOneAudioDevice()
    {
        var bindings = new List<AudioDeviceSwitchBinding>();
        var registry = new AudioSwitchBindingRegistry(bindings);
        var headphones = registry.Add();
        var speakers = registry.Add();

        registry.AssignControl(headphones.BindingId, "AP1", InputTrigger.Press);
        registry.AssignControl(speakers.BindingId, "ap1", InputTrigger.Press);

        Assert.Equal(string.Empty, headphones.ControlId);
        Assert.Equal("ap1", speakers.ControlId);
        Assert.Same(speakers, registry.Resolve("AP1", InputTrigger.Press));
    }

    [Fact]
    public void Update_ReassignsAnLedToOnlyOneAudioDevice()
    {
        var bindings = new List<AudioDeviceSwitchBinding>();
        var registry = new AudioSwitchBindingRegistry(bindings);
        var headphones = registry.Add();
        var speakers = registry.Add();

        Assert.True(registry.Update(headphones.BindingId, "headphones", OutputTargetKind.Ap1Led));
        Assert.True(registry.Update(speakers.BindingId, "speakers", OutputTargetKind.Ap1Led));

        Assert.Null(headphones.LedTarget);
        Assert.Equal(OutputTargetKind.Ap1Led, speakers.LedTarget);
    }

    [Fact]
    public void Update_ReturnsFalseWhenAComboBoxReportsTheExistingSelection()
    {
        var binding = new AudioDeviceSwitchBinding
        {
            DeviceId = "headphones",
            LedTarget = OutputTargetKind.Ap1Led
        };
        var registry = new AudioSwitchBindingRegistry([binding]);

        var changed = registry.Update(binding.BindingId, " headphones ", OutputTargetKind.Ap1Led);

        Assert.False(changed);
        Assert.Equal("headphones", binding.DeviceId);
        Assert.Equal(OutputTargetKind.Ap1Led, binding.LedTarget);
    }

    [Fact]
    public void IsLedActive_FollowsTheCurrentDefaultDevice()
    {
        var bindings = new[]
        {
            new AudioDeviceSwitchBinding { DeviceId = "headphones", LedTarget = OutputTargetKind.Ap1Led },
            new AudioDeviceSwitchBinding { DeviceId = "speakers", LedTarget = OutputTargetKind.Ap2Led }
        };

        Assert.True(AudioSwitchBindingRegistry.IsLedActive(bindings, OutputTargetKind.Ap1Led, "HEADPHONES"));
        Assert.False(AudioSwitchBindingRegistry.IsLedActive(bindings, OutputTargetKind.Ap2Led, "headphones"));
    }
}
