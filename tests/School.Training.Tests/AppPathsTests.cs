namespace School.Training.Tests;

public sealed class AppPathsTests : IDisposable
{
    readonly DirectoryInfo root = Directory.CreateTempSubdirectory("school-paths-");

    [Fact]
    public void Default_backup_directory_stays_under_the_data_directory()
    {
        var paths = new AppPaths(Path.Combine(root.FullName, "data"));
        Assert.Equal(Path.Combine(paths.Data, "backups"), paths.Backups);
        Assert.True(Directory.Exists(paths.Backups));
    }

    [Fact]
    public void Custom_backup_directory_is_created_outside_the_data_directory()
    {
        var backups = Path.Combine(root.FullName, "backups");
        var paths = new AppPaths(Path.Combine(root.FullName, "data"), backups);
        Assert.Equal(Path.GetFullPath(backups), paths.Backups);
        Assert.NotEqual(paths.Data, paths.Backups);
        Assert.True(Directory.Exists(paths.Backups));
    }

    [Fact]
    public void Relative_backup_directory_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new AppPaths(Path.Combine(root.FullName, "data-relative"), "relative/backups"));
    }

    [Fact]
    public void Backup_directory_cannot_equal_the_data_directory()
    {
        var data = Path.Combine(root.FullName, "data-same");
        Assert.Throws<InvalidOperationException>(() => new AppPaths(data, data));
    }

    public void Dispose()
    {
        try { root.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
