using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class ExamStudentEndpoints
{
    public static void MapExamStudentEndpoints(this RouteGroupBuilder all)
    {
        var learners = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Student"));
        learners.MapGet("/exam-hall", ListHall);
        learners.MapGet("/exam-hall/{id}", GetHallExam);
        learners.MapPost("/exam-hall/{id}/start", StartExam);
        learners.MapPost("/exam-hall/{id}/answers", SaveAnswers);
        learners.MapPost("/exam-hall/{id}/submit", SubmitExam);
        learners.MapGet("/exam-records", ListRecords);
        learners.MapGet("/exam-records/{attemptId}", GetRecord);
        learners.MapExamPracticalStudent();
    }

    static string CurrentUserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    static async Task<(TrainingExam? Exam, ExamStudent? Enrollment)> LoadAssignment(
        SchoolDbContext db, string userId, string examId, CancellationToken ct)
    {
        var assignment = await (from enrollment in db.ExamStudents.AsNoTracking()
                                join exam in db.TrainingExams.AsNoTracking() on enrollment.ExamId equals exam.Id
                                where enrollment.StudentUserId == userId && exam.Id == examId
                                select new { Exam = exam, Enrollment = enrollment }).FirstOrDefaultAsync(ct);
        return assignment == null ? (null, null) : (assignment.Exam, assignment.Enrollment);
    }

    static async Task<IResult> ListHall(HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx);
        var now = DateTime.UtcNow;
        var assignments = await (from enrollment in db.ExamStudents.AsNoTracking()
                                 join exam in db.TrainingExams.AsNoTracking() on enrollment.ExamId equals exam.Id
                                 where enrollment.StudentUserId == userId
                                 select new { Exam = exam, enrollment.DeploymentStatus, enrollment.DeploymentMessage }).ToListAsync(ct);
        var examIds = assignments.Select(item => item.Exam.Id).ToArray();
        var attempts = await db.ExamAttempts.AsNoTracking().Where(item => item.UserId == userId && examIds.Contains(item.ExamId)).ToListAsync(ct);
        var paperIds = assignments.Select(item => item.Exam.PaperId).Distinct().ToArray();
        var paperQuestions = await db.ExamPaperQuestions.AsNoTracking().Where(item => paperIds.Contains(item.PaperId))
            .Select(item => new { item.PaperId, item.Score, item.PracticalNodeId }).ToListAsync(ct);
        var papers = await db.ExamPapers.AsNoTracking().Where(item => paperIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Name }).ToDictionaryAsync(item => item.Id, item => item.Name, ct);
        var attemptByExam = attempts.ToDictionary(item => item.ExamId, StringComparer.Ordinal);
        var search = request.Query["search"].ToString().Trim();
        var items = assignments
            .Where(item => search.Length == 0 || item.Exam.Name.Contains(search))
            .Select(item =>
            {
                var exam = item.Exam;
                var questions = paperQuestions.Where(question => question.PaperId == exam.PaperId).ToArray();
                var attempt = attemptByExam.GetValueOrDefault(exam.Id);
                var status = ExamLogic.EffectiveStatus(exam, now);
                var requiresEnvironment = questions.Any(question => question.PracticalNodeId != "");
                var canStart = status == "open"
                               && (attempt == null || (attempt.Status == "inProgress" && attempt.Deadline > now))
                               && (item.DeploymentStatus == "deployed" || !requiresEnvironment);
                return new
                {
                    exam.Id, exam.Name, exam.Description, exam.StartAt, exam.EndAt, exam.DurationMinutes,
                    status, storedStatus = exam.Status,
                    paperName = papers.GetValueOrDefault(exam.PaperId, ""),
                    questionCount = questions.Length,
                    totalScore = questions.Sum(question => question.Score),
                    deploymentStatus = requiresEnvironment ? item.DeploymentStatus : "notRequired",
                    deploymentMessage = item.DeploymentMessage,
                    attemptId = attempt?.Id,
                    attemptStatus = attempt?.Status ?? "notStarted",
                    startedAt = attempt?.StartedAt,
                    deadline = attempt?.Deadline,
                    submittedAt = attempt?.SubmittedAt,
                    autoSubmitted = attempt?.AutoSubmitted ?? false,
                    objectiveScore = attempt?.ObjectiveScore,
                    practicalScore = attempt?.PracticalScore,
                    attemptScore = attempt?.TotalScore,
                    maxScore = attempt?.MaxScore,
                    remainingSeconds = attempt is { Status: "inProgress" }
                        ? Math.Max(0, (int)Math.Ceiling((attempt.Deadline - now).TotalSeconds))
                        : 0,
                    canStart,
                    canReview = status == "closed" && attempt is { Status: "graded" }
                };
            })
            .OrderBy(item => item.attemptStatus == "inProgress" ? 0 : item.status == "open" ? 1 : item.status == "scheduled" ? 2 : 3)
            .ThenByDescending(item => item.EndAt)
            .ToList();
        return Results.Ok(new { items });
    }

    static async Task<IResult> GetHallExam(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx);
        var (exam, enrollment) = await LoadAssignment(db, userId, id, ct);
        if (exam == null || enrollment == null) return NotFound("考试不存在或未分配给你");
        var now = DateTime.UtcNow;
        var attempt = await db.ExamAttempts.AsNoTracking().FirstOrDefaultAsync(item => item.ExamId == id && item.UserId == userId, ct);
        var questions = await db.ExamPaperQuestions.AsNoTracking().Where(item => item.PaperId == exam.PaperId)
            .OrderBy(item => item.SortOrder).ToListAsync(ct);
        var paper = await db.ExamPapers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == exam.PaperId, ct);
        var requiresEnvironment = questions.Any(question => question.PracticalNodeId != "");
        object? attemptState = null;
        object? questionList = null;
        if (attempt != null)
        {
            attemptState = new
            {
                attempt.Id, attempt.Status, attempt.StartedAt, attempt.Deadline, attempt.SubmittedAt,
                attempt.AutoSubmitted, attempt.ObjectiveScore, attempt.PracticalScore, attempt.TotalScore,
                attempt.MaxScore, attempt.GradedAt,
                remainingSeconds = attempt.Status == "inProgress"
                    ? Math.Max(0, (int)Math.Ceiling((attempt.Deadline - now).TotalSeconds))
                    : 0
            };
            if (attempt.Status == "inProgress" && attempt.Deadline > now && ExamLogic.EffectiveStatus(exam, now) == "open")
            {
                var answers = await db.ExamAnswers.AsNoTracking().Where(item => item.AttemptId == attempt.Id)
                    .ToDictionaryAsync(item => item.PaperQuestionId, StringComparer.Ordinal, ct);
                var savedProjects = (await db.ExamPracticalEnvironments.AsNoTracking()
                    .Where(item => item.EnrollmentId == enrollment.Id && item.HasWork).Select(item => item.PaperQuestionId)
                    .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
                questionList = questions.Select(question =>
                {
                    var answer = answers.GetValueOrDefault(question.Id);
                    return new
                    {
                        paperQuestionId = question.Id,
                        question.Type,
                        question.Stem,
                        options = ExamLogic.DeserializeOptions(question.OptionsJson),
                        question.Score,
                        question.SortOrder,
                        automaticPractical = question.PracticalNodeId != "",
                        practicalSaved = savedProjects.Contains(question.Id),
                        question.PracticalResources,
                        selected = answer == null ? [] : ExamLogic.DeserializeSelection(answer.SelectedJson),
                        textAnswer = answer?.TextAnswer ?? ""
                    };
                }).ToList();
            }
        }
        return Results.Ok(new
        {
            exam.Id, exam.Name, exam.Description, exam.StartAt, exam.EndAt, exam.DurationMinutes,
            status = ExamLogic.EffectiveStatus(exam, now), storedStatus = exam.Status,
            paperName = paper?.Name ?? "",
            questionCount = questions.Count,
            totalScore = questions.Sum(item => item.Score),
            serverNow = now,
            deploymentStatus = requiresEnvironment ? enrollment.DeploymentStatus : "notRequired",
            deploymentMessage = enrollment.DeploymentMessage,
            canStart = ExamLogic.EffectiveStatus(exam, now) == "open" && attempt == null
                && (enrollment.DeploymentStatus == "deployed" || !requiresEnvironment),
            attempt = attemptState,
            questions = questionList
        });
    }

    static async Task<IResult> StartExam(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var userId = CurrentUserId(ctx);
        var (exam, enrollment) = await LoadAssignment(db, userId, id, ct);
        if (exam == null || enrollment == null) return NotFound("考试不存在或未分配给你");
        var now = DateTime.UtcNow;
        var status = ExamLogic.EffectiveStatus(exam, now);
        if (status != "open") return Conflict(status == "closed" ? "考试已结束，不能再进入" : "考试尚未开始");
        var attempt = await db.ExamAttempts.FirstOrDefaultAsync(item => item.ExamId == id && item.UserId == userId, ct);
        if (attempt != null)
        {
            if (attempt.Status != "inProgress") return Conflict("本次考试已提交，不能重复作答");
            if (now >= attempt.Deadline)
            {
                await FinalizeAttemptAsync(db, attempt, attempt.Deadline, autoSubmitted: true, now, ct);
                Audit.Add(db, ctx, "超时自动提交", "考试", exam.Name);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Conflict("考试时间已到，系统已自动提交本次作答");
            }
            return Results.Ok(new
            {
                attemptId = attempt.Id, attempt.Status, attempt.StartedAt, attempt.Deadline,
                remainingSeconds = Math.Max(0, (int)Math.Ceiling((attempt.Deadline - now).TotalSeconds))
            });
        }
        var questions = await db.ExamPaperQuestions.AsNoTracking().Where(item => item.PaperId == exam.PaperId).ToListAsync(ct);
        if (questions.Count == 0) return Conflict("试卷还没有题目，请联系教师");
        var projectIds = questions.Where(question => question.PracticalNodeId != "").Select(question => question.Id).ToArray();
        if (projectIds.Length > 0 && (enrollment.DeploymentStatus != "deployed" ||
            await db.ExamPracticalEnvironments.CountAsync(item => item.EnrollmentId == enrollment.Id && item.DeploymentStatus == "deployed"
                && projectIds.Contains(item.PaperQuestionId), ct)
                != projectIds.Length))
            return Conflict("实操环境尚未全部部署，请联系教师。");
        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(item => item.UserId == userId && item.Kind == "student", ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, ct);
        if (person == null || user == null) return Conflict("学生档案不存在，请联系管理员");
        var plannedDeadline = now.AddMinutes(exam.DurationMinutes);
        var deadline = plannedDeadline < exam.EndAt ? plannedDeadline : exam.EndAt;
        if (deadline <= now) return Conflict("考试已结束，不能再进入");
        attempt = new ExamAttempt
        {
            ExamId = id, UserId = userId, StudentName = person.Name, StudentUserName = user.UserName,
            StudentNumber = person.Number, ClassName = enrollment.ClassName,
            StartedAt = now, Deadline = deadline, MaxScore = questions.Sum(item => item.Score)
        };
        db.ExamAttempts.Add(attempt);
        Audit.Add(db, ctx, "开始考试", "考试", exam.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new
        {
            attemptId = attempt.Id, attempt.Status, attempt.StartedAt, attempt.Deadline,
            remainingSeconds = Math.Max(0, (int)Math.Ceiling((attempt.Deadline - now).TotalSeconds))
        });
    }

    static async Task<IResult> SaveAnswers(string id, ExamSaveAnswersInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var userId = CurrentUserId(ctx);
        var (exam, _) = await LoadAssignment(db, userId, id, ct);
        if (exam == null) return NotFound("考试不存在或未分配给你");
        var attempt = await db.ExamAttempts.FirstOrDefaultAsync(item => item.ExamId == id && item.UserId == userId, ct);
        if (attempt == null) return Conflict("请先开始考试");
        if (attempt.Status != "inProgress") return Conflict("本次考试已提交，不能修改答案");
        if (input.LoadedAt is not DateTime loadedAt) return Bad("请求缺少作答基线，请刷新后重试");
        if (await db.ExamAnswers.AnyAsync(item => item.AttemptId == attempt.Id && item.AnsweredAt > loadedAt, ct))
            return Concurrency.Conflict("考试答案");
        var now = DateTime.UtcNow;
        var deadline = SubmissionDeadline(exam, attempt);
        if (now < exam.StartAt && now < deadline) return Conflict("考试尚未开始");
        if (now >= deadline)
        {
            await FinalizeAttemptAsync(db, attempt, deadline, autoSubmitted: true, now, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Conflict("考试时间已到，系统已自动提交本次作答");
        }
        var (saved, error) = await StoreAnswersAsync(db, exam, attempt, input.Answers, now, ct);
        if (error != null) return Bad(error);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { saved, serverNow = now });
    }

    static async Task<IResult> SubmitExam(string id, ExamSubmitInput? input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var userId = CurrentUserId(ctx);
        var (exam, _) = await LoadAssignment(db, userId, id, ct);
        if (exam == null) return NotFound("考试不存在或未分配给你");
        var attempt = await db.ExamAttempts.FirstOrDefaultAsync(item => item.ExamId == id && item.UserId == userId, ct);
        if (attempt == null) return Conflict("请先开始考试");
        if (attempt.Status != "inProgress") return Conflict("本次考试已提交，不能重复提交");
        var now = DateTime.UtcNow;
        var deadline = SubmissionDeadline(exam, attempt);
        var autoSubmitted = now >= deadline;
        if (!autoSubmitted && now < exam.StartAt) return Conflict("考试尚未开始");
        if (input?.Answers is { Length: > 0 })
        {
            if (input.LoadedAt is not DateTime loadedAt) return Bad("请求缺少作答基线，请刷新后重试");
            if (await db.ExamAnswers.AnyAsync(item => item.AttemptId == attempt.Id && item.AnsweredAt > loadedAt, ct))
                return Concurrency.Conflict("考试答案");
            var (_, error) = await StoreAnswersAsync(db, exam, attempt, input.Answers, now, ct);
            if (error != null) return Bad(error);
            await db.SaveChangesAsync(ct);
        }
        await FinalizeAttemptAsync(db, attempt, autoSubmitted ? deadline : now, autoSubmitted, now, ct);
        Audit.Add(db, ctx, autoSubmitted ? "超时自动提交" : "提交考试", "考试", exam.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new
        {
            attemptId = attempt.Id, attempt.Status, attempt.ObjectiveScore, attempt.PracticalScore,
            attempt.TotalScore, attempt.MaxScore, attempt.AutoSubmitted,
            pendingPractical = attempt.Status == "submitted"
        });
    }

    static async Task<(int Saved, string? Error)> StoreAnswersAsync(
        SchoolDbContext db, TrainingExam exam, ExamAttempt attempt, ExamSubmitAnswer[]? inputs, DateTime now, CancellationToken ct)
    {
        if (inputs == null || inputs.Length == 0) return (0, null);
        if (inputs.Length > 500) return (0, "单次保存的答案过多");
        var questions = await db.ExamPaperQuestions.AsNoTracking().Where(item => item.PaperId == exam.PaperId)
            .ToDictionaryAsync(item => item.Id, StringComparer.Ordinal, ct);
        var existing = await db.ExamAnswers.Where(item => item.AttemptId == attempt.Id)
            .ToDictionaryAsync(item => item.PaperQuestionId, StringComparer.Ordinal, ct);
        var saved = 0;
        foreach (var item in inputs.Where(item => item != null).GroupBy(item => item.PaperQuestionId).Select(group => group.Last()))
        {
            if (!questions.TryGetValue(item.PaperQuestionId, out var question)) return (0, "答案对应的题目不存在，请刷新后重试");
            if (question.PracticalNodeId != "") continue;
            var selected = ExamLogic.NormalizeSelection(item.Selected);
            var text = (item.Text ?? "").Trim();
            if (question.Type == "practical")
            {
                if (text.Length > 5000) return (0, "实操题作答不能超过 5000 个字符");
                selected = [];
            }
            else
            {
                text = "";
                var optionCount = ExamLogic.DeserializeOptions(question.OptionsJson).Length;
                if (selected.Any(index => index < 0 || index >= optionCount)) return (0, "答案选项无效，请刷新后重新选择");
            }
            if (!existing.TryGetValue(item.PaperQuestionId, out var answer))
            {
                answer = new ExamAnswer { AttemptId = attempt.Id, PaperQuestionId = item.PaperQuestionId };
                db.ExamAnswers.Add(answer);
                existing[item.PaperQuestionId] = answer;
            }
            answer.SelectedJson = System.Text.Json.JsonSerializer.Serialize(selected);
            answer.TextAnswer = text;
            answer.AnsweredAt = now;
            saved++;
        }
        return (saved, null);
    }

    internal static DateTime SubmissionDeadline(TrainingExam exam, ExamAttempt attempt)
    {
        var deadline = attempt.Deadline < exam.EndAt ? attempt.Deadline : exam.EndAt;
        return exam.Status == "closed" && exam.UpdatedAt < deadline ? exam.UpdatedAt : deadline;
    }

    // Callers serialize writes in a transaction before reading the attempt and its answers.
    internal static async Task FinalizeAttemptAsync(
        SchoolDbContext db, ExamAttempt attempt, DateTime submittedAt, bool autoSubmitted, DateTime now, CancellationToken ct)
    {
        var exam = await db.TrainingExams.AsNoTracking().FirstAsync(item => item.Id == attempt.ExamId, ct);
        var questions = await db.ExamPaperQuestions.AsNoTracking().Where(item => item.PaperId == exam.PaperId).ToListAsync(ct);
        var answers = await db.ExamAnswers.Where(item => item.AttemptId == attempt.Id).ToListAsync(ct);
        var byQuestion = answers.ToDictionary(item => item.PaperQuestionId, StringComparer.Ordinal);
        var objectiveScore = 0;
        var practicalScore = 0;
        var pendingManual = false;
        var environments = await (from work in db.ExamPracticalEnvironments.AsNoTracking()
                                  join enrollment in db.ExamStudents.AsNoTracking() on work.EnrollmentId equals enrollment.Id
                                  where enrollment.ExamId == attempt.ExamId && enrollment.StudentUserId == attempt.UserId
                                  select work).ToDictionaryAsync(item => item.PaperQuestionId, ct);
        foreach (var question in questions)
        {
            var answer = byQuestion.GetValueOrDefault(question.Id);
            if (!ExamLogic.IsObjective(question.Type))
            {
                if (question.PracticalNodeId == "")
                {
                    pendingManual = true;
                    continue;
                }
                if (answer == null)
                {
                    answer = new ExamAnswer { AttemptId = attempt.Id, PaperQuestionId = question.Id };
                    db.ExamAnswers.Add(answer);
                }
                var work = environments.GetValueOrDefault(question.Id);
                var rules = ClassroomCaseScoring.Rubric(question.PracticalRubricJson);
                var details = ClassroomCaseScoring.Evaluate(rules, work is { HasWork: true }
                    ? ClassroomCaseScoring.Devices(work.ConfigurationJson) : []);
                var sum = details.Sum(item => item.Score);
                var earned = details.Where(item => item.Matched).Sum(item => item.Score);
                answer.ScoreAwarded = sum == 0 ? 0 : (int)Math.Round(earned * (decimal)question.Score / sum, MidpointRounding.AwayFromZero);
                answer.PracticalDetailsJson = ClassroomCaseScoring.Write(details);
                answer.AnsweredAt = work is { HasWork: true } ? submittedAt : null;
                practicalScore += answer.ScoreAwarded.Value;
                continue;
            }
            var selected = answer == null ? [] : ExamLogic.DeserializeSelection(answer.SelectedJson);
            var correct = ExamLogic.IsCorrect(question.AnswerJson, selected);
            if (answer != null) answer.ScoreAwarded = correct ? question.Score : 0;
            if (correct) objectiveScore += question.Score;
        }
        attempt.ObjectiveScore = objectiveScore;
        attempt.PracticalScore = practicalScore;
        attempt.TotalScore = objectiveScore + practicalScore;
        attempt.MaxScore = questions.Sum(item => item.Score);
        attempt.Status = pendingManual ? "submitted" : "graded";
        attempt.SubmittedAt = submittedAt;
        attempt.AutoSubmitted = autoSubmitted;
        attempt.GradedAt = pendingManual ? null : now;
        attempt.GradedByUserId = "";
    }

    static async Task<IResult> ListRecords(HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx);
        var query = from attempt in db.ExamAttempts.AsNoTracking()
                    join exam in db.TrainingExams.AsNoTracking() on attempt.ExamId equals exam.Id
                    where attempt.UserId == userId && attempt.Status != "inProgress"
                    select new { attempt, exam };
        var now = DateTime.UtcNow;
        var search = request.Query["search"].ToString().Trim();
        if (search.Length > 0) query = query.Where(item => item.exam.Name.Contains(search));
        var (page, size) = Validation.Page(request);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(item => item.attempt.SubmittedAt).ThenByDescending(item => item.attempt.StartedAt)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var paperIds = rows.Select(item => item.exam.PaperId).Distinct().ToArray();
        var papers = await db.ExamPapers.AsNoTracking().Where(item => paperIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Name }).ToDictionaryAsync(item => item.Id, item => item.Name, ct);
        var questionCounts = await db.ExamPaperQuestions.AsNoTracking().Where(item => paperIds.Contains(item.PaperId))
            .GroupBy(item => item.PaperId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        return Results.Ok(new
        {
            total, page, pageSize = size,
            items = rows.Select(item => new
            {
                attemptId = item.attempt.Id,
                examId = item.exam.Id,
                examName = item.exam.Name,
                paperName = papers.GetValueOrDefault(item.exam.PaperId, ""),
                questionCount = questionCounts.GetValueOrDefault(item.exam.PaperId),
                item.attempt.Status,
                item.attempt.StartedAt,
                item.attempt.SubmittedAt,
                item.attempt.AutoSubmitted,
                item.attempt.ObjectiveScore,
                item.attempt.PracticalScore,
                item.attempt.TotalScore,
                item.attempt.MaxScore,
                item.attempt.GradedAt,
                canReview = item.attempt.Status == "graded" && ExamLogic.EffectiveStatus(item.exam, now) == "closed"
            })
        });
    }

    static async Task<IResult> GetRecord(string attemptId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx);
        var attempt = await db.ExamAttempts.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attemptId && item.UserId == userId, ct);
        if (attempt == null) return NotFound("考试记录不存在");
        if (attempt.Status == "inProgress") return Conflict("考试尚未提交，请先完成考试");
        var exam = await db.TrainingExams.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attempt.ExamId, ct);
        if (exam == null) return NotFound("考试不存在");
        if (attempt.Status != "graded" || ExamLogic.EffectiveStatus(exam, DateTime.UtcNow) != "closed")
            return Conflict("考试结束并完成评分后开放答题回顾");
        return Results.Ok(await ExamReview.BuildAsync(db, exam, attempt, ct));
    }

    static IResult Bad(string message) => Results.BadRequest(new { message });
    static IResult Conflict(string message) => Results.Conflict(new { message });
    static IResult NotFound(string message = "记录不存在") => Results.NotFound(new { message });
}
