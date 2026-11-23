using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using School.Training;

namespace School.Training.DesignHost;

/// <summary>
/// Keeps `dotnet ef` away from the real service data directory: the tooling builds the model
/// against an isolated scratch database instead of running the application's startup migration.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SchoolDbContext>
{
    public SchoolDbContext CreateDbContext(string[] args)
    {
        var directory = Environment.GetEnvironmentVariable("SCHOOL_DESIGN_DATA_DIR")
            ?? Path.Combine(Path.GetTempPath(), "schooltraining-design");
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "design.db"),
            ForeignKeys = true
        }.ToString();
        return new SchoolDbContext(new DbContextOptionsBuilder<SchoolDbContext>().UseSqlite(connection).Options);
    }
}
