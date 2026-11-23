using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public sealed record DiagnosticsSnapshot(
    DateTime GeneratedAtUtc, object Status, object Settings, object System, object Database, IReadOnlyList<string> LogFiles);

/// <summary>
/// Builds an administrator-only support bundle: status, configuration, database counters and
/// the tail of recent service logs. Passwords, session cookies, DataProtection keys and the
/// database itself are deliberately excluded.
/// </summary>
public static class DiagnosticsBundle
{
    const int MaxLogFiles = 3;
    const int MaxLogBytes = 512 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    static readonly string Readme = """
        SchoolTraining 诊断包

        status.json     服务状态、版本、数据库结构版本
        settings.json   数据/备份目录、保留策略与自动任务配置
        system.json     主机、进程与运行时信息
        database.json   表计数、文件大小与 PRAGMA 检查结果
        logs/           最近服务日志尾部（每个文件最多 512 KB）
        manifest.json   文件清单与生成时间

        不包含密码、会话 Cookie、DataProtection 密钥或数据库本体。
        诊断包只应交给受信任的支持人员。

        """;

    public static async Task<DiagnosticsSnapshot> CollectAsync(AppPaths paths, SchoolDbContext db, StorageOptions storage,
        AutomaticBackupOptions backup, AuditRetentionOptions retention, RollingFileLoggerProvider logs, CancellationToken ct)
    {
        var generatedAt = DateTime.UtcNow;
        var schoolName = await db.Settings.Where(x => x.Id == "schoolName").Select(x => x.Value).FirstOrDefaultAsync(ct) ?? "";
        var status = new
        {
            initialized = await db.Users.AnyAsync(ct),
            product = "实训资源管理平台",
            version = BuildInfo.Version,
            schemaVersion = DatabaseSafety.SchemaVersion,
            logWriteFailures = logs.WriteFailures,
            maxUploadBytes = Limits.MaxUploadBytes,
            schoolName
        };
        var settings = new
        {
            schoolName,
            dataDirectory = paths.Data,
            backupDirectory = paths.Backups,
            logRetentionDays = storage.LogRetentionDays,
            backupRetentionCount = storage.BackupRetentionCount,
            backupMaxTotalBytes = storage.BackupMaxTotalBytes,
            backupMinimumFreeBytes = storage.BackupMinimumFreeBytes,
            automaticBackup = new
            {
                enabled = backup.Enabled,
                timeOfDay = backup.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture)
            },
            auditRetention = new
            {
                retentionDays = retention.RetentionDays,
                cleanupTimeOfDay = retention.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture)
            }
        };
        using var process = Process.GetCurrentProcess();
        var system = new
        {
            machineName = Environment.MachineName,
            os = Environment.OSVersion.VersionString,
            framework = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            processUptimeSeconds = (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds,
            workingSetBytes = process.WorkingSet64,
            baseDirectory = AppContext.BaseDirectory
        };
        var database = await ReadDatabaseAsync(paths, db, ct);
        var logFiles = Directory.EnumerateFiles(paths.Logs, "service-*.log")
            .Select(file => new FileInfo(file))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaxLogFiles)
            .Select(file => file.FullName)
            .ToArray();
        return new DiagnosticsSnapshot(generatedAt, status, settings, system, database, logFiles);
    }

    static async Task<object> ReadDatabaseAsync(AppPaths paths, SchoolDbContext db, CancellationToken ct)
    {
        var counts = new
        {
            users = await db.Users.CountAsync(ct),
            people = await db.People.CountAsync(ct),
            courses = await db.Courses.CountAsync(ct),
            exams = await db.TrainingExams.CountAsync(ct),
            classrooms = await db.Classrooms.CountAsync(ct),
            cabinets = await db.Cabinets.CountAsync(ct),
            devices = await db.Devices.CountAsync(ct),
            nodes = await db.Nodes.CountAsync(ct),
            virtualMachines = await db.VirtualMachines.CountAsync(ct),
            audits = await db.Audits.CountAsync(ct)
        };
        var file = new FileInfo(paths.Database);
        var wal = new FileInfo(paths.Database + "-wal");
        var pragmas = new Dictionary<string, object?>();
        try
        {
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(ct);
            foreach (var pragma in new[] { "page_count", "page_size", "freelist_count", "journal_mode", "quick_check" })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA {pragma};";
                pragmas[pragma] = await command.ExecuteScalarAsync(ct);
            }
        }
        catch (Exception error) when (error is SqliteException or InvalidOperationException or IOException)
        {
            pragmas["error"] = error.Message;
        }
        finally { await db.Database.CloseConnectionAsync(); }
        return new
        {
            fileBytes = file.Exists ? file.Length : 0,
            walBytes = wal.Exists ? wal.Length : 0,
            counts,
            pragmas
        };
    }

    public static async Task<IResult> CreateResultAsync(DiagnosticsSnapshot snapshot, CancellationToken ct)
    {
        var fileName = $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        // The bundle is a few megabytes at most. Buffering lets a failure surface as a normal
        // JSON error instead of a truncated ZIP whose HTTP status was already committed.
        var buffer = new MemoryStream();
        await WriteAsync(buffer, snapshot, ct);
        buffer.Position = 0;
        return Results.File(buffer, "application/zip", fileName);
    }

    internal static async Task WriteAsync(Stream destination, DiagnosticsSnapshot snapshot, CancellationToken ct)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var entries = new List<string> { "README.txt", "status.json", "settings.json", "system.json", "database.json" };
        await WriteTextAsync(archive, "README.txt", Readme, ct);
        await WriteJsonAsync(archive, "status.json", snapshot.Status, ct);
        await WriteJsonAsync(archive, "settings.json", snapshot.Settings, ct);
        await WriteJsonAsync(archive, "system.json", snapshot.System, ct);
        await WriteJsonAsync(archive, "database.json", snapshot.Database, ct);
        foreach (var file in snapshot.LogFiles)
        {
            ct.ThrowIfCancellationRequested();
            var name = "logs/" + Path.GetFileName(file);
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            await using var target = entry.Open();
            var header = $"# {Path.GetFileName(file)} · {new FileInfo(file).Length} bytes · tail {MaxLogBytes}\n";
            await target.WriteAsync(Encoding.UTF8.GetBytes(header), ct);
            CopyTail(file, target);
            entries.Add(name);
        }
        entries.Add("manifest.json");
        await WriteJsonAsync(archive, "manifest.json", new
        {
            product = "SchoolTraining",
            version = BuildInfo.Version,
            schemaVersion = DatabaseSafety.SchemaVersion,
            generatedAtUtc = snapshot.GeneratedAtUtc,
            entries
        }, ct);
    }

    static async Task WriteJsonAsync(ZipArchive archive, string name, object value, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
    }

    static async Task WriteTextAsync(ZipArchive archive, string name, string value, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value), ct);
    }

    static void CopyTail(string path, Stream destination)
    {
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, source.Length - MaxLogBytes);
            source.Seek(start, SeekOrigin.Begin);
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var offset = 0;
            if (start > 0)
            {
                var newline = Array.IndexOf(bytes, (byte)'\n');
                offset = newline >= 0 ? newline + 1 : bytes.Length;
            }
            destination.Write(bytes, offset, bytes.Length - offset);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            destination.Write(Encoding.UTF8.GetBytes($"（读取日志失败：{error.Message}）\n"));
        }
    }
}
