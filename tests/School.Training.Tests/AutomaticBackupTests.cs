using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using School.Training;

namespace School.Training.Tests;

public sealed class AutomaticBackupTests
{
    static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value))
            .Build();

    [Fact]
    public void Options_DefaultToDailyAt0230()
    {
        var options = AutomaticBackupOptions.Load(Configuration());
        Assert.True(options.Enabled);
        Assert.Equal(new TimeOnly(2, 30), options.TimeOfDay);
    }

    [Fact]
    public void Options_ReadEnabledAndTimeOfDay()
    {
        var options = AutomaticBackupOptions.Load(Configuration(
            ("School:Backups:Automatic:Enabled", "false"),
            ("School:Backups:Automatic:TimeOfDay", "23:05")));
        Assert.False(options.Enabled);
        Assert.Equal(new TimeOnly(23, 5), options.TimeOfDay);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("2:30")]
    [InlineData("")]
    public void Options_FallBackWhenTimeOfDayIsInvalid(string value)
    {
        var options = AutomaticBackupOptions.Load(Configuration(("School:Backups:Automatic:TimeOfDay", value)));
        Assert.Equal(new TimeOnly(2, 30), options.TimeOfDay);
    }

    [Theory]
    [InlineData("2026-11-23T01:00:00", 1, 30)]
    [InlineData("2026-11-23T02:30:00", 24, 0)]
    [InlineData("2026-11-23T23:00:00", 3, 30)]
    public void DelayUntilSchedule_SelectsTheNextLocalOccurrence(string now, int expectedHours, int expectedMinutes)
    {
        var localNow = DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture);
        var delay = DailySchedule.DelayUntil(localNow, new TimeOnly(2, 30));
        Assert.Equal(new TimeSpan(expectedHours, expectedMinutes, 0), delay);
    }

    [Fact]
    public async Task Status_RoundTripsThroughSettings()
    {
        var directory = Directory.CreateTempSubdirectory("school-backup-state-");
        try
        {
            var paths = new AppPaths(directory.FullName);
            var connection = new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString();
            await using var db = new SchoolDbContext(new DbContextOptionsBuilder<SchoolDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            var attempt = new DateTime(2026, 11, 23, 2, 30, 0, DateTimeKind.Utc);
            var success = attempt.AddMinutes(1);
            await AutomaticBackupState.WriteAsync(db, attempt, success, "school-20261123023000-00000000.zip", CancellationToken.None);

            var state = await AutomaticBackupState.ReadAsync(db, CancellationToken.None);
            Assert.Equal(attempt, state.LastAttemptAt);
            Assert.Equal(success, state.LastSuccessAt);
            Assert.Equal("school-20261123023000-00000000.zip", state.LastMessage);

            var failedAttempt = success.AddHours(1);
            await AutomaticBackupState.WriteAsync(db, failedAttempt, null, "备份目录所在磁盘剩余空间不足，请清理后重试", CancellationToken.None);
            state = await AutomaticBackupState.ReadAsync(db, CancellationToken.None);
            Assert.Equal(failedAttempt, state.LastAttemptAt);
            Assert.Equal(success, state.LastSuccessAt);
            Assert.Contains("磁盘剩余空间不足", state.LastMessage);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
