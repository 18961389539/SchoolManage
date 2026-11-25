using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class ImportBatchStatus
{
    public const string Preview = "Preview";
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Cancelling = "Cancelling";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static bool IsTerminal(string status) => status is Completed or Failed or Cancelled;
}

public sealed record ImportJobRequest(string BatchId);

/// <summary>
/// The initial password intentionally stays in process memory. It is never written to
/// the database, logs, or job-status responses. A service restart therefore fails
/// unfinished batches and asks the administrator to submit them again.
/// </summary>
public sealed class ImportJobQueue
{
    readonly Channel<ImportJobRequest> queue = Channel.CreateBounded<ImportJobRequest>(
        new BoundedChannelOptions(20) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    readonly ConcurrentDictionary<string, CancellationTokenSource> running = new(StringComparer.Ordinal);

    public bool TryEnqueue(ImportJobRequest request) => queue.Writer.TryWrite(request);
    public IAsyncEnumerable<ImportJobRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        queue.Reader.ReadAllAsync(cancellationToken);

    public CancellationToken RegisterRunning(string batchId, CancellationToken stoppingToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!running.TryAdd(batchId, cancellation))
        {
            cancellation.Dispose();
            throw new InvalidOperationException("导入批次已经在运行");
        }
        return cancellation.Token;
    }

    public bool Cancel(string batchId) => running.TryGetValue(batchId, out var cancellation) && TryCancel(cancellation);

    public void Unregister(string batchId)
    {
        if (running.TryRemove(batchId, out var cancellation)) cancellation.Dispose();
    }

    static bool TryCancel(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }
}

