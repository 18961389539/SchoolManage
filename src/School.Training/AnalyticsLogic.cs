using Microsoft.EntityFrameworkCore;

namespace School.Training;

public readonly record struct LearningState(bool Learned, bool Passed);

public sealed record KindStats(int Total, int Learned, int Passed, int LearningRate, int PassRate);

public sealed record AnalyticsResource(
    string Id, string Name, string Kind, string CourseId, string CourseName,
    string ChapterId, string ChapterName, string SectionId, string SectionName,
    int ChapterSort, int SectionSort, int ResourceSort, int DurationMinutes, int MaxScore);

public sealed record AnalyticsStudent(
    string UserId, string PersonId, string Name, string UserName, string Number,
    string? ClassId, string ClassName, string ProfessionName);

public sealed record ActivityEvent(string UserId, string ClassroomId, string ResourceId, DateTime At, bool IsPractice, bool IsCompletion,
    bool Passed = false);

public sealed class LearnerData
{
    public Dictionary<string, LearningRecord> Records { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<PracticeAttempt>> Attempts { get; } = new(StringComparer.Ordinal);
    public List<ActivityEvent> Activities { get; } = [];

    public static string Key(string userId, string resourceId) => userId + "\u001f" + resourceId;

    public LearningRecord? RecordFor(string userId, string resourceId)
        => Records.GetValueOrDefault(Key(userId, resourceId));

    public IReadOnlyList<PracticeAttempt> AttemptsFor(string userId, string resourceId)
        => Attempts.GetValueOrDefault(Key(userId, resourceId)) ?? (IReadOnlyList<PracticeAttempt>)Array.Empty<PracticeAttempt>();
}

public static class SchoolTime
{
    /// <summary>School reports and "today" are defined in China Standard Time regardless of server locale.</summary>
    static readonly TimeZoneInfo Zone = ResolveZone();

    static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("SchoolCST", TimeSpan.FromHours(8), "School CST", "School CST");
    }

    public static (DateTime StartUtc, DateTime EndUtc) TodayUtc()
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified), Zone);
        return (startUtc, startUtc.AddDays(1));
    }

    public static DateTime ToLocal(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateTime LocalDate(DateTime utc) => ToLocal(utc).Date;

    public static string Format(DateTime utc) => ToLocal(utc).ToString("yyyy-MM-dd HH:mm:ss");
}

public static class AnalyticsLogic
{
    public const int PassScorePercent = 60;
    public const int OverallTargetPercent = 60;

    public static readonly string[] KindOrder = ["theory", "theoryPractice", "case", "casePractice"];

    public static bool IsLearned(LearningRecord? record)
        => record is not null && (record.Status is "inProgress" or "completed" || record.AttemptCount > 0);

    public static bool IsPassed(string kind, LearningRecord? record, int maxScore)
    {
        if (!IsLearned(record)) return false;
        return kind is "theoryPractice" or "casePractice"
            ? maxScore > 0 && record!.AttemptCount > 0 && (long)record.BestScore * 100 >= (long)maxScore * PassScorePercent
            : record!.Status == "completed";
    }

    public static LearningState Evaluate(string kind, LearningRecord? record, int maxScore)
        => new(IsLearned(record), IsPassed(kind, record, maxScore));

    public static int Rate(int part, int total)
        => total <= 0 ? 0 : (int)Math.Round(part * 100.0 / total, MidpointRounding.AwayFromZero);

    public static KindStats Metrics(IEnumerable<AnalyticsResource> resources, Func<AnalyticsResource, LearningState> stateOf)
    {
        var total = 0;
        var learned = 0;
        var passed = 0;
        foreach (var resource in resources)
        {
            total++;
            var state = stateOf(resource);
            if (state.Learned) learned++;
            if (state.Passed) passed++;
        }
        return new(total, learned, passed, Rate(learned, total), Rate(passed, total));
    }

    public static KindStats MetricsFor(LearnerData data, string userId, IEnumerable<AnalyticsResource> resources)
        => Metrics(resources, resource => Evaluate(resource.Kind, data.RecordFor(userId, resource.Id), resource.MaxScore));

    public static bool MeetsOverallTarget(KindStats stats)
        => stats.Total > 0 && (long)stats.Passed * 100 >= (long)stats.Total * OverallTargetPercent;

