using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace School.Training;

public partial class SchoolDbContext { public DbSet<LicenseRecord> Licenses => Set<LicenseRecord>(); }
public sealed class LicenseRecord
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}
public sealed record LicensePayload(string LicenseId, string Product, string MachineCode, DateTime IssuedAt, DateTime? ExpiresAt, List<LicenseResource> Resources);
public sealed record LicenseResource(string Name, string AuthorizationCode, int Quantity);
public sealed record LicenseEnvelope(string Payload, string Signature);
public sealed record SettingsRequest(string SchoolName);

public static class OperationsEndpoints
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };
    public static void MapOperationsEndpoints(this RouteGroupBuilder all)
    {
        all.MapGet("/dashboard", async (HttpContext ctx, SchoolDbContext db, CancellationToken ct) => Results.Ok(new
        {
            schoolName = await db.Settings.Where(x => x.Id == "schoolName").Select(x => x.Value).FirstOrDefaultAsync(ct) ?? "实训资源管理平台",
            counts = new { professions = await db.Professions.CountAsync(ct), classes = await db.Classes.CountAsync(ct), teachers = await db.People.CountAsync(x => x.Kind == "teacher", ct), students = await db.People.CountAsync(x => x.Kind == "student", ct), cabinets = await db.Cabinets.CountAsync(ct), devices = await db.Devices.CountAsync(ct), nodes = await db.Nodes.CountAsync(ct) },
            recentAudits = ctx.User.IsInRole("Admin") ? await db.Audits.AsNoTracking().OrderByDescending(x => x.At).ThenByDescending(x => x.Id).Take(8).ToListAsync(ct) : [],
            server = new { machineName = Environment.MachineName, os = Environment.OSVersion.VersionString, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription },
            environment = "simulated-resources",
            version = BuildInfo.Version
        }));
        var api = all.MapGroup("").RequireAuthorization("Admin");
        api.MapGet("/audits", async (HttpRequest req, SchoolDbContext db, CancellationToken ct) =>
        {
            var q = db.Audits.AsNoTracking(); var search = req.Query["search"].ToString(); if (search.Length > 0) q = q.Where(x => x.UserName.Contains(search) || x.Action.Contains(search) || x.Target.Contains(search));
            var (page, size) = Validation.Page(req); return Results.Ok(new { total = await q.CountAsync(ct), items = await q.OrderByDescending(x => x.At).ThenByDescending(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct) });
        });
        api.MapGet("/settings", async (SchoolDbContext db, AppPaths paths, CancellationToken ct) => Results.Ok(new { schoolName = await db.Settings.Where(x => x.Id == "schoolName").Select(x => x.Value).FirstOrDefaultAsync(ct) ?? "", backupDirectory = paths.Backups, dataDirectory = paths.Data, version = BuildInfo.Version, resourceMode = "simulated", backupIncludesKeys = true }));
        api.MapPut("/settings", async (SettingsRequest input, HttpContext ctx, SchoolDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(input.SchoolName) || input.SchoolName.Length > 100) return Results.BadRequest(new { message = "学校名称必填，最多100字" });
            var setting = await db.Settings.FindAsync(["schoolName"], ctx.RequestAborted); if (setting == null) db.Settings.Add(new AppSetting { Id = "schoolName", Value = input.SchoolName.Trim() }); else setting.Value = input.SchoolName.Trim();
            Audit.Add(db, ctx, "修改系统设置", "schoolName"); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { message = "已保存" });
        });
        api.MapGet("/backups", (AppPaths paths) => Results.Ok(new { items = Directory.EnumerateFiles(paths.Backups, "school-*.zip").Select(f => new FileInfo(f)).OrderByDescending(f => f.CreationTimeUtc).Select(f => new { fileName = f.Name, createdAt = f.CreationTimeUtc, size = f.Length }).ToArray() }));
        api.MapPost("/backups", async (AppPaths paths, SchoolDbContext db, StorageOptions storage, HttpContext ctx) =>
        {
            var ct = ctx.RequestAborted;
            var free = AvailableFreeBytes(paths.Backups);
            if (free is null) return Results.Json(new { message = "无法读取备份目录所在磁盘的剩余空间，请人工检查磁盘后再试" }, statusCode: 507);
            if (free < storage.BackupMinimumFreeBytes)
            {
                FileRetention.Apply(paths.Backups, "school-*.zip", storage.BackupRetentionCount, long.MaxValue);
                var afterRetention = AvailableFreeBytes(paths.Backups);
                if (afterRetention is null) return Results.Json(new { message = "无法读取备份目录所在磁盘的剩余空间，请人工检查磁盘后再试" }, statusCode: 507);
                if (afterRetention < storage.BackupMinimumFreeBytes)
                    return Results.Json(new { message = "备份目录所在磁盘剩余空间不足，请清理后重试" }, statusCode: 507);
            }
            var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
            var dbPath = Path.Combine(paths.Backups, "pending-" + suffix + ".db");
            var finalPath = Path.Combine(paths.Backups, "school-" + suffix + ".zip");
            var pendingPath = finalPath + ".pending";
            try
            {
                Audit.Add(db, ctx, "创建备份", Path.GetFileName(finalPath)); await db.SaveChangesAsync(ct);
                await using (var source = new SqliteConnection(db.Database.GetConnectionString()))
                await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
                { await source.OpenAsync(ct); await destination.OpenAsync(ct); source.BackupDatabase(destination); }
                using (var zip = ZipFile.Open(pendingPath, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(dbPath, "school.db", CompressionLevel.Optimal);
                    zip.CreateEntryFromFile(Path.Combine(paths.Data, ".schooltraining-data.json"), ".schooltraining-data.json", CompressionLevel.Optimal);
                    foreach (var key in Directory.EnumerateFiles(paths.Keys, "*.xml")) zip.CreateEntryFromFile(key, "keys/" + Path.GetFileName(key), CompressionLevel.Optimal);
                    var entry = zip.CreateEntry("manifest.json"); await using var stream = entry.Open();
                    await JsonSerializer.SerializeAsync(stream, new { product = "SchoolTraining", schemaVersion = 1, databaseSchemaVersion = DatabaseSafety.SchemaVersion, version = BuildInfo.Version, createdAt = DateTime.UtcNow }, JsonOptions, ct);
                }
                File.Move(pendingPath, finalPath);
                FileRetention.Apply(paths.Backups, "school-*.zip", storage.BackupRetentionCount, storage.BackupMaxTotalBytes, protectedFile: finalPath);
                var info = new FileInfo(finalPath);
                return Results.Ok(new { fileName = info.Name, createdAt = info.CreationTimeUtc, size = info.Length });
            }
            finally { if (File.Exists(dbPath)) File.Delete(dbPath); if (File.Exists(pendingPath)) File.Delete(pendingPath); }
        });
        api.MapGet("/backups/{fileName}", async (string fileName, AppPaths paths, HttpContext ctx, SchoolDbContext db) =>
        {
            if (Path.GetFileName(fileName) != fileName || !System.Text.RegularExpressions.Regex.IsMatch(fileName, "^school-[0-9]{14}-[a-f0-9]{8}\\.zip$")) return Results.NotFound();
            var path = Path.Combine(paths.Backups, fileName); if (!File.Exists(path)) return Results.NotFound(new { message = "备份文件不存在" });
            Audit.Add(db, ctx, "下载备份", fileName); await db.SaveChangesAsync(ctx.RequestAborted); return Results.File(path, "application/zip", fileName);
        });
        api.MapGet("/licenses", async (SchoolDbContext db, IConfiguration config, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var records = await db.Licenses.AsNoTracking().OrderByDescending(x => x.ImportedAt).ToListAsync(ct);
            return Results.Ok(new
            {
                machineCode = MachineCode(),
                publicKeyConfigured = File.Exists(PublicKeyPath(config, env)),
                mode = "simulated",
                items = records.Select(r =>
            {
                var payload = JsonSerializer.Deserialize<LicensePayload>(r.PayloadJson, JsonOptions)!;
                return new { r.Id, productAuthorizationCode = r.Id, r.FileName, r.ImportedAt, payload.IssuedAt, payload.ExpiresAt, payload.Resources, valid = payload.ExpiresAt == null || payload.ExpiresAt > DateTime.UtcNow };
            }).ToArray()
            });
        });
        api.MapPost("/licenses", async (HttpContext ctx, SchoolDbContext db, IConfiguration config, IWebHostEnvironment env) =>
        {
            if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { message = "请选择许可证文件" });
            var files = (await ctx.Request.ReadFormAsync(ctx.RequestAborted)).Files; if (files.Count is < 1 or > 20) return Results.BadRequest(new { message = "每次上传1–20个许可证文件" });
            var keyPath = PublicKeyPath(config, env); if (!File.Exists(keyPath)) return Results.BadRequest(new { message = "未配置供应商验签公钥，请由交付人员配置license-public.pem；模拟资源可正常使用" });
            using var rsa = RSA.Create(); rsa.ImportFromPem(await File.ReadAllTextAsync(keyPath, ctx.RequestAborted));
            var pending = new List<LicenseRecord>();
            foreach (var file in files)
            {
                if (file.Length is < 1 or > 262144) return Results.BadRequest(new { message = "许可证文件大小须在256KB以内" });
                try
                {
                    using var reader = new StreamReader(file.OpenReadStream()); var envelope = JsonSerializer.Deserialize<LicenseEnvelope>(await reader.ReadToEndAsync(ctx.RequestAborted), JsonOptions);
                    if (envelope is null) return Results.BadRequest(new { message = "许可证不能为空" });
                    var bytes = Convert.FromBase64String(envelope.Payload); var signature = Convert.FromBase64String(envelope.Signature);
                    if (!rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return Results.BadRequest(new { message = "许可证签名无效" });
                    var payload = JsonSerializer.Deserialize<LicensePayload>(bytes, JsonOptions);
                    if (payload is null || payload.Resources is null || payload.Resources.Any(r => r is null)) return Results.BadRequest(new { message = "许可证资源列表不能为空" });
                    if (payload.Product != "SchoolTraining" || payload.MachineCode != MachineCode()) return Results.BadRequest(new { message = "许可证产品或设备特征码不匹配" });
                    if (payload.ExpiresAt != null && payload.ExpiresAt <= DateTime.UtcNow) return Results.BadRequest(new { message = "许可证已过期" });
                    if (payload.LicenseId.Length is < 1 or > 100 || payload.Resources.Count is < 1 or > 100 || payload.Resources.Any(r => string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.AuthorizationCode) || r.Quantity < 0)) return Results.BadRequest(new { message = "许可证资源列表不完整" });
                    if (await db.Licenses.AnyAsync(x => x.Id == payload.LicenseId, ctx.RequestAborted) || pending.Any(x => x.Id == payload.LicenseId)) return Results.Conflict(new { message = "许可证已导入" });
                    pending.Add(new LicenseRecord { Id = payload.LicenseId, FileName = Path.GetFileName(file.FileName), PayloadJson = Encoding.UTF8.GetString(bytes) });
                }
                catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
                { return Results.BadRequest(new { message = "许可证格式或签名不正确" }); }
            }
            db.Licenses.AddRange(pending); Audit.Add(db, ctx, "导入许可证", string.Join(",", pending.Select(x => x.Id))); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { importedCount = pending.Count });
        });
        api.MapGet("/licenses/guide", () => Results.File(Encoding.UTF8.GetBytes("# 许可证使用说明\n\n1.复制许可证页面的设备特征码，交给供应商签发。\n2.交付人员配置供应商公钥license-public.pem（仅公钥，禁止分发私钥）。\n3.上传包含payload与signature的JSON许可证，系统校验RSA-SHA256签名、产品、设备绑定及有效期。\n4.资源列表展示资源名称、授权码与数量。\n5.当前交付采用模拟资源；许可证资源数量如何限制真实硬件分配需按最终授权规则确认。\n6.迁移机器需重新签发；备份与恢复操作应由管理员执行。\n"), "text/markdown; charset=utf-8", "license-guide.md"));
    }
    public static string MachineCode()
    {
        string machine = Environment.MachineName;
        if (OperatingSystem.IsWindows()) { using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"); machine += "|" + key?.GetValue("MachineGuid"); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("SchoolTraining|" + machine)))[..32];
    }
    static string PublicKeyPath(IConfiguration config, IWebHostEnvironment env) => Environment.GetEnvironmentVariable("SCHOOL_LICENSE_PUBLIC_KEY") ?? config["School:LicensePublicKey"] ?? Path.Combine(env.ContentRootPath, "license-public.pem");
    /// <summary>
    /// Returns null when free space cannot be determined. Backup callers must treat
    /// "unknown" as insufficient rather than proceeding with a write that may fill the disk.
    /// </summary>
    static long? AvailableFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }
}
