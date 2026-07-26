using FcuControl.Core;

namespace FcuControl.Core.Tests;

public sealed class BindingRegistryTests
{
    [Fact]
    public void Bind_ReplacesThePreviousBindingForAnAction()
    {
        var bindings = new List<InputBinding>();
        var registry = new BindingRegistry(bindings);

        registry.Bind("control-a", InputTrigger.Press, AppAction.VolumeUp);
        registry.Bind("control-b", InputTrigger.Press, AppAction.VolumeUp);

        Assert.Single(bindings);
        Assert.Equal("control-b", bindings[0].ControlId);
        Assert.Equal(AppAction.VolumeUp, registry.Resolve("CONTROL-B", InputTrigger.Press));
    }

    [Fact]
    public void Bind_ReassignsAControlToOnlyOneAction()
    {
        var bindings = new List<InputBinding>();
        var registry = new BindingRegistry(bindings);

        registry.Bind("knob-right", InputTrigger.Press, AppAction.VolumeUp);
        registry.Bind("knob-right", InputTrigger.Press, AppAction.BrightnessUp);

        Assert.Single(bindings);
        Assert.DoesNotContain(bindings, binding => binding.Action == AppAction.VolumeUp);
        Assert.Equal(AppAction.BrightnessUp, registry.Resolve("knob-right", InputTrigger.Press));
    }

    [Fact]
    public void Clear_RemovesOnlyTheRequestedAction()
    {
        var bindings = new List<InputBinding>();
        var registry = new BindingRegistry(bindings);
        registry.Bind("a", InputTrigger.Press, AppAction.VolumeUp);
        registry.Bind("b", InputTrigger.Press, AppAction.VolumeDown);

        Assert.True(registry.Clear(AppAction.VolumeUp));
        Assert.Single(bindings);
        Assert.Equal(AppAction.VolumeDown, bindings[0].Action);
    }
}
