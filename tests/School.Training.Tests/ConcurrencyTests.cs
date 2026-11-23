using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace School.Training.Tests;

public class ConcurrencyTests
{
    [Theory]
    [InlineData(null, 0, false)]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    public void Requires_an_exact_revision_match(int? submitted, int current, bool expected)
    {
        Assert.Equal(expected, Concurrency.Matches(submitted, current));
    }

    [Fact]
    public async Task Stale_writer_is_rejected_and_first_edit_survives()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SchoolDbContext>().UseSqlite(connection).Options;
        await using (var seed = new SchoolDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Courses.Add(new TrainingCourse { Name = "并发测试课程", Type = "general" });
            await seed.SaveChangesAsync();
        }

        // Two editors open the same course; both started from version 0.
        await using var first = new SchoolDbContext(options);
        await using var second = new SchoolDbContext(options);
        var early = await first.Courses.SingleAsync(item => item.Name == "并发测试课程");
        var late = await second.Courses.SingleAsync(item => item.Name == "并发测试课程");
        Assert.Equal(0, early.Version);
        Assert.Equal(0, late.Version);

        early.Description = "先保存的内容";
        early.Version++;
        await first.SaveChangesAsync();

        late.Description = "后保存的内容";
        late.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var verify = new SchoolDbContext(options);
        var stored = await verify.Courses.AsNoTracking().SingleAsync(item => item.Id == early.Id);
        Assert.Equal("先保存的内容", stored.Description);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task Upgrades_a_version_16_database_without_losing_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "school-v16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var connectionString = new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false, ForeignKeys = true }.ToString();
            var options = new DbContextOptionsBuilder<SchoolDbContext>().UseSqlite(connectionString).Options;
            await using (var old = new SchoolDbContext(options))
            {
                await old.Database.EnsureCreatedAsync();
                old.Courses.Add(new TrainingCourse { Name = "升级前课程", Description = "保留我" });
                await old.SaveChangesAsync();
                // Rewind to the v16 shape: no optimistic-lock columns, older schema marker.
                await old.Database.ExecuteSqlRawAsync("ALTER TABLE \"Courses\" DROP COLUMN \"Version\";");
                await old.Database.ExecuteSqlRawAsync("ALTER TABLE \"CourseChapters\" DROP COLUMN \"Version\";");
                await old.Database.ExecuteSqlRawAsync("ALTER TABLE \"CourseSections\" DROP COLUMN \"Version\";");
                await old.Database.ExecuteSqlRawAsync("ALTER TABLE \"CourseResources\" DROP COLUMN \"Version\";");
                await old.Database.ExecuteSqlRawAsync($"PRAGMA application_id={DatabaseSafety.ApplicationId};");
                await old.Database.ExecuteSqlRawAsync("PRAGMA user_version=16;");
            }

            await DatabaseMigrations.MigrateAsync(new SchoolDbContext(options),
                paths, new StorageOptions(), NullLogger.Instance);

            await using var upgraded = new SchoolDbContext(options);
            var course = await upgraded.Courses.AsNoTracking().SingleAsync(item => item.Name == "升级前课程");
            Assert.Equal("保留我", course.Description);
            Assert.Equal(0, course.Version);
            Assert.Equal(DatabaseSafety.SchemaVersion, await DatabaseSafety.Inspect(paths.Database));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // A lingering file handle on Windows is not worth failing the assertion over.
            }
        }
    }
}
