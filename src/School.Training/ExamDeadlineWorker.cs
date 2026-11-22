using Microsoft.EntityFrameworkCore;

namespace School.Training;

public sealed class ExamDeadlineWorker(IServiceScopeFactory scopes, ILogger<ExamDeadlineWorker> logger) : BackgroundService
{
    const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed;
                do { processed = await FinalizeDue(stoppingToken); }
                while (processed == BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Could not finalize due exam attempts. The next sweep will retry.");
            }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
                while (await CampEndpoints.FinalizeDueAsync(db, BatchSize, stoppingToken) == BatchSize)
                    db.ChangeTracker.Clear();
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Could not finalize due camp assessments. The next sweep will retry.");
            }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
                var shares = scope.ServiceProvider.GetRequiredService<ClassroomLiveShare>();
                while (await ClassroomCaseLogic.CloseDueAsync(db, shares, BatchSize, stoppingToken) == BatchSize)
                    db.ChangeTracker.Clear();
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Could not close expired classrooms. The next sweep will retry.");
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    async Task<int> FinalizeDue(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
        var now = DateTime.UtcNow;
        var due = from attempt in db.ExamAttempts
                  join exam in db.TrainingExams on attempt.ExamId equals exam.Id
                  where attempt.Status == "inProgress" && (attempt.Deadline <= now || exam.EndAt <= now || exam.Status == "closed")
                  orderby attempt.Deadline
                  select new { attempt, exam };
        if (!await due.AnyAsync(ct)) return 0;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await due.AsTracking().Take(BatchSize).ToListAsync(ct);
        foreach (var row in rows)
        {
            var deadline = ExamStudentEndpoints.SubmissionDeadline(row.exam, row.attempt);
            await ExamStudentEndpoints.FinalizeAttemptAsync(db, row.attempt, deadline, autoSubmitted: true, now, ct);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return rows.Count;
    }
}
