using FcuControl.Core;

namespace FcuControl.Core.Tests;

public sealed class ApplicationLaunchBindingRegistryTests
{
    [Fact]
    public void AssignControl_ReassignsAControlToOnlyOneApplication()
    {
        var bindings = new List<ApplicationLaunchBinding>();
        var registry = new ApplicationLaunchBindingRegistry(bindings);
        var first = registry.Add();
        var second = registry.Add();

        registry.AssignControl(first.BindingId, "AP1", InputTrigger.Press);
        registry.AssignControl(second.BindingId, "ap1", InputTrigger.Press);

        Assert.Equal(string.Empty, first.ControlId);
        Assert.Equal("ap1", second.ControlId);
        Assert.Same(second, registry.Resolve("AP1", InputTrigger.Press));
    }

    [Fact]
    public void UpdatePath_TrimsAndPersistsTheSelectedPath()
    {
        var bindings = new List<ApplicationLaunchBinding>();
        var registry = new ApplicationLaunchBindingRegistry(bindings);
        var binding = registry.Add();

        registry.UpdatePath(binding.BindingId, "  C:\\Apps\\Tool.exe  ");

        Assert.Equal("C:\\Apps\\Tool.exe", binding.ExecutablePath);
    }
}
