namespace FcuControl.Core;

public sealed class AudioSwitchBindingRegistry
{
    private readonly List<AudioDeviceSwitchBinding> _bindings;

    public AudioSwitchBindingRegistry(List<AudioDeviceSwitchBinding> bindings)
    {
        _bindings = bindings;
    }

    public AudioDeviceSwitchBinding Add()
    {
        var binding = new AudioDeviceSwitchBinding();
        _bindings.Add(binding);
        return binding;
    }

    public AudioDeviceSwitchBinding? Resolve(string controlId, InputTrigger trigger)
    {
        return _bindings.FirstOrDefault(binding =>
            binding.Trigger == trigger &&
            !string.IsNullOrWhiteSpace(binding.ControlId) &&
            string.Equals(binding.ControlId, controlId, StringComparison.OrdinalIgnoreCase));
    }

    public AudioDeviceSwitchBinding? Find(string bindingId)
    {
        return _bindings.FirstOrDefault(binding =>
            string.Equals(binding.BindingId, bindingId, StringComparison.OrdinalIgnoreCase));
    }

    public void AssignControl(string bindingId, string controlId, InputTrigger trigger)
    {
        var binding = Find(bindingId) ?? throw new InvalidOperationException("找不到音频切换绑定。");
        foreach (var other in _bindings.Where(item => !ReferenceEquals(item, binding) &&
                     item.Trigger == trigger &&
                     string.Equals(item.ControlId, controlId, StringComparison.OrdinalIgnoreCase)))
        {
            other.ControlId = string.Empty;
        }

        binding.ControlId = controlId.Trim();
        binding.Trigger = trigger;
    }

    public bool Update(string bindingId, string? deviceId, OutputTargetKind? ledTarget)
    {
        var binding = Find(bindingId) ?? throw new InvalidOperationException("找不到音频切换绑定。");
        if (ledTarget.HasValue && !IsLed(ledTarget.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(ledTarget), "音频切换只能绑定到 FCU LED。");
        }

        var normalizedDeviceId = deviceId?.Trim() ?? string.Empty;
        var changed = !string.Equals(binding.DeviceId, normalizedDeviceId, StringComparison.Ordinal) ||
                      binding.LedTarget != ledTarget;
        if (ledTarget.HasValue)
        {
            foreach (var other in _bindings.Where(item => !ReferenceEquals(item, binding) && item.LedTarget == ledTarget))
            {
                other.LedTarget = null;
                changed = true;
            }
        }

        if (!changed) return false;

        binding.DeviceId = normalizedDeviceId;
        binding.LedTarget = ledTarget;
        return true;
    }

    public bool Remove(string bindingId)
    {
        return _bindings.RemoveAll(binding =>
            string.Equals(binding.BindingId, bindingId, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public static bool IsLedActive(
        IEnumerable<AudioDeviceSwitchBinding> bindings,
        OutputTargetKind ledTarget,
        string? defaultDeviceId)
    {
        var binding = bindings.FirstOrDefault(item => item.LedTarget == ledTarget);
        return binding is not null &&
               !string.IsNullOrWhiteSpace(binding.DeviceId) &&
               string.Equals(binding.DeviceId, defaultDeviceId, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLed(OutputTargetKind target) => target is
        OutputTargetKind.LocLed or
        OutputTargetKind.Ap1Led or
        OutputTargetKind.Ap2Led or
        OutputTargetKind.AthrLed or
        OutputTargetKind.ExpedLed or
        OutputTargetKind.ApprLed;
}