    public static string ResourceStatus(bool learned, bool passed)
        => !learned ? "notStarted" : passed ? "passed" : "learning";

    public static string SkillState(int total, int passed, int learned)
        => total > 0 && passed >= total ? "mastered" : learned > 0 ? "learning" : "notStarted";

    public static int NextTarget(int score)
        => score >= 100 ? 100 : (score / 10 + 1) * 10;

    public static int ActiveDays(IEnumerable<DateTime> timestamps)
        => timestamps.Select(SchoolTime.LocalDate).Distinct().Count();

    public static DateTime? LastActivity(IEnumerable<DateTime> timestamps)
    {
        DateTime? last = null;
        foreach (var timestamp in timestamps)
            if (last is null || timestamp > last) last = timestamp;
        return last;
    }
}

public static class AnalyticsLoader
{
    public static async Task<Dictionary<string, List<AnalyticsResource>>> LoadResourcesAsync(
        SchoolDbContext db, IReadOnlyCollection<string> courseIds, CancellationToken ct)
    {
        var result = new Dictionary<string, List<AnalyticsResource>>(StringComparer.Ordinal);
        var ids = courseIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length == 0) return result;

        var courseNames = await db.Courses.AsNoTracking().Where(item => ids.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.Name, ct);
        var chapters = await db.CourseChapters.AsNoTracking().Where(item => ids.Contains(item.CourseId)).ToListAsync(ct);
        var chapterIds = chapters.Select(item => item.Id).ToArray();
        var sections = await db.CourseSections.AsNoTracking().Where(item => chapterIds.Contains(item.ChapterId)).ToListAsync(ct);
        var sectionIds = sections.Select(item => item.Id).ToArray();
        var resources = await db.CourseResources.AsNoTracking().Where(item => sectionIds.Contains(item.SectionId)).ToListAsync(ct);
        var resourceIds = resources.Select(item => item.Id).ToArray();
        var maxScores = await db.CourseQuestions.AsNoTracking().Where(item => resourceIds.Contains(item.ResourceId))
            .GroupBy(item => item.ResourceId)
            .Select(group => new { group.Key, Max = group.Sum(item => item.Score) })
            .ToDictionaryAsync(item => item.Key, item => item.Max, ct);

