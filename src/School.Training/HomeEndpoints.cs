using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class HomeEndpoints
{
    public static void MapHomeEndpoints(this RouteGroupBuilder all)
    {
        var student = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Student"));
        var teacher = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Teacher"));
        var admin = all.MapGroup("").RequireAuthorization("Admin");
        student.MapGet("/home/student", GetStudentHome);
        teacher.MapGet("/home/teacher", GetTeacherHome);
        admin.MapGet("/home/admin", GetAdminHome);
    }

    static async Task<IResult> GetStudentHome(HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx) ?? "";
        var profile = await LoadStudentProfileAsync(db, userId, ct);
        if (profile == null) return Results.NotFound(new { message = "学生档案不存在" });
        var classrooms = await EnrolledClassroomsAsync(db, userId, ct);
        var context = await HomeContext.LoadAsync(db, classrooms, ct);
        var resources = ResourcesForUser(context, userId);
        var summary = Summarize(context, userId, resources);
        var today = SchoolTime.TodayUtc();
        var mine = context.Data.Activities.Where(item => item.UserId == userId).ToArray();
        var todayActivities = mine.Where(item => InRange(item.At, today)).ToArray();
        var learnedToday = todayActivities.Where(item => item.IsCompletion).Select(item => item.ResourceId).Distinct().Count();
        var practicesToday = todayActivities.Count(item => item.IsPractice);
        var leaderboard = BuildLeaderboard(context, profile.ClassId, userId, includeIdentity: false);
        var todos = await HomeTodoLogic.StudentTodosAsync(db, userId, classrooms, ct);
        return Results.Ok(new
        {
            student = new { profile.UserId, profile.Name, profile.UserName, profile.Number, profile.ClassName, profile.ProfessionName },
            today = new { learned = learnedToday, practices = practicesToday },
            overview = OverviewView(summary, mine.Select(item => item.At), mine.Count(item => item.IsPractice)),
            kinds = KindsView(summary),
            classrooms = BuildClassroomCards(context, userId),
            classLeaderboard = leaderboard,
            todos,
            serverNow = DateTime.UtcNow
        });
    }

    static async Task<IResult> GetTeacherHome(HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx) ?? "";
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, ct);
        if (user == null) return Results.NotFound(new { message = "账号不存在" });
        var classrooms = await db.Classrooms.AsNoTracking().Where(item => item.TeacherUserId == userId).ToListAsync(ct);
        var context = await HomeContext.LoadAsync(db, classrooms, ct);
        var summaries = context.Students.Select(item => (Student: item, Summary: Summarize(context, item.UserId, ResourcesForUser(context, item.UserId)))).ToArray();
        var passed = summaries.Count(item => AnalyticsLogic.MeetsOverallTarget(item.Summary.Overall));
        var today = SchoolTime.TodayUtc();
        var activeStudents = context.Data.Activities.Where(item => InRange(item.At, today)).Select(item => item.UserId).Distinct().Count();
        var passedToday = TodayPassedStudents(context, today);
        var aggregate = Aggregate(summaries.Select(item => item.Summary));
        var (todos, canCreateClassroom) = await HomeTodoLogic.TeacherTodosAsync(db, userId, ct);
        return Results.Ok(new
        {
            teacher = new { user.Id, user.DisplayName, user.UserName },
            today = new { activeStudents, passedStudents = passedToday },
            overview = new
            {
                passedStudents = passed,
                managedClasses = context.Students.Where(item => item.ClassId != null).Select(item => item.ClassId).Distinct().Count(),
                managedClassrooms = classrooms.Count,
                managedStudents = context.Students.Count,
                learningRate = aggregate.LearningRate,
                resourcePassRate = aggregate.PassRate
            },
            classrooms = BuildClassroomCards(context, null),
            leaderboard = BuildLeaderboard(context, null, null, includeIdentity: true),
            todos,
            canCreateClassroom,
            serverNow = DateTime.UtcNow
        });
    }

    static async Task<IResult> GetAdminHome(SchoolDbContext db, LicenseQuotaPolicy licenses, CancellationToken ct)
    {
        var classrooms = await db.Classrooms.AsNoTracking().ToListAsync(ct);
        var context = await HomeContext.LoadAsync(db, classrooms, ct);
        var summaries = context.Students.Select(item => (Student: item, Summary: Summarize(context, item.UserId, ResourcesForUser(context, item.UserId)))).ToArray();
        var passed = summaries.Count(item => AnalyticsLogic.MeetsOverallTarget(item.Summary.Overall));
        var today = SchoolTime.TodayUtc();
        var todayActivities = context.Data.Activities.Where(item => InRange(item.At, today)).ToArray();
        var activeUserIds = todayActivities.Select(item => item.UserId).ToHashSet(StringComparer.Ordinal);
        var activeStudents = activeUserIds.Count;
        var activeClasses = context.Students.Where(item => item.ClassId != null && activeUserIds.Contains(item.UserId))
            .Select(item => item.ClassId).Distinct().Count();
        var managedClasses = await db.Classes.CountAsync(ct);
        var managedStudents = await db.People.CountAsync(item => item.Kind == "student", ct);
        var aggregate = Aggregate(summaries.Select(item => item.Summary));
        var classRanking = summaries.Where(item => item.Student.ClassId != null)
            .GroupBy(item => item.Student.ClassId!)
            .Select(group =>
            {
                var rows = group.ToArray();
                var passedCount = rows.Count(item => AnalyticsLogic.MeetsOverallTarget(item.Summary.Overall));
                return new
                {
                    classId = group.Key,
                    className = rows[0].Student.ClassName,
                    professionName = rows[0].Student.ProfessionName,
                    studentCount = rows.Length,
                    passedStudents = passedCount,
                    passRate = AnalyticsLogic.Rate(passedCount, rows.Length)
                };
            }).OrderByDescending(item => item.passRate).ThenByDescending(item => item.passedStudents)
            .ThenBy(item => item.className).Take(10).ToArray();

        var auditSince = DateTime.UtcNow.AddDays(-30);
        var teacherUsers = await db.Users.AsNoTracking()
            .Where(item => item.Role == "Teacher" || item.Role == "Admin")
            .Select(item => new { item.Id, item.UserName, item.DisplayName, item.Role }).ToListAsync(ct);
        var audits = await db.Audits.AsNoTracking().Where(item => item.At >= auditSince)
            .GroupBy(item => item.UserName)
            .Select(group => new { UserName = group.Key, Count = group.Count(), LastAt = group.Max(item => item.At) })
            .ToListAsync(ct);
        var auditByUser = audits.ToDictionary(item => item.UserName, item => item, StringComparer.OrdinalIgnoreCase);
        var managedClassCounts = classrooms.GroupBy(item => item.TeacherUserId)
            .ToDictionary(group => group.Key, group => group.Count());
        var teacherActivity = teacherUsers.Select(item =>
        {
            var audit = auditByUser.GetValueOrDefault(item.UserName);
            return new
            {
                item.Id, item.DisplayName, item.UserName, item.Role,
                managedClasses = managedClassCounts.GetValueOrDefault(item.Id),
                auditCount = audit?.Count ?? 0,
                lastActiveAt = audit?.LastAt
            };
        }).OrderByDescending(item => item.auditCount).ThenByDescending(item => item.managedClasses)
        .ThenBy(item => item.DisplayName).Take(10).ToArray();

        return Results.Ok(new
        {
            today = new { activeClasses, activeStudents, passedStudents = TodayPassedStudents(context, today) },
            overview = new
            {
                passedStudents = passed,
                managedClasses,
                managedStudents,
                totalClassrooms = classrooms.Count,
                openClassrooms = classrooms.Count(item => TeachingLogic.EffectiveStatus(item, DateTime.UtcNow) == "open"),
                learningRate = aggregate.LearningRate,
                resourcePassRate = aggregate.PassRate
            },
            classRanking,
            teacherActivity,
            todos = await HomeTodoLogic.AdminTodosAsync(db, licenses, ct),
            serverNow = DateTime.UtcNow
        });
    }

    static async Task<List<Classroom>> EnrolledClassroomsAsync(SchoolDbContext db, string userId, CancellationToken ct)
        => await (from enrollment in db.ClassroomStudents.AsNoTracking()
                  join classroom in db.Classrooms.AsNoTracking() on enrollment.ClassroomId equals classroom.Id
                  where enrollment.StudentUserId == userId
                  select classroom).ToListAsync(ct);

    static async Task<AnalyticsStudent?> LoadStudentProfileAsync(SchoolDbContext db, string userId, CancellationToken ct)
    {
        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(item => item.UserId == userId && item.Kind == "student", ct);
        if (person == null) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, ct);
        var schoolClass = person.ClassId == null ? null : await db.Classes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == person.ClassId, ct);
        var profession = schoolClass == null ? null : await db.Professions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == schoolClass.ProfessionId, ct);
        return new AnalyticsStudent(userId, person.Id, person.Name, user?.UserName ?? "", person.Number,
            person.ClassId, schoolClass?.Name ?? "", profession?.Name ?? "");
    }

    static string? CurrentUserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);

    static bool InRange(DateTime at, (DateTime StartUtc, DateTime EndUtc) range) => at >= range.StartUtc && at < range.EndUtc;

    static AnalyticsResource[] ResourcesForUser(HomeContext context, string userId)
    {
        if (!context.ClassroomsByUser.TryGetValue(userId, out var classroomIds)) return [];
        var courseIds = classroomIds.Select(id => context.ClassroomById[id].CourseId).Distinct();
        return courseIds.SelectMany(courseId => context.ResourcesByCourse.GetValueOrDefault(courseId) ?? [])
            .GroupBy(item => item.Id).Select(group => group.First()).ToArray();
    }

