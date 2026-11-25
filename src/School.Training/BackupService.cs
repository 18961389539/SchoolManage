using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public sealed record BackupResult(bool Success, string Message, string FileName, DateTime CreatedAt, long Size, int StatusCode)
{
    public IResult ToHttpResult() => Success
        ? Results.Ok(new { fileName = FileName, createdAt = CreatedAt, size = Size })
        : Results.Json(new { message = Message }, statusCode: StatusCode);
}

public static class BackupService
{
    internal const long MaxRestoreEntryBytes = 8L * 1024 * 1024 * 1024;
    internal const long MaxRestoreUncompressedBytes = 20L * 1024 * 1024 * 1024;
    internal const int MaxRestoreEntries = 100_000;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };

    public static async Task<BackupResult> CreateAsync(AppPaths paths, SchoolDbContext db, StorageOptions storage,
        string userName, string auditAction, Func<string, long?> readFreeBytes, CancellationToken ct)
    {
        var free = readFreeBytes(paths.Backups);
        if (free is null) return Failure("无法读取备份目录所在磁盘的剩余空间，请人工检查磁盘后再试");
        if (free < storage.BackupMinimumFreeBytes)
        {
            FileRetention.Apply(paths.Backups, "school-*.zip", storage.BackupRetentionCount, long.MaxValue);
            var afterRetention = readFreeBytes(paths.Backups);
            if (afterRetention is null) return Failure("无法读取备份目录所在磁盘的剩余空间，请人工检查磁盘后再试");
            if (afterRetention < storage.BackupMinimumFreeBytes) return Failure("备份目录所在磁盘剩余空间不足，请清理后重试");
        }

        var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var dbPath = Path.Combine(paths.Backups, "pending-" + suffix + ".db");
        var finalPath = Path.Combine(paths.Backups, "school-" + suffix + ".zip");
        var pendingPath = finalPath + ".pending";
        try
        {
            db.Audits.Add(new AuditEntry { UserName = userName, Action = auditAction, Target = Path.GetFileName(finalPath) });
            await db.SaveChangesAsync(ct);
            await using (var source = new SqliteConnection(db.Database.GetConnectionString()))
            await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
            {
                await source.OpenAsync(ct);
                await destination.OpenAsync(ct);
                source.BackupDatabase(destination);
            }
            if (!FitsRestoreLimits(paths, dbPath, out var limitError)) return Failure(limitError!, StatusCodes.Status413PayloadTooLarge);
            using (var zip = ZipFile.Open(pendingPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(dbPath, "school.db", CompressionLevel.Optimal);
                zip.CreateEntryFromFile(Path.Combine(paths.Data, ".schooltraining-data.json"), ".schooltraining-data.json", CompressionLevel.Optimal);
                foreach (var key in Directory.EnumerateFiles(paths.Keys, "*.xml")) zip.CreateEntryFromFile(key, "keys/" + Path.GetFileName(key), CompressionLevel.Optimal);
                AddBusinessFiles(zip, paths.CourseFiles, "course-files");
                AddBusinessFiles(zip, paths.CaseEvidence, "case-evidence");
                var entry = zip.CreateEntry("manifest.json");
                await using var stream = entry.Open();
                await JsonSerializer.SerializeAsync(stream, new { product = "SchoolTraining", schemaVersion = 1, databaseSchemaVersion = DatabaseSafety.SchemaVersion, version = BuildInfo.Version, createdAt = DateTime.UtcNow }, JsonOptions, ct);
            }
            File.Move(pendingPath, finalPath);
            FileRetention.Apply(paths.Backups, "school-*.zip", storage.BackupRetentionCount, storage.BackupMaxTotalBytes, protectedFile: finalPath);
            var info = new FileInfo(finalPath);
            return new BackupResult(true, "备份已创建", info.Name, info.CreationTimeUtc, info.Length, StatusCodes.Status200OK);
        }
        catch (Exception error) when (IsDiskFull(error))
        {
            return Failure("创建备份时磁盘空间耗尽，请清理后重试");
        }
        finally
        {
            foreach (var path in new[] { pendingPath, dbPath, dbPath + "-journal", dbPath + "-wal", dbPath + "-shm" })
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    static bool FitsRestoreLimits(AppPaths paths, string databaseSnapshot, out string? error)
    {
        long total = new FileInfo(databaseSnapshot).Length + new FileInfo(Path.Combine(paths.Data, ".schooltraining-data.json")).Length;
        var entries = 4;
        foreach (var directory in new[] { paths.Keys, paths.CourseFiles, paths.CaseEvidence })
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var length = new FileInfo(file).Length;
                if (length > MaxRestoreEntryBytes)
                {
                    error = "单个业务文件超过 8GB，当前备份无法保证可恢复；请拆分或联系供应商调整限制";
                    return false;
                }
                checked { total += length; entries++; }
                if (total > MaxRestoreUncompressedBytes || entries > MaxRestoreEntries)
                {
                    error = "业务文件总量超过可恢复上限，当前备份无法保证可恢复；请联系供应商调整恢复限制";
                    return false;
                }
            }
        }
        error = null;
        return true;
    }

    static void AddBusinessFiles(ZipArchive zip, string directory, string entryRoot)
    {
        zip.CreateEntry(entryRoot + "/");
        var root = Path.GetFullPath(directory);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file);
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
                throw new InvalidOperationException("业务文件路径不能超出数据目录");
            zip.CreateEntryFromFile(file, entryRoot + "/" + relative.Replace('\\', '/'), CompressionLevel.Optimal);
        }
    }

    static BackupResult Failure(string message, int statusCode = StatusCodes.Status507InsufficientStorage)
        => new(false, message, "", default, 0, statusCode);

    internal static bool IsDiskFull(Exception error) => error switch
    {
        IOException { HResult: unchecked((int)0x80070070) or unchecked((int)0x80070027) } => true,
        SqliteException { SqliteErrorCode: SQLitePCL.raw.SQLITE_FULL } => true,
        DbUpdateException { InnerException: SqliteException { SqliteErrorCode: SQLitePCL.raw.SQLITE_FULL } } => true,
        _ => false
    };

    /// <summary>
    /// Returns null when free space cannot be determined. Backup callers must treat
    /// "unknown" as insufficient rather than proceeding with a write that may fill the disk.
    /// </summary>
    internal static long? AvailableFreeBytes(string path)
    {
        try
        {
            var directory = Path.GetFullPath(path);
            if (!Directory.Exists(directory)) return null;
            if (OperatingSystem.IsWindows())
            {
                // Query the actual directory to account for mounted volumes and the service account's quota.
                if (!Path.EndsInDirectorySeparator(directory)) directory += Path.DirectorySeparatorChar;
                return GetDiskFreeSpaceEx(directory, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
            }
            var root = Path.GetPathRoot(directory);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
