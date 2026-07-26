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
            throw new InvalidOperationException(Localization.Get("Application.SelectFirst"));
        }

        var fullPath = Path.GetFullPath(executablePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(Localization.Get("Application.NotFound"), fullPath);
        }

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Localization.Get("Application.UnsupportedType"));
        }

        return new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
            UseShellExecute = true
        };
    }
}
