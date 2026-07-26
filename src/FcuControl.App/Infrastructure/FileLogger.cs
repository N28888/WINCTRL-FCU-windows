namespace FcuControl.App.Infrastructure;

public sealed class FileLogger
{
    private readonly string _logDirectory;
    private readonly object _gate = new();

    public FileLogger(string baseDirectory)
    {
        _logDirectory = Path.Combine(baseDirectory, "logs");
        Directory.CreateDirectory(_logDirectory);
        DeleteExpiredLogs();
    }

    public event Action<string>? LineWritten;

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        if (exception is not null)
        {
            line += $" | {exception.GetType().Name}: {exception.Message}";
        }

        lock (_gate)
        {
            try
            {
                File.AppendAllText(Path.Combine(_logDirectory, $"fcu-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take down the controller.
            }
        }

        LineWritten?.Invoke(line);
    }

    private void DeleteExpiredLogs()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "fcu-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}

