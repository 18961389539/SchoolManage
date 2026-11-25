namespace School.Training.Tests;

public class PracticeHistoryTests
{
    static PracticeAttempt Attempt(int score, int maxScore, DateTime? submittedAt = null)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            LearningRecordId = "record",
            Score = score,
            MaxScore = maxScore,
            SubmittedAt = submittedAt ?? DateTime.UtcNow
        };

    [Fact]
    public void Best_prefers_highest_score_rate_not_raw_score()
    {
        var earlier = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var later = earlier.AddDays(1);
        var best = PracticeHistory.Best([Attempt(90, 200, later), Attempt(60, 100, earlier)]);
        Assert.NotNull(best);
        Assert.Equal(60, best!.Score);
        Assert.Equal(100, best.MaxScore);
        Assert.Equal(60m, PracticeHistory.BestPercent([Attempt(90, 200, later), Attempt(60, 100, earlier)]));
    }

    [Fact]
    public void Invalid_or_zero_maximum_attempts_are_ignored()
    {
        Assert.Null(PracticeHistory.Best([Attempt(8, 0), Attempt(-1, 10), Attempt(20, 10)]));
        Assert.Null(PracticeHistory.AveragePercent([Attempt(8, 0), Attempt(20, 10)]));
        var record = new LearningRecord { Id = "r1", AttemptCount = 1, BestScore = 80 };
        PracticeHistory.Apply([record], [new PracticeAttempt { LearningRecordId = "r1", Score = 80, MaxScore = 0 }]);
        Assert.Null(record.PracticeBestScore);
        Assert.Null(record.PracticeMaxScore);
        Assert.Null(PracticeHistory.Percent(record));
    }

    [Fact]
    public void Average_percent_uses_each_attempt_original_maximum()
    {
        var average = PracticeHistory.AveragePercent([Attempt(60, 100), Attempt(20, 50)]);
        Assert.Equal(50m, average);
    }

    [Fact]
    public void Apply_only_matches_attempts_for_the_same_record()
    {
        var first = new LearningRecord { Id = "a", BestScore = 10, AttemptCount = 1 };
        var second = new LearningRecord { Id = "b", BestScore = 99, AttemptCount = 1 };
        PracticeHistory.Apply([first, second],
        [
            new PracticeAttempt { LearningRecordId = "a", Score = 6, MaxScore = 10, SubmittedAt = DateTime.UtcNow },
            new PracticeAttempt { LearningRecordId = "other", Score = 9, MaxScore = 10, SubmittedAt = DateTime.UtcNow }
        ]);
        Assert.Equal(6, first.PracticeBestScore);
        Assert.Equal(10, first.PracticeMaxScore);
        Assert.Null(second.PracticeBestScore);
        Assert.Null(second.PracticeMaxScore);
    }
}
