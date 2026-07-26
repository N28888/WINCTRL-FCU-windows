namespace FcuControl.Core;

public sealed class BindingRegistry
{
    private readonly List<InputBinding> _bindings;

    public BindingRegistry(List<InputBinding> bindings)
    {
        _bindings = bindings;
    }

    public IReadOnlyList<InputBinding> Bindings => _bindings;

    public AppAction? Resolve(string controlId, InputTrigger trigger)
    {
        return _bindings.FirstOrDefault(binding =>
            binding.Trigger == trigger &&
            string.Equals(binding.ControlId, controlId, StringComparison.OrdinalIgnoreCase))?.Action;
    }

    public InputBinding Bind(string controlId, InputTrigger trigger, AppAction action)
    {
        if (string.IsNullOrWhiteSpace(controlId))
        {
            throw new ArgumentException("Control ID cannot be empty.", nameof(controlId));
        }

        _bindings.RemoveAll(binding =>
            (binding.Trigger == trigger && string.Equals(binding.ControlId, controlId, StringComparison.OrdinalIgnoreCase)) ||
            binding.Action == action);

        var created = new InputBinding
        {
            ControlId = controlId.Trim(),
            Trigger = trigger,
            Action = action
        };
        _bindings.Add(created);
        return created;
    }

    public bool Clear(AppAction action)
    {
        return _bindings.RemoveAll(binding => binding.Action == action) > 0;
    }
}

