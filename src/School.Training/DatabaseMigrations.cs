using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace School.Training;

/// <summary>
/// Ordered, idempotent schema upgrades anchored on SQLite PRAGMA user_version.
/// A database snapshot is written before any v1 database is changed.
/// </summary>
public static class DatabaseMigrations
{
    public const int CurrentVersion = DatabaseSafety.SchemaVersion;
    const string ClassIndexName = "IX_Classes_ProfessionId_Name";

    public static async Task MigrateAsync(SchoolDbContext db, AppPaths paths, StorageOptions options, ILogger logger, CancellationToken cancellationToken = default)
    {
        var exists = File.Exists(paths.Database) && new FileInfo(paths.Database).Length > 0;
        if (!exists)
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync($"PRAGMA application_id={DatabaseSafety.ApplicationId};", cancellationToken);
            await db.Database.ExecuteSqlRawAsync($"PRAGMA user_version={CurrentVersion};", cancellationToken);
            logger.LogInformation("Created database schema version {Version}.", CurrentVersion);
            return;
        }

        var version = await DatabaseSafety.Inspect(paths.Database);
        if (version > CurrentVersion) throw new InvalidDataException($"数据库结构版本 {version} 高于当前程序支持的版本 {CurrentVersion}，请升级程序后再启动");
        if (version < 1) throw new InvalidDataException("数据库缺少有效的结构版本标记，拒绝自动迁移");
        if (version == CurrentVersion) return;

        var snapshot = Path.Combine(paths.Backups, $"pre-migration-v{version}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.db");
        await DatabaseBackups.CreateSnapshotAsync(db, snapshot);
        FileRetention.Apply(paths.Backups, "pre-migration-*.db", options.PreMigrationBackupCount, long.MaxValue, protectedFile: snapshot);
        logger.LogWarning("Database schema upgrade started from version {From} to {To}; pre-upgrade snapshot: {Snapshot}", version, CurrentVersion, snapshot);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (version == 1)
            {
                var duplicates = await FindDuplicateClasses(db, cancellationToken);
                if (duplicates.Count > 0)
                    throw new InvalidOperationException("无法建立班级唯一约束，存在同专业同名班级，请先合并或重命名：" + string.Join("；", duplicates));
                await db.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{ClassIndexName}\";", cancellationToken);
                await db.Database.ExecuteSqlRawAsync($"CREATE UNIQUE INDEX \"{ClassIndexName}\" ON \"Classes\" (\"ProfessionId\", \"Name\");", cancellationToken);
                version = 2;
            }
            if (version == 2)
            {
                await AddColumnIfMissingAsync(db, "ImportBatches", "Status", "\"Status\" TEXT NOT NULL DEFAULT 'Preview'", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "RemoveInvalidRows", "\"RemoveInvalidRows\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "TotalCount", "\"TotalCount\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "ProcessedCount", "\"ProcessedCount\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "ImportedCount", "\"ImportedCount\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "RemovedCount", "\"RemovedCount\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "FailureMessage", "\"FailureMessage\" TEXT NOT NULL DEFAULT ''", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "QueuedAt", "\"QueuedAt\" TEXT NULL", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "StartedAt", "\"StartedAt\" TEXT NULL", cancellationToken);
                await AddColumnIfMissingAsync(db, "ImportBatches", "CompletedAt", "\"CompletedAt\" TEXT NULL", cancellationToken);
                await db.Database.ExecuteSqlRawAsync("""
                    UPDATE "ImportBatches"
                    SET "Status" = CASE WHEN "Committed" = 1 THEN 'Completed' ELSE 'Preview' END;
                    """, cancellationToken);
                version = 3;
            }
            if (version == 3)
            {
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Audits_At_Id\" ON \"Audits\" (\"At\", \"Id\");", cancellationToken);
                version = 4;
            }
            await db.Database.ExecuteSqlRawAsync($"PRAGMA user_version={CurrentVersion};", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        logger.LogInformation("Database schema upgraded to version {Version}.", version);
    }

    static async Task AddColumnIfMissingAsync(SchoolDbContext db, string table, string column, string definition, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var info = connection.CreateCommand();
        info.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        info.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using (var reader = await info.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }
        await using var alter = connection.CreateCommand();
        alter.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    static async Task<List<string>> FindDuplicateClasses(SchoolDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            SELECT "ProfessionId", "Name", COUNT(*) AS "Count"
            FROM "Classes"
            GROUP BY "ProfessionId", "Name"
            HAVING COUNT(*) > 1
            ORDER BY "Count" DESC
            LIMIT 20;
            """;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add($"{reader.GetString(1)}（专业 {reader.GetString(0)}，{reader.GetInt64(2)} 条）");
        return result;
    }
}
