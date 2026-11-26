using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using System.IO.Compression;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class ImportEndpoints
{
    public static void MapImportEndpoints(this RouteGroupBuilder all)
    {
        var api = all.MapGroup("/imports").RequireAuthorization("Admin");
        api.MapGet("/template", (string kind) =>
        {
            if (kind is not ("student" or "teacher")) return Results.BadRequest(new { message = "师生类型无效" });
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("姓名,账号," + (kind == "teacher" ? "工号" : "学号") + ",班级,性别,手机号,电子邮箱\r\n")).ToArray(), "text/csv; charset=utf-8", kind + "-template.csv");
        });
        api.MapPost("/preview", async (string kind, int? pageSize, HttpContext ctx, SchoolDbContext db) =>
        {
            if (kind is not ("student" or "teacher")) return Results.BadRequest(new { message = "师生类型无效" });
            if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { message = "请选择Excel或CSV文件" });
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted); var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0 || file.Length > Limits.MaxUploadBytes) return Results.BadRequest(new { message = $"文件为空或超过{Limits.MaxUploadMegabytes}MB" });
            List<List<string>> sheet;
            try
            {
                await using var stream = file.OpenReadStream(); var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (ext == ".xlsx")
                {
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
                    {
                        if (archive.Entries.Count > 2000 || archive.Entries.Sum(e => e.Length) > 64 * 1024 * 1024)
                            return Results.BadRequest(new { message = "Excel解压后内容超过64MB或结构过于复杂" });
                    }
                    stream.Position = 0;
                    using var book = new XLWorkbook(stream); var ws = book.Worksheets.FirstOrDefault();
                    if (ws == null) return Results.BadRequest(new { message = "工作簿没有工作表" });
                    var used = ws.RangeUsed(); if (used == null) return Results.BadRequest(new { message = "工作表为空" });
                    var lastRow = ws.LastRowUsed()!.RowNumber(); var lastColumn = ws.LastColumnUsed()!.ColumnNumber();
                    if (lastRow > 10001 || lastColumn > 30) return Results.BadRequest(new { message = "数据必须位于前10001行、30列以内（含表头）" });
                    sheet = Enumerable.Range(1, lastRow).Select(r => Enumerable.Range(1, lastColumn).Select(c => ws.Cell(r, c).GetFormattedString().Trim()).ToList()).ToList();
                }
                else if (ext == ".csv")
                {
                    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true); sheet = ImportCsv.Parse(await reader.ReadToEndAsync(ctx.RequestAborted));
                }
                else return Results.BadRequest(new { message = "仅支持.xlsx和UTF-8编码的.csv文件" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return Results.BadRequest(new { message = "无法读取文件，请检查文件格式和UTF-8编码" }); }
            if (sheet.Count is < 2 or > ImportCsv.MaxSheetRows) return Results.BadRequest(new { message = "需要表头和至少一行数据，每次最多10000行" });
            List<ImportRow> rows;
            try { rows = ImportCsv.BuildRows(sheet); }
            catch (ImportSheetException ex) { return Results.BadRequest(new { message = ex.Message }); }
            await ValidateRows(rows, kind, db, ctx.RequestAborted);
            var owner = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var previewExpired = DateTime.UtcNow.AddHours(-1);
            var resultExpired = DateTime.UtcNow.AddHours(-24);
            await db.ImportBatches.Where(x =>
                    (x.Status == ImportBatchStatus.Preview && x.CreatedAt < previewExpired)
                    || (x.Status != ImportBatchStatus.Preview && x.Status != ImportBatchStatus.Queued
                        && x.Status != ImportBatchStatus.Running && x.Status != ImportBatchStatus.Cancelling && x.CreatedAt < resultExpired))
                .ExecuteDeleteAsync(ctx.RequestAborted);
            var batch = new ImportBatch { Kind = kind, UserId = owner, RowsJson = "[]" };
            await using var previewTransaction = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
            db.ImportBatches.Add(batch);
            Audit.Add(db, ctx, "预览导入", kind, $"共{rows.Count}行");
            await db.SaveChangesAsync(ctx.RequestAborted);
            await ImportBatchStore.InsertAsync(db, batch.Id, rows, ctx.RequestAborted);
            await previewTransaction.CommitAsync(ctx.RequestAborted);
            var firstPage = rows.Take(ImportBatchStore.ClampPageSize(pageSize)).ToList();
            return Results.Ok(new ImportPreviewResponse(
                batch.Id, firstPage, rows.Count(r => r.Errors.Count == 0), rows.Count(r => r.Errors.Count > 0)));
        }).RequireRateLimiting("expensive");
        api.MapPost("/{id}/commit", async (string id, CommitImportRequest input, HttpContext ctx, SchoolDbContext db, ImportJobQueue jobs) =>
        {

            var owner = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var batch = await db.ImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ctx.RequestAborted);
            if (batch == null || batch.CreatedAt < DateTime.UtcNow.AddHours(-1)) return Results.NotFound(new { message = "导入预览已过期，请重新上传" });
            if (batch.Status != ImportBatchStatus.Preview) return Results.Conflict(new { message = StatusMessage(batch.Status) });
            var rows = await ImportBatchStore.LoadAsync(db, batch, ctx.RequestAborted);
            await ValidateRows(rows, batch.Kind, db, ctx.RequestAborted);
            if (!input.RemoveInvalidRows && rows.Any(r => r.Errors.Count > 0)) return Results.BadRequest(new { message = "存在问题行，请先移除问题数据或修正文件后重新上传", rows });
            var valid = rows.Where(r => r.Errors.Count == 0).ToList();
            if (valid.Count == 0) return Results.BadRequest(new { message = "没有可导入的有效数据" });
            var removedCount = rows.Count - valid.Count;

            await using var transaction = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
            var claimed = await db.ImportBatches
                .Where(x => x.Id == id && x.UserId == owner && x.Status == ImportBatchStatus.Preview)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ImportBatchStatus.Queued)
                    .SetProperty(x => x.RowsJson, "[]")
                    .SetProperty(x => x.RemoveInvalidRows, input.RemoveInvalidRows)
                    .SetProperty(x => x.TotalCount, valid.Count)
                    .SetProperty(x => x.RemovedCount, removedCount)
                    .SetProperty(x => x.QueuedAt, DateTime.UtcNow), ctx.RequestAborted);
            if (claimed == 0)
            {
                await transaction.RollbackAsync(ctx.RequestAborted);
                return Results.Conflict(new { message = "该导入批次已提交，不能重复导入" });
            }
            await ImportBatchStore.KeepValidAsync(db, id, rows, valid, ctx.RequestAborted);
            Audit.Add(db, ctx, "提交导入", batch.Kind, $"排队{valid.Count}行，移除问题行{removedCount}行");
            await db.SaveChangesAsync(ctx.RequestAborted);
            await transaction.CommitAsync(ctx.RequestAborted);

            if (!jobs.TryEnqueue(new ImportJobRequest(id)))
            {
                await db.ImportBatches.Where(x => x.Id == id && x.Status == ImportBatchStatus.Queued).ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ImportBatchStatus.Failed)
                    .SetProperty(x => x.FailureMessage, "导入队列已满，请稍后重新预览并提交")
                    .SetProperty(x => x.RowsJson, "[]")
                    .SetProperty(x => x.CompletedAt, DateTime.UtcNow), ctx.RequestAborted);
                await ImportBatchStore.DeleteAsync(db, id, ctx.RequestAborted);
                return Results.Json(new { message = "导入队列已满，请稍后重试" }, statusCode: 503);
            }
            batch.Status = ImportBatchStatus.Queued;
            batch.TotalCount = valid.Count;
            batch.ProcessedCount = 0;
            batch.RemovedCount = removedCount;
            return Results.Accepted($"/api/imports/{id}", StatusResponse(batch));
        }).RequireRateLimiting("expensive");
        api.MapGet("/{id}", async (string id, HttpContext ctx, SchoolDbContext db) =>
        {
            var owner = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var batch = await db.ImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ctx.RequestAborted);
            return batch == null ? Results.NotFound(new { message = "导入批次不存在" }) : Results.Ok(StatusResponse(batch));
        });
        api.MapGet("/{id}/rows", async (string id, int? page, int? pageSize, string? filter, HttpContext ctx, SchoolDbContext db) =>
        {
            var owner = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var batch = await db.ImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ctx.RequestAborted);
            if (batch == null || (batch.Status == ImportBatchStatus.Preview && batch.CreatedAt < DateTime.UtcNow.AddHours(-1)))
                return Results.NotFound(new { message = "导入预览已过期，请重新上传" });
            var rows = await ImportBatchStore.PageAsync(db, batch, page ?? 1, pageSize ?? ImportBatchStore.DefaultPageSize, filter, ctx.RequestAborted);
            return Results.Ok(rows);
        });
        api.MapGet("/{id}/receipt", async (string id, HttpContext ctx, SchoolDbContext db, ImportPasswordReceipts receipts) =>
        {
            var owner = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            var batch = await db.ImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ctx.RequestAborted);
            if (batch == null) return Results.NotFound(new { message = "导入批次不存在" });
            if (batch.Status != ImportBatchStatus.Completed) return Results.Conflict(new { message = "导入尚未完成，暂时不能领取初始密码" });
            var items = receipts.Take(id, owner);
            return items == null
                ? Results.Conflict(new { message = "初始密码回执已领取或已过期；如未保存，请在人员管理中逐人重置密码" })
                : Results.Ok(new { items });
        });
        api.MapPost("/{id}/cancel", async (string id, HttpContext ctx, SchoolDbContext db, ImportJobQueue jobs) =>
        {
            var owner = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var batch = await db.ImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ctx.RequestAborted);
            if (batch == null) return Results.NotFound(new { message = "导入批次不存在" });
            if (ImportBatchStatus.IsTerminal(batch.Status)) return Results.Conflict(new { message = StatusMessage(batch.Status) });

            var cancelling = batch.Status == ImportBatchStatus.Running;
            var status = cancelling ? ImportBatchStatus.Cancelling : ImportBatchStatus.Cancelled;
            var pending = db.ImportBatches.Where(x => x.Id == id && x.UserId == owner
                && (x.Status == ImportBatchStatus.Preview || x.Status == ImportBatchStatus.Queued || x.Status == ImportBatchStatus.Running));
            var cancelled = cancelling
                ? await pending.ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.FailureMessage, "导入任务已取消")
                    .SetProperty(x => x.CompletedAt, (DateTime?)null), ctx.RequestAborted)
                : await pending.ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.FailureMessage, "导入任务已取消")
                    .SetProperty(x => x.RowsJson, "[]")
                    .SetProperty(x => x.CompletedAt, DateTime.UtcNow), ctx.RequestAborted);
            if (cancelled > 0 && !cancelling)
                await ImportBatchStore.DeleteAsync(db, id, ctx.RequestAborted);
            if (status == ImportBatchStatus.Cancelling) jobs.Cancel(id);
            var current = await db.ImportBatches.AsNoTracking().SingleAsync(x => x.Id == id, ctx.RequestAborted);
            return Results.Ok(StatusResponse(current));
        });
    }

    internal static ImportStatusResponse StatusResponse(ImportBatch batch) => new(
        batch.Id,
        batch.Status.ToLowerInvariant(),
        batch.ProcessedCount,
        batch.TotalCount,
        batch.ImportedCount,
        batch.RemovedCount,
        batch.FailureMessage,
        batch.CreatedAt,
        batch.QueuedAt,
        batch.StartedAt,
        batch.CompletedAt,
        batch.Status switch
        {
            ImportBatchStatus.Completed => $"已导入{batch.ImportedCount}条记录",
            ImportBatchStatus.Failed => batch.FailureMessage,
            ImportBatchStatus.Cancelled => "导入任务已取消",
            ImportBatchStatus.Queued => "导入任务已进入队列",
            ImportBatchStatus.Running => "正在创建账号",
            ImportBatchStatus.Cancelling => "正在取消导入任务",
            _ => "导入预览待提交"
        });

    static string StatusMessage(string status) => status switch
    {
        ImportBatchStatus.Completed => "该批次已导入，不能重复提交",
        ImportBatchStatus.Queued or ImportBatchStatus.Running or ImportBatchStatus.Cancelling => "该批次正在处理中，不能重复提交",
        ImportBatchStatus.Cancelled => "该批次已取消，请重新预览",
        ImportBatchStatus.Failed => "该批次已失败，请重新预览",
        _ => "该批次不可再提交"
    };

    public static async Task ValidateRows(List<ImportRow> rows, string kind, SchoolDbContext db, CancellationToken cancellationToken = default)
    {
        var userNames = rows.Select(r => r.Data.UserName).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var numbers = rows.Select(r => r.Data.Number).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var classNames = rows.Select(r => r.Data.ClassName).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

        var users = await QueryUniqueStringsAsync(userNames, chunk =>
            db.Users.AsNoTracking().Where(x => chunk.Contains(x.UserName)).Select(x => x.UserName).ToListAsync(cancellationToken));
        var existingNumbers = await QueryUniqueStringsAsync(numbers, chunk =>
            db.People.AsNoTracking().Where(x => x.Kind == kind && chunk.Contains(x.Number)).Select(x => x.Number).ToListAsync(cancellationToken));
        var classCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var chunk in classNames.Chunk(400))
        {
            var found = await db.Classes.AsNoTracking().Where(x => chunk.Contains(x.Name)).Select(x => x.Name).ToListAsync(cancellationToken);
            foreach (var name in found) classCounts[name] = classCounts.GetValueOrDefault(name) + 1;
        }

        var duplicateUsers = rows.GroupBy(r => r.Data.UserName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateNumbers = rows.GroupBy(r => r.Data.Number).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var r in rows)
        {
            r.Errors.Clear(); var d = r.Data;
            void Error(string column, string message) => r.Errors.Add(new ImportError(column, message));
            if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 100) Error("姓名", "必填，最多100字");
            var err = Validation.UserName(d.UserName); if (err != null) Error("账号", err);
            if (users.Contains(d.UserName)) Error("账号", "系统中已存在此账号");
            if (duplicateUsers.Contains(d.UserName)) Error("账号", "文件中账号重复");
            if (string.IsNullOrWhiteSpace(d.Number) || d.Number.Length > 64) Error(kind == "teacher" ? "工号" : "学号", "必填，最多64字");
            if (existingNumbers.Contains(d.Number)) Error(kind == "teacher" ? "工号" : "学号", "系统中已存在");
            if (duplicateNumbers.Contains(d.Number)) Error(kind == "teacher" ? "工号" : "学号", "文件中重复");
            if (d.ClassName.Length > 0 && !classCounts.ContainsKey(d.ClassName)) Error("班级", "班级不存在，请先创建班级");
            if (d.ClassName.Length > 0 && classCounts.GetValueOrDefault(d.ClassName) > 1) Error("班级", "存在同名班级，请先将班级名称区分后导入");
            if (d.Gender.Length > 20) Error("性别", "最多20字");
            if (d.Phone.Length > 30) Error("手机号", "最多30字");
            if (d.Email.Length > 254 || (d.Email.Length > 0 && (!System.Net.Mail.MailAddress.TryCreate(d.Email, out var address) || address.Address != d.Email))) Error("电子邮箱", "邮箱格式不正确");
        }
    }

    public static async Task<Dictionary<string, string>> LoadUniqueClassMapAsync(SchoolDbContext db, List<ImportRow> rows, CancellationToken cancellationToken)
    {
        var names = rows.Select(r => r.Data.ClassName).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var matches = new List<(string Name, string Id)>();
        foreach (var chunk in names.Chunk(400))
            matches.AddRange(await db.Classes.AsNoTracking().Where(x => chunk.Contains(x.Name))
                .Select(x => new ValueTuple<string, string>(x.Name, x.Id)).ToListAsync(cancellationToken));
        return matches.GroupBy(x => x.Name, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Id, StringComparer.Ordinal);
    }

    static async Task<HashSet<string>> QueryUniqueStringsAsync(string[] values, Func<string[], Task<List<string>>> query)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in values.Chunk(400))
            foreach (var value in await query(chunk)) result.Add(value);
        return result;
    }
}
public sealed class ImportRow
{
    public int RowNumber { get; set; }
    public ImportData Data { get; set; } = new("", "", "", "", "", "", "");
    public List<ImportError> Errors { get; set; } = [];
}
public record ImportData(string Name, string UserName, string Number, string ClassName, string Gender, string Phone, string Email);
public record ImportError(string Column, string Message);
public record CommitImportRequest(bool RemoveInvalidRows, string? InitialPassword = null);
