using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class DatabaseBackups
{
    public static async Task CreateSnapshotAsync(SchoolDbContext db, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var source = new SqliteConnection(db.Database.GetConnectionString());
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        await source.OpenAsync(); await target.OpenAsync();
        source.BackupDatabase(target);
    }
}
