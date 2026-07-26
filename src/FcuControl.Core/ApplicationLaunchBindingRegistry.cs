namespace FcuControl.Core;

public sealed class ApplicationLaunchBindingRegistry
{
    private readonly List<ApplicationLaunchBinding> _bindings;

    public ApplicationLaunchBindingRegistry(List<ApplicationLaunchBinding> bindings)
    {
        _bindings = bindings;
    }

    public ApplicationLaunchBinding Add()
    {
        var binding = new ApplicationLaunchBinding();
        _bindings.Add(binding);
        return binding;
    }

    public ApplicationLaunchBinding? Find(string bindingId)
    {
        return _bindings.FirstOrDefault(binding =>
            string.Equals(binding.BindingId, bindingId, StringComparison.OrdinalIgnoreCase));
    }

    public ApplicationLaunchBinding? Resolve(string controlId, InputTrigger trigger)
    {
        return _bindings.FirstOrDefault(binding =>
            binding.Trigger == trigger &&
            !string.IsNullOrWhiteSpace(binding.ControlId) &&
            string.Equals(binding.ControlId, controlId, StringComparison.OrdinalIgnoreCase));
    }

    public void AssignControl(string bindingId, string controlId, InputTrigger trigger)
    {
        var binding = Find(bindingId) ?? throw new InvalidOperationException("找不到软件启动绑定。");
        foreach (var other in _bindings.Where(item => !ReferenceEquals(item, binding) &&
                     item.Trigger == trigger &&
                     string.Equals(item.ControlId, controlId, StringComparison.OrdinalIgnoreCase)))
        {
            other.ControlId = string.Empty;
        }

        binding.ControlId = controlId.Trim();
        binding.Trigger = trigger;
    }

    public void UpdatePath(string bindingId, string? executablePath)
    {
        var binding = Find(bindingId) ?? throw new InvalidOperationException("找不到软件启动绑定。");
        binding.ExecutablePath = executablePath?.Trim() ?? string.Empty;
    }

    public bool Remove(string bindingId)
    {
        return _bindings.RemoveAll(binding =>
            string.Equals(binding.BindingId, bindingId, StringComparison.OrdinalIgnoreCase)) > 0;
    }
}
