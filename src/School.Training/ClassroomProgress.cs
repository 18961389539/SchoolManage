using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

/// <summary>
/// Keeps per-student classroom totals current while learning happens, and freezes them
/// once a classroom is closed. Admin and teacher home then rank from these rows.
/// </summary>
public static class ClassroomProgress
{
    const int Chunk = 400;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task EnsureAsync(SchoolDbContext db, IReadOnlyCollection<Classroom> classrooms, CancellationToken ct)
    {
        if (classrooms.Count == 0) return;
        var now = DateTime.UtcNow;
        var closed = classrooms.Where(item => TeachingLogic.EffectiveStatus(item, now) == "closed").ToArray();
        var live = classrooms.Where(item => TeachingLogic.EffectiveStatus(item, now) != "closed").ToArray();
        var ids = classrooms.Select(item => item.Id).ToArray();
        var freezes = await db.ClassroomProgressFreezes.Where(item => ids.Contains(item.ClassroomId)).ToListAsync(ct);
        var frozenIds = freezes.Select(item => item.ClassroomId).ToHashSet(StringComparer.Ordinal);

        foreach (var classroom in live.Where(item => frozenIds.Contains(item.Id)))
            await UnfreezeAsync(db, classroom.Id, ct);

        foreach (var classroom in closed.Where(item => !frozenIds.Contains(item.Id)))
            await FreezeAsync(db, classroom, ct);

        await SyncLiveAsync(db, live.Select(item => item.Id).ToArray(), ct);
    }

