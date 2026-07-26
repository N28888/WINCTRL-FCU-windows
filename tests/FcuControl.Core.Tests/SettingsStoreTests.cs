using FcuControl.App.Infrastructure;
using FcuControl.Core;

namespace FcuControl.Core.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Load_MigratesAndNormalizesAnOlderConfiguration()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory);
            var store = new SettingsStore(logger, directory);
            File.WriteAllText(store.SettingsPath,
                """{"SettingsVersion":0,"VolumeStepPercent":99,"BrightnessStepPercent":0,"ConflictProcesses":[" SimAppPro.exe ","simapppro"],"OutputBindings":[]}""");

            var settings = store.Load();

            Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
            Assert.Equal(20, settings.VolumeStepPercent);
            Assert.Equal(1, settings.BrightnessStepPercent);
            Assert.Equal(["SimAppPro"], settings.ConflictProcesses);
            Assert.Equal(Enum.GetValues<OutputTargetKind>().Length, settings.OutputBindings.Count);
            Assert.Contains(settings.OutputBindings, binding =>
                binding.Target == OutputTargetKind.Altitude && binding.Source == OutputSourceKind.FcuLcdBrightness);
            Assert.Contains(settings.OutputBindings, binding =>
                binding.Target == OutputTargetKind.VerticalSpeed && binding.Source == OutputSourceKind.FcuBacklightBrightness);
            Assert.Empty(settings.AudioDeviceSwitchBindings);
            Assert.Empty(settings.ApplicationLaunchBindings);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_AddsHardwareBrightnessDisplaysWithoutOverwritingACustomAltitudeSource()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory);
            var store = new SettingsStore(logger, directory);
            File.WriteAllText(store.SettingsPath,
                """{"SettingsVersion":3,"HardwareBrightnessStepPercent":99,"OutputBindings":[{"Target":"Altitude","Source":"MonitorBrightness","MonitorId":"screen-2"},{"Target":"VerticalSpeed","Source":"Blank"}]}""");

            var settings = store.Load();

            var altitude = settings.OutputBindings.Single(binding => binding.Target == OutputTargetKind.Altitude);
            var verticalSpeed = settings.OutputBindings.Single(binding => binding.Target == OutputTargetKind.VerticalSpeed);
            Assert.Equal(OutputSourceKind.MonitorBrightness, altitude.Source);
            Assert.Equal("screen-2", altitude.MonitorId);
            Assert.Equal(OutputSourceKind.FcuBacklightBrightness, verticalSpeed.Source);
            Assert.Equal(20, settings.HardwareBrightnessStepPercent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_RemovesControlConflictsAcrossDynamicBindings()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory);
            var store = new SettingsStore(logger, directory);
            File.WriteAllText(store.SettingsPath,
                """{"SettingsVersion":2,"InputBindings":[{"ControlId":"AP1","Trigger":"Press","Action":"ToggleMute"}],"AudioDeviceSwitchBindings":[{"BindingId":"audio","ControlId":"AP2","DeviceId":"headphones"}],"ApplicationLaunchBindings":[{"BindingId":"app","ControlId":"ap2","ExecutablePath":" C:\\Apps\\Tool.exe "}]}""");

            var settings = store.Load();

            Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
            Assert.Equal("C:\\Apps\\Tool.exe", settings.ApplicationLaunchBindings[0].ExecutablePath);
            Assert.Equal(string.Empty, settings.ApplicationLaunchBindings[0].ControlId);
            Assert.Single(settings.InputBindings);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_NormalizesDuplicateAudioSwitchControlsAndLeds()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory);
            var store = new SettingsStore(logger, directory);
            File.WriteAllText(store.SettingsPath,
                """{"SettingsVersion":1,"AudioDeviceSwitchBindings":[{"BindingId":"same","ControlId":"AP1","DeviceId":" headphones ","LedTarget":"Ap1Led"},{"BindingId":"same","ControlId":"ap1","DeviceId":"speakers","LedTarget":"Ap1Led"}]}""");

            var settings = store.Load();

            Assert.Equal(2, settings.AudioDeviceSwitchBindings.Count);
            Assert.Equal("headphones", settings.AudioDeviceSwitchBindings[0].DeviceId);
            Assert.NotEqual(settings.AudioDeviceSwitchBindings[0].BindingId, settings.AudioDeviceSwitchBindings[1].BindingId);
            Assert.Equal(string.Empty, settings.AudioDeviceSwitchBindings[1].ControlId);
            Assert.Null(settings.AudioDeviceSwitchBindings[1].LedTarget);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_BacksUpCorruptConfigurationAndReturnsDefaults()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var logger = new FileLogger(directory);
            var store = new SettingsStore(logger, directory);
            File.WriteAllText(store.SettingsPath, "{ definitely not json");

            var settings = store.Load();

            Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
            Assert.Single(Directory.GetFiles(directory, "settings.invalid-*.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "FcuControl.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
