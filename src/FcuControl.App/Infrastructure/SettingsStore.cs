using System.Text.Json;
using System.Text.Json.Serialization;
using FcuControl.Core;

namespace FcuControl.App.Infrastructure;

public sealed class SettingsStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsStore(FileLogger logger, string? baseDirectory = null)
    {
        Logger = logger;
        BaseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FcuControl");
        SettingsPath = Path.Combine(BaseDirectory, "settings.json");
        Directory.CreateDirectory(BaseDirectory);
    }

    public string BaseDirectory { get; }
    public string SettingsPath { get; }
    private FileLogger Logger { get; }

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return AppSettings.CreateDefault();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), _jsonOptions)
                         ?? AppSettings.CreateDefault();
            Normalize(loaded);
            return loaded;
        }
        catch (Exception exception)
        {
            var backupPath = Path.Combine(BaseDirectory, $"settings.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Copy(SettingsPath, backupPath, overwrite: true);
            }
            catch
            {
                // Preserve startup even if backup fails.
            }

            Logger.Error($"设置损坏，已恢复默认值。备份：{backupPath}", exception);
            return AppSettings.CreateDefault();
        }
    }

    public void Save(AppSettings settings)
    {
        Normalize(settings);
        var temporaryPath = SettingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private static void Normalize(AppSettings settings)
    {
        var previousVersion = settings.SettingsVersion;
        settings.SettingsVersion = AppSettings.CurrentVersion;
        settings.Language = settings.Language == "en-US" ? "en-US" : "zh-CN";
        settings.VolumeStepPercent = Math.Clamp(settings.VolumeStepPercent, 1, 20);
        settings.BrightnessStepPercent = Math.Clamp(settings.BrightnessStepPercent, 1, 20);
        settings.HardwareBrightnessStepPercent = Math.Clamp(settings.HardwareBrightnessStepPercent, 1, 20);
        settings.BacklightBrightness = Math.Clamp(settings.BacklightBrightness, 0, 100);
        settings.LcdBrightness = Math.Clamp(settings.LcdBrightness, 0, 100);
        settings.LedBrightness = Math.Clamp(settings.LedBrightness, 0, 100);
        settings.InputBindings ??= [];
        settings.BrightnessTargets ??= [];
        settings.OutputBindings ??= OutputDefaults.Create();
        settings.AudioDeviceSwitchBindings ??= [];
        settings.ApplicationLaunchBindings ??= [];
        settings.ConflictProcesses = (settings.ConflictProcesses ?? [])
            .Select(ProcessName.Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var defaultBinding in OutputDefaults.Create())
        {
            if (settings.OutputBindings.All(binding => binding.Target != defaultBinding.Target))
            {
                settings.OutputBindings.Add(defaultBinding);
            }
        }

        if (previousVersion < 4)
        {
            var altitude = settings.OutputBindings.First(binding => binding.Target == OutputTargetKind.Altitude);
            if (altitude.Source == OutputSourceKind.Blank)
            {
                altitude.Source = OutputSourceKind.FcuLcdBrightness;
                altitude.MonitorId = null;
            }

            var verticalSpeed = settings.OutputBindings.First(binding => binding.Target == OutputTargetKind.VerticalSpeed);
            if (verticalSpeed.Source == OutputSourceKind.Blank)
            {
                verticalSpeed.Source = OutputSourceKind.FcuBacklightBrightness;
                verticalSpeed.MonitorId = null;
            }
        }

        var bindingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var controlIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ledTargets = new HashSet<OutputTargetKind>();
        foreach (var binding in settings.AudioDeviceSwitchBindings)
        {
            binding.BindingId = binding.BindingId?.Trim() ?? string.Empty;
            if (binding.BindingId.Length == 0 || !bindingIds.Add(binding.BindingId))
            {
                binding.BindingId = Guid.NewGuid().ToString("N");
                bindingIds.Add(binding.BindingId);
            }

            binding.ControlId = binding.ControlId?.Trim() ?? string.Empty;
            binding.DeviceId = binding.DeviceId?.Trim() ?? string.Empty;
            if (binding.ControlId.Length > 0 && !controlIds.Add(binding.ControlId))
            {
                binding.ControlId = string.Empty;
            }
            if (binding.LedTarget.HasValue &&
                (!AudioSwitchBindingRegistry.IsLed(binding.LedTarget.Value) || !ledTargets.Add(binding.LedTarget.Value)))
            {
                binding.LedTarget = null;
            }
        }

        foreach (var binding in settings.ApplicationLaunchBindings)
        {
            binding.BindingId = binding.BindingId?.Trim() ?? string.Empty;
            if (binding.BindingId.Length == 0 || !bindingIds.Add(binding.BindingId))
            {
                binding.BindingId = Guid.NewGuid().ToString("N");
                bindingIds.Add(binding.BindingId);
            }

            binding.ControlId = binding.ControlId?.Trim() ?? string.Empty;
            binding.ExecutablePath = binding.ExecutablePath?.Trim() ?? string.Empty;
            if (binding.ControlId.Length > 0 && !controlIds.Add(binding.ControlId))
            {
                binding.ControlId = string.Empty;
            }
        }

        if (controlIds.Count > 0)
        {
            settings.InputBindings.RemoveAll(binding => controlIds.Contains(binding.ControlId));
        }
    }
}

public static class ProcessName
{
    public static string Normalize(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return value;
    }
}
