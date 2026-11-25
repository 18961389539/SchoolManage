using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class ExamManagementEndpoints
{
    public static void MapExamManagementEndpoints(this RouteGroupBuilder all)
    {
        var staff = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Admin", "Teacher"));
        staff.MapGet("/exam-student-options", StudentOptions);
        staff.MapGet("/exams", ListExams);
        staff.MapPost("/exams", CreateExam);
        staff.MapGet("/exams/{id}", GetExam);
        staff.MapPut("/exams/{id}", UpdateExam);
        staff.MapDelete("/exams/{id}", DeleteExam);
        staff.MapPost("/exams/batch-delete", BatchDeleteExams);
        staff.MapPost("/exams/{id}/open", OpenExam);
        staff.MapPost("/exams/{id}/close", CloseExam);
        staff.MapGet("/exams/{id}/students", GetExamStudents);
        staff.MapPost("/exams/{id}/students", AddExamStudents);
        staff.MapPost("/exams/{id}/students/remove", RemoveExamStudents);
        staff.MapExamPracticalStaff();
        staff.MapGet("/exams/{id}/scores", GetScores);
        staff.MapGet("/exams/{id}/attempts/{attemptId}", GetAttemptReview);
        staff.MapPost("/exams/{id}/attempts/{attemptId}/grade", GradeAttempt);
        staff.MapGet("/exams/{id}/monitor", GetMonitor);
        staff.MapGet("/exams/{id}/statistics", GetStatistics);
        staff.MapGet("/exams/{id}/statistics/export", ExportStatistics).RequireRateLimiting("expensive");
        staff.MapGet("/exams/{id}/question-analysis", GetQuestionAnalysis);
        staff.MapGet("/exams/{id}/question-analysis/export", ExportQuestionAnalysis).RequireRateLimiting("expensive");
    }

    internal static IQueryable<TrainingExam> Scoped(SchoolDbContext db, HttpContext ctx)
    {
        var userId = CurrentUserId(ctx);
        return ctx.User.IsInRole("Admin") ? db.TrainingExams : db.TrainingExams.Where(item => item.CreatedByUserId == userId);
    }

    static async Task<IResult> StudentOptions(HttpRequest request, SchoolDbContext db, CancellationToken ct)
    {
        var search = request.Query["search"].ToString().Trim();
        var classId = request.Query["classId"].ToString();
        var examId = request.Query["examId"].ToString();
        var query = from person in db.People.AsNoTracking()
                    join user in db.Users.AsNoTracking() on person.UserId equals user.Id
                    join schoolClass in db.Classes.AsNoTracking() on person.ClassId equals schoolClass.Id into classes
                    from schoolClass in classes.DefaultIfEmpty()
                    where person.Kind == "student" && user.Role == "Student"
                    select new
                    {
                        userId = person.UserId, personId = person.Id, person.Name, userName = user.UserName,
                        person.Number, person.ClassId, className = schoolClass == null ? "" : schoolClass.Name
                    };
        if (search.Length > 0) query = query.Where(item => item.Name.Contains(search) || item.Number.Contains(search) || item.userName.Contains(search));
        if (classId.Length > 0) query = query.Where(item => item.ClassId == classId);
        if (examId.Length > 0)
        {
            var assigned = db.ExamStudents.AsNoTracking().Where(item => item.ExamId == examId).Select(item => item.StudentUserId);
            query = query.Where(item => !assigned.Contains(item.userId));
        }
        var items = await query.OrderBy(item => item.Number).Take(50).ToListAsync(ct);
        return Results.Ok(new { items });
    }

    static async Task<IResult> ListExams(HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var query = Scoped(db, ctx).AsNoTracking();
        var search = request.Query["search"].ToString().Trim();
        if (search.Length > 0) query = query.Where(item => item.Name.Contains(search) || item.Description.Contains(search));
        var status = request.Query["status"].ToString();
        query = status switch
        {
            "scheduled" => query.Where(item => item.Status != "closed" && item.EndAt > now &&
                (item.Status == "scheduled" || item.StartAt > now)),
            "open" => query.Where(item => item.Status == "open" && item.StartAt <= now && item.EndAt > now),
            "closed" => query.Where(item => item.Status == "closed" || item.EndAt <= now),
            _ => query
        };
        var (page, size) = Validation.Page(request);
        var total = await query.CountAsync(ct);
        var exams = await query.OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Name)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = exams.Select(item => item.Id).ToArray();
        var paperIds = exams.Select(item => item.PaperId).Distinct().ToArray();
        var papers = await db.ExamPapers.AsNoTracking().Where(item => paperIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Name }).ToDictionaryAsync(item => item.Id, item => item.Name, ct);
        var paperQuestions = await db.ExamPaperQuestions.AsNoTracking().Where(item => paperIds.Contains(item.PaperId))
            .Select(item => new { item.PaperId, item.Score, item.Type, item.PracticalNodeId }).ToListAsync(ct);
        var studentCounts = await db.ExamStudents.AsNoTracking().Where(item => ids.Contains(item.ExamId))
            .GroupBy(item => item.ExamId).Select(group => new
            {
                group.Key,
                Count = group.Count(),
                Deployed = group.Count(item => item.DeploymentStatus == "deployed")
            })
            .ToDictionaryAsync(item => item.Key, ct);
        var attemptCounts = await db.ExamAttempts.AsNoTracking().Where(item => ids.Contains(item.ExamId))
            .GroupBy(item => item.ExamId).Select(group => new
            {
                group.Key,
                Started = group.Count(),
                Submitted = group.Count(attempt => attempt.Status != "inProgress"),
                Graded = group.Count(attempt => attempt.Status == "graded")
            }).ToDictionaryAsync(item => item.Key, ct);
        return Results.Ok(new
        {
            total, page, pageSize = size,
            items = exams.Select(exam =>
            {
                var counts = attemptCounts.GetValueOrDefault(exam.Id);
                var questions = paperQuestions.Where(item => item.PaperId == exam.PaperId).ToArray();
                var roster = studentCounts.GetValueOrDefault(exam.Id);
                var studentCount = roster?.Count ?? 0;
                var requiresPractical = questions.Any(item => item.Type == "practical" && item.PracticalNodeId != "");
                var deployedCount = requiresPractical ? roster?.Deployed ?? 0 : 0;
                return new
                {
                    exam.Id, exam.Name, exam.PaperId, exam.Description, exam.StartAt, exam.EndAt, exam.DurationMinutes,
                    status = ExamLogic.EffectiveStatus(exam, now), storedStatus = exam.Status,
                    exam.CreatedAt,
                    paperName = papers.GetValueOrDefault(exam.PaperId, ""),
                    questionCount = questions.Length,
                    totalScore = questions.Sum(item => item.Score),
                    studentCount,
                    attemptCount = counts?.Started ?? 0,
                    submittedCount = counts?.Submitted ?? 0,
                    gradedCount = counts?.Graded ?? 0,
                    requiresPracticalEnvironment = requiresPractical,
                    deployedCount,
                    pendingDeployCount = requiresPractical ? Math.Max(0, studentCount - deployedCount) : 0
                };
            })
        });
    }

    static async Task<IResult> GetExam(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var exam = await Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        var paper = await db.ExamPapers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == exam.PaperId, ct);
        var questions = await db.ExamPaperQuestions.AsNoTracking().Where(item => item.PaperId == exam.PaperId)
            .Select(item => new { item.Score, item.Type, item.PracticalNodeId }).ToListAsync(ct);
        var studentCount = await db.ExamStudents.CountAsync(item => item.ExamId == id, ct);
        var deployedCount = await db.ExamStudents.CountAsync(item => item.ExamId == id && item.DeploymentStatus == "deployed", ct);
        var attempts = await db.ExamAttempts.AsNoTracking().Where(item => item.ExamId == id).ToListAsync(ct);
        var requiresPractical = questions.Any(item => item.Type == "practical" && item.PracticalNodeId != "");
        var deployed = requiresPractical ? deployedCount : 0;
        return Results.Ok(new
        {
            exam.Id, exam.Name, exam.PaperId, exam.Description, exam.StartAt, exam.EndAt, exam.DurationMinutes,
            status = ExamLogic.EffectiveStatus(exam, now), storedStatus = exam.Status,
            exam.CreatedAt, exam.UpdatedAt,
            paperName = paper?.Name ?? "",
            questionCount = questions.Count,
            totalScore = questions.Sum(item => item.Score),
            studentCount,
            attemptCount = attempts.Count,
            submittedCount = attempts.Count(item => item.Status != "inProgress"),
            gradedCount = attempts.Count(item => item.Status == "graded"),
            canEdit = exam.Status == "scheduled" && attempts.Count == 0,
            requiresPracticalEnvironment = requiresPractical,
            deployedCount = deployed,
            pendingDeployCount = requiresPractical ? Math.Max(0, studentCount - deployed) : 0
        });
    }

    static async Task<IResult> CreateExam(TrainingExamInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var error = ExamLogic.ValidateExam(input);
        if (error != null) return Bad(error);
        var paper = await db.ExamPapers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.PaperId, ct);
        if (paper == null) return Bad("请选择有效的试卷");
        if (!await db.ExamPaperQuestions.AnyAsync(item => item.PaperId == paper.Id, ct)) return Bad("试卷还没有题目，请先在试卷管理中添加题目");
        var name = input.Name.Trim();
        if (await db.TrainingExams.AnyAsync(item => item.Name == name, ct)) return Conflict("考试名称已存在");
        var exam = new TrainingExam
        {
            Name = name, PaperId = paper.Id, Description = (input.Description ?? "").Trim(),
            StartAt = input.StartAt, EndAt = input.EndAt, DurationMinutes = input.DurationMinutes,
            CreatedByUserId = CurrentUserId(ctx) ?? ""
        };
        db.TrainingExams.Add(exam);
        var (added, enrollError) = await AddStudentsAsync(db, exam.Id, input.ClassIds, input.ProfessionIds, null, input.ClassroomIds, ctx, ct);
        if (enrollError != null) return Bad(enrollError);
        Audit.Add(db, ctx, "新增考试", "考试", $"{exam.Name}，分配学生{added}人");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = exam.Id, studentCount = added });
    }

    static async Task<IResult> UpdateExam(string id, TrainingExamInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var error = ExamLogic.ValidateExam(input);
        if (error != null) return Bad(error);
        var exam = await Scoped(db, ctx).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        if (exam.Status != "scheduled") return Conflict("考试已开始或结束，不能再编辑");
        if (await db.ExamAttempts.AnyAsync(item => item.ExamId == id, ct)) return Conflict("已有学生开始作答，不能再编辑");
        var paper = await db.ExamPapers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.PaperId, ct);
        if (paper == null) return Bad("请选择有效的试卷");
        if (!await db.ExamPaperQuestions.AnyAsync(item => item.PaperId == paper.Id, ct)) return Bad("试卷还没有题目");
        var name = input.Name.Trim();
        if (await db.TrainingExams.AnyAsync(item => item.Id != id && item.Name == name, ct)) return Conflict("考试名称已存在");
        exam.Name = name;
        if (exam.PaperId != paper.Id)
        {
            await db.ExamPracticalEnvironments.Where(item =>
                db.ExamStudents.Any(student => student.Id == item.EnrollmentId && student.ExamId == id)).ExecuteDeleteAsync(ct);
            await db.ExamStudents.Where(item => item.ExamId == id)
                .ExecuteUpdateAsync(setter => setter.SetProperty(item => item.DeploymentStatus, "pending")
                    .SetProperty(item => item.DeploymentMessage, "").SetProperty(item => item.DeployedAt, (DateTime?)null), ct);
            exam.PaperId = paper.Id;
        }
        exam.Description = (input.Description ?? "").Trim();
        exam.StartAt = input.StartAt;
        exam.EndAt = input.EndAt;
        exam.DurationMinutes = input.DurationMinutes;
        exam.UpdatedAt = DateTime.UtcNow;
        Audit.Add(db, ctx, "编辑考试", "考试", exam.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> DeleteExam(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var exam = await Scoped(db, ctx).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        if (await db.ExamAttempts.AnyAsync(item => item.ExamId == id, ct)) return Conflict("考试已有作答记录，不能删除");
        db.TrainingExams.Remove(exam);
        Audit.Add(db, ctx, "删除考试", "考试", exam.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> BatchDeleteExams(IdListInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var ids = (input.Ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length == 0) return Bad("请选择要删除的考试");
        var scoped = Scoped(db, ctx);
        var blocked = await db.ExamAttempts.AsNoTracking()
            .Where(item => scoped.Any(exam => exam.Id == item.ExamId && ids.Contains(exam.Id)))
            .Select(item => item.ExamId).Distinct().CountAsync(ct);
        if (blocked > 0) return Conflict($"所选考试中有 {blocked} 场已有作答记录，不能删除");
        var exams = await scoped.Where(item => ids.Contains(item.Id)).ToListAsync(ct);
        if (exams.Count == 0) return NotFound("考试不存在");
        db.TrainingExams.RemoveRange(exams);
        Audit.Add(db, ctx, "批量删除考试", "考试", $"删除{exams.Count}场考试");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { deleted = exams.Count });
    }

    static async Task<IResult> OpenExam(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var exam = await Scoped(db, ctx).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        if (exam.Status != "scheduled") return Conflict(StatusMessage(exam.Status));
        if (exam.EndAt <= DateTime.UtcNow) return Conflict("考试结束时间已过，请先修改考试时间");
        if (!await db.ExamPaperQuestions.AnyAsync(item => item.PaperId == exam.PaperId, ct)) return Conflict("试卷还没有题目，不能开考");
        exam.Status = "open";
        exam.UpdatedAt = DateTime.UtcNow;
        Audit.Add(db, ctx, "开考", "考试", exam.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { status = exam.Status });
    }

    static async Task<IResult> CloseExam(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var exam = await Scoped(db, ctx).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        if (exam.Status == "closed") return Conflict("考试已结束");
        exam.Status = "closed";
        exam.UpdatedAt = DateTime.UtcNow;
        Audit.Add(db, ctx, "结考", "考试", exam.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { status = exam.Status });
    }

    static async Task<IResult> GetExamStudents(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var exam = await Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        var rows = await (from enrollment in db.ExamStudents.AsNoTracking()
                          join person in db.People.AsNoTracking() on enrollment.StudentPersonId equals person.Id
                          join user in db.Users.AsNoTracking() on enrollment.StudentUserId equals user.Id
                          where enrollment.ExamId == id
                          select new
                          {
                              enrollment.StudentUserId, enrollment.StudentPersonId, person.Name, userName = user.UserName,
                              person.Number, person.Gender, person.Phone, person.Email, enrollment.ClassId, enrollment.ClassName, enrollment.DeploymentStatus,
                              enrollment.DeploymentMessage, enrollment.DeployedAt, enrollment.AddedAt
                          }).OrderBy(item => item.Number).ToListAsync(ct);
        var attempts = await db.ExamAttempts.AsNoTracking().Where(item => item.ExamId == id)
            .ToDictionaryAsync(item => item.UserId, StringComparer.Ordinal, ct);
        var items = rows.Select(row =>
        {
            var attempt = attempts.GetValueOrDefault(row.StudentUserId);
            return new
            {
                row.StudentUserId, row.StudentPersonId, row.Name, row.userName, row.Number, row.Gender, row.Phone,
                row.Email, row.ClassId, row.ClassName,
                row.DeploymentStatus, row.DeploymentMessage, row.DeployedAt, row.AddedAt,
                attemptId = attempt?.Id,
                attemptStatus = attempt?.Status ?? "notStarted",
                startedAt = attempt?.StartedAt,
                submittedAt = attempt?.SubmittedAt,
                autoSubmitted = attempt?.AutoSubmitted ?? false,
                totalScore = attempt?.TotalScore,
                maxScore = attempt?.MaxScore
            };
        }).ToList();
        return Results.Ok(new { items, practicalProjectCount = await ExamPracticalEndpoints.Projects(db, exam.PaperId).CountAsync(ct) });
    }

    static async Task<IResult> AddExamStudents(string id, ExamStudentAdjustInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var exam = await Scoped(db, ctx).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        if (exam.Status != "scheduled") return Conflict("考试已开始，不能再调整学生名单");
        if (await db.ExamAttempts.AnyAsync(item => item.ExamId == id, ct)) return Conflict("已有学生开始作答，不能再调整学生名单");
        var (added, error) = await AddStudentsAsync(db, id, input.ClassIds, input.ProfessionIds, input.StudentUserIds, input.ClassroomIds, ctx, ct);
        if (error != null) return Bad(error);
        Audit.Add(db, ctx, "调整考试学生", "考试", $"{exam.Name}：新增{added}人");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { added });
    }

    static async Task<IResult> RemoveExamStudents(string id, ExamStudentAdjustInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var exam = await Scoped(db, ctx).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        if (exam.Status != "scheduled") return Conflict("考试已开始，不能再调整学生名单");
        if (await db.ExamAttempts.AnyAsync(item => item.ExamId == id, ct)) return Conflict("已有学生开始作答，不能再调整学生名单");
        var userIds = (input.StudentUserIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        if (userIds.Length == 0) return Bad("请选择要移除的学生");
        var items = await db.ExamStudents.Where(item => item.ExamId == id && userIds.Contains(item.StudentUserId)).ToListAsync(ct);
        db.ExamStudents.RemoveRange(items);
        Audit.Add(db, ctx, "移除考试学生", "考试", $"{exam.Name}：移除{items.Count}人");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { removed = items.Count });
    }

    sealed class ScoreSource
    {
        public string StudentUserId { get; set; } = "";
        public string Name { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Number { get; set; } = "";
        public string ClassName { get; set; } = "";
        public ExamAttempt? Attempt { get; set; }
        public string Status => Attempt?.Status ?? "notStarted";
    }

    static async Task<IResult> GetScores(HttpRequest request, string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var exam = await Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        var search = request.Query["search"].ToString().Trim();
        var statusFilter = request.Query["status"].ToString();
        var query = from enrollment in db.ExamStudents.AsNoTracking()
                    join person in db.People.AsNoTracking() on enrollment.StudentPersonId equals person.Id
                    join user in db.Users.AsNoTracking() on enrollment.StudentUserId equals user.Id
                    join attempt in db.ExamAttempts.AsNoTracking().Where(item => item.ExamId == id)
                        on enrollment.StudentUserId equals attempt.UserId into attempts
                    from attempt in attempts.DefaultIfEmpty()
                    where enrollment.ExamId == id
                    select new
                    {
                        enrollment.StudentUserId, person.Name, UserName = user.UserName,
                        person.Number, enrollment.ClassName,
                        Attempt = attempt
                    };
        if (search.Length > 0)
            query = query.Where(row => row.Name.Contains(search) || row.Number.Contains(search) || row.UserName.Contains(search));
        if (statusFilter.Length > 0 && statusFilter != "all")
        {
            if (statusFilter == "notStarted") query = query.Where(row => row.Attempt == null);
            else query = query.Where(row => row.Attempt != null && row.Attempt.Status == statusFilter);
        }
        var (page, size) = Validation.Page(request);
        var total = await query.CountAsync(ct);
        var pageRows = await query.OrderBy(row => row.Number).Skip((page - 1) * size).Take(size)
            .Select(row => new ScoreSource
            {
                StudentUserId = row.StudentUserId, Name = row.Name, UserName = row.UserName,
                Number = row.Number, ClassName = row.ClassName, Attempt = row.Attempt
            }).ToListAsync(ct);
        var attemptIds = pageRows.Where(row => row.Attempt != null).Select(row => row.Attempt!.Id).ToArray();
        var questions = await db.ExamPaperQuestions.AsNoTracking().Where(item => item.PaperId == exam.PaperId)
            .Select(item => new { item.Id, item.Type, item.Score }).ToListAsync(ct);
        var typeMax = questions.GroupBy(item => item.Type).ToDictionary(group => group.Key, group => group.Sum(item => item.Score));
        var typeOf = questions.ToDictionary(item => item.Id, item => item.Type, StringComparer.Ordinal);
        var answers = await db.ExamAnswers.AsNoTracking().Where(item => attemptIds.Contains(item.AttemptId)).ToListAsync(ct);
        var items = pageRows.Select(row =>
        {
            var attempt = row.Attempt;
            var own = attempt == null ? [] : answers.Where(item => item.AttemptId == attempt.Id).ToArray();
            var typeScores = ExamLogic.QuestionTypes.Where(typeMax.ContainsKey).Select(type => new
            {
                type,
                label = ExamLogic.QuestionTypeLabel(type),
                maxScore = typeMax[type],
                score = own.Where(answer => typeOf.GetValueOrDefault(answer.PaperQuestionId) == type).Sum(answer => answer.ScoreAwarded ?? 0)
            }).ToArray();
            return new
            {
                row.StudentUserId, row.Name, row.UserName, row.Number, row.ClassName,
                attemptId = attempt?.Id,
                status = row.Status,
                statusLabel = ExamLogic.AttemptStatusLabel(row.Status),
                startedAt = attempt?.StartedAt,
                submittedAt = attempt?.SubmittedAt,
                elapsedSeconds = attempt?.SubmittedAt is { } submittedAt
                    ? (int?)Math.Max(0, (submittedAt - attempt.StartedAt).TotalSeconds) : null,
                autoSubmitted = attempt?.AutoSubmitted ?? false,
                objectiveScore = attempt?.ObjectiveScore ?? 0,
                practicalScore = attempt?.PracticalScore ?? 0,
                totalScore = attempt?.TotalScore ?? 0,
                maxScore = attempt?.MaxScore ?? questions.Sum(item => item.Score),
                gradedAt = attempt?.GradedAt,
                typeScores
            };
        }).ToList();
        return Results.Ok(new { items, total, page, pageSize = size });
    }

    static async Task<IResult> GetAttemptReview(string id, string attemptId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var exam = await Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        var attempt = await db.ExamAttempts.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attemptId && item.ExamId == id, ct);
        if (attempt == null) return NotFound("作答记录不存在");
        return Results.Ok(await ExamReview.BuildAsync(db, exam, attempt, ct));
    }

    static async Task<IResult> GradeAttempt(string id, string attemptId, ExamGradeInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var exam = await Scoped(db, ctx).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        var attempt = await db.ExamAttempts.FirstOrDefaultAsync(item => item.Id == attemptId && item.ExamId == id, ct);
        if (attempt == null) return NotFound("作答记录不存在");
        if (attempt.Status == "inProgress") return Conflict("学生尚未提交，不能批阅");
        var grades = (input.Answers ?? []).Where(item => item != null).ToArray();
        if (grades.Length == 0) return Bad("请填写批阅分数");
        var questions = await db.ExamPaperQuestions.AsNoTracking().Where(item => item.PaperId == exam.PaperId)
            .ToDictionaryAsync(item => item.Id, StringComparer.Ordinal, ct);
        var answers = await db.ExamAnswers.Where(item => item.AttemptId == attemptId).ToDictionaryAsync(item => item.PaperQuestionId, StringComparer.Ordinal, ct);
        foreach (var grade in grades.GroupBy(item => item.PaperQuestionId).Select(group => group.Last()))
        {
            if (!questions.TryGetValue(grade.PaperQuestionId, out var question) || question.Type != "practical")
                return Bad("只能批阅实操题，请刷新后重试");
            var automatic = question.PracticalNodeId != "";
            if (automatic && string.IsNullOrWhiteSpace(grade.Comment))
                return Bad("覆盖自动评分时请填写原因");
            if (grade.Score < 0 || grade.Score > question.Score) return Bad($"分数应在 0–{question.Score} 之间");
            if ((grade.Comment ?? "").Length > 500) return Bad("评语不能超过 500 个字符");
            if (!answers.TryGetValue(grade.PaperQuestionId, out var answer))
            {
                answer = new ExamAnswer { AttemptId = attemptId, PaperQuestionId = grade.PaperQuestionId };
                db.ExamAnswers.Add(answer);
                answers[grade.PaperQuestionId] = answer;
            }
            answer.ScoreAwarded = grade.Score;
            answer.Comment = (grade.Comment ?? "").Trim();
            answer.AnsweredAt ??= DateTime.UtcNow;
        }
        var practical = questions.Values.Where(item => item.Type == "practical").ToArray();
        var practicalAnswers = answers.Values.Where(item => questions.TryGetValue(item.PaperQuestionId, out var question) && question.Type == "practical").ToArray();
        attempt.PracticalScore = practicalAnswers.Sum(item => item.ScoreAwarded ?? 0);
        attempt.TotalScore = attempt.ObjectiveScore + attempt.PracticalScore;
        var allGraded = practical.All(item => answers.TryGetValue(item.Id, out var answer) && answer.ScoreAwarded != null);
        attempt.Status = allGraded ? "graded" : "submitted";
        if (allGraded)
        {
            attempt.GradedAt = DateTime.UtcNow;
            attempt.GradedByUserId = CurrentUserId(ctx) ?? "";
        }
        var overridden = grades.Any(item => questions.TryGetValue(item.PaperQuestionId, out var question) && question.PracticalNodeId != "");
        Audit.Add(db, ctx, overridden ? "覆盖自动实操评分" : "批阅考试", "考试",
            $"{exam.Name}：{attempt.StudentName} {attempt.TotalScore}/{attempt.MaxScore}");
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExamReview.BuildAsync(db, exam, attempt, ct));
    }

    static async Task<IResult> GetMonitor(HttpRequest request, string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var exam = await Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        var search = request.Query["search"].ToString().Trim();
        var rows = await (from enrollment in db.ExamStudents.AsNoTracking()
                          join person in db.People.AsNoTracking() on enrollment.StudentPersonId equals person.Id
                          join user in db.Users.AsNoTracking() on enrollment.StudentUserId equals user.Id
                          where enrollment.ExamId == id
                          select new
                          {
                              enrollment.StudentUserId, person.Name, userName = user.UserName, person.Number,
                              enrollment.ClassName, enrollment.DeploymentStatus, enrollment.DeploymentMessage
                          }).ToListAsync(ct);
        var attempts = await db.ExamAttempts.AsNoTracking().Where(item => item.ExamId == id)
            .ToDictionaryAsync(item => item.UserId, StringComparer.Ordinal, ct);
        var attemptIds = attempts.Values.Select(item => item.Id).ToArray();
        var answerStats = await db.ExamAnswers.AsNoTracking()
            .Where(item => attemptIds.Contains(item.AttemptId))
            .GroupBy(item => item.AttemptId)
            .Select(group => new
            {
                AttemptId = group.Key,
                Answered = group.Count(item =>
                    item.TextAnswer.Trim() != ""
                    || item.SelectedJson != "[]"
                    || (item.PracticalDetailsJson != "[]" && item.AnsweredAt != null)),
                LastAnsweredAt = group.Max(item => item.AnsweredAt)
            })
            .ToDictionaryAsync(row => row.AttemptId, StringComparer.Ordinal, ct);
        var activePracticalCounts = await (from work in db.ExamPracticalEnvironments.AsNoTracking()
                                           join enrollment in db.ExamStudents.AsNoTracking() on work.EnrollmentId equals enrollment.Id
                                           where enrollment.ExamId == id && work.HasWork
                                           group work by enrollment.StudentUserId into own
                                           select new { UserId = own.Key, Count = own.Count() })
            .ToDictionaryAsync(row => row.UserId, row => row.Count, ct);
        var questionCount = await db.ExamPaperQuestions.CountAsync(item => item.PaperId == exam.PaperId, ct);
        var filtered = rows.Where(row => search.Length == 0 || row.Name.Contains(search) || row.Number.Contains(search) || row.userName.Contains(search))
            .OrderBy(row => row.Number).Select(row =>
            {
                var attempt = attempts.GetValueOrDefault(row.StudentUserId);
                var stats = attempt == null ? null : answerStats.GetValueOrDefault(attempt.Id);
                var answered = stats?.Answered ?? 0;
                if (attempt?.Status == "inProgress") answered += activePracticalCounts.GetValueOrDefault(row.StudentUserId);
                return new
                {
                    row.StudentUserId, row.Name, row.userName, row.Number, row.ClassName,
                    row.DeploymentStatus, row.DeploymentMessage,
                    attemptId = attempt?.Id,
                    status = attempt?.Status ?? "notStarted",
                    statusLabel = ExamLogic.AttemptStatusLabel(attempt?.Status ?? "notStarted"),
                    answeredCount = answered,
                    questionCount,
                    progress = questionCount == 0 ? 0 : (int)Math.Round(answered * 100d / questionCount),
                    startedAt = attempt?.StartedAt,
                    submittedAt = attempt?.SubmittedAt,
                    autoSubmitted = attempt?.AutoSubmitted ?? false,
                    lastAnsweredAt = stats?.LastAnsweredAt,
                    totalScore = attempt?.TotalScore,
                    maxScore = attempt?.MaxScore
                };
            }).ToList();
        return Results.Ok(new
        {
            items = filtered,
            counts = new
            {
                expected = rows.Count,
                notStarted = rows.Count(row => !attempts.ContainsKey(row.StudentUserId)),
                inProgress = attempts.Values.Count(item => item.Status == "inProgress"),
                submitted = attempts.Values.Count(item => item.Status == "submitted"),
                graded = attempts.Values.Count(item => item.Status == "graded"),
                deployed = rows.Count(row => row.DeploymentStatus == "deployed")
            }
        });
    }

    sealed record ExamTypeStatistics(string Type, string Label, int QuestionCount, int MaxScore, int Answered,
        int Correct, int Accuracy, int ScoreRate);
    sealed record ExamStatisticsView(int Expected, int Started, int Submitted, int Graded, int Absent, int MaxScore,
        double AverageScore, int HighestScore, int LowestScore, int PassRate, int AverageRate,
        ScoreBand[] Bands, ExamTypeStatistics[] TypeStats);

    static async Task<IResult> GetStatistics(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var exam = await Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        return Results.Ok(await BuildStatistics(db, exam, ct));
    }

    static async Task<ExamStatisticsView> BuildStatistics(SchoolDbContext db, TrainingExam exam, CancellationToken ct)
    {
        var expected = await db.ExamStudents.CountAsync(item => item.ExamId == exam.Id, ct);
        var attempts = await db.ExamAttempts.AsNoTracking().Where(item => item.ExamId == exam.Id).ToListAsync(ct);
        var questions = await db.ExamPaperQuestions.AsNoTracking().Where(item => item.PaperId == exam.PaperId)
            .OrderBy(item => item.SortOrder).ToListAsync(ct);
        var submitted = attempts.Where(item => item.Status != "inProgress").ToArray();
        var graded = submitted.Where(item => item.Status == "graded").ToArray();
        var finishedIds = submitted.Select(item => item.Id).ToArray();
        var gradedIds = graded.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var answers = (await db.ExamAnswers.AsNoTracking().Where(item => finishedIds.Contains(item.AttemptId)).ToListAsync(ct))
            .ToLookup(item => item.PaperQuestionId);
        var percentages = graded.Select(item => item.MaxScore <= 0 ? 0 : item.TotalScore * 100m / item.MaxScore).ToArray();
        var passCount = percentages.Count(value => value >= 60);
        var absent = Math.Max(expected - attempts.Count, 0);
        var bands = ExamLogic.ScoreBands(percentages, expected, absent, attempts.Count - graded.Length);

        var typeStats = ExamLogic.QuestionTypes.Select(type =>
        {
            var typeQuestions = questions.Where(item => item.Type == type).ToArray();
            var answered = 0;
            var evaluated = 0;
            var correct = 0;
            foreach (var question in typeQuestions)
            {
                var participates = answers[question.Id].Where(ExamLogic.HasAnswer).ToArray();
                answered += participates.Length;
                if (ExamLogic.IsObjective(type))
                {
                    evaluated += participates.Length;
                    correct += participates.Count(answer => IsCorrect(question, answer));
                }
                else
                {
                    var scored = answers[question.Id].Where(answer => answer.ScoreAwarded != null).ToArray();
                    evaluated += scored.Length;
                    correct += scored.Count(answer => IsCorrect(question, answer));
                }
            }
            var max = typeQuestions.Sum(item => item.Score);
            var score = typeQuestions.SelectMany(item => answers[item.Id]).Where(item => gradedIds.Contains(item.AttemptId))
                .Sum(item => (long)(item.ScoreAwarded ?? 0));
            return new ExamTypeStatistics(type, ExamLogic.QuestionTypeLabel(type), typeQuestions.Length, max,
                answered, correct, Rate(correct, evaluated), Rate(score, (long)max * graded.Length));
        }).ToArray();
        return new ExamStatisticsView(expected, attempts.Count, submitted.Length, graded.Length, absent, questions.Sum(item => item.Score),
            graded.Length == 0 ? 0 : Math.Round(graded.Average(item => item.TotalScore), 1),
            graded.Length == 0 ? 0 : graded.Max(item => item.TotalScore),
            graded.Length == 0 ? 0 : graded.Min(item => item.TotalScore),
            Rate(passCount, graded.Length), percentages.Length == 0 ? 0 : (int)Math.Round(percentages.Average()), bands, typeStats);
    }

    static bool IsCorrect(ExamPaperQuestion question, ExamAnswer answer)
        => ExamLogic.IsObjective(question.Type)
            ? ExamLogic.IsCorrect(question.AnswerJson, ExamLogic.DeserializeSelection(answer.SelectedJson))
            : ExamLogic.HasAnswer(answer) && answer.ScoreAwarded == question.Score;

    static int Rate(long count, long total) => total <= 0 ? 0 : (int)Math.Round(count * 100m / total);

    static async Task<IResult> ExportStatistics(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var exam = await Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        var stats = await BuildStatistics(db, exam, ct);
        var builder = new StringBuilder();
        builder.AppendLine("数据总览");
        builder.AppendLine("应考人数,实到人数,缺考人数,已交卷,已批阅,试卷总分,最高得分,最低得分,平均得分,及格率");
        builder.AppendLine(FormattableString.Invariant(
            $"{stats.Expected},{stats.Started},{stats.Absent},{stats.Submitted},{stats.Graded},{stats.MaxScore},{stats.HighestScore},{stats.LowestScore},{stats.AverageScore},{stats.PassRate}%"));
        builder.AppendLine().AppendLine("分数各段比例").AppendLine("分数段,人数,占应考人数比例");
        foreach (var band in stats.Bands)
            builder.AppendLine(FormattableString.Invariant($"{Csv(band.Label)},{band.Count},{band.Percent}%"));
        builder.AppendLine().AppendLine("各题型正确率").AppendLine("题型,题量,单份分值,作答人次,正确人次,正确率,得分率");
        foreach (var type in stats.TypeStats)
            builder.AppendLine(FormattableString.Invariant(
                $"{Csv(type.Label)},{type.QuestionCount},{type.MaxScore},{type.Answered},{type.Correct},{type.Accuracy}%,{type.ScoreRate}%"));
        return Results.File(
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray(),
            "text/csv; charset=utf-8", SafeFileName($"考试统计-{exam.Name}.csv"));
    }

    static async Task<IResult> GetQuestionAnalysis(HttpRequest request, string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var exam = await Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        var (rows, finishedCount) = await BuildQuestionAnalysis(db, exam, ct);
        var filtered = FilterQuestionAnalysis(rows, request).ToArray();
        var (page, size) = Validation.Page(request);
        return Results.Ok(new
        {
            items = filtered.Skip((page - 1) * size).Take(size), total = filtered.Length, page, pageSize = size, finishedCount,
            maxScore = (await db.ExamPaperQuestions.AsNoTracking().Where(item => item.PaperId == exam.PaperId).SumAsync(item => item.Score, ct))
        });
    }

    static IEnumerable<QuestionAnalysisRow> FilterQuestionAnalysis(IEnumerable<QuestionAnalysisRow> rows, HttpRequest request)
    {
        var search = request.Query["search"].ToString().Trim();
        var type = request.Query["type"].ToString();
        return rows.Where(row => (type.Length == 0 || row.Type == type)
            && (search.Length == 0 || row.Stem.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    static async Task<IResult> ExportQuestionAnalysis(string id, HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var exam = await Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return NotFound("考试不存在");
        var (rows, _) = await BuildQuestionAnalysis(db, exam, ct);
        var builder = new StringBuilder();
        builder.AppendLine("题号,题型,题干,分值,作答人数,未作答人数,正确人数,正确率");
        foreach (var row in FilterQuestionAnalysis(rows, request))
        {
            builder.AppendLine(string.Join(',', new[]
            {
                row.Number.ToString(), Csv(row.TypeLabel), Csv(row.Stem), row.MaxScore.ToString(),
                row.Answered.ToString(), row.Unanswered.ToString(),
                row.CorrectCount.ToString(), row.Evaluated == 0 ? "" : row.Accuracy + "%"
            }));
        }
        return Results.File(
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray(),
            "text/csv; charset=utf-8", SafeFileName($"试题分析-{exam.Name}.csv"));
    }

    sealed class QuestionAnalysisRow
    {
        public string PaperQuestionId { get; set; } = "";
        public string Type { get; set; } = "";
        public string TypeLabel { get; set; } = "";
        public string Stem { get; set; } = "";
        public int MaxScore { get; set; }
        public int SortOrder { get; set; }
        public int Number { get; set; }
        public int Answered { get; set; }
        public int Unanswered { get; set; }
        public int CorrectCount { get; set; }
        public int Evaluated { get; set; }
        public int Accuracy { get; set; }
    }

    static async Task<(List<QuestionAnalysisRow> Rows, int FinishedCount)> BuildQuestionAnalysis(SchoolDbContext db, TrainingExam exam, CancellationToken ct)
    {
        var questions = await db.ExamPaperQuestions.AsNoTracking().Where(item => item.PaperId == exam.PaperId)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id).ToListAsync(ct);
        var attempts = await db.ExamAttempts.AsNoTracking().Where(item => item.ExamId == exam.Id && item.Status != "inProgress").ToListAsync(ct);
        var attemptIds = attempts.Select(item => item.Id).ToArray();
        var answers = (await db.ExamAnswers.AsNoTracking().Where(item => attemptIds.Contains(item.AttemptId)).ToListAsync(ct))
            .ToLookup(item => item.PaperQuestionId);
        var rows = questions.Select((question, index) =>
        {
            var own = answers[question.Id];
            var answered = own.Count(ExamLogic.HasAnswer);
            var evaluated = (ExamLogic.IsObjective(question.Type)
                ? own.Where(ExamLogic.HasAnswer)
                : own.Where(item => item.ScoreAwarded != null)).ToArray();
            var correct = evaluated.Count(item => IsCorrect(question, item));
            return new QuestionAnalysisRow
            {
                PaperQuestionId = question.Id, Type = question.Type, TypeLabel = ExamLogic.QuestionTypeLabel(question.Type),
                Stem = question.Stem, MaxScore = question.Score, SortOrder = question.SortOrder, Number = index + 1,
                Answered = answered, Unanswered = attempts.Count - answered, Evaluated = evaluated.Length,
                CorrectCount = correct, Accuracy = Rate(correct, evaluated.Length)
            };
        }).ToList();
        return (rows, attempts.Count);
    }

    static async Task<(int Added, string? Error)> AddStudentsAsync(
        SchoolDbContext db, string examId, string[]? classIds, string[]? professionIds, string[]? studentUserIds,
        string[]? classroomIds, HttpContext ctx, CancellationToken ct)
    {
        var requestedClasses = (classIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        var requestedProfessions = (professionIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        var requestedStudents = (studentUserIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        var requestedClassrooms = (classroomIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        if (requestedClasses.Length > 0)
        {
            var found = await db.Classes.AsNoTracking().Where(item => requestedClasses.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct);
            if (found.Count != requestedClasses.Length) return (0, "部分班级不存在");
        }
        if (requestedProfessions.Length > 0)
        {
            var found = await db.Professions.AsNoTracking().Where(item => requestedProfessions.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct);
            if (found.Count != requestedProfessions.Length) return (0, "部分专业不存在");
        }
        if (requestedClassrooms.Length > 0)
        {
            var classrooms = await db.Classrooms.AsNoTracking().Where(item => requestedClassrooms.Contains(item.Id)).ToListAsync(ct);
            if (classrooms.Count != requestedClassrooms.Length) return (0, "部分课堂不存在");
            if (ctx.User.IsInRole("Teacher"))
            {
                var teacherId = CurrentUserId(ctx) ?? "";
                if (classrooms.Any(item => item.TeacherUserId != teacherId))
                    return (0, "只能从自己负责的课堂导入名单");
            }
        }
        var targetClassIds = await db.Classes.AsNoTracking()
            .Where(item => requestedClasses.Contains(item.Id) || requestedProfessions.Contains(item.ProfessionId))
            .Select(item => item.Id).ToArrayAsync(ct);
        var candidates = new List<(string UserId, string PersonId, string? ClassId, string ClassName)>();
        if (targetClassIds.Length > 0)
        {
            var byClass = await (from person in db.People.AsNoTracking()
                                 join user in db.Users.AsNoTracking() on person.UserId equals user.Id
                                 join schoolClass in db.Classes.AsNoTracking() on person.ClassId equals schoolClass.Id into classes
                                 from schoolClass in classes.DefaultIfEmpty()
                                 where person.Kind == "student" && user.Role == "Student"
                                       && person.ClassId != null && targetClassIds.Contains(person.ClassId)
                                 select new { person.UserId, person.Id, person.ClassId, ClassName = schoolClass == null ? "" : schoolClass.Name })
                .ToListAsync(ct);
            candidates.AddRange(byClass.Select(item => (item.UserId, item.Id, (string?)item.ClassId, item.ClassName)));
        }
        if (requestedClassrooms.Length > 0)
        {
            var byClassroom = await (from enrollment in db.ClassroomStudents.AsNoTracking()
                                     join person in db.People.AsNoTracking() on enrollment.StudentUserId equals person.UserId
                                     join user in db.Users.AsNoTracking() on person.UserId equals user.Id
                                     join schoolClass in db.Classes.AsNoTracking() on person.ClassId equals schoolClass.Id into classes
                                     from schoolClass in classes.DefaultIfEmpty()
                                     where requestedClassrooms.Contains(enrollment.ClassroomId)
                                           && person.Kind == "student" && user.Role == "Student"
                                     select new { person.UserId, person.Id, person.ClassId, ClassName = schoolClass == null ? "" : schoolClass.Name })
                .ToListAsync(ct);
            candidates.AddRange(byClassroom.Select(item => (item.UserId, item.Id, (string?)item.ClassId, item.ClassName)));
        }
        if (requestedStudents.Length > 0)
        {
            var byStudent = await (from person in db.People.AsNoTracking()
                                   join user in db.Users.AsNoTracking() on person.UserId equals user.Id
                                   join schoolClass in db.Classes.AsNoTracking() on person.ClassId equals schoolClass.Id into classes
                                   from schoolClass in classes.DefaultIfEmpty()
                                   where requestedStudents.Contains(person.UserId) && person.Kind == "student" && user.Role == "Student"
                                   select new { person.UserId, person.Id, person.ClassId, ClassName = schoolClass == null ? "" : schoolClass.Name })
                .ToListAsync(ct);
            if (byStudent.Count != requestedStudents.Length) return (0, "部分学生不存在或不是学生账号");
            candidates.AddRange(byStudent.Select(item => (item.UserId, item.Id, (string?)item.ClassId, item.ClassName)));
        }
        if (candidates.Count == 0) return (0, null);
        var existing = (await db.ExamStudents.Where(item => item.ExamId == examId)
            .Select(item => item.StudentUserId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var candidate in candidates.DistinctBy(item => item.UserId))
        {
            if (!existing.Add(candidate.UserId)) continue;
            if (existing.Count > 5000) return (0, "单场考试最多 5000 名学生");
            db.ExamStudents.Add(new ExamStudent
            {
                ExamId = examId, StudentUserId = candidate.UserId, StudentPersonId = candidate.PersonId,
                ClassId = candidate.ClassId, ClassName = candidate.ClassName
            });
            added++;
        }
        return (added, null);
    }

    static string? CurrentUserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);

    static string StatusMessage(string status) => status switch
    {
        "open" => "考试已开始，不能重复开考",
        "closed" => "考试已结束，不能再次开考",
        _ => "考试当前状态不允许此操作"
    };

    static string Csv(string value)
    {
        if (value.TrimStart() is [('=' or '+' or '-' or '@'), ..]) value = "'" + value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    static IResult Bad(string message) => Results.BadRequest(new { message });
    static IResult Conflict(string message) => Results.Conflict(new { message });
    static IResult NotFound(string message = "记录不存在") => Results.NotFound(new { message });
}
