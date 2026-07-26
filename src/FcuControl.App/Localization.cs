using System.Globalization;
using System.Windows;

namespace FcuControl.App;

internal static class Localization
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";
    private const string ResourcePrefix = "Localization/Strings.";

    public static string CurrentLanguage { get; private set; } = Chinese;
    public static event Action? LanguageChanged;

    public static void SetLanguage(string? language)
    {
        language = language == English ? English : Chinese;
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var dictionaries = resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains(ResourcePrefix, StringComparison.OrdinalIgnoreCase) == true);
        var source = new Uri($"{ResourcePrefix}{language}.xaml", UriKind.Relative);
        if (CurrentLanguage == language && existing is not null)
        {
            return;
        }

        if (existing is not null) dictionaries.Remove(existing);
        dictionaries.Add(new ResourceDictionary { Source = source });
        CurrentLanguage = language;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        LanguageChanged?.Invoke();
    }

    public static string Get(string key, params object?[] args)
    {
        var value = Application.Current?.TryFindResource(key) as string ?? key switch
        {
            "Application.SelectFirst" => "请先选择要启动的软件。",
            "Application.NotFound" => "所选软件不存在，请重新选择。",
            "Application.UnsupportedType" => "只支持启动 .exe 应用程序或 .lnk 快捷方式。",
            "Audio.NoOutputDevice" => "无可用输出设备",
            "Audio.NoDefaultOutput" => "没有可用的默认音频输出设备。",
            "Audio.SelectTarget" => "请先选择目标音频输出设备。",
            "Audio.TargetUnavailable" => "目标音频设备当前不可用，请刷新设备列表。",
            "Audio.SwitchFailed" => "Windows 未能切换到“{0}”。",
            _ => key
        };
        return args.Length == 0 ? value : string.Format(CultureInfo.CurrentUICulture, value, args);
    }
}
