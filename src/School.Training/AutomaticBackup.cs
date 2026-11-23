using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public sealed class AutomaticBackupOptions
{
    public bool Enabled { get; init; } = true;
    public TimeOnly TimeOfDay { get; init; } = new(2, 30);

    public static AutomaticBackupOptions Load(IConfiguration configuration)
    {
        var text = configuration["School:Backups:Automatic:TimeOfDay"];
        return new AutomaticBackupOptions
        {
            Enabled = configuration.GetValue<bool?>("School:Backups:Automatic:Enabled") ?? true,
            TimeOfDay = TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : new TimeOnly(2, 30)
        };
    }
}

/// <summary>
/// Serializes manual and scheduled backups. A manual request is refused while a backup is
/// running; the scheduled worker waits instead of skipping, so a busy operator does not
/// silently drop the daily backup.
/// </summary>
public sealed class BackupCoordinator
{
    readonly SemaphoreSlim gate = new(1, 1);
    public Task<bool> TryEnterAsync(CancellationToken cancellationToken) => gate.WaitAsync(0, cancellationToken);
    public Task EnterAsync(CancellationToken cancellationToken) => gate.WaitAsync(cancellationToken);
    public void Exit() => gate.Release();
}

public sealed record AutomaticBackupState(DateTime? LastAttemptAt, DateTime? LastSuccessAt, string LastMessage)
{
    const string AttemptKey = "backupAutomatic.lastAttemptAt";
    const string SuccessKey = "backupAutomatic.lastSuccessAt";
    const string MessageKey = "backupAutomatic.lastMessage";

    public static async Task<AutomaticBackupState> ReadAsync(SchoolDbContext db, CancellationToken ct)
    {
        var values = await db.Settings.AsNoTracking()
            .Where(x => x.Id == AttemptKey || x.Id == SuccessKey || x.Id == MessageKey)
            .ToDictionaryAsync(x => x.Id, x => x.Value, ct);
        return new AutomaticBackupState(Parse(values.GetValueOrDefault(AttemptKey)),
            Parse(values.GetValueOrDefault(SuccessKey)), values.GetValueOrDefault(MessageKey) ?? "");
    }

    public static async Task WriteAsync(SchoolDbContext db, DateTime attemptAtUtc, DateTime? successAtUtc, string message, CancellationToken ct)
    {
        await SetAsync(db, AttemptKey, attemptAtUtc.ToString("O"), ct);
        if (successAtUtc is DateTime success) await SetAsync(db, SuccessKey, success.ToString("O"), ct);
        await SetAsync(db, MessageKey, message.Length > 500 ? message[..500] : message, ct);
        await db.SaveChangesAsync(ct);
    }

    static DateTime? Parse(string? text)
        => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;

    static async Task SetAsync(SchoolDbContext db, string key, string value, CancellationToken ct)
    {
        var setting = await db.Settings.FindAsync([key], ct);
        if (setting == null) db.Settings.Add(new AppSetting { Id = key, Value = value });
        else setting.Value = value;
    }
}

public sealed class AutomaticBackupWorker(
    IServiceScopeFactory scopes, AppPaths paths, StorageOptions storage, AutomaticBackupOptions options,
    BackupCoordinator coordinator, ILogger<AutomaticBackupWorker> logger) : BackgroundService
{
    static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);
    static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);
    static readonly TimeSpan DailyInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Automatic backups are disabled.");
            return;
        }
        logger.LogInformation("Automatic backups are enabled; scheduled daily at {Time}.", options.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture));
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try { delay = await NextDelayAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic backup schedule state is unavailable; retrying in 30 minutes.");
                delay = TimeSpan.FromMinutes(30);
            }
            // A restart during the scheduled minute should not start a backup while startup
            // checks and client requests are still settling.
            if (first && delay < StartupGrace) delay = StartupGrace;
            first = false;
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
            await RunOnceAsync(stoppingToken);
        }
    }

    async Task<TimeSpan> NextDelayAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
        var state = await AutomaticBackupState.ReadAsync(db, ct);
        var now = DateTime.UtcNow;
        if (state.LastSuccessAt is DateTime success && now - success < DailyInterval)
            return DailySchedule.DelayUntil(DateTime.Now, options.TimeOfDay);
        // A recent attempt without a success is retried after a quiet period, so a full disk
        // cannot turn into a tight retry loop while the status page keeps the failure visible.
        var sinceAttempt = state.LastAttemptAt is DateTime attempt ? now - attempt : (TimeSpan?)null;
        if (sinceAttempt is TimeSpan quiet && quiet < RetryInterval) return RetryInterval - quiet;
        return TimeSpan.Zero;
    }

    async Task RunOnceAsync(CancellationToken ct)
    {
        var attemptAt = DateTime.UtcNow;
        BackupResult result;
        await coordinator.EnterAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
            result = await BackupService.CreateAsync(paths, db, storage, "system", "自动备份", BackupService.AvailableFreeBytes, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Automatic backup failed unexpectedly.");
            result = new BackupResult(false, "自动备份失败，请查看服务日志", "", default, 0, StatusCodes.Status500InternalServerError);
        }
        finally { coordinator.Exit(); }

        if (result.Success) logger.LogInformation("Automatic backup created: {File}", result.FileName);
        else logger.LogWarning("Automatic backup failed: {Message}", result.Message);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
            await AutomaticBackupState.WriteAsync(db, attemptAt,
                result.Success ? attemptAt : null, result.Success ? result.FileName : result.Message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "Automatic backup status could not be saved."); }
    }
}