static StudentSummary Summarize(HomeContext context, string userId, IEnumerable<AnalyticsResource> resources, string? classroomId = null)
    {
        var list = resources as IReadOnlyCollection<AnalyticsResource> ?? resources.ToArray();
        LearningRecord? RecordFor(AnalyticsResource resource) => classroomId == null
            ? context.Data.BestFor(userId, resource.Id)
            : context.Data.RecordFor(classroomId, userId, resource.Id);
        KindStats For(string kind) => AnalyticsLogic.Metrics(list.Where(item => item.Kind == kind),
            resource => AnalyticsLogic.Evaluate(resource.Kind, RecordFor(resource), resource.MaxScore));
        return new StudentSummary(
            AnalyticsLogic.Metrics(list, resource => AnalyticsLogic.Evaluate(resource.Kind, RecordFor(resource), resource.MaxScore)),
            For("theory"), For("theoryPractice"), For("case"), For("casePractice"));
    }

    static KindStats Aggregate(IEnumerable<StudentSummary> summaries)
    {
        var rows = summaries.ToArray();
        var total = rows.Sum(item => item.Overall.Total);
        var learned = rows.Sum(item => item.Overall.Learned);
        var passed = rows.Sum(item => item.Overall.Passed);
        var verifiable = rows.Sum(item => item.Overall.VerifiableTotal);
        return new KindStats(total, learned, passed, AnalyticsLogic.Rate(learned, total), AnalyticsLogic.Rate(passed, verifiable), verifiable);
    }

    static object OverviewView(StudentSummary summary, IEnumerable<DateTime> timestamps, int practices)
    {
        var all = timestamps.ToArray();
        var overall = summary.Overall;
        return new
        {
            totalResources = overall.Total,
            practices,
            learned = overall.Learned,
            passed = overall.Passed,
            learningRate = overall.LearningRate,
            passRate = overall.PassRate,
            isPassing = AnalyticsLogic.MeetsOverallTarget(overall),
            activeDays = AnalyticsLogic.ActiveDays(all),
            lastActivity = AnalyticsLogic.LastActivity(all)
        };
    }

    static object KindsView(StudentSummary summary) => new
    {
        theory = summary.Theory,
        theoryPractice = summary.TheoryPractice,
        @case = summary.Case,
        casePractice = summary.CasePractice
    };

    static object[] BuildLeaderboard(HomeContext context, string? classId, string? currentUserId, bool includeIdentity)
    {
        var rows = context.Students.Where(item => classId != null ? item.ClassId == classId
                : currentUserId == null || item.UserId == currentUserId)
            .Select(item =>
            {
                var summary = Summarize(context, item.UserId, ResourcesForUser(context, item.UserId));
                var activities = context.Data.Activities.Where(activity => activity.UserId == item.UserId).Select(activity => activity.At);
                return new
                {
                    item.UserId, item.Name, item.Number, item.ClassName,
                    isMe = item.UserId == currentUserId,
                    learned = summary.Overall.Learned,
                    passed = summary.Overall.Passed,
                    totalResources = summary.Overall.Total,
                    learningRate = summary.Overall.LearningRate,
                    passRate = summary.Overall.PassRate,
                    activeDays = AnalyticsLogic.ActiveDays(activities),
                    lastActivity = AnalyticsLogic.LastActivity(activities)
                };
            }).OrderByDescending(item => item.learned).ThenByDescending(item => item.passed)
            .ThenByDescending(item => item.passRate).ThenBy(item => item.Number).Take(10).ToArray();
        return includeIdentity
            ? rows.Cast<object>().ToArray()
            : rows.Select(item => (object)new
            {
                item.Name, item.isMe, item.learned, item.passed, item.totalResources, item.learningRate, item.passRate
            }).ToArray();
    }

    static object[] BuildClassroomCards(HomeContext context, string? userId)
    {
        var now = DateTime.UtcNow;
        return context.Classrooms.OrderBy(item => TeachingLogic.EffectiveStatus(item, now) switch { "open" => 0, "scheduled" => 1, _ => 2 })
            .ThenByDescending(item => item.OpenAt).Select(classroom =>
            {
                var resources = context.ResourcesByCourse.GetValueOrDefault(classroom.CourseId) ?? [];
                var summary = userId == null ? null : Summarize(context, userId, resources, classroom.Id);
                var studentIds = context.StudentsByClassroom.GetValueOrDefault(classroom.Id) ?? [];
                var passing = studentIds.Count(studentId =>
                    AnalyticsLogic.MeetsOverallTarget(Summarize(context, studentId, resources, classroom.Id).Overall));
                return new
                {
                    classroom.Id, classroom.Name, classroom.CourseId,
                    courseName = context.CourseNames.GetValueOrDefault(classroom.CourseId, ""),
                    teacherName = context.TeacherNames.GetValueOrDefault(classroom.TeacherUserId, ""),
                    status = TeachingLogic.EffectiveStatus(classroom, now), classroom.OpenAt, classroom.CloseAt,
                    studentCount = studentIds.Count,
                    learned = summary?.Overall.Learned ?? 0,
                    passed = summary?.Overall.Passed ?? 0,
                    totalResources = resources.Count,
                    learningRate = summary?.Overall.LearningRate ?? 0,
                    passRate = summary?.Overall.PassRate ?? 0,
                    classPassRate = AnalyticsLogic.Rate(passing, studentIds.Count)
                };
            }).ToArray();
    }

    static int TodayPassedStudents(HomeContext context, (DateTime StartUtc, DateTime EndUtc) today)
    {
        var users = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activity in context.Data.Activities)
        {
            if (!InRange(activity.At, today)) continue;
            if (activity.IsPractice)
            {
                if (activity.Passed) users.Add(activity.UserId);
                continue;
            }
            if (!activity.IsCompletion) continue;
            var resource = context.ResourceById.GetValueOrDefault(activity.ResourceId);
            if (resource == null || resource.Kind is "theoryPractice" or "casePractice") continue;
            if (AnalyticsLogic.IsPassed(resource.Kind, context.Data.BestFor(activity.UserId, resource.Id), resource.MaxScore))
                users.Add(activity.UserId);
        }
        return users.Count;
    }

    sealed record StudentSummary(KindStats Overall, KindStats Theory, KindStats TheoryPractice, KindStats Case, KindStats CasePractice);

    sealed class HomeContext
    {
        public List<Classroom> Classrooms { get; } = [];
        public Dictionary<string, Classroom> ClassroomById { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<AnalyticsResource>> ResourcesByCourse { get; private set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> CourseNames { get; } = new(StringComparer.Ordinal);
        public List<AnalyticsStudent> Students { get; } = [];
        public LearnerData Data { get; } = new();
        public Dictionary<string, List<string>> ClassroomsByUser { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> StudentsByClassroom { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> TeacherNames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AnalyticsResource> ResourceById { get; } = new(StringComparer.Ordinal);

        public static async Task<HomeContext> LoadAsync(SchoolDbContext db, IReadOnlyCollection<Classroom> classrooms, CancellationToken ct)
        {
            var context = new HomeContext();
            context.Classrooms.AddRange(classrooms);
            foreach (var classroom in classrooms) context.ClassroomById[classroom.Id] = classroom;
            var classroomIds = classrooms.Select(item => item.Id).ToArray();
            var courseIds = classrooms.Select(item => item.CourseId).Distinct().ToArray();
            context.ResourcesByCourse = await AnalyticsLoader.LoadResourcesAsync(db, courseIds, ct);
            foreach (var (courseId, resources) in context.ResourcesByCourse)
            {
                context.CourseNames[courseId] = resources.FirstOrDefault()?.CourseName ?? "";
                foreach (var resource in resources) context.ResourceById[resource.Id] = resource;
            }
            if (courseIds.Length > 0)
            {
                var missing = courseIds.Where(id => !context.CourseNames.ContainsKey(id)).ToArray();
                if (missing.Length > 0)
                {
                    var names = await db.Courses.AsNoTracking().Where(item => missing.Contains(item.Id))
                        .Select(item => new { item.Id, item.Name }).ToListAsync(ct);
                    foreach (var course in names) context.CourseNames[course.Id] = course.Name;
                }
            }
            context.Students.AddRange(await AnalyticsLoader.LoadStudentsAsync(db, classroomIds, ct));
            var learnerData = await AnalyticsLoader.LoadLearnerDataAsync(db, classroomIds, ct);
            foreach (var (key, record) in learnerData.Records) context.Data.Records[key] = record;
            foreach (var (key, attempts) in learnerData.Attempts) context.Data.Attempts[key] = attempts;
            context.Data.Activities.AddRange(learnerData.Activities);
            if (classroomIds.Length > 0)
            {
                var enrollments = await db.ClassroomStudents.AsNoTracking().Where(item => classroomIds.Contains(item.ClassroomId))
                    .Select(item => new { item.ClassroomId, item.StudentUserId }).ToListAsync(ct);
                foreach (var enrollment in enrollments)
                {
                    if (!context.ClassroomsByUser.TryGetValue(enrollment.StudentUserId, out var classroomList))
                        context.ClassroomsByUser[enrollment.StudentUserId] = classroomList = [];
                    classroomList.Add(enrollment.ClassroomId);
                    if (!context.StudentsByClassroom.TryGetValue(enrollment.ClassroomId, out var studentList))
                        context.StudentsByClassroom[enrollment.ClassroomId] = studentList = [];
                    studentList.Add(enrollment.StudentUserId);
                }
                var teacherIds = classrooms.Select(item => item.TeacherUserId).Distinct().ToArray();
                var teachers = await db.Users.AsNoTracking().Where(item => teacherIds.Contains(item.Id))
                    .Select(item => new { item.Id, item.DisplayName }).ToListAsync(ct);
                foreach (var teacher in teachers) context.TeacherNames[teacher.Id] = teacher.DisplayName;
            }
            return context;
        }
    }
}
