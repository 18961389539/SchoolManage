using Microsoft.EntityFrameworkCore;

namespace School.Training;

// Read-only projections: never infer a historical denominator from current questions.
public static class PracticeHistory
{
    public static PracticeAttempt? Best(IEnumerable<PracticeAttempt> attempts)
        => attempts.Where(item => item.MaxScore > 0 && item.Score >= 0 && item.Score <= item.MaxScore)
            .OrderByDescending(item => (decimal)item.Score / item.MaxScore)
            .ThenByDescending(item => item.SubmittedAt).FirstOrDefault();

    public static void Apply(IEnumerable<LearningRecord> records, IEnumerable<PracticeAttempt> attempts)
    {
        var byRecord = attempts.ToLookup(item => item.LearningRecordId);
        foreach (var record in records)
        {
            var best = Best(byRecord[record.Id]);
            record.PracticeBestScore = best?.Score;
            record.PracticeMaxScore = best?.MaxScore;
        }
    }

    public static async Task LoadAsync(SchoolDbContext db, IReadOnlyCollection<LearningRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return;
        var ids = records.Select(item => item.Id).ToArray();
        Apply(records, await db.PracticeAttempts.AsNoTracking().Where(item => ids.Contains(item.LearningRecordId)).ToListAsync(ct));
    }

    public static decimal? Percent(LearningRecord? record)
        => record?.PracticeMaxScore is > 0 && record.PracticeBestScore is { } score
            ? (decimal)score * 100 / record.PracticeMaxScore.Value : null;

    public static int Score(string kind, LearningRecord? record)
        => kind == "theoryPractice" ? record?.PracticeBestScore ?? 0 : record?.BestScore ?? 0;

    public static int Maximum(string kind, LearningRecord? record, int currentMaximum)
        => kind == "theoryPractice" ? record?.PracticeMaxScore ?? 0 : currentMaximum;

    public static decimal? AveragePercent(IEnumerable<PracticeAttempt> attempts)
    {
        var rates = attempts.Where(item => item.MaxScore > 0 && item.Score >= 0 && item.Score <= item.MaxScore)
            .Select(item => (decimal)item.Score * 100 / item.MaxScore).ToArray();
        return rates.Length == 0 ? null : Math.Round(rates.Average(), 1);
    }

    public static decimal? BestPercent(IEnumerable<PracticeAttempt> attempts)
    {
        var best = Best(attempts);
        return best == null ? null : Math.Round((decimal)best.Score * 100 / best.MaxScore, 1);
    }
}
