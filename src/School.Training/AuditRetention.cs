using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public sealed class AuditRetentionOptions
{
    /// <summary>Zero keeps audit entries forever; the default until a school agrees on a retention period.</summary>
    public int RetentionDays { get; init; }
    public TimeOnly TimeOfDay { get; init; } = new(3, 30);

    public static AuditRetentionOptions Load(IConfiguration configuration)
    {
        var text = configuration["School:Audits:CleanupTimeOfDay"];
        return new AuditRetentionOptions
        {
            RetentionDays = Math.Clamp(configuration.GetValue<int?>("School:Audits:RetentionDays") ?? 0, 0, 3650),
            TimeOfDay = TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : new TimeOnly(3, 30)
        };
    }
}

public static class DailySchedule
{
    public static TimeSpan DelayUntil(DateTime localNow, TimeOnly time)
    {
        var next = localNow.Date.Add(time.ToTimeSpan());
        if (next <= localNow) next = next.AddDays(1);
        return next - localNow;
    }
}

public static class AuditRetention
{
    const int BatchSize = 5000;

    public static async Task<int> DeleteAsync(SchoolDbContext db, DateTime cutoffUtc, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var ids = await db.Audits.AsNoTracking()
                .Where(x => x.At < cutoffUtc).OrderBy(x => x.At).Select(x => x.Id).Take(BatchSize)
                .ToListAsync(ct);
            if (ids.Count == 0) break;
            total += await db.Audits.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
            if (ids.Count < BatchSize) break;
        }
        return total;
    }
}

public sealed class AuditRetentionWorker(
    IServiceScopeFactory scopes, AuditRetentionOptions options, ILogger<AuditRetentionWorker> logger) : BackgroundService
{
    static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.RetentionDays == 0)
        {
            logger.LogInformation("Audit retention cleanup is disabled.");
            return;
        }
        logger.LogInformation("Audit retention keeps {Days} days; cleanup runs daily at {Time}.",
            options.RetentionDays, options.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture));
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = first ? StartupGrace : DailySchedule.DelayUntil(DateTime.Now, options.TimeOfDay);
            first = false;
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
            await RunOnceAsync(stoppingToken);
        }
    }

    async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
            var cutoff = DateTime.UtcNow.AddDays(-options.RetentionDays);
            var deleted = await AuditRetention.DeleteAsync(db, cutoff, ct);
            if (deleted == 0)
            {
                logger.LogInformation("Audit retention found nothing older than {Cutoff:O}.", cutoff);
                return;
            }
            db.Audits.Add(new AuditEntry
            {
                UserName = "system",
                Action = "清理操作日志",
                Target = cutoff.ToString("O"),
                Detail = $"删除{deleted}条"
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Audit retention deleted {Count} entries older than {Cutoff:O}.", deleted, cutoff);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "Audit retention cleanup failed."); }
    }
}
