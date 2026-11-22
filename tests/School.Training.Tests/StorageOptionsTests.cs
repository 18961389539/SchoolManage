using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using School.Training;

namespace School.Training.Tests;

public sealed class StorageOptionsTests
{
    static StorageOptions Load(params (string Key, string? Value)[] settings)
        => StorageOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(item => new KeyValuePair<string, string?>(item.Key, item.Value)))
            .Build());

    [Fact]
    public void Load_UsesDocumentedDefaults()
    {
        var options = Load();

        Assert.Equal(14, options.LogRetentionDays);
        Assert.Equal(10 * 1024 * 1024, options.LogMaxFileBytes);
        Assert.Equal(200 * 1024 * 1024, options.LogMaxTotalBytes);
        Assert.Equal(20, options.BackupRetentionCount);
        Assert.Equal(5L * 1024 * 1024 * 1024, options.BackupMaxTotalBytes);
        Assert.Equal(256L * 1024 * 1024, options.BackupMinimumFreeBytes);
        Assert.Equal(5, options.PreMigrationBackupCount);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("-3", 1)]
    [InlineData("30", 30)]
    [InlineData("99999", 3650)]
    public void Load_ClampsLogRetentionDays(string configured, int expected)
        => Assert.Equal(expected, Load(("School:Logs:RetentionDays", configured)).LogRetentionDays);

    [Theory]
    [InlineData("0", 1)]
    [InlineData("5000", 1000)]
    public void Load_ClampsBackupRetentionCount(string configured, int expected)
        => Assert.Equal(expected, Load(("School:Backups:RetentionCount", configured)).BackupRetentionCount);

    [Fact]
    public void Load_NeverLetsTotalLogBudgetFallBelowSingleFileBudget()
    {
        var options = Load(
            ("School:Logs:MaxFileBytes", (5 * 1024 * 1024).ToString()),
            ("School:Logs:MaxTotalBytes", (1024 * 1024).ToString()));

        Assert.True(options.LogMaxTotalBytes >= options.LogMaxFileBytes);
    }

    [Fact]
    public void Load_ClampsMinimumFreeBytesToZero()
        => Assert.Equal(0, Load(("School:Backups:MinimumFreeBytes", "-1")).BackupMinimumFreeBytes);

    [Fact]
    public void Load_ClampsPreMigrationBackupCount()
        => Assert.Equal(100, Load(("School:Migrations:PreMigrationBackupCount", "500")).PreMigrationBackupCount);

    [Fact]
    public void Load_DefaultsFileLogLevelToInformation()
        => Assert.Equal(LogLevel.Information, Load().LogMinimumLevel);

    [Theory]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("None", LogLevel.None)]
    public void Load_ReadsConfiguredFileLogLevel(string configured, LogLevel expected)
        => Assert.Equal(expected, Load(("School:Logs:MinimumLevel", configured)).LogMinimumLevel);

    [Theory]
    [InlineData("verbose-ish")]
    [InlineData("")]
    public void Load_FallsBackToInformationForUnknownFileLogLevel(string configured)
        => Assert.Equal(LogLevel.Information, Load(("School:Logs:MinimumLevel", configured)).LogMinimumLevel);
}
