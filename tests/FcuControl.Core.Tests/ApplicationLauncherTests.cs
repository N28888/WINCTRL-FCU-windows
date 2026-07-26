using FcuControl.App.Services;

namespace FcuControl.Core.Tests;

public sealed class ApplicationLauncherTests
{
    [Fact]
    public void CreateStartInfo_UsesShellExecuteForAnExistingExecutable()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "sample.exe");
            File.WriteAllBytes(path, []);

            var result = ApplicationLauncher.CreateStartInfo(path);

            Assert.Equal(Path.GetFullPath(path), result.FileName);
            Assert.Equal(directory, result.WorkingDirectory);
            Assert.True(result.UseShellExecute);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreateStartInfo_RejectsUnsupportedFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "notes.txt");
            File.WriteAllText(path, "test");

            var exception = Assert.Throws<InvalidOperationException>(() => ApplicationLauncher.CreateStartInfo(path));

            Assert.Contains(".exe", exception.Message);
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
