using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class CampEndpoints
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapCampEndpoints(this RouteGroupBuilder all)
    {
        var reader = all.MapGroup("");
        var admin = all.MapGroup("").RequireAuthorization("Admin");
        var staff = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Admin", "Teacher"));
        var learners = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Student"));

        reader.MapGet("/camp/positions", ListPositions);
        reader.MapGet("/camp/positions/{id}", GetPosition);
        reader.MapGet("/camp/positions/{id}/environment", PositionEnvironment);
        reader.MapGet("/camp/attempts/{attemptId}/report", Report);
        reader.MapGet("/camp/attempts/{attemptId}/record", Record);
        admin.MapPost("/camp/positions", CreatePosition);
        admin.MapPut("/camp/positions/{id}", UpdatePosition);
        admin.MapPost("/camp/positions/{id}/publish", PublishPosition);
        admin.MapDelete("/camp/positions/{id}", DeletePosition);

        learners.MapPost("/camp/positions/{id}/start", StartPosition);
        learners.MapPost("/camp/positions/{id}/answers", SaveAnswers);
        learners.MapPost("/camp/positions/{id}/submit", Submit);
        learners.MapGet("/camp/history", History);
        learners.MapGet("/camp/positions/{id}/practical/{questionId}", GetPracticalProject);
        learners.MapPut("/camp/positions/{id}/practical/{questionId}/devices/{deviceId}", SavePracticalDevice);

        staff.MapGet("/camp/reports", ListReports);
        staff.MapGet("/camp/records", ListRecords);
        staff.MapGet("/camp/monitor", Monitor);
        staff.MapPost("/camp/monitor/{attemptId}/restart", RestartEnvironment);
        staff.MapGet("/camp/monitor/{attemptId}/environment", MonitorEnvironment);
    }

    static async Task<IResult> ListPositions(HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var isAdmin = ctx.User.IsInRole("Admin");
        var search = request.Query["search"].ToString().Trim();
        var query = db.JobPositions.AsNoTracking();
        if (!isAdmin)
        {
            var learnerId = CurrentUserId(ctx);
            var activeIds = await db.JobAttempts.AsNoTracking()
                .Where(item => item.UserId == learnerId && item.Status == "inProgress")
                .Select(item => item.PositionId).ToListAsync(ct);
            query = query.Where(item => item.Published || activeIds.Contains(item.Id));
        }
        if (search.Length > 0) query = query.Where(item => item.Name.Contains(search) || item.Description.Contains(search));
        var positions = await query.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Name).Take(500).ToListAsync(ct);
        var ids = positions.Select(item => item.Id).ToArray();
        var stats = await db.JobQuestions.AsNoTracking().Where(item => ids.Contains(item.PositionId))
            .Select(item => new { item.PositionId, item.ContentVersion, item.Type, item.Score }).ToListAsync(ct);
        var skillStats = await db.JobSkills.AsNoTracking().Where(item => ids.Contains(item.PositionId))
            .Select(item => new { item.PositionId, item.ContentVersion }).ToListAsync(ct);
        if (isAdmin)
        {
            var attemptCounts = await db.JobAttempts.AsNoTracking().Where(item => ids.Contains(item.PositionId))
                .GroupBy(item => item.PositionId)
                .Select(group => new { group.Key, Count = group.Select(item => item.UserId).Distinct().Count() })
                .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
            return Results.Ok(new
            {
                items = positions.Select(item =>
                {
                    var own = stats.Where(stat => stat.PositionId == item.Id && stat.ContentVersion == item.ContentVersion).ToArray();
                    return new
                    {
                        item.Id, item.Name, item.Description, item.DurationMinutes, item.StartupNodeId, item.Published, item.CreatedAt, item.UpdatedAt,
                        skillCount = skillStats.Count(skill => skill.PositionId == item.Id && skill.ContentVersion == item.ContentVersion),
                        questionCount = own.Length,
                        practicalCount = own.Count(question => question.Type == "practical"),
                        maxScore = own.Sum(question => question.Score),
                        attemptCount = attemptCounts.GetValueOrDefault(item.Id)
                    };
                })
            });
        }
        var userId = CurrentUserId(ctx);
        var now = DateTime.UtcNow;
        var attempts = await db.JobAttempts.AsNoTracking().Where(item => item.UserId == userId && ids.Contains(item.PositionId)).ToListAsync(ct);
        var byPosition = attempts.GroupBy(item => item.PositionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => PreferredAttempt(group)!, StringComparer.Ordinal);
        var completedCounts = attempts.Where(item => item.Status == "graded")
            .GroupBy(item => item.PositionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return Results.Ok(new
        {
            items = positions.Select(position =>
            {
                var attempt = byPosition.GetValueOrDefault(position.Id);
                var version = attempt is { Status: "inProgress" } ? attempt.ContentVersion : position.ContentVersion;
                var own = stats.Where(stat => stat.PositionId == position.Id && stat.ContentVersion == version).ToArray();
                var remaining = attempt is { Status: "inProgress" }
                    ? Math.Max(0, (int)Math.Ceiling((attempt.Deadline - now).TotalSeconds)) : 0;
                return new
                {
                    position.Id, position.Name, position.Description, position.DurationMinutes, position.StartupNodeId, position.Published,
                    skillCount = skillStats.Count(skill => skill.PositionId == position.Id && skill.ContentVersion == version),
                    questionCount = own.Length,
                    practicalCount = own.Count(question => question.Type == "practical"),
                    maxScore = own.Sum(question => question.Score),
                    completedCount = completedCounts.GetValueOrDefault(position.Id),
                    attempt = attempt == null ? null : new
                    {
                        attempt.Id, attempt.Status, attempt.StartedAt, attempt.Deadline, attempt.SubmittedAt,
                        attempt.AutoSubmitted, attempt.TotalScore, attempt.MaxScore, remainingSeconds = remaining
                    },
                    canStart = own.Length > 0 && (attempt == null || attempt.Status is "inProgress" or "graded")
                };
            })
        });
    }

    static async Task<IResult> GetPosition(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var position = await db.JobPositions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (position == null) return NotFound("岗位不存在");
        if (!ctx.User.IsInRole("Admin") && !position.Published)
        {
            var viewerId = CurrentUserId(ctx);
            if (!await db.JobAttempts.AsNoTracking().AnyAsync(item => item.PositionId == id && item.UserId == viewerId, ct))
                return NotFound("岗位不存在");
        }
        var skills = await db.JobSkills.AsNoTracking().Where(item => item.PositionId == id).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var questions = await db.JobQuestions.AsNoTracking().Where(item => item.PositionId == id).OrderBy(item => item.SortOrder).ToListAsync(ct);
        if (ctx.User.IsInRole("Admin"))
        {
            var currentSkills = skills.Where(item => item.ContentVersion == position.ContentVersion).ToList();
            var currentQuestions = questions.Where(item => item.ContentVersion == position.ContentVersion).ToList();
            var indexById = currentSkills.Select((skill, index) => new { skill.Id, Index = index }).ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
            var attemptCount = await db.JobAttempts.Where(item => item.PositionId == id).Select(item => item.UserId).Distinct().CountAsync(ct);
            return Results.Ok(new
            {
                position.Id, position.Name, position.Description, position.DurationMinutes, position.StartupNodeId, position.Published,
                position.CreatedAt, position.UpdatedAt, attemptCount,
                canEditStructure = true,
                questionCount = currentQuestions.Count,
                practicalCount = currentQuestions.Count(question => question.Type == "practical"),
                maxScore = currentQuestions.Sum(question => question.Score),
                skills = currentSkills.Select(skill => new { skill.Id, skill.Name, skill.SubName, skill.Ability, skill.SortOrder }),
                questions = currentQuestions.Select(question => new
                {
                    question.Id, question.Type, question.Stem,
                    options = ExamLogic.DeserializeOptions(question.OptionsJson),
                    answers = ExamLogic.DeserializeAnswers(question.AnswerJson),
                    question.Analysis, question.Score, question.SortOrder,
                    skillIndex = question.SkillId == null ? -1 : indexById.GetValueOrDefault(question.SkillId, -1),
                    rubric = CampLogic.DeserializeRubric(question.RubricJson),
                    practicalRubric = ClassroomCaseScoring.Rubric(question.PracticalRubricJson)
                })
            });
        }
        var userId = CurrentUserId(ctx);
        var ownAttempts = await db.JobAttempts.AsNoTracking().Where(item => item.PositionId == id && item.UserId == userId).ToListAsync(ct);
        var attempt = PreferredAttempt(ownAttempts);
        var now = DateTime.UtcNow;
        var version = attempt is { Status: "inProgress" } ? attempt.ContentVersion : position.ContentVersion;
        var versionSkills = skills.Where(item => item.ContentVersion == version).ToList();
        var versionQuestions = questions.Where(item => item.ContentVersion == version).ToList();
        object? attemptState = attempt == null ? null : new
        {
            attempt.Id, attempt.Status, attempt.StartedAt, attempt.Deadline, attempt.SubmittedAt,
            attempt.AutoSubmitted, attempt.ObjectiveScore, attempt.PracticalScore, attempt.TotalScore,
            attempt.MaxScore, attempt.GradedAt,
            remainingSeconds = attempt.Status == "inProgress"
                ? Math.Max(0, (int)Math.Ceiling((attempt.Deadline - now).TotalSeconds)) : 0
        };
        object? questionRows = null;
        if (attempt is { Status: "inProgress" })
        {
            var projects = ReadProjects(attempt);
            var answers = await db.JobAnswers.AsNoTracking().Where(item => item.AttemptId == attempt.Id)
                .ToDictionaryAsync(item => item.QuestionId, StringComparer.Ordinal, ct);
            var skillNames = versionSkills.ToDictionary(skill => skill.Id, skill => skill.Name, StringComparer.Ordinal);
            questionRows = versionQuestions.Select(question =>
            {
                var answer = answers.GetValueOrDefault(question.Id);
                return new
                {
                    question.Id, question.Type, question.Stem,
                    options = ExamLogic.DeserializeOptions(question.OptionsJson),
                    question.Score, question.SortOrder,
                    skillName = question.SkillId == null ? "" : skillNames.GetValueOrDefault(question.SkillId, ""),
                    selected = answer == null ? [] : ExamLogic.DeserializeSelection(answer.SelectedJson),
                    textAnswer = answer?.TextAnswer ?? "",
                    automaticPractical = question.PracticalRubricJson != "[]",
                    hasWork = projects.GetValueOrDefault(question.Id)?.HasWork ?? false
                };
            }).ToList();
        }
        return Results.Ok(new
        {
            position.Id, position.Name, position.Description, position.DurationMinutes, position.StartupNodeId,
            questionCount = versionQuestions.Count,
            practicalCount = versionQuestions.Count(question => question.Type == "practical"),
            maxScore = versionQuestions.Sum(question => question.Score),
            skillCount = versionSkills.Count,
            completedCount = ownAttempts.Count(item => item.Status == "graded"),
            attempt = attemptState,
            serverNow = now,
            canStart = versionQuestions.Count > 0 && (attempt == null || attempt.Status is "inProgress" or "graded"),
            canReview = attempt is { Status: "graded" } && (!position.Published || ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Teacher")),
            questions = questionRows
        });
    }

    static async Task<IResult> CreatePosition(CampPositionInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var error = CampLogic.ValidatePosition(input);
        if (error != null) return Bad(error);
        var name = input.Name.Trim();
        if (await db.JobPositions.AnyAsync(item => item.Name == name, ct)) return Conflict("岗位名称已存在");
        if (!await StartupNodeExists(db, input.StartupNodeId, ct)) return Bad("启动节点不存在");
        if (await ValidatePracticalRubrics(input, db, ct) is { } rubricError) return Bad(rubricError);
        var entity = new JobPosition
        {
            Name = name, Description = (input.Description ?? "").Trim(), DurationMinutes = input.DurationMinutes,
            StartupNodeId = string.IsNullOrWhiteSpace(input.StartupNodeId) ? null : input.StartupNodeId,
            Published = false
        };
        db.JobPositions.Add(entity);
        var skills = CampLogic.BuildSkills(entity.Id, input.Skills, entity.ContentVersion);
        db.JobSkills.AddRange(skills);
        CampLogic.AddQuestions(db, entity.Id, skills, input.Questions, entity.ContentVersion);
        Audit.Add(db, ctx, "新增岗位测评", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id });
    }

    static async Task<IResult> UpdatePosition(string id, CampPositionInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var entity = await db.JobPositions.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity == null) return NotFound("岗位不存在");
        var error = CampLogic.ValidatePosition(input);
        if (error != null) return Bad(error);
        if (input.UpdatedAt is not DateTime updatedAt) return Concurrency.MissingVersion();
        if (updatedAt != entity.UpdatedAt) return Concurrency.Conflict("岗位");
        var name = input.Name.Trim();
        if (await db.JobPositions.AnyAsync(item => item.Id != id && item.Name == name, ct)) return Conflict("岗位名称已存在");
        if (!await StartupNodeExists(db, input.StartupNodeId, ct)) return Bad("启动节点不存在");
        var skills = await db.JobSkills.Where(item => item.PositionId == id && item.ContentVersion == entity.ContentVersion).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var questions = await db.JobQuestions.Where(item => item.PositionId == id && item.ContentVersion == entity.ContentVersion).OrderBy(item => item.SortOrder).ToListAsync(ct);
        entity.Name = name;
        entity.Description = (input.Description ?? "").Trim();
        entity.DurationMinutes = input.DurationMinutes;
        entity.StartupNodeId = string.IsNullOrWhiteSpace(input.StartupNodeId) ? null : input.StartupNodeId;
        entity.UpdatedAt = DateTime.UtcNow;
        if (!CampLogic.PaperMatches(skills, questions, input))
        {
            if (await ValidatePracticalRubrics(input, db, ct) is { } rubricError) return Bad(rubricError);
            var referenced = await db.JobAttempts.AnyAsync(item => item.PositionId == id && item.ContentVersion == entity.ContentVersion, ct);
            if (referenced) entity.ContentVersion++;
            else
            {
                if (questions.Count > 0)
                {
                    db.JobQuestions.RemoveRange(questions);
                    await db.SaveChangesAsync(ct);
                }
                if (skills.Count > 0)
                {
                    db.JobSkills.RemoveRange(skills);
                    await db.SaveChangesAsync(ct);
                }
            }
            var newSkills = CampLogic.BuildSkills(id, input.Skills, entity.ContentVersion);
            db.JobSkills.AddRange(newSkills);
            CampLogic.AddQuestions(db, id, newSkills, input.Questions, entity.ContentVersion);
        }
        Audit.Add(db, ctx, "编辑岗位测评", entity.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { entity.Id });
    }

    static async Task<string?> ValidatePracticalRubrics(CampPositionInput input, SchoolDbContext db, CancellationToken ct)
    {
        foreach (var question in (input.Questions ?? []).Where(item => item.Type == "practical" && item.PracticalRubric is { Length: > 0 }))
            if (await ClassroomCaseScoring.ValidateRubric(db, input.StartupNodeId, question.PracticalRubric!, ct) is { } error)
                return $"实操题“{question.Stem}”：{error}";
        return null;
    }

    static Dictionary<string, CampPracticalProject> ReadProjects(JobAttempt attempt)
        => attempt.EnvironmentJson == "[]" ? [] : JsonSerializer.Deserialize<Dictionary<string, CampPracticalProject>>(attempt.EnvironmentJson, Json) ?? [];

    static async Task<IResult> GetPracticalProject(string id, string questionId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx);
        var attempt = await OpenAttempt(db, id, userId, ct);
        if (attempt == null || ReadProjects(attempt).GetValueOrDefault(questionId) is not { } project) return NotFound("实操项目不存在");
        if (attempt.Status != "inProgress" || attempt.Deadline <= DateTime.UtcNow) return Conflict("本次测评已结束");
        return Results.Ok(project);
    }

    static async Task<IResult> SavePracticalDevice(string id, string questionId, string deviceId, ExamDeviceUpdate input,
        HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var userId = CurrentUserId(ctx);
        var attempt = await OpenAttempt(db, id, userId, ct, tracking: true);
        if (attempt == null) return NotFound("实操项目不存在");
        if (attempt.Status != "inProgress" || attempt.Deadline <= DateTime.UtcNow) return Conflict("本次测评已结束");
        var projects = ReadProjects(attempt);
        if (!projects.TryGetValue(questionId, out var project)) return NotFound("实操项目不存在");
        if (project.Revision != input.Revision) return Conflict("配置已更新，请刷新实操环境");
        var index = Array.FindIndex(project.Devices, item => item.Id == deviceId);
        if (index < 0) return NotFound("设备不属于本实操项目");
        if (input.Configuration == null || !ClassroomCaseScoring.ValidConfiguration(project.Devices[index].Configuration, input.Configuration))
            return Bad("设备配置格式不正确");
        project.Devices[index] = project.Devices[index] with { Configuration = input.Configuration };
        project = project with { Revision = project.Revision + 1, HasWork = true };
        projects[questionId] = project;
        attempt.EnvironmentJson = ClassroomCaseScoring.Write(projects);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(project);
    }

    static async Task<IResult> DeletePosition(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.JobPositions.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity == null) return NotFound("岗位不存在");
        if (await db.JobAttempts.AnyAsync(item => item.PositionId == id, ct))
            return Conflict("岗位已有测评记录，不能删除");
        db.JobPositions.Remove(entity);
        Audit.Add(db, ctx, "删除岗位测评", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { message = "已删除" });
    }

    static async Task<IResult> PublishPosition(string id, CampPublishInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.JobPositions.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity == null) return NotFound("岗位不存在");
        entity.Published = input.Published;
        entity.UpdatedAt = DateTime.UtcNow;
        Audit.Add(db, ctx, input.Published ? "发布岗位测评" : "取消发布岗位测评", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id, entity.Published });
    }

    static async Task<IResult> StartPosition(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var userId = CurrentUserId(ctx);
        var position = await db.JobPositions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (position == null) return NotFound("岗位不存在");
        var now = DateTime.UtcNow;
        var attempt = await OpenAttempt(db, id, userId, ct, tracking: true);
        if (!position.Published && attempt == null) return Conflict("该岗位尚未发布");
        if (attempt != null)
        {
            if (now >= attempt.Deadline)
            {
                await FinalizeAttemptAsync(db, position, attempt, attempt.Deadline, autoSubmitted: true, now, ct);
                Audit.Add(db, ctx, "岗位测评超时自动提交", "岗位测评", position.Name);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Conflict("测评时间已到，系统已自动提交本次作答");
            }
            return Results.Ok(AttemptState(attempt, now));
        }
        var questions = await db.JobQuestions.AsNoTracking()
            .Where(item => item.PositionId == id && item.ContentVersion == position.ContentVersion).ToListAsync(ct);
        if (questions.Count == 0) return Conflict("该岗位还没有配置测评题，请联系管理员");
        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(item => item.UserId == userId && item.Kind == "student", ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, ct);
        if (person == null || user == null) return Conflict("学生档案不存在，请联系管理员");
        var className = person.ClassId == null ? ""
            : await db.Classes.AsNoTracking().Where(item => item.Id == person.ClassId).Select(item => item.Name).FirstOrDefaultAsync(ct) ?? "";
        var practicalQuestions = questions.Where(item => item.PracticalRubricJson != "[]").ToArray();
        var projects = new Dictionary<string, CampPracticalProject>();
        if (practicalQuestions.Length > 0)
        {
            var devices = await ClassroomCaseScoring.LoadDevices(db, position.StartupNodeId, ct);
            if (devices.Count == 0 || devices.Count > ClassroomCaseScoring.MaxDevices)
                return Conflict("岗位实操节点的模拟设备不可用");
            var available = devices.ToDictionary(device => device.Id, StringComparer.Ordinal);
            foreach (var question in practicalQuestions)
            {
                var targetIds = ClassroomCaseScoring.Rubric(question.PracticalRubricJson).Select(item => item.DeviceId).Distinct().ToArray();
                if (targetIds.Any(deviceId => !available.ContainsKey(deviceId))) return Conflict("评分设备已变更，请联系管理员");
                var snapshot = targetIds.Select(deviceId => available[deviceId]).Select(device =>
                    new CaseDeviceState(device.Id, device.Name, SimulationDeviceConfiguration.Read(device))).ToArray();
                projects.Add(question.Id, new CampPracticalProject(0, false, snapshot));
            }
        }
        var vm = await SimulatedVirtualMachines.Acquire(db, position.StartupNodeId, null, now, ct);
        attempt = new JobAttempt
        {
            PositionId = id, UserId = userId, StudentName = person.Name, StudentUserName = user.UserName,
            StudentNumber = person.Number, ClassName = className, VirtualMachineId = vm?.Id,
            StartupNodeId = string.IsNullOrWhiteSpace(position.StartupNodeId) ? null : position.StartupNodeId,
            ContentVersion = position.ContentVersion,
            EnvironmentJson = ClassroomCaseScoring.Write(projects),
            StartedAt = now, Deadline = now.AddMinutes(position.DurationMinutes), MaxScore = questions.Sum(item => item.Score)
        };
        db.JobAttempts.Add(attempt);
        Audit.Add(db, ctx, "开始岗位测评", "岗位测评", position.Name);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsActiveAttemptConflict(ex))
        {
            return Conflict("已有进行中的测评，请继续当前测评");
        }
        return Results.Ok(AttemptState(attempt, now));
    }

    static async Task<IResult> SaveAnswers(string id, CampSaveAnswersInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var userId = CurrentUserId(ctx);
        var position = await db.JobPositions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (position == null) return NotFound("岗位不存在");
        var attempt = await OpenAttempt(db, id, userId, ct, tracking: true);
        if (attempt == null) return Conflict("请先开始测评");
        if (input.LoadedAt is not DateTime loadedAt) return Bad("请求缺少作答基线，请刷新后重试");
        if (await db.JobAnswers.AnyAsync(item => item.AttemptId == attempt.Id && item.AnsweredAt > loadedAt, ct))
            return Concurrency.Conflict("测评答案");
        var now = DateTime.UtcNow;
        if (now >= attempt.Deadline)
        {
            await FinalizeAttemptAsync(db, position, attempt, attempt.Deadline, autoSubmitted: true, now, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Conflict("测评时间已到，系统已自动提交本次作答");
        }
        var (saved, error) = await StoreAnswersAsync(db, id, attempt, input.Answers, now, ct);
        if (error != null) return Bad(error);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { saved, serverNow = now });
    }

    static async Task<IResult> Submit(string id, CampSaveAnswersInput? input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var userId = CurrentUserId(ctx);
        var position = await db.JobPositions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (position == null) return NotFound("岗位不存在");
        var attempt = await OpenAttempt(db, id, userId, ct, tracking: true);
        if (attempt == null) return Conflict("请先开始测评");
        var now = DateTime.UtcNow;
        var autoSubmitted = now >= attempt.Deadline;
        if (input?.Answers is { Length: > 0 })
        {
            if (input.LoadedAt is not DateTime loadedAt) return Bad("请求缺少作答基线，请刷新后重试");
            if (await db.JobAnswers.AnyAsync(item => item.AttemptId == attempt.Id && item.AnsweredAt > loadedAt, ct))
                return Concurrency.Conflict("测评答案");
            var (_, error) = await StoreAnswersAsync(db, id, attempt, input.Answers, now, ct);
            if (error != null) return Bad(error);
            await db.SaveChangesAsync(ct);
        }
        await FinalizeAttemptAsync(db, position, attempt, autoSubmitted ? attempt.Deadline : now, autoSubmitted, now, ct);
        Audit.Add(db, ctx, "提交岗位测评", "岗位测评", position.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new
        {
            attemptId = attempt.Id, attempt.Status, attempt.ObjectiveScore, attempt.PracticalScore,
            attempt.TotalScore, attempt.MaxScore, attempt.AutoSubmitted,
            percent = ExamLogic.Percent(attempt.TotalScore, attempt.MaxScore)
        });
    }

    static async Task<IResult> History(HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx);
        var now = DateTime.UtcNow;
        var search = request.Query["search"].ToString().Trim();
        var query = from attempt in db.JobAttempts.AsNoTracking()
                    join position in db.JobPositions.AsNoTracking() on attempt.PositionId equals position.Id
                    where attempt.UserId == userId
                    select new { attempt, position };
        if (search.Length > 0) query = query.Where(item => item.position.Name.Contains(search));
        var rows = await query.OrderByDescending(item => item.attempt.StartedAt).Take(500).ToListAsync(ct);
        return Results.Ok(new
        {
            items = rows.Select(item => new
            {
                attemptId = item.attempt.Id,
                positionId = item.position.Id,
                positionName = item.position.Name,
                item.attempt.Status,
                item.attempt.StartedAt,
                item.attempt.Deadline,
                item.attempt.SubmittedAt,
                item.attempt.AutoSubmitted,
                item.attempt.TotalScore,
                item.attempt.MaxScore,
                percent = ExamLogic.Percent(item.attempt.TotalScore, item.attempt.MaxScore),
                remainingSeconds = item.attempt.Status == "inProgress"
                    ? Math.Max(0, (int)Math.Ceiling((item.attempt.Deadline - now).TotalSeconds)) : 0,
                hasReport = item.attempt.Status == "graded"
            })
        });
    }

    static async Task<IResult> ListReports(HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var search = request.Query["search"].ToString().Trim();
        var attempts = ctx.User.IsInRole("Admin")
            ? db.JobAttempts.AsNoTracking()
            : CampAccess.TeacherControlScope(db.JobAttempts.AsNoTracking(), db, CurrentUserId(ctx));
        var query = from attempt in attempts
                    join position in db.JobPositions.AsNoTracking() on attempt.PositionId equals position.Id
                    where attempt.Status == "graded"
                    select new { attempt, position };
        if (search.Length > 0) query = query.Where(item =>
            item.attempt.StudentName.Contains(search) || item.attempt.StudentNumber.Contains(search)
            || item.attempt.StudentUserName.Contains(search) || item.position.Name.Contains(search));
        var (page, size) = Validation.Page(request);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(item => item.attempt.SubmittedAt).ThenBy(item => item.attempt.StudentName)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new
        {
            total, page, pageSize = size,
            items = rows.Select(item => new
            {
                attemptId = item.attempt.Id,
                positionId = item.position.Id,
                positionName = item.position.Name,
                item.attempt.StudentName,
                item.attempt.StudentNumber,
                item.attempt.StudentUserName,
                item.attempt.ClassName,
                item.attempt.TotalScore,
                item.attempt.MaxScore,
                percent = ExamLogic.Percent(item.attempt.TotalScore, item.attempt.MaxScore),
                item.attempt.SubmittedAt,
                item.attempt.GradedAt
            })
        });
    }

    static async Task<IResult> ListRecords(HttpRequest request, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var search = request.Query["search"].ToString().Trim();
        var attempts = ctx.User.IsInRole("Admin")
            ? db.JobAttempts.AsNoTracking()
            : CampAccess.TeacherControlScope(db.JobAttempts.AsNoTracking(), db, CurrentUserId(ctx));
        var query = from attempt in attempts
                    join position in db.JobPositions.AsNoTracking() on attempt.PositionId equals position.Id
                    where attempt.Status == "graded"
                    select new { attempt, position };
        if (search.Length > 0) query = query.Where(item =>
            item.attempt.StudentName.Contains(search) || item.attempt.StudentNumber.Contains(search)
            || item.attempt.StudentUserName.Contains(search) || item.position.Name.Contains(search));
        var (page, size) = Validation.Page(request);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(item => item.attempt.SubmittedAt).ThenBy(item => item.attempt.StudentName)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new
        {
            total, page, pageSize = size,
            items = rows.Select(item => new
            {
                attemptId = item.attempt.Id,
                positionId = item.position.Id,
                positionName = item.position.Name,
                item.attempt.StudentName,
                item.attempt.StudentNumber,
                item.attempt.StudentUserName,
                item.attempt.ClassName,
                item.attempt.TotalScore,
                item.attempt.MaxScore,
                item.attempt.StartedAt,
                item.attempt.SubmittedAt,
                item.attempt.AutoSubmitted
            })
        });
    }

    static async Task<IResult> Report(string attemptId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var attempt = await db.JobAttempts.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attemptId, ct);
        if (attempt == null) return NotFound("测评记录不存在");
        if (!await CanViewAttempt(db, ctx, attempt)) return Forbidden("无权查看该测评报告");
        if (attempt.Status == "inProgress") return Conflict("测评尚未提交，暂时没有岗位报告");
        var position = await db.JobPositions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attempt.PositionId, ct);
        if (position == null) return NotFound("岗位不存在");
        var series = await db.JobAttempts.AsNoTracking()
            .Where(item => item.PositionId == attempt.PositionId && item.UserId == attempt.UserId && item.Status == "graded")
            .OrderBy(item => item.StartedAt).ThenBy(item => item.Id).ToListAsync(ct);
        var versions = series.Select(item => item.ContentVersion).Distinct().ToArray();
        var skills = await db.JobSkills.AsNoTracking().Where(item => item.PositionId == position.Id && versions.Contains(item.ContentVersion)).ToListAsync(ct);
        var questions = await db.JobQuestions.AsNoTracking().Where(item => item.PositionId == position.Id && versions.Contains(item.ContentVersion)).ToListAsync(ct);
        var attemptIds = series.Select(item => item.Id).ToArray();
        var answers = await db.JobAnswers.AsNoTracking().Where(item => attemptIds.Contains(item.AttemptId)).ToListAsync(ct);
        var history = series.Select((item, index) =>
        {
            var awarded = answers.Where(answer => answer.AttemptId == item.Id)
                .ToDictionary(answer => answer.QuestionId, answer => answer.ScoreAwarded, StringComparer.Ordinal);
            var rowsForAttempt = CampLogic.SkillRows(
                skills.Where(skill => skill.ContentVersion == item.ContentVersion),
                questions.Where(question => question.ContentVersion == item.ContentVersion), awarded);
            return new
            {
                attemptId = item.Id,
                attemptNumber = index + 1,
                item.SubmittedAt,
                item.TotalScore,
                item.MaxScore,
                percent = ExamLogic.Percent(item.TotalScore, item.MaxScore),
                item.AutoSubmitted,
                current = item.Id == attempt.Id,
                radar = CampLogic.Radar(rowsForAttempt)
            };
        }).ToArray();
        var currentIndex = Array.FindIndex(history, item => item.current);
        var currentAwarded = answers.Where(answer => answer.AttemptId == attempt.Id)
            .ToDictionary(answer => answer.QuestionId, answer => answer.ScoreAwarded, StringComparer.Ordinal);
        var currentQuestions = questions.Where(item => item.ContentVersion == attempt.ContentVersion).ToList();
        var rows = CampLogic.SkillRows(
            skills.Where(item => item.ContentVersion == attempt.ContentVersion), currentQuestions, currentAwarded);
        var percent = ExamLogic.Percent(attempt.TotalScore, attempt.MaxScore);
        return Results.Ok(new
        {
            attemptId = attempt.Id,
            attemptNumber = currentIndex < 0 ? 1 : history[currentIndex].attemptNumber,
            position = new { position.Id, position.Name, position.Description },
            attempt = new
            {
                attempt.StudentName, attempt.StudentNumber, attempt.StudentUserName, attempt.ClassName,
                attempt.StartedAt, attempt.SubmittedAt, attempt.AutoSubmitted,
                attempt.ObjectiveScore, attempt.PracticalScore, attempt.TotalScore, attempt.MaxScore, percent,
                level = attempt.MaxScore <= 0 ? "待评估" : CampLogic.Level(percent)
            },
            skills = rows,
            radar = CampLogic.Radar(rows),
            history,
            questionCount = currentQuestions.Count,
            evaluatedAt = attempt.SubmittedAt ?? attempt.GradedAt
        });
    }

    static async Task<IResult> Record(string attemptId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var attempt = await db.JobAttempts.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attemptId, ct);
        if (attempt == null) return NotFound("测评记录不存在");
        if (!await CanViewAttempt(db, ctx, attempt)) return Forbidden("无权查看该测评记录");
        if (attempt.Status == "inProgress") return Conflict("测评尚未提交，请先完成测评");
        var position = await db.JobPositions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attempt.PositionId, ct);
        if (position == null) return NotFound("岗位不存在");
        var skills = await db.JobSkills.AsNoTracking()
            .Where(item => item.PositionId == position.Id && item.ContentVersion == attempt.ContentVersion).ToListAsync(ct);
        var skillById = skills.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var questions = await db.JobQuestions.AsNoTracking()
            .Where(item => item.PositionId == position.Id && item.ContentVersion == attempt.ContentVersion)
            .OrderBy(item => item.SortOrder).ToListAsync(ct);
        var answers = await db.JobAnswers.AsNoTracking().Where(item => item.AttemptId == attempt.Id)
            .ToDictionaryAsync(item => item.QuestionId, StringComparer.Ordinal, ct);
        var revealAnswers = ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Teacher") || !position.Published;
        return Results.Ok(new
        {
            attemptId = attempt.Id,
            revealAnswers,
            position = new { position.Id, position.Name, position.Description },
            attempt = new
            {
                attempt.StudentName, attempt.StudentNumber, attempt.StudentUserName, attempt.ClassName,
                attempt.Status, attempt.StartedAt, attempt.Deadline, attempt.SubmittedAt, attempt.AutoSubmitted,
                attempt.ObjectiveScore, attempt.PracticalScore, attempt.TotalScore, attempt.MaxScore,
                percent = ExamLogic.Percent(attempt.TotalScore, attempt.MaxScore), attempt.GradedAt
            },
            questions = questions.Select(question =>
            {
                var answer = answers.GetValueOrDefault(question.Id);
                var selected = answer == null ? [] : ExamLogic.DeserializeSelection(answer.SelectedJson);
                var skill = question.SkillId == null ? null : skillById.GetValueOrDefault(question.SkillId);
                var (_, rubricMatches) = CampLogic.ScorePractical(question.RubricJson, answer?.TextAnswer ?? "");
                var automatic = question.PracticalRubricJson != "[]";
                var answered = question.Type != "practical" ? selected.Length > 0
                    : automatic ? answer?.AnsweredAt != null : !string.IsNullOrWhiteSpace(answer?.TextAnswer);
                return new
                {
                    questionId = question.Id,
                    question.Type,
                    question.Stem,
                    options = ExamLogic.DeserializeOptions(question.OptionsJson),
                    answers = revealAnswers ? ExamLogic.DeserializeAnswers(question.AnswerJson) : [],
                    analysis = revealAnswers ? question.Analysis : "",
                    maxScore = question.Score,
                    question.SortOrder,
                    skillName = skill?.Name ?? "",
                    subSkill = skill?.SubName ?? "",
                    skillAbility = skill?.Ability ?? "",
                    selected,
                    textAnswer = answer?.TextAnswer ?? "",
                    scoreAwarded = answer?.ScoreAwarded ?? 0,
                    correct = revealAnswers && answered && (ExamLogic.IsObjective(question.Type)
                        ? ExamLogic.IsCorrect(question.AnswerJson, selected) : answer?.ScoreAwarded == question.Score),
                    answered,
                    automaticPractical = automatic,
                    practicalDetails = revealAnswers ? ClassroomCaseScoring.ScoreDetails(answer?.PracticalDetailsJson ?? "[]") : [],
                    rubric = revealAnswers
                        ? rubricMatches.Select(match => new { match.Item.Item, match.Item.Keyword, match.Item.Score, match.Matched })
                        : []
                };
            })
        });
    }

    static async Task<IResult> Monitor(HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var attempts = ctx.User.IsInRole("Admin")
            ? db.JobAttempts.AsNoTracking()
            : CampAccess.TeacherControlScope(db.JobAttempts.AsNoTracking(), db, CurrentUserId(ctx));
        var rows = await (from attempt in attempts
                          join position in db.JobPositions.AsNoTracking() on attempt.PositionId equals position.Id
                          where attempt.Status == "inProgress"
                          join vm in db.VirtualMachines.AsNoTracking() on attempt.VirtualMachineId equals vm.Id into vms
                          from vm in vms.DefaultIfEmpty()
                          select new
                          {
                              AttemptId = attempt.Id,
                              PositionId = position.Id,
                              PositionName = position.Name,
                              attempt.StudentName,
                              attempt.StudentNumber,
                              attempt.StudentUserName,
                              attempt.ClassName,
                              attempt.StartedAt,
                              attempt.Deadline,
                              attempt.VirtualMachineId,
                              VirtualMachineName = vm == null ? "" : vm.Name,
                              VirtualMachineStatus = vm == null ? "" : vm.Status,
                              vmNodeId = vm == null ? null : vm.NodeId,
                              StartupNodeId = attempt.StartupNodeId ?? position.StartupNodeId
                          }).OrderBy(item => item.StartedAt).ToListAsync(ct);
        return Results.Ok(new
        {
            items = rows.Select(item => new
            {
                item.AttemptId, item.PositionId, item.PositionName, item.StudentName, item.StudentNumber,
                item.StudentUserName, item.ClassName, item.StartedAt, item.Deadline,
                remainingSeconds = Math.Max(0, (int)Math.Ceiling((item.Deadline - now).TotalSeconds)),
                item.VirtualMachineId, item.VirtualMachineName, item.VirtualMachineStatus,
                canRestart = item.VirtualMachineId != null || item.StartupNodeId != null
            })
        });
    }

    static async Task<IResult> RestartEnvironment(string attemptId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var attempt = await db.JobAttempts.FirstOrDefaultAsync(item => item.Id == attemptId, ct);
        if (attempt == null) return NotFound("测评记录不存在");
        if (!await CampAccess.CanControlAsync(db, ctx, attempt)) return NotFound("测评记录不存在或无权操作");
        if (attempt.Status != "inProgress" || attempt.Deadline <= DateTime.UtcNow) return Conflict("测评已结束，无需重启环境");
        var position = await db.JobPositions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attempt.PositionId, ct);
        if (position == null) return NotFound("岗位不存在");
        var now = DateTime.UtcNow;
        var nodeId = attempt.StartupNodeId ?? position.StartupNodeId;
        var vm = await SimulatedVirtualMachines.Acquire(db, nodeId, attempt.VirtualMachineId, now, ct, campAttemptId: attempt.Id);
        if (vm == null) return Conflict("该岗位未配置启动节点或没有可用的虚拟机");
        vm.Status = "running";
        vm.StartedAt = now;
        attempt.VirtualMachineId = vm.Id;
        Audit.Add(db, ctx, "重启测评虚拟机", "岗位测评", $"{attempt.StudentName} · {vm.Name}");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { virtualMachineId = vm.Id, virtualMachineName = vm.Name, virtualMachineStatus = vm.Status, restartedAt = now });
    }

    static async Task<IResult> PositionEnvironment(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var position = await db.JobPositions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (position == null) return NotFound("岗位不存在");
        var userId = CurrentUserId(ctx);
        var active = await SimulatedVirtualMachines.ActiveCamp(db, DateTime.UtcNow).AsNoTracking()
            .Where(item => item.PositionId == id && item.UserId == userId)
            .Select(item => new { item.VirtualMachineId, item.StartupNodeId }).FirstOrDefaultAsync(ct);
        return Results.Ok(await BuildEnvironment(db, ctx, active?.StartupNodeId ?? position.StartupNodeId, active?.VirtualMachineId, ct));
    }

    static async Task<IResult> MonitorEnvironment(string attemptId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var attempt = await db.JobAttempts.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attemptId, ct);
        if (attempt == null) return NotFound("测评记录不存在");
        if (!await CampAccess.CanControlAsync(db, ctx, attempt)) return NotFound("测评记录不存在或无权操作");
        var position = await db.JobPositions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == attempt.PositionId, ct);
        if (position == null) return NotFound("岗位不存在");
        var nodeId = attempt.StartupNodeId ?? position.StartupNodeId;
        if (attempt.VirtualMachineId != null)
        {
            nodeId = await db.VirtualMachines.AsNoTracking().Where(item => item.Id == attempt.VirtualMachineId)
                .Select(item => item.NodeId).FirstOrDefaultAsync(ct) ?? nodeId;
        }
        return Results.Ok(await BuildEnvironment(db, ctx, nodeId, attempt.VirtualMachineId, ct));
    }

    static async Task<object> BuildEnvironment(SchoolDbContext db, HttpContext ctx, string? nodeId, string? virtualMachineId, CancellationToken ct)
    {
        var isAdmin = ctx.User.IsInRole("Admin");
        var node = nodeId == null ? null : await db.Nodes.AsNoTracking().Where(item => item.Id == nodeId)
            .Select(item => new { item.Id, item.Name, item.Host, item.Enabled, item.Remark }).FirstOrDefaultAsync(ct);
        var deviceQuery = isAdmin
            ? db.Devices.AsNoTracking().Where(item => item.ConnectionType == ResourceProviders.Simulated && nodeId != null && item.NodeId == nodeId)
            : TrainingResourceAccess.AccessibleDevices(db, CurrentUserId(ctx), ctx.User.FindFirstValue("stamp") ?? "")
                .AsNoTracking().Where(item => nodeId != null && item.NodeId == nodeId);
        var devices = await (from device in deviceQuery
                             join cabinet in db.Cabinets.AsNoTracking() on device.CabinetId equals cabinet.Id
                             orderby device.Name
                             select new { device.Id, device.Name, cabinetName = cabinet.Name, isSimulated = true, canAccess = true })
            .ToListAsync(ct);
        var assignedNodes = TrainingResourceAccess.AssignedNodes(db, CurrentUserId(ctx));
        var vmQuery = db.VirtualMachines.AsNoTracking().Where(vm => vm.Id == virtualMachineId
            || (nodeId != null && vm.NodeId == nodeId && (isAdmin || assignedNodes.Any(assigned => assigned.Id == vm.NodeId))));
        var virtualMachines = await vmQuery.OrderBy(item => item.Name)
            .Select(item => new { item.Id, item.Name, item.Status, item.CpuCount, item.MemoryMb, item.DiskGb, selected = item.Id == virtualMachineId })
            .ToListAsync(ct);
        return new { nodeId, node, devices, virtualMachines };
    }

    static async Task<(int Saved, string? Error)> StoreAnswersAsync(
        SchoolDbContext db, string positionId, JobAttempt attempt, CampAnswerInput[]? inputs, DateTime now, CancellationToken ct)
    {
        if (inputs == null || inputs.Length == 0) return (0, null);
        if (inputs.Length > 200) return (0, "单次保存的答案过多");
        var pending = inputs.Where(item => item != null).GroupBy(item => item.QuestionId).Select(group => group.Last()).ToArray();
        if (pending.Length == 0) return (0, null);
        var ids = pending.Select(item => item.QuestionId).Distinct(StringComparer.Ordinal).ToArray();
        var questions = await db.JobQuestions.AsNoTracking()
            .Where(item => item.PositionId == positionId && item.ContentVersion == attempt.ContentVersion && ids.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, StringComparer.Ordinal, ct);
        var existing = await db.JobAnswers.Where(item => item.AttemptId == attempt.Id && ids.Contains(item.QuestionId))
            .ToDictionaryAsync(item => item.QuestionId, StringComparer.Ordinal, ct);
        var saved = 0;
        foreach (var item in pending)
        {
            if (!questions.TryGetValue(item.QuestionId, out var question)) return (0, "答案对应的题目不存在，请刷新后重试");
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
            var selectedJson = System.Text.Json.JsonSerializer.Serialize(selected);
            if (!existing.TryGetValue(item.QuestionId, out var answer))
            {
                if (selected.Length == 0 && text.Length == 0) continue;
                answer = new JobAnswer { AttemptId = attempt.Id, QuestionId = item.QuestionId };
                db.JobAnswers.Add(answer);
                existing[item.QuestionId] = answer;
            }
            else if (answer.SelectedJson == selectedJson && answer.TextAnswer == text)
            {
                continue;
            }
            answer.SelectedJson = selectedJson;
            answer.TextAnswer = text;
            answer.AnsweredAt = now;
            saved++;
        }
        return (saved, null);
    }

    static async Task FinalizeAttemptAsync(
        SchoolDbContext db, JobPosition position, JobAttempt attempt, DateTime submittedAt, bool autoSubmitted, DateTime now, CancellationToken ct)
    {
        var questions = await db.JobQuestions.AsNoTracking()
            .Where(item => item.PositionId == position.Id && item.ContentVersion == attempt.ContentVersion).ToListAsync(ct);
        var answers = await db.JobAnswers.Where(item => item.AttemptId == attempt.Id).ToListAsync(ct);
        var byQuestion = answers.ToDictionary(item => item.QuestionId, StringComparer.Ordinal);
        var projects = ReadProjects(attempt);
        var objectiveScore = 0;
        var practicalScore = 0;
        foreach (var question in questions)
        {
            var answer = byQuestion.GetValueOrDefault(question.Id);
            if (ExamLogic.IsObjective(question.Type))
            {
                var selected = answer == null ? [] : ExamLogic.DeserializeSelection(answer.SelectedJson);
                var correct = ExamLogic.IsCorrect(question.AnswerJson, selected);
                if (answer != null) answer.ScoreAwarded = correct ? question.Score : 0;
                if (correct) objectiveScore += question.Score;
            }
            else if (question.PracticalRubricJson != "[]")
            {
                var project = projects.GetValueOrDefault(question.Id);
                var details = ClassroomCaseScoring.Evaluate(ClassroomCaseScoring.Rubric(question.PracticalRubricJson),
                    project is { HasWork: true } ? project.Devices : []);
                if (answer == null)
                {
                    answer = new JobAnswer { AttemptId = attempt.Id, QuestionId = question.Id };
                    db.JobAnswers.Add(answer);
                }
                answer.ScoreAwarded = details.Where(item => item.Matched).Sum(item => item.Score);
                answer.PracticalDetailsJson = ClassroomCaseScoring.Write(details);
                answer.AnsweredAt = project is { HasWork: true } ? submittedAt : null;
                practicalScore += answer.ScoreAwarded;
            }
            else
            {
                var (score, _) = CampLogic.ScorePractical(question.RubricJson, answer?.TextAnswer ?? "");
                if (answer != null) answer.ScoreAwarded = score;
                practicalScore += score;
            }
        }
        attempt.ObjectiveScore = objectiveScore;
        attempt.PracticalScore = practicalScore;
        attempt.TotalScore = objectiveScore + practicalScore;
        attempt.MaxScore = questions.Sum(item => item.Score);
        attempt.Status = "graded";
        attempt.SubmittedAt = submittedAt;
        attempt.AutoSubmitted = autoSubmitted;
        attempt.GradedAt = now;
    }

    internal static async Task<int> FinalizeDueAsync(SchoolDbContext db, int batchSize, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = from attempt in db.JobAttempts
                  join position in db.JobPositions on attempt.PositionId equals position.Id
                  where attempt.Status == "inProgress" && attempt.Deadline <= now
                  orderby attempt.Deadline
                  select new { attempt, position };
        if (!await due.AnyAsync(ct)) return 0;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await due.Take(batchSize).ToListAsync(ct);
        foreach (var row in rows)
            await FinalizeAttemptAsync(db, row.position, row.attempt, row.attempt.Deadline, autoSubmitted: true, now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return rows.Count;
    }

    static JobAttempt? PreferredAttempt(IEnumerable<JobAttempt> attempts)
    {
        JobAttempt? active = null;
        JobAttempt? latest = null;
        foreach (var attempt in attempts)
        {
            if (attempt.Status == "inProgress" && (active == null || attempt.StartedAt > active.StartedAt)) active = attempt;
            if (latest == null || attempt.StartedAt > latest.StartedAt
                || (attempt.StartedAt == latest.StartedAt && string.CompareOrdinal(attempt.Id, latest.Id) > 0))
                latest = attempt;
        }
        return active ?? latest;
    }

    static Task<JobAttempt?> OpenAttempt(SchoolDbContext db, string positionId, string userId, CancellationToken ct, bool tracking = false)
    {
        var query = tracking ? db.JobAttempts : db.JobAttempts.AsNoTracking();
        return query.FirstOrDefaultAsync(item => item.PositionId == positionId && item.UserId == userId && item.Status == "inProgress", ct);
    }

    static bool IsActiveAttemptConflict(DbUpdateException exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("IX_JobAttempts_PositionId_UserId_Active", StringComparison.Ordinal)
            || message.Contains("UNIQUE constraint failed", StringComparison.Ordinal);
    }

    static object AttemptState(JobAttempt attempt, DateTime now) => new
    {
        attemptId = attempt.Id, attempt.Status, attempt.StartedAt, attempt.Deadline,
        remainingSeconds = attempt.Status == "inProgress"
            ? Math.Max(0, (int)Math.Ceiling((attempt.Deadline - now).TotalSeconds)) : 0
    };

    static async Task<bool> StartupNodeExists(SchoolDbContext db, string? nodeId, CancellationToken ct)
        => string.IsNullOrWhiteSpace(nodeId) || await db.Nodes.AnyAsync(item => item.Id == nodeId, ct);

    static Task<bool> CanViewAttempt(SchoolDbContext db, HttpContext ctx, JobAttempt attempt)
        => ctx.User.IsInRole("Admin") || attempt.UserId == CurrentUserId(ctx)
            ? Task.FromResult(true)
            : CampAccess.CanControlAsync(db, ctx, attempt);

    static string CurrentUserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    static IResult Bad(string message) => Results.BadRequest(new { message });
    static IResult Conflict(string message) => Results.Conflict(new { message });
    static IResult NotFound(string message = "记录不存在") => Results.NotFound(new { message });
    static IResult Forbidden(string message) => Results.Json(new { message }, statusCode: StatusCodes.Status403Forbidden);
}
