using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace School.Training.Tests;

public sealed class BackupSpaceTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("school-backup-space-");

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0L, 2)]
    public async Task RefusalBeforeBackupLeavesExistingArchiveUnchanged(long? available, int expectedReads)
    {
        var paths = new AppPaths(directory.FullName);
        var original = Path.Combine(paths.Backups, "school-20260101000000-00000000.zip");
        await File.WriteAllTextAsync(original, "existing backup");
        await using var db = Open(paths);
        var reads = 0;

        var result = await BackupService.CreateAsync(paths, db,
            new StorageOptions { BackupMinimumFreeBytes = 1024 }, "admin", "创建备份", path =>
            {
                Assert.Equal(paths.Backups, path);
                reads++;
                return available;
            }, CancellationToken.None);

        Assert.Equal(507, result.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(expectedReads, reads);
        Assert.Equal(new[] { original }, Directory.GetFiles(paths.Backups));
        Assert.Equal("existing backup", await File.ReadAllTextAsync(original));
        Assert.False(File.Exists(paths.Database));
    }

    [Fact]
    public async Task FullSqliteWriteAfterSuccessfulSpaceCheckReturns507()
    {
        var paths = new AppPaths(directory.FullName);
        await using var db = Open(paths);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA page_count";
        var pageCount = Convert.ToInt64(await command.ExecuteScalarAsync());
        command.CommandText = $"PRAGMA max_page_count={pageCount}";
        Assert.Equal(pageCount, await command.ExecuteScalarAsync());

        // Force a real SQLITE_FULL on the audit insert without filling the host volume.
        var result = await BackupService.CreateAsync(paths, db, new StorageOptions(),
            new string('x', 262_144), "创建备份", _ => long.MaxValue, CancellationToken.None);

        Assert.Equal(507, result.StatusCode);
        Assert.False(result.Success);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Backups));
        Assert.Equal(0, await db.Audits.CountAsync());
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070), true)]
    [InlineData(unchecked((int)0x80070027), true)]
    [InlineData(unchecked((int)0x80070005), false)]
    public void WindowsWriteCodesDistinguishFullDiskFromOtherIoFailures(int hresult, bool full)
        => Assert.Equal(full, BackupService.IsDiskFull(new IOException("test write failure", hresult)));

    [Fact]
    public void DirectoryProbeReadsAnExistingDirectoryAndRejectsMissingDirectories()
    {
        Assert.True(BackupService.AvailableFreeBytes(directory.FullName) is >= 0);
        Assert.Null(BackupService.AvailableFreeBytes(Path.Combine(directory.FullName, "missing")));
    }

    private static SchoolDbContext Open(AppPaths paths)
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString();
        return new SchoolDbContext(new DbContextOptionsBuilder<SchoolDbContext>().UseSqlite(connection).Options);
    }

    public void Dispose() => directory.Delete(recursive: true);
}