        var chapterById = chapters.ToDictionary(item => item.Id);
        var sectionById = sections.ToDictionary(item => item.Id);
        var rows = new List<AnalyticsResource>();
        foreach (var resource in resources)
        {
            if (!sectionById.TryGetValue(resource.SectionId, out var section)) continue;
            if (!chapterById.TryGetValue(section.ChapterId, out var chapter)) continue;
            rows.Add(new AnalyticsResource(
                resource.Id, resource.Name, resource.Kind, chapter.CourseId, courseNames.GetValueOrDefault(chapter.CourseId, ""),
                chapter.Id, chapter.Name, section.Id, section.Name,
                chapter.SortOrder, section.SortOrder, resource.SortOrder, resource.DurationMinutes,
                resource.Kind == "casePractice" ? 100 : maxScores.GetValueOrDefault(resource.Id)));
        }
        foreach (var group in rows.GroupBy(item => item.CourseId))
            result[group.Key] = group.OrderBy(item => item.ChapterSort)
                .ThenBy(item => item.SectionSort).ThenBy(item => item.ResourceSort).ToList();
        return result;
    }

    public static async Task<List<AnalyticsStudent>> LoadStudentsAsync(
        SchoolDbContext db, IReadOnlyCollection<string> classroomIds, CancellationToken ct)
    {
        var ids = classroomIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length == 0) return [];
        var rows = await (from enrollment in db.ClassroomStudents.AsNoTracking()
                          join person in db.People.AsNoTracking() on enrollment.StudentPersonId equals person.Id
                          join user in db.Users.AsNoTracking() on enrollment.StudentUserId equals user.Id
                          join schoolClass in db.Classes.AsNoTracking() on person.ClassId equals schoolClass.Id into classes
                          from schoolClass in classes.DefaultIfEmpty()
                          where ids.Contains(enrollment.ClassroomId)
                          select new
                          {
                              UserId = enrollment.StudentUserId, PersonId = enrollment.StudentPersonId,
                              Name = person.Name, UserName = user.UserName, Number = person.Number,
                              person.ClassId, ClassName = schoolClass == null ? "" : schoolClass.Name,
                              ProfessionId = schoolClass == null ? null : schoolClass.ProfessionId
                          }).ToListAsync(ct);
        var professionIds = rows.Where(item => item.ProfessionId != null).Select(item => item.ProfessionId!)
            .Distinct().ToArray();
        var professions = professionIds.Length == 0
            ? new Dictionary<string, string>()
            : await db.Professions.AsNoTracking().Where(item => professionIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.Name, ct);
        return rows.GroupBy(item => item.UserId).Select(group => group.First())
            .Select(item => new AnalyticsStudent(
                item.UserId, item.PersonId, item.Name, item.UserName, item.Number,
                item.ClassId, item.ClassName, item.ProfessionId == null ? "" : professions.GetValueOrDefault(item.ProfessionId, "")))
            .OrderBy(item => item.Number).ToList();
    }

    public static async Task<LearnerData> LoadLearnerDataAsync(
        SchoolDbContext db, IReadOnlyCollection<string> classroomIds, CancellationToken ct)
    {
        var data = new LearnerData();
        var ids = classroomIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length == 0) return data;

        var records = await db.LearningRecords.AsNoTracking().Where(item => ids.Contains(item.ClassroomId)).ToListAsync(ct);
        foreach (var record in records)
        {
            var key = LearnerData.Key(record.UserId, record.ResourceId);
            if (!data.Records.TryGetValue(key, out var current) || Rank(record).CompareTo(Rank(current)) >= 0) data.Records[key] = record;
            data.Activities.Add(new ActivityEvent(record.UserId, record.ClassroomId, record.ResourceId, record.StartedAt, false, false));
            if (record.CompletedAt is { } completedAt)
                data.Activities.Add(new ActivityEvent(record.UserId, record.ClassroomId, record.ResourceId, completedAt, false, true));
        }

        var attempts = await (from attempt in db.PracticeAttempts.AsNoTracking()
                              join record in db.LearningRecords.AsNoTracking() on attempt.LearningRecordId equals record.Id
                              where ids.Contains(record.ClassroomId)
                              select new { record.UserId, record.ClassroomId, record.ResourceId, Attempt = attempt }).ToListAsync(ct);
        foreach (var row in attempts)
        {
            var key = LearnerData.Key(row.UserId, row.ResourceId);
            if (!data.Attempts.TryGetValue(key, out var list)) data.Attempts[key] = list = [];
            list.Add(row.Attempt);
            if (row.Attempt.StartedAt is { } started)
                data.Activities.Add(new ActivityEvent(row.UserId, row.ClassroomId, row.ResourceId, started, false, false));
            data.Activities.Add(new ActivityEvent(row.UserId, row.ClassroomId, row.ResourceId, row.Attempt.SubmittedAt, true, false,
                row.Attempt.MaxScore > 0 && (long)row.Attempt.Score * 100 >= (long)row.Attempt.MaxScore * AnalyticsLogic.PassScorePercent));
        }
        data.Activities.AddRange(await LoadCaseActivities(db, ids, ct));
        foreach (var list in data.Attempts.Values) list.Sort((left, right) => left.SubmittedAt.CompareTo(right.SubmittedAt));
        return data;
    }

    internal static Task<List<ActivityEvent>> LoadCaseActivities(SchoolDbContext db, string[] classroomIds, CancellationToken ct)
        => (from grade in db.ClassroomCaseGrades.AsNoTracking()
            join session in db.ClassroomCaseSessions.AsNoTracking() on grade.SessionId equals session.Id
            where classroomIds.Contains(session.ClassroomId) && grade.IsFinal
            select new ActivityEvent(session.UserId, session.ClassroomId, session.ResourceId, grade.ScoredAt, true, false,
                grade.MaxScore > 0 && (long)grade.Score * 100 >= (long)grade.MaxScore * AnalyticsLogic.PassScorePercent)).ToListAsync(ct);

    static (int Status, int Best, int Attempts) Rank(LearningRecord record)
        => (record.Status switch { "completed" => 2, "inProgress" => 1, _ => 0 }, record.BestScore, record.AttemptCount);
}
