namespace School.Training.Tests;

public class AnalyticsLogicTests
{
    static LearningRecord Record(string status, int bestScore = 0, int attemptCount = 0, int? practiceMaxScore = null)
        => new()
        {
            Status = status,
            BestScore = bestScore,
            AttemptCount = attemptCount,
            PracticeBestScore = practiceMaxScore is null ? null : bestScore,
            PracticeMaxScore = practiceMaxScore
        };

    static AnalyticsResource Resource(string id, string kind = "theory", int maxScore = 0)
        => new(id, id, kind, "course", "课程", "chapter", "章节", "section", "小节", 1, 1, 1, 30, maxScore);

    [Theory]
    [InlineData("inProgress", 0, 0, true)]
    [InlineData("completed", 0, 0, true)]
    [InlineData("notStarted", 0, 1, true)]
    [InlineData("notStarted", 0, 0, false)]
    public void Learning_requires_progress_completion_or_attempts(string status, int score, int attempts, bool expected)
    {
        Assert.Equal(expected, AnalyticsLogic.IsLearned(Record(status, score, attempts)));
        Assert.False(AnalyticsLogic.IsLearned(null));
    }

    [Theory]
    [InlineData("theory", "completed", 0, 0, 0, true)]
    [InlineData("theory", "inProgress", 0, 0, 0, false)]
    [InlineData("case", "completed", 0, 0, 0, true)]
    [InlineData("casePractice", "completed", 0, 0, 100, false)]
    [InlineData("casePractice", "completed", 59, 1, 100, false)]
    [InlineData("casePractice", "inProgress", 60, 1, 100, true)]
    [InlineData("theoryPractice", "completed", 60, 1, 100, true)]
    [InlineData("theoryPractice", "completed", 59, 1, 100, false)]
    [InlineData("theoryPractice", "inProgress", 80, 1, 100, true)]
    [InlineData("theoryPractice", "completed", 80, 0, 100, false)]
    [InlineData("theoryPractice", "completed", 0, 1, 0, false)]
    public void Passed_uses_completion_or_sixty_percent_on_practice(
        string kind, string status, int bestScore, int attempts, int maxScore, bool expected)
    {
        var record = kind == "theoryPractice"
            ? Record(status, bestScore, attempts, maxScore)
            : Record(status, bestScore, attempts);
        Assert.Equal(expected, AnalyticsLogic.IsPassed(kind, record, maxScore));
    }

    [Fact]
    public void Missing_record_never_passes()
    {
        Assert.False(AnalyticsLogic.IsPassed("theory", null, 100));
        Assert.Equal(new LearningState(false, false), AnalyticsLogic.Evaluate("theoryPractice", null, 100));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 3, 33)]
    [InlineData(2, 3, 67)]
    [InlineData(3, 3, 100)]
    [InlineData(5, 0, 0)]
    public void Rate_rounds_half_away_from_zero(int part, int total, int expected)
    {
        Assert.Equal(expected, AnalyticsLogic.Rate(part, total));
    }

    [Fact]
    public void Metrics_aggregate_learned_and_passed_per_resource()
    {
        var records = new Dictionary<string, LearningRecord>
        {
            ["a"] = Record("completed"),
            ["b"] = Record("inProgress"),
            ["c"] = Record("completed", 60, 1, 100)
        };
        var resources = new[]
        {
            Resource("a"),
            Resource("b"),
            Resource("c", "theoryPractice", 100),
            Resource("d")
        };
        var stats = AnalyticsLogic.Metrics(resources,
            resource => AnalyticsLogic.Evaluate(resource.Kind, records.GetValueOrDefault(resource.Id), resource.MaxScore));
        Assert.Equal(4, stats.Total);
        Assert.Equal(3, stats.Learned);
        Assert.Equal(2, stats.Passed);
        Assert.Equal(75, stats.LearningRate);
        Assert.Equal(50, stats.PassRate);
    }

    [Theory]
    [InlineData(0, 0, 0, "notStarted")]
    [InlineData(3, 0, 1, "learning")]
    [InlineData(3, 1, 2, "learning")]
    [InlineData(3, 3, 3, "mastered")]
    public void Skill_state_requires_every_resource_passed_for_mastery(int total, int passed, int learned, string expected)
    {
        Assert.Equal(expected, AnalyticsLogic.SkillState(total, passed, learned));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(10, 5, false)]
    [InlineData(10, 6, true)]
    [InlineData(5, 3, true)]
    public void Overall_target_requires_sixty_percent_of_resources(int total, int passed, bool expected)
    {
        Assert.Equal(expected, AnalyticsLogic.MeetsOverallTarget(new KindStats(total, total, passed, 100, 0)));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(29, 30)]
    [InlineData(30, 40)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    public void Next_target_moves_to_the_next_ten_point_milestone(int score, int expected)
    {
        Assert.Equal(expected, AnalyticsLogic.NextTarget(score));
    }

    [Theory]
    [InlineData(false, false, "notStarted")]
    [InlineData(true, false, "learning")]
    [InlineData(true, true, "passed")]
    public void Resource_status_separates_learning_from_passing(bool learned, bool passed, string expected)
    {
        Assert.Equal(expected, AnalyticsLogic.ResourceStatus(learned, passed));
    }

    [Fact]
    public void Active_days_use_china_standard_time_and_last_activity_is_latest()
    {
        var timestamps = new[]
        {
            new DateTime(2026, 11, 20, 15, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 20, 17, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc)
        };
        Assert.Equal(2, AnalyticsLogic.ActiveDays(timestamps));
        Assert.Equal(new DateTime(2026, 11, 20, 17, 0, 0, DateTimeKind.Utc), AnalyticsLogic.LastActivity(timestamps));
        Assert.Null(AnalyticsLogic.LastActivity([]));
    }

    [Fact]
    public void Theory_practice_pass_stays_stable_when_current_maximum_changes()
    {
        var record = Record("completed", 60, 1, 100);
        Assert.True(AnalyticsLogic.IsPassed("theoryPractice", record, 200));
        Assert.True(AnalyticsLogic.IsPassed("theoryPractice", record, 50));
        Assert.Equal(60, PracticeHistory.Percent(record));
    }

    [Fact]
    public void Theory_practice_without_historical_maximum_is_not_inferred_as_passed()
    {
        var record = Record("completed", 80, 1);
        Assert.False(AnalyticsLogic.IsPassed("theoryPractice", record, 100));
        Assert.Null(PracticeHistory.Percent(record));
        Assert.Equal(0, PracticeHistory.Maximum("theoryPractice", record, 100));
    }
}
