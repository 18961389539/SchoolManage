using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class ClassroomStatisticsEndpoints
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapClassroomStatisticsEndpoints(this RouteGroupBuilder all)
    {
        var staff = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Admin", "Teacher"));
        var learners = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Student"));
        staff.MapGet("/classrooms/{id}/statistics", GetStatistics);
        learners.MapGet("/classrooms/{id}/statistics/mine", GetMyStatistics);
        all.MapGet("/classrooms/{id}/students/{userId}/statistics", GetStudentStatistics);
        staff.MapGet("/classrooms/{id}/statistics/resources/{resourceId}/students", GetResourceStudents);
        staff.MapGet("/classrooms/{id}/statistics/exports/resources", ExportResources).RequireRateLimiting("expensive");
        staff.MapGet("/classrooms/{id}/statistics/exports/students", ExportStudents).RequireRateLimiting("expensive");
        all.MapGet("/classrooms/{id}/statistics/practice-attempts/{attemptId}", GetPracticeAttempt);
        all.MapGet("/classrooms/{id}/statistics/case-records/{recordId}", GetCaseRecord);
    }

    static async Task<IResult> GetStatistics(string id, HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return Results.NotFound(new { message = "课堂不存在" });
        if (!CanManage(ctx, classroom)) return Results.Json(new { message = "无权查看该课堂学情" }, statusCode: 403);
        var data = await LoadAsync(db, classroom, ct);
        if (data == null) return Results.NotFound(new { message = "课堂关联课程不存在" });
        return Results.Ok(BuildStatistics(data, request.Query["chapterId"].ToString()));
    }

    static async Task<IResult> GetMyStatistics(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx);
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return Results.NotFound(new { message = "课堂不存在" });
        var data = await LoadAsync(db, classroom, ct);
        if (data == null) return Results.NotFound(new { message = "课堂关联课程不存在" });
        var student = data.Students.FirstOrDefault(item => item.UserId == userId);
        if (student == null) return Results.Json(new { message = "你不在该课堂中" }, statusCode: 403);
        return Results.Ok(BuildStudentDetail(data, student));
    }

    static async Task<IResult> GetStudentStatistics(string id, string userId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return Results.NotFound(new { message = "课堂不存在" });
        var self = CurrentUserId(ctx);
        var studentRequest = ctx.User.IsInRole("Student");
        if (studentRequest && userId != self) return Results.Json(new { message = "只能查看自己的课堂学情" }, statusCode: 403);
        if (!studentRequest && !CanManage(ctx, classroom)) return Results.Json(new { message = "无权查看该课堂学情" }, statusCode: 403);
        var data = await LoadAsync(db, classroom, ct);
        if (data == null) return Results.NotFound(new { message = "课堂关联课程不存在" });
        var student = data.Students.FirstOrDefault(item => item.UserId == userId);
        if (student == null) return Results.NotFound(new { message = "学生不在该课堂中" });
        return Results.Ok(BuildStudentDetail(data, student));
    }

    static async Task<IResult> GetResourceStudents(string id, string resourceId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return Results.NotFound(new { message = "课堂不存在" });
        if (!CanManage(ctx, classroom)) return Results.Json(new { message = "无权查看该课堂学情" }, statusCode: 403);
        var data = await LoadAsync(db, classroom, ct);
        if (data == null) return Results.NotFound(new { message = "课堂关联课程不存在" });
        var resource = data.Resources.FirstOrDefault(item => item.Id == resourceId);
        if (resource == null) return Results.NotFound(new { message = "资源不属于该课堂课程" });
        var items = data.Students.Select(student =>
        {
            var record = data.RecordFor(student.UserId, resource.Id);
            var state = AnalyticsLogic.Evaluate(resource.Kind, record, resource.MaxScore);
            return new
            {
                student.UserId, student.PersonId, student.Name, student.UserName, student.Number, student.ClassId, student.ClassName,
                learned = state.Learned, passed = state.Passed,
                status = AnalyticsLogic.ResourceStatus(state.Learned, state.Passed),
                bestScore = record?.BestScore ?? 0,
                maxScore = resource.MaxScore,
                attemptCount = record?.AttemptCount ?? 0,
                lastActivity = LastActivityOf(record)
            };
        }).ToArray();
        return Results.Ok(new
        {
            classroom = new { data.Classroom.Id, data.Classroom.Name },
            resource = new { resource.Id, resource.Name, resource.Kind, resource.ChapterName, resource.SectionName, resource.MaxScore },
            learnedCount = items.Count(item => item.learned),
            passedCount = items.Count(item => item.passed),
            learningRate = AnalyticsLogic.Rate(items.Count(item => item.learned), items.Length),
            passRate = AnalyticsLogic.Rate(items.Count(item => item.passed), items.Length),
            items
        });
    }

    static async Task<IResult> GetPracticeAttempt(string id, string attemptId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var attempt = await db.PracticeAttempts.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attemptId, ct);
        if (attempt == null) return Results.NotFound(new { message = "练习记录不存在" });
        var record = await db.LearningRecords.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attempt.LearningRecordId, ct);
        if (record == null || record.ClassroomId != id) return Results.NotFound(new { message = "练习记录不属于该课堂" });
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return Results.NotFound(new { message = "课堂不存在" });
        if (!CanViewStudent(ctx, classroom, record.UserId)) return Results.Json(new { message = "无权查看该练习记录" }, statusCode: 403);
        var resource = await FindResourceAsync(db, classroom.CourseId, record.ResourceId, ct);
        if (resource == null) return Results.NotFound(new { message = "资源不属于该课堂课程" });
        var questionResults = ReadPracticeResults(attempt.AnswersJson);
        if (questionResults == null)
        {
            var questions = await db.CourseQuestions.AsNoTracking().Where(item => item.ResourceId == resource.Id)
                .OrderBy(item => item.SortOrder).ToListAsync(ct);
            var answers = ParseAnswers(attempt.AnswersJson);
            questionResults = questions.Select(question => TeachingEndpoints.PracticeResult(question, answers.GetValueOrDefault(question.Id) ?? [])).ToArray();
        }
        var history = await db.PracticeAttempts.AsNoTracking().Where(item => item.LearningRecordId == record.Id)
            .OrderBy(item => item.SubmittedAt).ThenBy(item => item.Id).ToListAsync(ct);
        var attemptNumber = history.FindIndex(item => item.Id == attempt.Id) + 1;
        var student = await db.People.AsNoTracking().FirstOrDefaultAsync(item => item.UserId == record.UserId, ct);
        return Results.Ok(new
        {
            attemptId = attempt.Id,
            attemptNumber,
            attemptCount = history.Count,
            averageScore = Math.Round(history.Average(item => item.Score), 1),
            bestScore = history.Max(item => item.Score),
            score = attempt.Score,
            maxScore = attempt.MaxScore,
            passed = attempt.MaxScore > 0 && (long)attempt.Score * 100 >= (long)attempt.MaxScore * AnalyticsLogic.PassScorePercent,
            submittedAt = attempt.SubmittedAt,
            startedAt = attempt.StartedAt ?? (attemptNumber == 1 ? record.StartedAt : null),
            student = new { name = student?.Name ?? "", number = student?.Number ?? "" },
            resource = new { resource.Id, resource.Name, sectionName = resource.SectionName, chapterName = resource.ChapterName },
            questions = questionResults
        });
    }

    static PracticeQuestionResult[]? ReadPracticeResults(string json)
    {
        try
        {
            var rows = JsonSerializer.Deserialize<PracticeQuestionResult[]>(json, JsonOptions);
            return rows is { Length: > 0 } && rows.All(item => item.Stem != null && item.Options != null) ? rows : null;
        }
        catch (JsonException) { return null; }
    }

    static async Task<IResult> GetCaseRecord(string id, string recordId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var record = await db.LearningRecords.AsNoTracking().FirstOrDefaultAsync(item => item.Id == recordId && item.ClassroomId == id, ct);
        if (record == null) return Results.NotFound(new { message = "学习记录不存在" });
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return Results.NotFound(new { message = "课堂不存在" });
        if (!CanViewStudent(ctx, classroom, record.UserId)) return Results.Json(new { message = "无权查看该案例记录" }, statusCode: 403);
        var resource = await FindRawResourceAsync(db, classroom.CourseId, record.ResourceId, ct);
        if (resource == null) return Results.NotFound(new { message = "资源不属于该课堂课程" });
        var nodeName = resource.StartupNodeId == null ? "" : await db.Nodes.AsNoTracking()
            .Where(item => item.Id == resource.StartupNodeId).Select(item => item.Name).FirstOrDefaultAsync(ct) ?? "";
        var placement = await (from section in db.CourseSections.AsNoTracking()
                               join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                               where section.Id == resource.SectionId
                               select new { sectionName = section.Name, chapterName = chapter.Name }).FirstAsync(ct);
        var student = await db.People.AsNoTracking().FirstOrDefaultAsync(item => item.UserId == record.UserId, ct);
        var grades = await (from grade in db.ClassroomCaseGrades.AsNoTracking()
                            join session in db.ClassroomCaseSessions.AsNoTracking() on grade.SessionId equals session.Id
                            where session.LearningRecordId == record.Id
                            orderby grade.ScoredAt descending
                            select grade).ToListAsync(ct);
        return Results.Ok(new
        {
            recordId = record.Id,
            status = record.Status,
            startedAt = record.StartedAt,
            completedAt = record.CompletedAt,
            attemptCount = record.AttemptCount,
            grades = grades.Select(ClassroomCaseScoring.GradeView),
            student = new { name = student?.Name ?? "", number = student?.Number ?? "" },
            resource = new
            {
                resource.Id, resource.Name, resource.Kind, resource.Content, resource.Topology,
                startupNodeId = resource.StartupNodeId, startupNodeName = nodeName,
                placement.sectionName, placement.chapterName
            }
        });
    }

    static async Task<IResult> ExportResources(string id, HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return Results.NotFound(new { message = "课堂不存在" });
        if (!CanManage(ctx, classroom)) return Results.Json(new { message = "无权导出该课堂学情" }, statusCode: 403);
        var data = await LoadAsync(db, classroom, ct);
        if (data == null) return Results.NotFound(new { message = "课堂关联课程不存在" });
        var chapterId = request.Query["chapterId"].ToString();
        var resources = string.IsNullOrEmpty(chapterId) ? data.Resources.ToArray() : data.Resources.Where(item => item.ChapterId == chapterId).ToArray();
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', new[]
        {
            "课堂", "课程", "大章", "小节", "资源", "资源类型", "学生", "学号", "班级",
            "学习状态", "是否达标", "最好成绩", "满分", "练习次数", "最近学习时间"
        }.Select(Csv)));
        foreach (var resource in resources)
            foreach (var student in data.Students)
            {
                var record = data.RecordFor(student.UserId, resource.Id);
                var state = AnalyticsLogic.Evaluate(resource.Kind, record, resource.MaxScore);
                builder.AppendLine(string.Join(',', new[]
                {
                    Csv(classroom.Name), Csv(data.Course.Name), Csv(resource.ChapterName), Csv(resource.SectionName),
                    Csv(resource.Name), Csv(TeachingLogic.ResourceKindLabel(resource.Kind)),
                    Csv(student.Name), Csv(student.Number), Csv(student.ClassName),
                    Csv(StatusLabel(state)), state.Passed ? "是" : "否",
                    (record?.BestScore ?? 0).ToString(), resource.MaxScore.ToString(), (record?.AttemptCount ?? 0).ToString(),
                    Csv(LastActivityOf(record) is { } at ? SchoolTime.Format(at) : "")
                }));
            }
        return CsvFile($"课堂资源学习记录-{classroom.Name}.csv", builder.ToString());
    }

    static async Task<IResult> ExportStudents(string id, HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return Results.NotFound(new { message = "课堂不存在" });
        if (!CanManage(ctx, classroom)) return Results.Json(new { message = "无权导出该课堂学情" }, statusCode: 403);
        var data = await LoadAsync(db, classroom, ct);
        if (data == null) return Results.NotFound(new { message = "课堂关联课程不存在" });
        var chapterId = request.Query["chapterId"].ToString();
        var resources = string.IsNullOrEmpty(chapterId) ? data.Resources.ToArray() : data.Resources.Where(item => item.ChapterId == chapterId).ToArray();
        var activity = ActivitiesByStudent(data, resources);
        var search = request.Query["search"].ToString().Trim();
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', new[]
        {
            "课堂", "学生", "学号", "班级", "学习资源数", "达标资源数", "资源学习率", "资源达标率",
            "理论讲解学习率", "理论讲解达标率", "理论练习学习率", "理论练习达标率",
            "案例讲解学习率", "案例讲解达标率", "案例实操学习率", "案例实操达标率", "活跃天数", "最近学习时间"
        }.Select(Csv)));
        foreach (var student in data.Students.Where(item => search.Length == 0 ||
            new[] { item.Name, item.Number, item.ClassName, item.UserName }.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase))))
        {
            var summary = Summarize(data, student.UserId, resources);
            var timestamps = activity[student.UserId];
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(classroom.Name), Csv(student.Name), Csv(student.Number), Csv(student.ClassName),
                summary.Overall.Learned.ToString(), summary.Overall.Passed.ToString(),
                summary.Overall.LearningRate + "%", summary.Overall.PassRate + "%",
                summary.Theory.LearningRate + "%", summary.Theory.PassRate + "%",
                summary.TheoryPractice.LearningRate + "%", summary.TheoryPractice.PassRate + "%",
                summary.Case.LearningRate + "%", summary.Case.PassRate + "%",
                summary.CasePractice.LearningRate + "%", summary.CasePractice.PassRate + "%",
                AnalyticsLogic.ActiveDays(timestamps).ToString(),
                Csv(AnalyticsLogic.LastActivity(timestamps) is { } at ? SchoolTime.Format(at) : "")
            }));
        }
        return CsvFile($"课堂学生维度统计-{classroom.Name}.csv", builder.ToString());
    }

    static object BuildStatistics(StatsData data, string chapterId)
    {
        var resources = string.IsNullOrEmpty(chapterId) ? data.Resources.ToArray() : data.Resources.Where(item => item.ChapterId == chapterId).ToArray();
        var activity = ActivitiesByStudent(data, resources);
        var allSummaries = data.Students.Select(student => Summarize(data, student.UserId, resources)).ToArray();
        var rows = data.Students.Select((student, index) =>
        {
            var summary = allSummaries[index];
            var timestamps = activity[student.UserId];
            return new
            {
                student.UserId, student.PersonId, student.Name, student.UserName, student.Number, student.ClassId, student.ClassName,
                summary.Overall, summary.Theory, summary.TheoryPractice, summary.Case, summary.CasePractice,
                activeDays = AnalyticsLogic.ActiveDays(timestamps),
                lastActivity = AnalyticsLogic.LastActivity(timestamps)
            };
        }).ToArray();
        var chapterTree = data.Resources.GroupBy(item => new { item.ChapterId, item.ChapterName, item.ChapterSort })
            .OrderBy(group => group.Key.ChapterSort).Select(chapterGroup => new
            {
                id = chapterGroup.Key.ChapterId,
                name = chapterGroup.Key.ChapterName,
                sections = chapterGroup.GroupBy(item => new { item.SectionId, item.SectionName, item.SectionSort })
                    .OrderBy(group => group.Key.SectionSort).Select(sectionGroup => new
                    {
                        id = sectionGroup.Key.SectionId,
                        name = sectionGroup.Key.SectionName,
                        resources = sectionGroup.Select(resource =>
                        {
                            var learnedCount = data.Students.Count(student => data.RecordFor(student.UserId, resource.Id) is { } record
                                && AnalyticsLogic.IsLearned(record));
                            var passedCount = data.Students.Count(student => data.RecordFor(student.UserId, resource.Id) is { } record
                                && AnalyticsLogic.IsPassed(resource.Kind, record, resource.MaxScore));
                            return new
                            {
                                resource.Id, resource.Name, resource.Kind,
                                kindLabel = TeachingLogic.ResourceKindLabel(resource.Kind),
                                resource.DurationMinutes, resource.MaxScore,
                                learnedCount, passedCount,
                                learningRate = AnalyticsLogic.Rate(learnedCount, data.Students.Count),
                                passRate = AnalyticsLogic.Rate(passedCount, data.Students.Count)
                            };
                        }).ToArray()
                    }).ToArray()
            }).ToArray();

        return new
        {
            classroom = new
            {
                data.Classroom.Id, data.Classroom.Name, data.Classroom.CourseId, courseName = data.Course.Name,
                teacherName = data.TeacherName, status = TeachingLogic.EffectiveStatus(data.Classroom, DateTime.UtcNow),
                data.Classroom.OpenAt, data.Classroom.CloseAt,
                studentCount = data.Students.Count
            },
            serverNow = DateTime.UtcNow,
            remainingSeconds = RemainingSeconds(data.Classroom),
            chapterId,
            summary = SummaryView(allSummaries, data.Students.Count),
            students = rows,
            chapters = chapterTree
        };
    }

    static ILookup<string, DateTime> ActivitiesByStudent(StatsData data, AnalyticsResource[] resources)
    {
        var resourceIds = resources.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return data.Activities.Where(item => resourceIds.Contains(item.ResourceId)).ToLookup(item => item.UserId, item => item.At);
    }

    static object BuildStudentDetail(StatsData data, AnalyticsStudent student)
    {
        var summary = Summarize(data, student.UserId, data.Resources);
        var timestamps = data.Activities.Where(item => item.UserId == student.UserId).Select(item => item.At).ToArray();
        var transcript = data.Resources.Select(resource =>
        {
            var record = data.RecordFor(student.UserId, resource.Id);
            var state = AnalyticsLogic.Evaluate(resource.Kind, record, resource.MaxScore);
            return new
            {
                resource.Id, resource.Name, resource.Kind,
                kindLabel = TeachingLogic.ResourceKindLabel(resource.Kind),
                resource.ChapterId, resource.ChapterName, resource.SectionId, resource.SectionName,
                learned = state.Learned, passed = state.Passed,
                status = AnalyticsLogic.ResourceStatus(state.Learned, state.Passed),
                bestScore = record?.BestScore ?? 0, resource.MaxScore,
                attemptCount = record?.AttemptCount ?? 0, lastActivity = LastActivityOf(record)
            };
        }).ToArray();
        var practiceHistory = data.Records.Values.Where(record => record.UserId == student.UserId)
            .Select(record => new { record, resource = data.ResourceById.GetValueOrDefault(record.ResourceId) })
            .Where(item => item.resource?.Kind == "theoryPractice").OrderByDescending(item => item.record.StartedAt)
            .SelectMany(item =>
            {
                var attempts = data.AttemptsForRecord(item.record.Id);
                return attempts.Select((attempt, index) => new
                {
                    attemptId = attempt.Id, resourceId = item.resource!.Id, resourceName = item.resource.Name,
                    chapterName = item.resource.ChapterName, sectionName = item.resource.SectionName,
                    attemptNumber = index + 1, attempt.Score, attempt.MaxScore,
                    passed = attempt.MaxScore > 0 && (long)attempt.Score * 100 >= (long)attempt.MaxScore * AnalyticsLogic.PassScorePercent,
                    submittedAt = attempt.SubmittedAt,
                    startedAt = attempt.StartedAt ?? (index == 0 ? item.record.StartedAt : null)
                });
            }).OrderByDescending(item => item.submittedAt).ToArray();
        var caseHistory = data.Records.Values.Where(record => record.UserId == student.UserId)
            .Select(record => new { record, resource = data.ResourceById.GetValueOrDefault(record.ResourceId) })
            .Where(item => item.resource?.Kind == "casePractice").OrderByDescending(item => item.record.CompletedAt ?? item.record.StartedAt)
            .Select(item => new
            {
                recordId = item.record.Id, resourceId = item.resource!.Id, resourceName = item.resource.Name,
                chapterName = item.resource.ChapterName, sectionName = item.resource.SectionName,
                item.record.Status, item.record.StartedAt, item.record.CompletedAt,
                item.record.AttemptCount, lastActivity = item.record.CompletedAt ?? item.record.StartedAt
            }).ToArray();
        return new
        {
            classroom = new { data.Classroom.Id, data.Classroom.Name, courseName = data.Course.Name, remainingSeconds = RemainingSeconds(data.Classroom) },
            student = new { student.UserId, student.PersonId, student.Name, student.UserName, student.Number, student.ClassId, student.ClassName, student.ProfessionName },
            summary = new
            {
                summary.Overall,
                summary.Theory, summary.TheoryPractice, summary.Case, summary.CasePractice,
                isPassing = AnalyticsLogic.MeetsOverallTarget(summary.Overall),
                activeDays = AnalyticsLogic.ActiveDays(timestamps),
                lastActivity = AnalyticsLogic.LastActivity(timestamps)
            },
            transcript,
            practiceHistory,
            caseHistory
        };
    }

    static object SummaryView(StudentSummary[] rows, int studentCount)
    {
        object View(KindStats stats, Func<StudentSummary, KindStats> selector)
        {
            var passedStudents = rows.Count(item => { var kind = selector(item); return kind.Total > 0 && (long)kind.Passed * 100 >= (long)kind.Total * AnalyticsLogic.OverallTargetPercent; });
            return new
            {
                totalStudents = studentCount,
                passedStudents,
                totalResources = stats.Total,
                learnedResources = stats.Learned,
                passedResources = stats.Passed,
                learningRate = stats.LearningRate,
                passRate = stats.PassRate
            };
        }
        var overall = Merge(rows.Select(item => item.Overall));
        return new
        {
            overall = View(overall, item => item.Overall),
            theory = View(Merge(rows.Select(item => item.Theory)), item => item.Theory),
            theoryPractice = View(Merge(rows.Select(item => item.TheoryPractice)), item => item.TheoryPractice),
            @case = View(Merge(rows.Select(item => item.Case)), item => item.Case),
            casePractice = View(Merge(rows.Select(item => item.CasePractice)), item => item.CasePractice)
        };
    }

    static KindStats Merge(IEnumerable<KindStats> rows)
    {
        var list = rows.ToArray();
        var total = list.Sum(item => item.Total);
        var learned = list.Sum(item => item.Learned);
        var passed = list.Sum(item => item.Passed);
        return new KindStats(total, learned, passed, AnalyticsLogic.Rate(learned, total), AnalyticsLogic.Rate(passed, total));
    }

    static StudentSummary Summarize(StatsData data, string userId, IEnumerable<AnalyticsResource> resources)
    {
        var list = resources as IReadOnlyCollection<AnalyticsResource> ?? resources.ToArray();
        KindStats For(string kind) => AnalyticsLogic.Metrics(list.Where(item => item.Kind == kind),
            resource => AnalyticsLogic.Evaluate(resource.Kind, data.RecordFor(userId, resource.Id), resource.MaxScore));
        return new StudentSummary(
            AnalyticsLogic.Metrics(list, resource => AnalyticsLogic.Evaluate(resource.Kind, data.RecordFor(userId, resource.Id), resource.MaxScore)),
            For("theory"), For("theoryPractice"), For("case"), For("casePractice"));
    }

    static async Task<StatsData?> LoadAsync(SchoolDbContext db, Classroom classroom, CancellationToken ct)
    {
        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(item => item.Id == classroom.CourseId, ct);
        if (course == null) return null;
        var resourcesByCourse = await AnalyticsLoader.LoadResourcesAsync(db, [classroom.CourseId], ct);
        var resources = resourcesByCourse.GetValueOrDefault(classroom.CourseId) ?? [];
        var students = await AnalyticsLoader.LoadStudentsAsync(db, [classroom.Id], ct);
        var records = await db.LearningRecords.AsNoTracking().Where(item => item.ClassroomId == classroom.Id).ToListAsync(ct);
        var recordIds = records.Select(item => item.Id).ToArray();
        var attempts = recordIds.Length == 0 ? new List<PracticeAttempt>() : await db.PracticeAttempts.AsNoTracking()
            .Where(item => recordIds.Contains(item.LearningRecordId)).OrderBy(item => item.SubmittedAt).ThenBy(item => item.Id).ToListAsync(ct);
        var teacher = await db.Users.AsNoTracking().Where(item => item.Id == classroom.TeacherUserId)
            .Select(item => item.DisplayName).FirstOrDefaultAsync(ct) ?? "";
        var data = new StatsData
        {
            Classroom = classroom, Course = course, TeacherName = teacher, Students = students,
            Resources = resources, Activities = LoadActivities(classroom.Id, records, attempts)
        };
        data.Activities.AddRange(await AnalyticsLoader.LoadCaseActivities(db, [classroom.Id], ct));
        foreach (var record in records) data.Records[StatsData.Key(record.UserId, record.ResourceId)] = record;
        foreach (var attempt in attempts)
        {
            if (!data.Attempts.TryGetValue(attempt.LearningRecordId, out var list)) data.Attempts[attempt.LearningRecordId] = list = [];
            list.Add(attempt);
        }
        foreach (var resource in resources) data.ResourceById[resource.Id] = resource;
        return data;
    }

    static List<ActivityEvent> LoadActivities(string classroomId,
        IReadOnlyCollection<LearningRecord> records, IReadOnlyCollection<PracticeAttempt> attempts)
    {
        var activities = new List<ActivityEvent>();
        var recordUser = records.ToDictionary(item => item.Id, item => (item.UserId, item.ResourceId));
        foreach (var record in records)
        {
            activities.Add(new ActivityEvent(record.UserId, classroomId, record.ResourceId, record.StartedAt, false, false));
            if (record.CompletedAt is { } completed) activities.Add(new ActivityEvent(record.UserId, classroomId, record.ResourceId, completed, false, true));
        }
        foreach (var attempt in attempts)
        {
            if (!recordUser.TryGetValue(attempt.LearningRecordId, out var owner)) continue;
            if (attempt.StartedAt is { } started) activities.Add(new ActivityEvent(owner.UserId, classroomId, owner.ResourceId, started, false, false));
            activities.Add(new ActivityEvent(owner.UserId, classroomId, owner.ResourceId, attempt.SubmittedAt, true, false));
        }
        return activities;
    }

    static async Task<AnalyticsResource?> FindResourceAsync(SchoolDbContext db, string courseId, string resourceId, CancellationToken ct)
    {
        var map = await AnalyticsLoader.LoadResourcesAsync(db, [courseId], ct);
        return map.GetValueOrDefault(courseId)?.FirstOrDefault(item => item.Id == resourceId);
    }

    static async Task<CourseResource?> FindRawResourceAsync(SchoolDbContext db, string courseId, string resourceId, CancellationToken ct)
        => await (from resource in db.CourseResources.AsNoTracking()
                  join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                  join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                  where resource.Id == resourceId && chapter.CourseId == courseId
                  select resource).FirstOrDefaultAsync(ct);

    static IReadOnlyDictionary<string, int[]> ParseAnswers(string json)
    {
        try
        {
            var answers = JsonSerializer.Deserialize<PracticeAnswer[]>(json, JsonOptions) ?? [];
            return answers.Where(item => !string.IsNullOrWhiteSpace(item.QuestionId))
                .GroupBy(item => item.QuestionId).ToDictionary(group => group.Key,
                    group => TeachingLogic.NormalizeSelection(group.Last().Selected));
        }
        catch (JsonException) { return new Dictionary<string, int[]>(); }
    }

    static string StatusLabel(LearningState state)
        => !state.Learned ? "未学习" : state.Passed ? "已达标" : "学习中";

    static DateTime? LastActivityOf(LearningRecord? record)
        => record == null ? null : record.CompletedAt ?? record.StartedAt;

    static long RemainingSeconds(Classroom classroom)
        => classroom.Status == "closed" ? 0 : Math.Max(0, (long)(classroom.CloseAt - DateTime.UtcNow).TotalSeconds);

    static bool CanManage(HttpContext ctx, Classroom classroom)
        => ctx.User.IsInRole("Admin")
           || (ctx.User.IsInRole("Teacher") && classroom.TeacherUserId == CurrentUserId(ctx));

    static bool CanViewStudent(HttpContext ctx, Classroom classroom, string studentUserId)
        => CanManage(ctx, classroom) || (ctx.User.IsInRole("Student") && studentUserId == CurrentUserId(ctx));

    static string? CurrentUserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);

    static IResult CsvFile(string fileName, string content)
        => Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray(),
            "text/csv; charset=utf-8", SafeFileName(fileName));

    static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (safe.Length == 0) safe = "课堂学情";
        return safe.Length > 100 ? safe[..100] + ".csv" : safe;
    }

    sealed record StudentSummary(KindStats Overall, KindStats Theory, KindStats TheoryPractice, KindStats Case, KindStats CasePractice);

    sealed class StatsData
    {
        public required Classroom Classroom { get; init; }
        public required TrainingCourse Course { get; init; }
        public required string TeacherName { get; init; }
        public required List<AnalyticsStudent> Students { get; init; }
        public required List<AnalyticsResource> Resources { get; init; }
        public required List<ActivityEvent> Activities { get; init; }
        public Dictionary<string, LearningRecord> Records { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<PracticeAttempt>> Attempts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AnalyticsResource> ResourceById { get; } = new(StringComparer.Ordinal);

        public static string Key(string userId, string resourceId) => userId + "\u001f" + resourceId;
        public LearningRecord? RecordFor(string userId, string resourceId) => Records.GetValueOrDefault(Key(userId, resourceId));

        public IReadOnlyList<PracticeAttempt> AttemptsForRecord(string recordId)
            => Attempts.GetValueOrDefault(recordId) ?? (IReadOnlyList<PracticeAttempt>)Array.Empty<PracticeAttempt>();
    }
}
