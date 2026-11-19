using School.Training;

namespace School.Training.Tests;

public sealed class FileRetentionTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "school-retention-" + Guid.NewGuid().ToString("N"));
    static readonly DateTime Now = new(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);

    public FileRetentionTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    string Create(string name, int bytes, TimeSpan age)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    [Fact]
    public void Apply_DeletesOldestFilesBeyondCount()
    {
        Create("a.log", 10, TimeSpan.FromHours(3));
        Create("b.log", 10, TimeSpan.FromHours(2));
        Create("c.log", 10, TimeSpan.FromHours(1));

        var deleted = FileRetention.Apply(directory, "*.log", maxCount: 2, maxTotalBytes: long.MaxValue);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(directory, "a.log")));
        Assert.True(File.Exists(Path.Combine(directory, "b.log")));
        Assert.True(File.Exists(Path.Combine(directory, "c.log")));
    }

    [Fact]
    public void Apply_EnforcesTotalSizeBudgetFromNewestToOldest()
    {
        Create("a.log", 100, TimeSpan.FromHours(3));
        Create("b.log", 100, TimeSpan.FromHours(2));
        Create("c.log", 100, TimeSpan.FromHours(1));

        var deleted = FileRetention.Apply(directory, "*.log", maxCount: 10, maxTotalBytes: 150);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(Path.Combine(directory, "a.log")));
        Assert.False(File.Exists(Path.Combine(directory, "b.log")));
        Assert.True(File.Exists(Path.Combine(directory, "c.log")));
    }

    [Fact]
    public void Apply_DeletesFilesOlderThanThreshold()
    {
        Create("old.log", 10, TimeSpan.FromDays(10));
        Create("new.log", 10, TimeSpan.FromHours(1));

        var deleted = FileRetention.Apply(directory, "*.log", maxCount: 10, maxTotalBytes: long.MaxValue, olderThanUtc: Now.AddDays(-5));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(directory, "old.log")));
        Assert.True(File.Exists(Path.Combine(directory, "new.log")));
    }

    [Fact]
    public void Apply_NeverDeletesProtectedFile()
    {
        Create("a.log", 10, TimeSpan.FromHours(3));
        Create("b.log", 10, TimeSpan.FromHours(2));
        var protectedPath = Create("c.log", 10, TimeSpan.FromHours(1));

        var deleted = FileRetention.Apply(directory, "*.log", maxCount: 1, maxTotalBytes: long.MaxValue, protectedFile: protectedPath);

        Assert.Equal(2, deleted);
        Assert.True(File.Exists(protectedPath));
        Assert.False(File.Exists(Path.Combine(directory, "a.log")));
        Assert.False(File.Exists(Path.Combine(directory, "b.log")));
    }

    [Fact]
    public void Apply_OnlyTouchesMatchingFilesAtTopLevel()
    {
        Create("a.log", 10, TimeSpan.FromHours(3));
        Create("keep.txt", 10, TimeSpan.FromHours(3));
        var nested = Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllBytes(Path.Combine(nested.FullName, "nested.log"), new byte[10]);

        var deleted = FileRetention.Apply(directory, "*.log", maxCount: 1, maxTotalBytes: long.MaxValue);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(Path.Combine(directory, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(nested.FullName, "nested.log")));
    }

    [Fact]
    public void Apply_ReturnsZeroForMissingDirectoryOrInvalidLimit()
    {
        Assert.Equal(0, FileRetention.Apply(Path.Combine(directory, "missing"), "*.log", 5, long.MaxValue));
        Assert.Equal(0, FileRetention.Apply(directory, "*.log", maxCount: 0, maxTotalBytes: long.MaxValue));
        Assert.Equal(0, FileRetention.Apply(directory, "*.log", maxCount: 5, maxTotalBytes: -1));
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        Create("a.log", 10, TimeSpan.FromHours(2));
        Create("b.log", 10, TimeSpan.FromHours(1));

        Assert.Equal(1, FileRetention.Apply(directory, "*.log", 1, long.MaxValue));
        Assert.Equal(0, FileRetention.Apply(directory, "*.log", 1, long.MaxValue));
    }
}