public sealed class ImportJobWorker(ImportJobQueue jobs, ImportPasswordReceipts passwordReceipts, IServiceScopeFactory scopes, ILogger<ImportJobWorker> logger) : BackgroundService
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RecoverInterruptedAsync(); }
        catch (Exception ex)
        {
            // Startup recovery is best effort. A transient database error must not stop the
            // worker, otherwise every later import would be rejected until the next restart.
            logger.LogError(ex, "Recovering interrupted import batches did not complete.");
        }
        try
        {
            await foreach (var request in jobs.ReadAllAsync(stoppingToken))
            {
                try { await ProcessAsync(request, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // ProcessAsync records its own failures on the batch row. This guard only
                    // keeps one bad batch from faulting ExecuteAsync, which would stop the host
                    // and drop every open terminal session.
                    logger.LogError(ex, "Import job {BatchId} failed outside its own error handling.", request.BatchId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown is handled by the host. Unfinished rows stay uncommitted.
        }
    }

    async Task RecoverInterruptedAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
        var now = DateTime.UtcNow;
        var recovered = await db.ImportBatches
            .Where(x => x.Status == ImportBatchStatus.Queued || x.Status == ImportBatchStatus.Running || x.Status == ImportBatchStatus.Cancelling)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, ImportBatchStatus.Failed)
                .SetProperty(x => x.FailureMessage, "服务重启导致导入任务中断，请重新预览并提交")
                .SetProperty(x => x.CompletedAt, now));
        if (recovered > 0) logger.LogWarning("Marked {Count} interrupted import batches as failed.", recovered);
    }

    async Task ProcessAsync(ImportJobRequest request, CancellationToken stoppingToken)
    {
        CancellationToken token;
        try { token = jobs.RegisterRunning(request.BatchId, stoppingToken); }
        catch (InvalidOperationException ex)
        {
            // A duplicate queue entry never owns the registration; unregistering here would
            // dispose the cancellation source of the run that already holds the batch.
            logger.LogWarning(ex, "Import batch {BatchId} is already running; the duplicate queue entry was ignored.", request.BatchId);
            return;
        }
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
            var claimed = await db.ImportBatches
                .Where(x => x.Id == request.BatchId && x.Status == ImportBatchStatus.Queued)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ImportBatchStatus.Running)
                    .SetProperty(x => x.StartedAt, DateTime.UtcNow)
                    .SetProperty(x => x.ProcessedCount, 0), token);
            if (claimed == 0) return;

            var batch = await db.ImportBatches.AsNoTracking().SingleAsync(x => x.Id == request.BatchId, token);
            var rows = JsonSerializer.Deserialize<List<ImportRow>>(batch.RowsJson, JsonOptions)
                ?? throw new InvalidDataException("导入预览数据无法读取，请重新上传");
            if (rows.Count == 0 || rows.Count != batch.TotalCount) throw new InvalidDataException("导入预览数据不完整，请重新上传");

            // Validation must run before hashing: a stale preview fails the whole batch.
            await ImportEndpoints.ValidateRows(rows, batch.Kind, db, token);
            if (rows.Any(row => row.Errors.Count > 0))
            {
                await FinishFailedAsync(request.BatchId, "预览后数据已变化，问题行未导入；请重新预览并确认");
                return;
            }

            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
            var prepared = new Dictionary<int, AppUser>(rows.Count);
            var credentials = new List<ImportCredential>(rows.Count);
            const int progressInterval = 250;
            var processed = 0;
            foreach (var chunk in rows.Chunk(progressInterval))
            {
                token.ThrowIfCancellationRequested();
                foreach (var row in chunk)
                {
                    var data = row.Data;
                    var password = ImportPasswordReceipts.GeneratePassword();
                    var user = new AppUser
                    {
                        UserName = data.UserName,
                        DisplayName = data.Name,
                        Role = batch.Kind == "teacher" ? "Teacher" : "Student",
                        MustChangePassword = true
                    };
                    user.PasswordHash = hasher.HashPassword(user, password);
                    prepared.Add(row.RowNumber, user);
                    credentials.Add(new ImportCredential(data.UserName, password));
                }
                processed += chunk.Length;
                await ReportProgressAsync(request.BatchId, processed, token);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var current = await db.ImportBatches.SingleAsync(x => x.Id == request.BatchId, token);
            if (current.Status == ImportBatchStatus.Cancelling)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await FinishCancelledAsync(request.BatchId);
                return;
            }
            if (current.Status != ImportBatchStatus.Running)
            {
                await transaction.RollbackAsync(token);
                return;
            }

            var classMap = await ImportEndpoints.LoadUniqueClassMapAsync(db, rows, token);
            foreach (var row in rows)
            {
                var data = row.Data;
                if (!prepared.TryGetValue(row.RowNumber, out var user)) throw new InvalidDataException("导入任务数据不完整，请重新上传");
                db.Users.Add(user);
                db.People.Add(AcademicEndpoints.CreatePerson(
                    new PersonRequest(batch.Kind, data.Name, data.UserName, data.Number,
                        classMap.GetValueOrDefault(data.ClassName), data.Gender, data.Phone, data.Email), user.Id));
            }

            var userName = await db.Users.Where(x => x.Id == batch.UserId).Select(x => x.UserName).FirstOrDefaultAsync(token) ?? "system";
            current.Committed = true;
            current.Status = ImportBatchStatus.Completed;
            current.ProcessedCount = rows.Count;
            current.TotalCount = rows.Count;
            current.ImportedCount = rows.Count;
            current.CompletedAt = DateTime.UtcNow;
            current.RowsJson = "[]";
            db.Audits.Add(new AuditEntry
            {
                UserName = userName,
                Action = "完成导入",
                Target = batch.Kind,
                Detail = $"成功{rows.Count}行，移除问题行{batch.RemovedCount}行"
            });
            passwordReceipts.Put(request.BatchId, batch.UserId, credentials.ToArray(), DateTime.UtcNow.AddHours(24));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (stoppingToken.IsCancellationRequested) await FinishFailedAsync(request.BatchId, "服务停止导致导入任务中断，请重新预览并提交");
            else await FinishCancelledAsync(request.BatchId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import job {BatchId} failed.", request.BatchId);
            await FinishFailedAsync(request.BatchId, ex is DbUpdateException ? "账号或学号在导入期间发生重复，请重新预览" : "导入任务失败，请查看服务日志");
        }
        finally
        {
            jobs.Unregister(request.BatchId);
        }
    }

    async Task ReportProgressAsync(string batchId, int processed, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
            await db.ImportBatches
                .Where(x => x.Id == batchId && (x.Status == ImportBatchStatus.Running || x.Status == ImportBatchStatus.Cancelling))
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ProcessedCount, processed), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Progress is cosmetic: a busy database must not abort an otherwise valid import.
            logger.LogWarning(ex, "Reporting import progress for {BatchId} failed at row {Processed}.", batchId, processed);
        }
    }

    async Task FinishFailedAsync(string batchId, string message)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
            await db.ImportBatches
                .Where(x => x.Id == batchId && (x.Status == ImportBatchStatus.Queued || x.Status == ImportBatchStatus.Running || x.Status == ImportBatchStatus.Cancelling))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ImportBatchStatus.Failed)
                    .SetProperty(x => x.FailureMessage, message)
                    .SetProperty(x => x.RowsJson, "[]")
                    .SetProperty(x => x.CompletedAt, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            // This runs from a catch block: letting it escape would fault ExecuteAsync and stop
            // the host. A batch left in Running is failed by startup recovery on the next start.
            logger.LogError(ex, "Persisting the failed state for import batch {BatchId} did not complete.", batchId);
        }
    }

    async Task FinishCancelledAsync(string batchId)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
            await db.ImportBatches
                .Where(x => x.Id == batchId && (x.Status == ImportBatchStatus.Queued || x.Status == ImportBatchStatus.Running || x.Status == ImportBatchStatus.Cancelling))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ImportBatchStatus.Cancelled)
                    .SetProperty(x => x.FailureMessage, "导入任务已取消")
                    .SetProperty(x => x.RowsJson, "[]")
                    .SetProperty(x => x.CompletedAt, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Persisting the cancelled state for import batch {BatchId} did not complete.", batchId);
        }
    }
}
