using Microsoft.Extensions.Logging;

namespace School.Training.Tests;

public sealed class RollingFileLoggerTests : IDisposable
{
    readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("school-log-");

    [Fact]
    public void Dispose_flushes_queued_messages()
    {
        using var provider = new RollingFileLoggerProvider(directory.FullName, new StorageOptions());
        var logger = provider.CreateLogger("Tests");
        logger.LogInformation("first {Value}", 1);
        logger.LogError(new InvalidOperationException("boom"), "second");
        provider.Dispose();

        var files = Directory.GetFiles(directory.FullName, "service-*.log");
        Assert.Single(files);
        var text = File.ReadAllText(files[0]);
        Assert.Contains("first 1", text);
        Assert.Contains("boom", text);
    }

    public void Dispose()
    {
        try { directory.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
