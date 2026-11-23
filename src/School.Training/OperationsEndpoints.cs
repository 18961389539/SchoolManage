using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
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
        // Counts are readable by any signed-in account (the smoke suite asserts this); the audit
        // feed is administrator-only, and the console UI is only rendered for administrators.
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
        api.MapGet("/audits/export", (HttpRequest req, SchoolDbContext db, HttpContext ctx) => AuditExport.CsvAsync(req, db, ctx)).RequireRateLimiting("expensive");
        api.MapGet("/diagnostics", async (AppPaths paths, SchoolDbContext db, StorageOptions storage,
            AutomaticBackupOptions backup, AuditRetentionOptions retention, RollingFileLoggerProvider logs, HttpContext ctx) =>
        {
            var snapshot = await DiagnosticsBundle.CollectAsync(paths, db, storage, backup, retention, logs, ctx.RequestAborted);
            db.Audits.Add(new AuditEntry { UserName = ctx.User.Identity?.Name ?? "system", Action = "下载诊断包", Target = "support" });
            await db.SaveChangesAsync(ctx.RequestAborted);
            return await DiagnosticsBundle.CreateResultAsync(snapshot, ctx.RequestAborted);
        }).RequireRateLimiting("expensive");
        api.MapGet("/settings", async (SchoolDbContext db, AppPaths paths, CancellationToken ct) => Results.Ok(new { schoolName = await db.Settings.Where(x => x.Id == "schoolName").Select(x => x.Value).FirstOrDefaultAsync(ct) ?? "", backupDirectory = paths.Backups, dataDirectory = paths.Data, version = BuildInfo.Version, resourceMode = "simulated", backupIncludesKeys = true }));
        api.MapPut("/settings", async (SettingsRequest input, HttpContext ctx, SchoolDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(input.SchoolName) || input.SchoolName.Length > 100) return Results.BadRequest(new { message = "学校名称必填，最多100字" });
            var setting = await db.Settings.FindAsync(["schoolName"], ctx.RequestAborted); if (setting == null) db.Settings.Add(new AppSetting { Id = "schoolName", Value = input.SchoolName.Trim() }); else setting.Value = input.SchoolName.Trim();
            Audit.Add(db, ctx, "修改系统设置", "schoolName"); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { message = "已保存" });
        });
        api.MapGet("/backups", async (AppPaths paths, SchoolDbContext db, AutomaticBackupOptions automatic, CancellationToken ct) =>
        {
            var state = await AutomaticBackupState.ReadAsync(db, ct);
            var items = Directory.EnumerateFiles(paths.Backups, "school-*.zip").Select(f => new FileInfo(f)).OrderByDescending(f => f.CreationTimeUtc).Select(f => new { fileName = f.Name, createdAt = f.CreationTimeUtc, size = f.Length }).ToArray();
            return Results.Ok(new
            {
                items,
                automatic = new
                {
                    enabled = automatic.Enabled,
                    timeOfDay = automatic.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture),
                    lastAttemptAt = state.LastAttemptAt,
                    lastSuccessAt = state.LastSuccessAt,
                    lastMessage = state.LastMessage
                }
            });
        });
        api.MapPost("/backups", async (AppPaths paths, SchoolDbContext db, StorageOptions storage, BackupCoordinator coordinator, HttpContext ctx) =>
        {
            if (!await coordinator.TryEnterAsync(ctx.RequestAborted))
                return Results.Conflict(new { message = "已有备份任务正在进行，请稍后重试" });
            try
            {
                var result = await BackupService.CreateAsync(paths, db, storage,
                    ctx.User.Identity?.Name ?? "system", "创建备份", BackupService.AvailableFreeBytes, ctx.RequestAborted);
                return result.ToHttpResult();
            }
            finally { coordinator.Exit(); }
        }).RequireRateLimiting("expensive");
        api.MapGet("/backups/{fileName}", async (string fileName, AppPaths paths, HttpContext ctx, SchoolDbContext db) =>
        {
            if (Path.GetFileName(fileName) != fileName || !System.Text.RegularExpressions.Regex.IsMatch(fileName, "^school-[0-9]{14}-[a-f0-9]{8}\\.zip$")) return Results.NotFound();
            var path = Path.Combine(paths.Backups, fileName); if (!File.Exists(path)) return Results.NotFound(new { message = "备份文件不存在" });
            Audit.Add(db, ctx, "下载备份", fileName); await db.SaveChangesAsync(ctx.RequestAborted); return Results.File(path, "application/zip", fileName);
        }).RequireRateLimiting("expensive");
        api.MapGet("/licenses", async (SchoolDbContext db, LicenseQuotaPolicy policy, CancellationToken ct) =>
        {
            var records = await db.Licenses.AsNoTracking().OrderByDescending(x => x.ImportedAt).ToListAsync(ct);
            using var key = await policy.OpenVerificationKeyAsync(ct);
            var now = DateTime.UtcNow;
            return Results.Ok(new
            {
                machineCode = MachineCode(),
                publicKeyConfigured = key is not null,
                mode = "simulated",
                quotas = await policy.ReadQuotasAsync(db, ct),
                items = records.Select<LicenseRecord, object>(record =>
                {
                    if (!LicenseQuotaPolicy.TryReadStored(record.PayloadJson, out var payload, out var envelope))
                        return new { record.Id, productAuthorizationCode = record.Id, record.FileName, record.ImportedAt, IssuedAt = (DateTime?)null, ExpiresAt = (DateTime?)null, Resources = Array.Empty<LicenseResource>(), valid = false, legacy = false };
                    var valid = envelope is not null && key is not null && LicenseQuotaPolicy.Verify(envelope, key) && LicenseQuotaPolicy.IsEffective(payload, now);
                    return new { record.Id, productAuthorizationCode = record.Id, record.FileName, record.ImportedAt, payload.IssuedAt, payload.ExpiresAt, payload.Resources, valid, legacy = envelope is null };
                }).ToArray()
            });
        });
        api.MapPost("/licenses", async (HttpContext ctx, SchoolDbContext db, LicenseQuotaPolicy policy) =>
        {
            if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { message = "请选择许可证文件" });
            var ct = ctx.RequestAborted;
            var files = (await ctx.Request.ReadFormAsync(ct)).Files;
            if (files.Count is < 1 or > 20) return Results.BadRequest(new { message = "每次上传1–20个许可证文件" });
            using var rsa = await policy.OpenVerificationKeyAsync(ct);
            if (rsa is null) return Results.BadRequest(new { message = "供应商验签公钥缺失、无法读取或格式不正确。请检查 license-public.pem。" });
            var pending = new List<LicenseRecord>();
            var activatedCodes = new HashSet<string>(StringComparer.Ordinal);
            var now = DateTime.UtcNow;
            foreach (var file in files)
            {
                if (file.Length is < 1 or > 262144) return Results.BadRequest(new { message = "许可证文件大小须在256KB以内" });
                try
                {
                    using var reader = new StreamReader(file.OpenReadStream());
                    var envelope = JsonSerializer.Deserialize<LicenseEnvelope>(await reader.ReadToEndAsync(ct), LicenseQuotaPolicy.JsonOptions);
                    if (envelope is null) return Results.BadRequest(new { message = "许可证不能为空" });
                    if (!LicenseQuotaPolicy.Verify(envelope, rsa)) return Results.BadRequest(new { message = "许可证签名无效" });
                    if (!LicenseQuotaPolicy.TryReadEnvelope(envelope, out var payload)) return Results.BadRequest(new { message = "许可证资源列表或数据格式不正确" });
                    if (payload.Product != "SchoolTraining" || payload.MachineCode != MachineCode()) return Results.BadRequest(new { message = "许可证产品或设备特征码不匹配" });
                    if (payload.IssuedAt.Kind == DateTimeKind.Unspecified || payload.ExpiresAt?.Kind == DateTimeKind.Unspecified)
                        return Results.BadRequest(new { message = "许可证日期必须包含时区" });
                    if (payload.ExpiresAt?.ToUniversalTime() <= now) return Results.BadRequest(new { message = "许可证已过期" });
                    if (payload.IssuedAt.ToUniversalTime() > now) return Results.BadRequest(new { message = "许可证签发时间晚于服务器时间，请检查两端时钟" });
                    if (payload.LicenseId.Length is < 1 or > 100 || payload.Resources.Count is < 1 or > 100 || payload.Resources.Any(r => string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.AuthorizationCode) || r.Quantity < 0)) return Results.BadRequest(new { message = "许可证资源列表不完整" });
                    if (!LicenseQuotaPolicy.UniqueManagedCodes(payload)) return Results.BadRequest(new { message = "同一张许可证不能重复列出同一种平台配额" });
                    if (pending.Any(x => x.Id == payload.LicenseId)) return Results.Conflict(new { message = "许可证已导入" });
                    pending.Add(new LicenseRecord { Id = payload.LicenseId, FileName = Path.GetFileName(file.FileName), PayloadJson = JsonSerializer.Serialize(envelope, LicenseQuotaPolicy.JsonOptions) });
                    activatedCodes.UnionWith(payload.Resources.Select(resource => resource.AuthorizationCode));
                }
                catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
                { return Results.BadRequest(new { message = "许可证格式或签名不正确" }); }
            }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var ids = pending.Select(x => x.Id).ToArray();
            var existing = await db.Licenses.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            foreach (var entry in pending)
            {
                if (!existing.TryGetValue(entry.Id, out var prior)) { db.Licenses.Add(entry); continue; }
                if (!LicenseQuotaPolicy.TryReadStored(prior.PayloadJson, out _, out var stored) || stored is not null)
                    return Results.Conflict(new { message = "许可证已导入" });
                prior.PayloadJson = entry.PayloadJson;
                prior.FileName = entry.FileName;
                prior.ImportedAt = entry.ImportedAt;
            }
            await LicenseQuotaPolicy.MarkActivatedAsync(db, activatedCodes, ct);
            Audit.Add(db, ctx, "导入许可证", string.Join(",", ids));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { importedCount = pending.Count });
        }).RequireRateLimiting("expensive");
        api.MapGet("/licenses/guide", () => Results.File(Encoding.UTF8.GetBytes(LicenseGuide), "text/markdown; charset=utf-8", "license-guide.md"));
    }
    public static string MachineCode()
    {
        string machine = Environment.MachineName;
        if (OperatingSystem.IsWindows()) { using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"); machine += "|" + key?.GetValue("MachineGuid"); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("SchoolTraining|" + machine)))[..32];
    }
    private const string LicenseGuide = """
        # 许可证使用说明

        复制许可证页面的设备特征码，由供应商签发并配置验签公钥 `license-public.pem`。
        上传包含 `payload` 与 `signature` 的 JSON 文件。可一次导入 1–20 个文件，整批验证通过才保存。
        系统验证 RSA-SHA256 签名、产品、设备绑定和日期。迁移机器需重新签发。

        | 资源授权码 | 计量口径 |
        | --- | --- |
        | TRAINING.CABINETS | 已登记机柜数量 |
        | TRAINING.DEVICES | 已登记设备数量 |
        | TRAINING.NODES | 已登记节点数量 |
        | TRAINING.VMS | 已建立的虚拟机数量，含停止的虚拟机 |
        | TRAINING.ASSIGNMENTS | 节点与账号的关联数，同一账号分到两个节点计两项 |

        某类首次导入对应授权码后开始执行该类额度。同类的多个有效额度取最高值，续期不会叠加。
        该类的授权全部过期时暂停新增，已有配置和资源仍可管理及释放。节点自身配额也继续生效。
        其他资源授权码仅用于记录。早期版本存入的旧记录可重新上传原签名文件以启用额度。
        """;
}
