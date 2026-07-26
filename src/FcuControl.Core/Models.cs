using System.Text.Json.Serialization;

namespace FcuControl.Core;

[JsonConverter(typeof(JsonStringEnumConverter<AppAction>))]
public enum AppAction
{
    VolumeUp,
    VolumeDown,
    ToggleMute,
    BrightnessUp,
    BrightnessDown,
    FcuLcdBrightnessUp,
    FcuLcdBrightnessDown,
    FcuBacklightBrightnessUp,
    FcuBacklightBrightnessDown,
    ToggleManualPause
}

[JsonConverter(typeof(JsonStringEnumConverter<InputTrigger>))]
public enum InputTrigger
{
    Press
}

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeMode>))]
public enum RuntimeMode
{
    Active,
    WaitingForDevice,
    Yielded,
    ManualPaused,
    DeviceError
}

[JsonConverter(typeof(JsonStringEnumConverter<MonitorBackend>))]
public enum MonitorBackend
{
    Wmi,
    DdcCi,
    Unsupported
}

[JsonConverter(typeof(JsonStringEnumConverter<OutputTargetKind>))]
public enum OutputTargetKind
{
    Speed,
    Heading,
    Altitude,
    VerticalSpeed,
    LocLed,
    Ap1Led,
    Ap2Led,
    AthrLed,
    ExpedLed,
    ApprLed
}

[JsonConverter(typeof(JsonStringEnumConverter<OutputSourceKind>))]
public enum OutputSourceKind
{
    Blank,
    MasterVolume,
    MonitorBrightness,
    FcuLcdBrightness,
    FcuBacklightBrightness,
    Muted,
    AppActive,
    Yielded,
    DeviceError,
    ConstantOn,
    ConstantOff
}

public sealed class InputBinding
{
    public string ControlId { get; set; } = string.Empty;
    public InputTrigger Trigger { get; set; } = InputTrigger.Press;
    public AppAction Action { get; set; }
}

public sealed class BrightnessTargetSetting
{
    public string MonitorId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
}

public sealed class OutputBinding
{
    public OutputTargetKind Target { get; set; }
    public OutputSourceKind Source { get; set; }
    public string? MonitorId { get; set; }
}

public sealed class AudioDeviceSwitchBinding
{
    public string BindingId { get; set; } = Guid.NewGuid().ToString("N");
    public string ControlId { get; set; } = string.Empty;
    public InputTrigger Trigger { get; set; } = InputTrigger.Press;
    public string DeviceId { get; set; } = string.Empty;
    public OutputTargetKind? LedTarget { get; set; }
}

public sealed class ApplicationLaunchBinding
{
    public string BindingId { get; set; } = Guid.NewGuid().ToString("N");
    public string ControlId { get; set; } = string.Empty;
    public InputTrigger Trigger { get; set; } = InputTrigger.Press;
    public string ExecutablePath { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    public const int CurrentVersion = 4;

    public int SettingsVersion { get; set; } = CurrentVersion;
    public string Language { get; set; } = "zh-CN";
    public int VolumeStepPercent { get; set; } = 2;
    public int BrightnessStepPercent { get; set; } = 5;
    public int HardwareBrightnessStepPercent { get; set; } = 5;
    public bool ManualPaused { get; set; }
    public int BacklightBrightness { get; set; } = 20;
    public int LcdBrightness { get; set; } = 100;
    public int LedBrightness { get; set; } = 100;
    public List<InputBinding> InputBindings { get; set; } = [];
    public List<BrightnessTargetSetting> BrightnessTargets { get; set; } = [];
    public List<string> ConflictProcesses { get; set; } = [];
    public List<OutputBinding> OutputBindings { get; set; } = [];
    public List<AudioDeviceSwitchBinding> AudioDeviceSwitchBindings { get; set; } = [];
    public List<ApplicationLaunchBinding> ApplicationLaunchBindings { get; set; } = [];

    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            ConflictProcesses =
            [
                "SimAppPro",
                "MobiFlightConnector",
                "SPAD.neXt",
                "FlightSimulator",
                "FlightSimulator2024",
                "X-Plane",
                "DCS",
                "Prepar3D",
                "fsx"
            ],
            OutputBindings = OutputDefaults.Create()
        };
    }
}

public sealed record HidControlActivation(string ControlId, InputTrigger Trigger, DateTimeOffset Timestamp);

public sealed record MonitorSnapshot(
    string Id,
    string Name,
    MonitorBackend Backend,
    bool IsInternal,
    bool IsControllable,
    int? Brightness,
    string Status);

public sealed record BrightnessChange(string MonitorId, string MonitorName, int Previous, int Target);

public sealed record AudioDeviceSnapshot(string Id, string Name, bool IsDefault);

public static class OutputDefaults
{
    public static List<OutputBinding> Create() =>
    [
        new() { Target = OutputTargetKind.Speed, Source = OutputSourceKind.MasterVolume },
        new() { Target = OutputTargetKind.Heading, Source = OutputSourceKind.Blank },
        new() { Target = OutputTargetKind.Altitude, Source = OutputSourceKind.FcuLcdBrightness },
        new() { Target = OutputTargetKind.VerticalSpeed, Source = OutputSourceKind.FcuBacklightBrightness },
        new() { Target = OutputTargetKind.LocLed, Source = OutputSourceKind.ConstantOff },
        new() { Target = OutputTargetKind.Ap1Led, Source = OutputSourceKind.ConstantOff },
        new() { Target = OutputTargetKind.Ap2Led, Source = OutputSourceKind.ConstantOff },
        new() { Target = OutputTargetKind.AthrLed, Source = OutputSourceKind.Muted },
        new() { Target = OutputTargetKind.ExpedLed, Source = OutputSourceKind.ConstantOff },
        new() { Target = OutputTargetKind.ApprLed, Source = OutputSourceKind.ConstantOff }
    ];
}
