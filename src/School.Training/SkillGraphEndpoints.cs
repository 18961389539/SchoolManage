using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class SkillGraphEndpoints
{
    public static void MapSkillGraphEndpoints(this RouteGroupBuilder all)
    {
        var staff = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Admin", "Teacher"));
        var learners = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Student"));
        learners.MapGet("/skill-graph", GetMyGraph);
        staff.MapGet("/skill-graph/students", ListStudents);
        staff.MapGet("/skill-graph/students/{userId}", GetStudentGraph);
    }

    static async Task<IResult> GetMyGraph(HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx);
        if (userId == null) return Results.Unauthorized();
        var graph = await BuildGraphAsync(db, userId, null, ct);
        return graph ?? Results.NotFound(new { message = "学生档案不存在" });
    }

    static async Task<IResult> GetStudentGraph(string userId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        string[]? teacherScope = null;
        if (!ctx.User.IsInRole("Admin"))
        {
            var teacherId = CurrentUserId(ctx) ?? "";
            var ownClassroomIds = await db.Classrooms.AsNoTracking().Where(item => item.TeacherUserId == teacherId)
                .Select(item => item.Id).ToArrayAsync(ct);
            if (ownClassroomIds.Length == 0) return Results.NotFound(new { message = "学生不在你的课堂中" });
            teacherScope = await db.ClassroomStudents.AsNoTracking()
                .Where(item => item.StudentUserId == userId && ownClassroomIds.Contains(item.ClassroomId))
                .Select(item => item.ClassroomId).ToArrayAsync(ct);
            if (teacherScope.Length == 0) return Results.NotFound(new { message = "学生不在你的课堂中" });
        }
        var graph = await BuildGraphAsync(db, userId, teacherScope, ct);
        return graph ?? Results.NotFound(new { message = "学生档案不存在" });
    }

    static async Task<IResult> ListStudents(HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        string[] scopedClassrooms;
        if (ctx.User.IsInRole("Admin"))
            scopedClassrooms = await db.Classrooms.AsNoTracking().Select(item => item.Id).ToArrayAsync(ct);
        else
        {
            var teacherId = CurrentUserId(ctx) ?? "";
            scopedClassrooms = await db.Classrooms.AsNoTracking().Where(item => item.TeacherUserId == teacherId)
                .Select(item => item.Id).ToArrayAsync(ct);
        }
        if (scopedClassrooms.Length == 0) return Results.Ok(new { total = 0, items = Array.Empty<object>(), classes = Array.Empty<object>() });

        var rows = await (from enrollment in db.ClassroomStudents.AsNoTracking()
                          join person in db.People.AsNoTracking() on enrollment.StudentPersonId equals person.Id
                          join user in db.Users.AsNoTracking() on enrollment.StudentUserId equals user.Id
                          join schoolClass in db.Classes.AsNoTracking() on person.ClassId equals schoolClass.Id into classes
                          from schoolClass in classes.DefaultIfEmpty()
                          where scopedClassrooms.Contains(enrollment.ClassroomId)
                          select new
                          {
                              UserId = enrollment.StudentUserId, PersonId = enrollment.StudentPersonId,
                              person.Name, UserName = user.UserName, person.Number, person.ClassId,
                              ClassName = schoolClass == null ? "" : schoolClass.Name,
                              ProfessionId = schoolClass == null ? null : schoolClass.ProfessionId,
                              ClassroomId = enrollment.ClassroomId
                          }).ToListAsync(ct);
        var professionIds = rows.Where(item => item.ProfessionId != null).Select(item => item.ProfessionId!).Distinct().ToArray();
        var professions = professionIds.Length == 0 ? new Dictionary<string, string>()
            : await db.Professions.AsNoTracking().Where(item => professionIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.Name, ct);
        var search = request.Query["search"].ToString().Trim();
        var classId = request.Query["classId"].ToString();
        var students = rows.GroupBy(item => item.UserId).Select(group =>
        {
            var first = group.First();
            return new
            {
                first.UserId, first.PersonId, first.Name, first.UserName, first.Number, first.ClassId, first.ClassName,
                professionName = first.ProfessionId == null ? "" : professions.GetValueOrDefault(first.ProfessionId, ""),
                classroomCount = group.Select(item => item.ClassroomId).Distinct().Count()
            };
        }).Where(item => classId.Length == 0 || item.ClassId == classId)
          .Where(item => search.Length == 0 || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
              || item.UserName.Contains(search, StringComparison.OrdinalIgnoreCase)
              || item.Number.Contains(search, StringComparison.OrdinalIgnoreCase)
              || item.ClassName.Contains(search, StringComparison.OrdinalIgnoreCase))
          .OrderBy(item => item.Number).ToArray();
        var classOptions = rows.Where(item => item.ClassId != null).GroupBy(item => item.ClassId!)
            .Select(group => new { id = group.Key, name = group.First().ClassName, studentCount = group.Select(item => item.UserId).Distinct().Count() })
            .OrderBy(item => item.name).ToArray();
        var (page, size) = Validation.Page(request);
        return Results.Ok(new
        {
            total = students.Length,
            items = students.Skip((page - 1) * size).Take(size).ToArray(),
            classes = classOptions
        });
    }

    static async Task<IResult?> BuildGraphAsync(SchoolDbContext db, string userId, string[]? teacherScope, CancellationToken ct)
    {
        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(item => item.UserId == userId && item.Kind == "student", ct);
        if (person == null) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, ct);
        var schoolClass = person.ClassId == null ? null : await db.Classes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == person.ClassId, ct);
        var profession = schoolClass == null ? null : await db.Professions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == schoolClass.ProfessionId, ct);
        var profile = new AnalyticsStudent(userId, person.Id, person.Name, user?.UserName ?? "", person.Number,
            person.ClassId, schoolClass?.Name ?? "", profession?.Name ?? "");

        var enrolled = await db.ClassroomStudents.AsNoTracking().Where(item => item.StudentUserId == userId)
            .Select(item => item.ClassroomId).ToArrayAsync(ct);
        var scope = teacherScope == null ? enrolled : enrolled.Where(id => teacherScope.Contains(id)).ToArray();
        var scopedClassrooms = scope.Length == 0 ? new List<Classroom>()
            : await db.Classrooms.AsNoTracking().Where(item => scope.Contains(item.Id)).ToListAsync(ct);
        var courseIds = scopedClassrooms.Select(item => item.CourseId).Distinct().ToArray();
        var courses = courseIds.Length == 0 ? new Dictionary<string, TrainingCourse>()
            : await db.Courses.AsNoTracking().Where(item => courseIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, ct);
        var chosen = scopedClassrooms.Where(item => courses.ContainsKey(item.CourseId)).GroupBy(item => item.CourseId)
            .Select(group => new
            {
                Classroom = group.OrderBy(item => StatusRank(item.Status)).ThenByDescending(item => item.OpenAt).First(),
                Course = courses[group.Key]
            }).ToArray();
        var resourcesByCourse = await AnalyticsLoader.LoadResourcesAsync(db, chosen.Select(item => item.Course.Id).ToArray(), ct);
        var learnerData = await AnalyticsLoader.LoadLearnerDataAsync(db, scope, ct);
        var categories = chosen.Select(item => BuildCategory(item.Course, resourcesByCourse.GetValueOrDefault(item.Course.Id) ?? [],
            item.Classroom, learnerData, userId)).ToArray();
        var skills = categories.SelectMany(category => category.Skills).ToArray();
        var totalResources = skills.Sum(item => item.TotalResources);
        var learnedResources = skills.Sum(item => item.LearnedCount);
        var passedResources = skills.Sum(item => item.PassedCount);
        var score = skills.Length == 0 ? 0 : (int)Math.Round(skills.Average(item => item.Score), MidpointRounding.AwayFromZero);
        var timestamps = learnerData.Activities.Where(item => item.UserId == userId).Select(item => item.At).ToArray();
        return Results.Ok(new
        {
            student = new { profile.UserId, profile.PersonId, profile.Name, profile.UserName, profile.Number, profile.ClassName, profile.ProfessionName },
            scope = new { classroomCount = scope.Length, teacherScoped = teacherScope != null },
            summary = new
            {
                score,
                currentTarget = AnalyticsLogic.NextTarget(score),
                fullScore = 100,
                masteredSkills = skills.Count(item => item.State == "mastered"),
                learnedSkills = skills.Count(item => item.LearnedCount > 0),
                learningSkills = skills.Count(item => item.State == "learning"),
                notStartedSkills = skills.Count(item => item.State == "notStarted"),
                totalSkills = skills.Length,
                totalResources,
                learnedResources,
                passedResources,
                activeDays = AnalyticsLogic.ActiveDays(timestamps),
                lastActivity = AnalyticsLogic.LastActivity(timestamps)
            },
            categories = categories.Select(item => new
            {
                item.Category.Id, item.Category.Name, item.Category.Type,
                item.Category.Score, item.Category.SkillCount, item.Category.MasteredCount,
                chapters = item.Chapters.Select(chapter => new
                {
                    chapter.Id, chapter.Name,
                    skills = chapter.Skills.Select(skill => new
                    {
                        skill.Id, skill.Name, skill.ChapterId, skill.ChapterName,
                        skill.CategoryId, skill.CategoryName, skill.Score, skill.State,
                        skill.TotalResources, skill.LearnedCount, skill.PassedCount,
                        resources = skill.Resources
                    }).ToArray()
                }).ToArray()
            }).ToArray(),
            updatedAt = DateTime.UtcNow
        });
    }

    static CategoryBuild BuildCategory(TrainingCourse course, List<AnalyticsResource> resources, Classroom classroom, LearnerData data, string userId)
    {
        var skillRows = resources.GroupBy(item => new { item.SectionId, item.SectionName, item.SectionSort, item.ChapterId, item.ChapterName, item.ChapterSort })
            .OrderBy(group => group.Key.ChapterSort).ThenBy(group => group.Key.SectionSort).Select(group =>
            {
                var resourceViews = group.Select(resource =>
                {
                    var record = data.RecordFor(userId, resource.Id);
                    var state = AnalyticsLogic.Evaluate(resource.Kind, record, resource.MaxScore);
                    var progress = ProgressOf(resource, record, state);
                    return new SkillResourceView(
                        resource.Id, resource.Name, resource.Kind, TeachingLogic.ResourceKindLabel(resource.Kind),
                        resource.DurationMinutes, classroom.Id, classroom.Name,
                        state.Learned, state.Passed, AnalyticsLogic.ResourceStatus(state.Learned, state.Passed),
                        record?.BestScore ?? 0, resource.MaxScore, record?.AttemptCount ?? 0, progress,
                        record == null ? null : record.CompletedAt ?? record.StartedAt);
                }).ToArray();
                var score = resourceViews.Length == 0 ? 0 : (int)Math.Round(resourceViews.Average(item => item.Progress), MidpointRounding.AwayFromZero);
                var learnedCount = resourceViews.Count(item => item.Learned);
                var passedCount = resourceViews.Count(item => item.Passed);
                return new SkillBuild(group.Key.SectionId, group.Key.SectionName, group.Key.ChapterId, group.Key.ChapterName,
                    course.Id, course.Name, score, AnalyticsLogic.SkillState(resourceViews.Length, passedCount, learnedCount),
                    resourceViews.Length, learnedCount, passedCount, resourceViews);
            }).ToArray();
        var chapters = skillRows.GroupBy(item => new { item.ChapterId, item.ChapterName })
            .Select(group => new ChapterBuild(group.Key.ChapterId, group.Key.ChapterName, group.ToArray())).ToArray();
        var score = skillRows.Length == 0 ? 0 : (int)Math.Round(skillRows.Average(item => item.Score), MidpointRounding.AwayFromZero);
        return new CategoryBuild(new CategoryInfo(course.Id, course.Name, course.Type, score, skillRows.Length,
            skillRows.Count(item => item.State == "mastered")), chapters, skillRows);
    }

    static int ProgressOf(AnalyticsResource resource, LearningRecord? record, LearningState state)
    {
        if (!state.Learned || record == null) return 0;
        if (resource.Kind is "theoryPractice" or "casePractice" && resource.MaxScore > 0 && record.AttemptCount > 0)
            return Math.Clamp(AnalyticsLogic.Rate(Math.Min(record.BestScore, resource.MaxScore), resource.MaxScore), 0, 100);
        return state.Passed ? 100 : 50;
    }

    static int StatusRank(string status) => status switch { "open" => 0, "scheduled" => 1, _ => 2 };

    static string? CurrentUserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);

    sealed record CategoryInfo(string Id, string Name, string Type, int Score, int SkillCount, int MasteredCount);
    sealed record ChapterBuild(string Id, string Name, SkillBuild[] Skills);
    sealed record CategoryBuild(CategoryInfo Category, ChapterBuild[] Chapters, SkillBuild[] Skills);
    sealed record SkillBuild(string Id, string Name, string ChapterId, string ChapterName, string CategoryId, string CategoryName,
        int Score, string State, int TotalResources, int LearnedCount, int PassedCount, SkillResourceView[] Resources);
    sealed record SkillResourceView(string ResourceId, string Name, string Kind, string KindLabel, int DurationMinutes,
        string ClassroomId, string ClassroomName, bool Learned, bool Passed, string Status, int BestScore, int MaxScore,
        int AttemptCount, int Progress, DateTime? LastActivity);
}