    public static async Task RefreshStudentAsync(SchoolDbContext db, string classroomId, string userId, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == classroomId, ct);
        if (classroom == null) return;
        var resources = await ResourcesAsync(db, classroom.CourseId, ct);
        await RebuildUsersAsync(db, classroom.Id, resources, [userId], ct);
        await db.SaveChangesAsync(ct);
    }

    public static async Task ForgetStudentsAsync(SchoolDbContext db, string classroomId, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length == 0) return;
        var stats = await db.ClassroomStudentStats.Where(item => item.ClassroomId == classroomId && ids.Contains(item.UserId)).ToListAsync(ct);
        db.ClassroomStudentStats.RemoveRange(stats);
        await db.SaveChangesAsync(ct);
    }

    public static async Task UnfreezeAsync(SchoolDbContext db, string classroomId, CancellationToken ct)
    {
        var freeze = await db.ClassroomProgressFreezes.FirstOrDefaultAsync(item => item.ClassroomId == classroomId, ct);
        if (freeze != null) db.ClassroomProgressFreezes.Remove(freeze);
        var stats = await db.ClassroomStudentStats.Where(item => item.ClassroomId == classroomId).ToListAsync(ct);
        db.ClassroomStudentStats.RemoveRange(stats);
        if (freeze != null || stats.Count > 0) await db.SaveChangesAsync(ct);
    }

    public static async Task FreezeAsync(SchoolDbContext db, Classroom classroom, CancellationToken ct)
    {
        if (await db.ClassroomProgressFreezes.AnyAsync(item => item.ClassroomId == classroom.Id, ct)) return;
        var resources = await ResourcesAsync(db, classroom.CourseId, ct);
        var userIds = await db.LearningRecords.AsNoTracking().Where(item => item.ClassroomId == classroom.Id)
            .Select(item => item.UserId).Distinct().ToListAsync(ct);
        await RebuildUsersAsync(db, classroom.Id, resources, userIds, ct);
        db.ClassroomProgressFreezes.Add(new ClassroomProgressFreeze
        {
            ClassroomId = classroom.Id,
            ResourceCount = resources.Count,
            VerifiableCount = resources.Count(item => AnalyticsLogic.IsVerifiable(item.Kind)),
            ResourcesJson = JsonSerializer.Serialize(resources.Select(item => new FrozenResource(item.Id, item.Kind, item.MaxScore)), Json),
            FrozenAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    public static async Task<List<ActivityEvent>> LoadTodayAsync(
        SchoolDbContext db, IReadOnlyCollection<string> classroomIds, (DateTime StartUtc, DateTime EndUtc) today, CancellationToken ct)
    {
        var events = new List<ActivityEvent>();
        foreach (var ids in Chunks(classroomIds))
        {
            var records = await db.LearningRecords.AsNoTracking()
                .Where(item => ids.Contains(item.ClassroomId)
                    && ((item.StartedAt >= today.StartUtc && item.StartedAt < today.EndUtc)
                        || (item.CompletedAt != null && item.CompletedAt >= today.StartUtc && item.CompletedAt < today.EndUtc)))
                .Select(item => new { item.UserId, item.ClassroomId, item.ResourceId, item.StartedAt, item.CompletedAt })
                .ToListAsync(ct);
            foreach (var record in records)
            {
                if (record.StartedAt >= today.StartUtc && record.StartedAt < today.EndUtc)
                    events.Add(new ActivityEvent(record.UserId, record.ClassroomId, record.ResourceId, record.StartedAt, false, false));
                if (record.CompletedAt is { } completed && completed >= today.StartUtc && completed < today.EndUtc)
                    events.Add(new ActivityEvent(record.UserId, record.ClassroomId, record.ResourceId, completed, false, true));
            }

            var submitted = await (
                from attempt in db.PracticeAttempts.AsNoTracking()
                join record in db.LearningRecords.AsNoTracking() on attempt.LearningRecordId equals record.Id
                where ids.Contains(record.ClassroomId) && attempt.SubmittedAt >= today.StartUtc && attempt.SubmittedAt < today.EndUtc
                select new { record.UserId, record.ClassroomId, record.ResourceId, attempt.Score, attempt.MaxScore, attempt.SubmittedAt, attempt.StartedAt }
            ).ToListAsync(ct);
            foreach (var attempt in submitted)
            {
                if (attempt.StartedAt is { } started && started >= today.StartUtc && started < today.EndUtc)
                    events.Add(new ActivityEvent(attempt.UserId, attempt.ClassroomId, attempt.ResourceId, started, false, false));
                var passed = attempt.MaxScore > 0 && (long)attempt.Score * 100 >= (long)attempt.MaxScore * AnalyticsLogic.PassScorePercent;
                events.Add(new ActivityEvent(attempt.UserId, attempt.ClassroomId, attempt.ResourceId, attempt.SubmittedAt, true, false, passed));
            }

            var startedOnly = await (
                from attempt in db.PracticeAttempts.AsNoTracking()
                join record in db.LearningRecords.AsNoTracking() on attempt.LearningRecordId equals record.Id
                where ids.Contains(record.ClassroomId) && attempt.StartedAt != null
                    && attempt.StartedAt >= today.StartUtc && attempt.StartedAt < today.EndUtc
                    && (attempt.SubmittedAt < today.StartUtc || attempt.SubmittedAt >= today.EndUtc)
                select new { record.UserId, record.ClassroomId, record.ResourceId, attempt.StartedAt }
            ).ToListAsync(ct);
            foreach (var attempt in startedOnly)
                events.Add(new ActivityEvent(attempt.UserId, attempt.ClassroomId, attempt.ResourceId, attempt.StartedAt!.Value, false, false));

            var grades = await (
                from grade in db.ClassroomCaseGrades.AsNoTracking()
                join session in db.ClassroomCaseSessions.AsNoTracking() on grade.SessionId equals session.Id
                where ids.Contains(session.ClassroomId) && grade.IsFinal && grade.ScoredAt >= today.StartUtc && grade.ScoredAt < today.EndUtc
                select new { session.UserId, session.ClassroomId, session.ResourceId, grade.Score, grade.MaxScore, grade.ScoredAt }
            ).ToListAsync(ct);
            foreach (var grade in grades)
            {
                var passed = grade.MaxScore > 0 && (long)grade.Score * 100 >= (long)grade.MaxScore * AnalyticsLogic.PassScorePercent;
                events.Add(new ActivityEvent(grade.UserId, grade.ClassroomId, grade.ResourceId, grade.ScoredAt, true, false, passed));
            }
        }
        return events;
    }

    public static async Task<StaffProgress> ReadStaffAsync(
        SchoolDbContext db, IReadOnlyDictionary<string, Classroom> classroomById,
        IReadOnlyDictionary<string, List<string>> classroomsByUser,
        IReadOnlyDictionary<string, List<AnalyticsResource>> resourcesByCourse, CancellationToken ct)
    {
        var progress = new StaffProgress();
        var classroomIds = classroomById.Keys.ToArray();
        if (classroomIds.Length == 0) return progress;
        foreach (var ids in Chunks(classroomIds))
        {
            foreach (var stat in await db.ClassroomStudentStats.AsNoTracking().Where(item => ids.Contains(item.ClassroomId)).ToListAsync(ct))
                progress.Stats[Key(stat.ClassroomId, stat.UserId)] = stat;
            foreach (var freeze in await db.ClassroomProgressFreezes.AsNoTracking().Where(item => ids.Contains(item.ClassroomId)).ToListAsync(ct))
                progress.Freezes[freeze.ClassroomId] = freeze;
        }
        await ResolveOverlapsAsync(db, classroomById, classroomsByUser, resourcesByCourse, progress, ct);
        return progress;
    }

    static async Task SyncLiveAsync(SchoolDbContext db, string[] classroomIds, CancellationToken ct)
    {
        if (classroomIds.Length == 0) return;
        var observed = await ObserveAsync(db, classroomIds, ct);
        var stats = new List<ClassroomStudentStat>();
        foreach (var ids in Chunks(classroomIds))
            stats.AddRange(await db.ClassroomStudentStats.Where(item => ids.Contains(item.ClassroomId)).ToListAsync(ct));
        var known = new HashSet<string>(StringComparer.Ordinal);
        var dirty = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var stat in stats)
        {
            var key = Key(stat.ClassroomId, stat.UserId);
            known.Add(key);
            if (!observed.TryGetValue(key, out var signature) || !signature.Matches(stat))
                AddDirty(dirty, stat.ClassroomId, stat.UserId);
        }
        foreach (var pair in observed)
        {
            if (known.Contains(pair.Key)) continue;
            var split = pair.Key.Split('\u001f');
            AddDirty(dirty, split[0], split[1]);
        }
        if (dirty.Count == 0)
        {
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            return;
        }
        var courseByClassroom = await db.Classrooms.AsNoTracking().Where(item => dirty.Keys.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.CourseId, ct);
        foreach (var (classroomId, users) in dirty)
        {
            if (!courseByClassroom.TryGetValue(classroomId, out var courseId)) continue;
            var resources = await ResourcesAsync(db, courseId, ct);
            var stale = stats.Where(item => item.ClassroomId == classroomId && users.Contains(item.UserId)
                && !observed.ContainsKey(Key(classroomId, item.UserId))).ToArray();
            db.ClassroomStudentStats.RemoveRange(stale);
            var rebuild = users.Where(userId => observed.ContainsKey(Key(classroomId, userId))).ToArray();
            if (rebuild.Length > 0) await RebuildUsersAsync(db, classroomId, resources, rebuild, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    static async Task RebuildUsersAsync(
        SchoolDbContext db, string classroomId, IReadOnlyList<AnalyticsResource> resources, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length == 0) return;
        var records = await db.LearningRecords.AsNoTracking()
            .Where(item => item.ClassroomId == classroomId && ids.Contains(item.UserId)).ToListAsync(ct);
        var recordIds = records.Select(item => item.Id).ToArray();
        var attempts = await AttemptsAsync(db, recordIds, ct);
        ApplyBest(records, attempts);
        var grades = await GradesAsync(db, classroomId, ids, ct);
        var signatures = await ObserveAsync(db, [classroomId], ct);
        var existing = await db.ClassroomStudentStats.Where(item => item.ClassroomId == classroomId && ids.Contains(item.UserId)).ToListAsync(ct);
        var stats = existing.ToDictionary(item => item.UserId, StringComparer.Ordinal);
        var attemptsByRecord = attempts.ToLookup(item => item.RecordId);
        var gradesByUser = grades.ToLookup(item => item.UserId);
        foreach (var group in records.GroupBy(item => item.UserId))
        {
            if (!signatures.TryGetValue(Key(classroomId, group.Key), out var signature)) continue;
            var ownAttempts = group.SelectMany(record => attemptsByRecord[record.Id]).ToArray();
            var ownGrades = gradesByUser[group.Key].ToArray();
            var measured = Measure(resources, group.ToArray(), ownAttempts, ownGrades);
            if (!stats.TryGetValue(group.Key, out var stat))
            {
                stat = new ClassroomStudentStat { ClassroomId = classroomId, UserId = group.Key };
                db.ClassroomStudentStats.Add(stat);
                stats[group.Key] = stat;
            }
            stat.LearnedCount = measured.Learned;
            stat.PassedCount = measured.Passed;
            stat.RecordCount = signature.Records;
            stat.AttemptCount = signature.Attempts;
            stat.GradeCount = signature.Grades;
            stat.SourceStamp = signature.Stamp;
            stat.LastActivityAt = measured.LastActivity;
            stat.ActiveDates = string.Join(',', measured.Dates);
            stat.UpdatedAt = DateTime.UtcNow;
        }
    }

    static async Task ResolveOverlapsAsync(
        SchoolDbContext db, IReadOnlyDictionary<string, Classroom> classroomById,
        IReadOnlyDictionary<string, List<string>> classroomsByUser,
        IReadOnlyDictionary<string, List<AnalyticsResource>> resourcesByCourse, StaffProgress progress, CancellationToken ct)
    {
        var jobs = new List<(string UserId, string CourseId, string[] ClassroomIds)>();
        foreach (var (userId, classroomIds) in classroomsByUser)
        {
            foreach (var group in classroomIds.GroupBy(id => classroomById[id].CourseId))
            {
                var ids = group.Distinct(StringComparer.Ordinal).ToArray();
                if (ids.Length > 1) jobs.Add((userId, group.Key, ids));
            }
        }
        if (jobs.Count == 0) return;
        var overlapRooms = jobs.SelectMany(job => job.ClassroomIds).Distinct(StringComparer.Ordinal).ToArray();
        var userIds = jobs.Select(job => job.UserId).Distinct(StringComparer.Ordinal).ToArray();
        var records = await db.LearningRecords.AsNoTracking()
            .Where(item => overlapRooms.Contains(item.ClassroomId) && userIds.Contains(item.UserId)).ToListAsync(ct);
        ApplyBest(records, await AttemptsAsync(db, records.Select(item => item.Id).ToArray(), ct));
        var data = new LearnerData();
        foreach (var record in records) data.Records[LearnerData.Key(record.ClassroomId, record.UserId, record.ResourceId)] = record;
        foreach (var job in jobs)
        {
            var allFrozen = job.ClassroomIds.All(progress.Freezes.ContainsKey);
            IReadOnlyList<AnalyticsResource> resources;
            if (allFrozen)
            {
                var latest = job.ClassroomIds.Select(id => progress.Freezes[id]).OrderByDescending(item => item.FrozenAt).First();
                resources = Thaw(latest, job.CourseId);
            }
            else resources = resourcesByCourse.GetValueOrDefault(job.CourseId) ?? [];
            var allowed = job.ClassroomIds.ToHashSet(StringComparer.Ordinal);
            var stats = AnalyticsLogic.Metrics(resources, resource =>
            {
                var record = data.Records.Values.Where(item => item.UserId == job.UserId && item.ResourceId == resource.Id && allowed.Contains(item.ClassroomId))
                    .OrderByDescending(item => item.Status switch { "completed" => 2, "inProgress" => 1, _ => 0 })
                    .ThenByDescending(item => item.BestScore).ThenByDescending(item => item.AttemptCount).FirstOrDefault();
                return AnalyticsLogic.Evaluate(resource.Kind, record, resource.MaxScore);
            });
            progress.Overlaps[Key(job.UserId, job.CourseId)] = stats;
        }
    }

    static async Task<Dictionary<string, ProgressSignature>> ObserveAsync(SchoolDbContext db, IReadOnlyCollection<string> classroomIds, CancellationToken ct)
    {
        var result = new Dictionary<string, ProgressSignature>(StringComparer.Ordinal);
        foreach (var ids in Chunks(classroomIds))
        {
            var records = await db.LearningRecords.AsNoTracking().Where(item => ids.Contains(item.ClassroomId))
                .GroupBy(item => new { item.ClassroomId, item.UserId })
                .Select(group => new
                {
                    group.Key.ClassroomId,
                    group.Key.UserId,
                    Count = group.Count(),
                    Started = group.Max(item => item.StartedAt),
                    Completed = group.Max(item => item.CompletedAt)
                }).ToListAsync(ct);
            foreach (var row in records)
                Merge(result, row.ClassroomId, row.UserId, records: row.Count, attempts: 0, grades: 0, row.Started, row.Completed);

            var attempts = await (
                from attempt in db.PracticeAttempts.AsNoTracking()
                join record in db.LearningRecords.AsNoTracking() on attempt.LearningRecordId equals record.Id
                where ids.Contains(record.ClassroomId)
                group attempt by new { record.ClassroomId, record.UserId } into grouped
                select new
                {
                    grouped.Key.ClassroomId,
                    grouped.Key.UserId,
                    Count = grouped.Count(),
                    Submitted = grouped.Max(item => item.SubmittedAt),
                    Started = grouped.Max(item => item.StartedAt)
                }).ToListAsync(ct);
            foreach (var row in attempts)
                Merge(result, row.ClassroomId, row.UserId, records: 0, attempts: row.Count, grades: 0, row.Submitted, row.Started);

            var grades = await (
                from grade in db.ClassroomCaseGrades.AsNoTracking()
                join session in db.ClassroomCaseSessions.AsNoTracking() on grade.SessionId equals session.Id
                where ids.Contains(session.ClassroomId) && grade.IsFinal
                group grade by new { session.ClassroomId, session.UserId } into grouped
                select new
                {
                    grouped.Key.ClassroomId,
                    grouped.Key.UserId,
                    Count = grouped.Count(),
                    Scored = grouped.Max(item => item.ScoredAt)
                }).ToListAsync(ct);
            foreach (var row in grades)
                Merge(result, row.ClassroomId, row.UserId, records: 0, attempts: 0, grades: row.Count, row.Scored);
        }
        return result;
    }

    static void Merge(Dictionary<string, ProgressSignature> target, string classroomId, string userId,
        int records, int attempts, int grades, params DateTime?[] stamps)
    {
        var key = Key(classroomId, userId);
        target.TryGetValue(key, out var current);
        var stamp = current.Stamp;
        foreach (var value in stamps)
            if (value is { } at && at.Ticks > stamp) stamp = at.Ticks;
        target[key] = new ProgressSignature(current.Records + records, current.Attempts + attempts, current.Grades + grades, stamp);
    }

    static async Task<List<AttemptSlice>> AttemptsAsync(SchoolDbContext db, string[] recordIds, CancellationToken ct)
    {
        var result = new List<AttemptSlice>();
        foreach (var ids in Chunks(recordIds))
        {
            result.AddRange(await db.PracticeAttempts.AsNoTracking().Where(item => ids.Contains(item.LearningRecordId))
                .Select(item => new AttemptSlice(item.LearningRecordId, item.Score, item.MaxScore, item.SubmittedAt, item.StartedAt))
                .ToListAsync(ct));
        }
        return result;
    }

    static async Task<List<GradeSlice>> GradesAsync(SchoolDbContext db, string classroomId, string[] userIds, CancellationToken ct)
    {
        var result = new List<GradeSlice>();
        foreach (var ids in Chunks(userIds))
        {
            result.AddRange(await (
                from grade in db.ClassroomCaseGrades.AsNoTracking()
                join session in db.ClassroomCaseSessions.AsNoTracking() on grade.SessionId equals session.Id
                where session.ClassroomId == classroomId && grade.IsFinal && ids.Contains(session.UserId)
                select new GradeSlice(session.UserId, grade.ScoredAt)
            ).ToListAsync(ct));
        }
        return result;
    }

    static void ApplyBest(IReadOnlyCollection<LearningRecord> records, IEnumerable<AttemptSlice> attempts)
    {
        if (records.Count == 0) return;
        PracticeHistory.Apply(records, attempts.Select(item => new PracticeAttempt
        {
            LearningRecordId = item.RecordId,
            Score = item.Score,
            MaxScore = item.MaxScore,
            SubmittedAt = item.SubmittedAt,
            StartedAt = item.StartedAt
        }));
    }

    static Measured Measure(IReadOnlyList<AnalyticsResource> resources, IReadOnlyCollection<LearningRecord> records,
        IReadOnlyCollection<AttemptSlice> attempts, IReadOnlyCollection<GradeSlice> grades)
    {
        var byResource = records.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);
        var learned = 0;
        var passed = 0;
        foreach (var resource in resources)
        {
            var state = AnalyticsLogic.Evaluate(resource.Kind, byResource.GetValueOrDefault(resource.Id), resource.MaxScore);
            if (state.Learned) learned++;
            if (state.Passed) passed++;
        }
        var dates = new SortedSet<string>(StringComparer.Ordinal);
        DateTime? last = null;
        void Note(DateTime at)
        {
            dates.Add(SchoolTime.LocalDate(at).ToString("yyyy-MM-dd"));
            if (last == null || at > last) last = at;
        }
        foreach (var record in records)
        {
            Note(record.StartedAt);
            if (record.CompletedAt is { } completed) Note(completed);
        }
        foreach (var attempt in attempts)
        {
            if (attempt.StartedAt is { } started) Note(started);
            Note(attempt.SubmittedAt);
        }
        foreach (var grade in grades) Note(grade.ScoredAt);
        return new Measured(learned, passed, last, dates);
    }

    static async Task<List<AnalyticsResource>> ResourcesAsync(SchoolDbContext db, string courseId, CancellationToken ct)
        => (await AnalyticsLoader.LoadResourcesAsync(db, [courseId], ct)).GetValueOrDefault(courseId) ?? [];

    static List<AnalyticsResource> Thaw(ClassroomProgressFreeze freeze, string courseId)
    {
        var items = JsonSerializer.Deserialize<List<FrozenResource>>(freeze.ResourcesJson, Json) ?? [];
        return items.Select(item => new AnalyticsResource(
            item.Id, "", item.Kind, courseId, "", "", "", "", "", 0, 0, 0, 0, item.MaxScore)).ToList();
    }

    static void AddDirty(Dictionary<string, HashSet<string>> dirty, string classroomId, string userId)
    {
        if (!dirty.TryGetValue(classroomId, out var users)) dirty[classroomId] = users = new HashSet<string>(StringComparer.Ordinal);
        users.Add(userId);
    }

    public static string Key(string left, string right) => left + "\u001f" + right;

    static IEnumerable<string[]> Chunks(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) yield break;
        var list = ids as string[] ?? ids.ToArray();
        for (var index = 0; index < list.Length; index += Chunk)
        {
            var length = Math.Min(Chunk, list.Length - index);
            var slice = new string[length];
            Array.Copy(list, index, slice, 0, length);
            yield return slice;
        }
    }

    readonly record struct ProgressSignature(int Records, int Attempts, int Grades, long Stamp)
    {
        public bool Matches(ClassroomStudentStat stat)
            => stat.RecordCount == Records && stat.AttemptCount == Attempts && stat.GradeCount == Grades && stat.SourceStamp == Stamp;
    }

    readonly record struct AttemptSlice(string RecordId, int Score, int MaxScore, DateTime SubmittedAt, DateTime? StartedAt);
    readonly record struct GradeSlice(string UserId, DateTime ScoredAt);
    readonly record struct Measured(int Learned, int Passed, DateTime? LastActivity, SortedSet<string> Dates);
    sealed record FrozenResource(string Id, string Kind, int MaxScore);
}

public sealed class StaffProgress
{
    public Dictionary<string, ClassroomStudentStat> Stats { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ClassroomProgressFreeze> Freezes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, KindStats> Overlaps { get; } = new(StringComparer.Ordinal);

    public KindStats Overall(IReadOnlyDictionary<string, Classroom> classroomById,
        IReadOnlyDictionary<string, List<string>> classroomsByUser,
        IReadOnlyDictionary<string, List<AnalyticsResource>> resourcesByCourse, string userId)
    {
        if (!classroomsByUser.TryGetValue(userId, out var classroomIds)) return Pack(0, 0, 0, 0);
        var learned = 0;
        var passed = 0;
        var total = 0;
        var verifiable = 0;
        foreach (var group in classroomIds.GroupBy(id => classroomById[id].CourseId))
        {
            var ids = group.Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length > 1 && Overlaps.TryGetValue(ClassroomProgress.Key(userId, group.Key), out var combined))
            {
                learned += combined.Learned;
                passed += combined.Passed;
                total += combined.Total;
                verifiable += combined.VerifiableTotal;
                continue;
            }
            var classroomId = ids[0];
            Stats.TryGetValue(ClassroomProgress.Key(classroomId, userId), out var stat);
            learned += stat?.LearnedCount ?? 0;
            passed += stat?.PassedCount ?? 0;
            var counts = Counts(resourcesByCourse, classroomId, classroomById[classroomId].CourseId);
            total += counts.Total;
            verifiable += counts.Verifiable;
        }
        return Pack(total, learned, passed, verifiable);
    }

    public KindStats Classroom(IReadOnlyDictionary<string, Classroom> classroomById,
        IReadOnlyDictionary<string, List<AnalyticsResource>> resourcesByCourse, string classroomId, string userId)
    {
        Stats.TryGetValue(ClassroomProgress.Key(classroomId, userId), out var stat);
        var counts = Counts(resourcesByCourse, classroomId, classroomById[classroomId].CourseId);
        return Pack(counts.Total, stat?.LearnedCount ?? 0, stat?.PassedCount ?? 0, counts.Verifiable);
    }

    public int ActiveDays(IReadOnlyDictionary<string, List<string>> classroomsByUser, string userId)
    {
        if (!classroomsByUser.TryGetValue(userId, out var classroomIds)) return 0;
        var dates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var classroomId in classroomIds)
        {
            if (!Stats.TryGetValue(ClassroomProgress.Key(classroomId, userId), out var stat) || stat.ActiveDates.Length == 0) continue;
            foreach (var date in stat.ActiveDates.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                dates.Add(date);
        }
        return dates.Count;
    }

    public DateTime? LastActivity(IReadOnlyDictionary<string, List<string>> classroomsByUser, string userId)
    {
        if (!classroomsByUser.TryGetValue(userId, out var classroomIds)) return null;
        DateTime? last = null;
        foreach (var classroomId in classroomIds)
        {
            if (!Stats.TryGetValue(ClassroomProgress.Key(classroomId, userId), out var stat) || stat.LastActivityAt is not { } at) continue;
            if (last == null || at > last) last = at;
        }
        return last;
    }

    (int Total, int Verifiable) Counts(IReadOnlyDictionary<string, List<AnalyticsResource>> resourcesByCourse, string classroomId, string courseId)
    {
        if (Freezes.TryGetValue(classroomId, out var freeze)) return (freeze.ResourceCount, freeze.VerifiableCount);
        var resources = resourcesByCourse.GetValueOrDefault(courseId) ?? [];
        return (resources.Count, resources.Count(item => AnalyticsLogic.IsVerifiable(item.Kind)));
    }

    static KindStats Pack(int total, int learned, int passed, int verifiable)
        => new(total, learned, passed, AnalyticsLogic.Rate(learned, total), AnalyticsLogic.Rate(passed, verifiable), verifiable);
}
