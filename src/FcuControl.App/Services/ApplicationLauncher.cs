using System.Diagnostics;

namespace FcuControl.App.Services;

public sealed class ApplicationLauncher
{
    public string Launch(string executablePath)
    {
        var startInfo = CreateStartInfo(executablePath);
        Process.Start(startInfo);
        return Path.GetFileNameWithoutExtension(startInfo.FileName);
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("请先选择要启动的软件。");
        }

        var fullPath = Path.GetFullPath(executablePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("所选软件不存在，请重新选择。", fullPath);
        }

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只支持启动 .exe 应用程序或 .lnk 快捷方式。");
        }

        return new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
            UseShellExecute = true
        };
    }
}
