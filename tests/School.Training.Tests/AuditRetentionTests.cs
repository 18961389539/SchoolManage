using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using School.Training;

namespace School.Training.Tests;

public sealed class AuditRetentionTests : IDisposable
{
    readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("school-audit-retention-");

    [Fact]
    public void Options_default_to_keeping_audits_forever()
    {
        var options = AuditRetentionOptions.Load(Configuration());
        Assert.Equal(0, options.RetentionDays);
        Assert.Equal(new TimeOnly(3, 30), options.TimeOfDay);
    }

    [Fact]
    public void Options_read_retention_days_and_cleanup_time()
    {
        var options = AuditRetentionOptions.Load(Configuration(
            ("School:Audits:RetentionDays", "180"),
            ("School:Audits:CleanupTimeOfDay", "04:15")));
        Assert.Equal(180, options.RetentionDays);
        Assert.Equal(new TimeOnly(4, 15), options.TimeOfDay);
    }

    [Fact]
    public void Options_clamp_out_of_range_retention_days()
    {
        Assert.Equal(3650, AuditRetentionOptions.Load(Configuration(("School:Audits:RetentionDays", "99999"))).RetentionDays);
        Assert.Equal(0, AuditRetentionOptions.Load(Configuration(("School:Audits:RetentionDays", "-5"))).RetentionDays);
    }

    [Theory]
    [InlineData("普通文本", "普通文本")]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+123", "'+123")]
    [InlineData("-123", "'-123")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\nb", "\"a\nb\"")]
    [InlineData(null, "")]
    public void Cell_escapes_formulas_and_delimiters(string? value, string expected)
        => Assert.Equal(expected, AuditExport.Cell(value));

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public void BeijingTime_treats_naive_database_values_as_utc(DateTimeKind kind)
    {
        var value = new DateTime(2026, 11, 23, 2, 0, 0, kind);
        Assert.Equal(new DateTime(2026, 11, 23, 10, 0, 0), AuditExport.BeijingTime(value));
    }

    [Fact]
    public async Task Delete_removes_only_entries_older_than_the_cutoff()
    {
        var paths = new AppPaths(directory.FullName);
        await using var db = Open(paths);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        db.Audits.AddRange(
            new AuditEntry { At = now.AddDays(-40), Action = "过期一" },
            new AuditEntry { At = now.AddDays(-31), Action = "过期二" },
            new AuditEntry { At = now.AddDays(-1), Action = "保留" });
        await db.SaveChangesAsync();

        var deleted = await AuditRetention.DeleteAsync(db, now.AddDays(-30), CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(new[] { "保留" }, await db.Audits.OrderBy(x => x.At).Select(x => x.Action).ToListAsync());
    }

    static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value))
            .Build();

    static SchoolDbContext Open(AppPaths paths)
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString();
        return new SchoolDbContext(new DbContextOptionsBuilder<SchoolDbContext>().UseSqlite(connection).Options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { directory.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
