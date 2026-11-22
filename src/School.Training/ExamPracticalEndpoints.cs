using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class ExamPracticalEndpoints
{
    const int MaxDeployments = 2000;

    public static void MapExamPracticalStaff(this RouteGroupBuilder staff)
    {
        staff.MapPost("/exams/{id}/deployment", Deploy);
        staff.MapGet("/exams/{id}/deployment/{userId}", ListProjects);
        staff.MapGet("/exams/{id}/deployment/{userId}/{questionId}", StaffEnvironment);
        staff.MapPost("/exams/{id}/deployment/{userId}/{questionId}/restart", Restart);
    }

    public static void MapExamPracticalStudent(this RouteGroupBuilder learners)
    {
        var project = learners.MapGroup("/exam-hall/{id}/practical/{questionId}");
        project.MapGet("", StudentEnvironment);
        project.MapPut("/devices/{deviceId}", SaveDevice);
    }

    public static async Task<string?> ValidateQuestion(SchoolDbContext db, ExamQuestionInput input, CancellationToken ct)
    {
        var nodeId = input.PracticalNodeId?.Trim();
        var rules = input.PracticalRubric ?? [];
        if (string.IsNullOrEmpty(nodeId) && rules.Length == 0) return null;
        if (input.Type != "practical") return "只有实操题可以配置模拟环境和评分表。";
        if (string.IsNullOrEmpty(nodeId) || rules.Length == 0) return "请选择模拟节点并配置至少一条评分项。";
        return await ClassroomCaseScoring.ValidateRubric(db, nodeId, rules, ct);
    }

    public static IQueryable<ExamPaperQuestion> Projects(SchoolDbContext db, string paperId)
        => db.ExamPaperQuestions.Where(item => item.PaperId == paperId && item.Type == "practical" && item.PracticalNodeId != "");

    static async Task<IResult> Deploy(string id, ExamDeploymentInput? input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var exam = await ExamManagementEndpoints.Scoped(db, ctx).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return Missing();
        if (exam.Status == "closed" || exam.EndAt <= DateTime.UtcNow) return Conflict("考试已结束，不能部署环境。");
        var projects = await Projects(db, exam.PaperId).AsNoTracking().OrderBy(item => item.SortOrder).ToListAsync(ct);
        if (projects.Count == 0) return Conflict("试卷中没有配置模拟环境的实操题。");
        var userIds = input?.StudentUserIds?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        if (userIds is { Length: 0 }) return Bad("请选择要部署的学生。");
        if (userIds?.Length > MaxDeployments) return Bad("选择的学生过多，请分批部署。");
        var query = db.ExamStudents.Where(item => item.ExamId == id);
        if (userIds != null) query = query.Where(item => userIds.Contains(item.StudentUserId));
        var targets = await query.OrderBy(item => item.Id).Take(MaxDeployments + 1).ToListAsync(ct);
        if (targets.Count == 0 || (userIds != null && targets.Count != userIds.Length))
            return Bad("部分学生不在考试名单中，请刷新后重试。");
        var targetIds = targets.Select(item => item.Id).ToArray();
        var projectIds = projects.Select(item => item.Id).ToArray();
        var existing = (await db.ExamPracticalEnvironments
            .Where(item => targetIds.Contains(item.EnrollmentId) && projectIds.Contains(item.PaperQuestionId))
            .ToListAsync(ct)).ToDictionary(item => (item.EnrollmentId, item.PaperQuestionId));
        bool Ready(string enrollmentId, string projectId)
            => existing.GetValueOrDefault((enrollmentId, projectId))?.DeploymentStatus == "deployed";
        var finishedUsers = (await db.ExamAttempts.Where(item => item.ExamId == id && item.Status != "inProgress")
            .Select(item => item.UserId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var pending = targets.Where(student => projects.Any(project => !Ready(student.Id, project.Id))
            && !finishedUsers.Contains(student.StudentUserId)).ToArray();
        if ((long)pending.Length * projects.Count > MaxDeployments || targets.Count > MaxDeployments)
            return Bad($"每批最多部署 {MaxDeployments} 个学生实操环境，请分批部署。");
        var templates = new Dictionary<string, string>();
        var failures = new Dictionary<string, string>();
        foreach (var project in projects)
        {
            if (!pending.Any(student => !Ready(student.Id, project.Id))) continue;
            var enabled = await db.Nodes.AnyAsync(node => node.Id == project.PracticalNodeId && node.Enabled
                && node.Provider == ResourceProviders.Simulated, ct);
            if (!enabled)
            {
                failures[project.Id] = "模拟节点不可用或已停用。";
                continue;
            }
            var deployedBaseline = await (from environment in db.ExamPracticalEnvironments.AsNoTracking()
                                          join enrollment in db.ExamStudents.AsNoTracking() on environment.EnrollmentId equals enrollment.Id
                                          where enrollment.ExamId == id && environment.PaperQuestionId == project.Id
                                              && environment.DeploymentStatus == "deployed"
                                          orderby environment.DeployedAt, environment.EnrollmentId
                                          select environment.BaselineJson).FirstOrDefaultAsync(ct);
            if (deployedBaseline != null)
            {
                templates[project.Id] = deployedBaseline;
                continue;
            }
            var rules = ClassroomCaseScoring.Rubric(project.PracticalRubricJson);
            var problem = await ClassroomCaseScoring.ValidateRubric(db, project.PracticalNodeId, rules, ct);
            if (problem != null || rules.Length == 0)
            {
                failures[project.Id] = problem ?? "未配置评分项。";
                continue;
            }
            var deviceIds = rules.Select(rule => rule.DeviceId).ToHashSet(StringComparer.Ordinal);
            var devices = await ClassroomCaseScoring.LoadDevices(db, project.PracticalNodeId, ct);
            templates[project.Id] = ClassroomCaseScoring.Write(devices.Where(device => deviceIds.Contains(device.Id))
                .Select(device => new CaseDeviceState(device.Id, device.Name, SimulationDeviceConfiguration.Read(device))));
        }
        var now = DateTime.UtcNow;
        if (exam.EndAt <= now) return Conflict("考试已结束，不能部署环境。");
        var failureMessages = projects.Where(project => failures.ContainsKey(project.Id))
            .Select(project => $"第 {project.SortOrder + 1} 题：{failures[project.Id]}").ToArray();
        var deployed = 0;
        foreach (var student in pending)
        {
            var ready = 0;
            foreach (var project in projects)
            {
                if (Ready(student.Id, project.Id)) { ready++; continue; }
                if (!existing.TryGetValue((student.Id, project.Id), out var work))
                {
                    work = new ExamPracticalEnvironment { EnrollmentId = student.Id, PaperQuestionId = project.Id };
                    db.ExamPracticalEnvironments.Add(work);
                }
                work.DeployedAt = now;
                if (templates.TryGetValue(project.Id, out var baseline))
                {
                    work.BaselineJson = baseline;
                    work.ConfigurationJson = baseline;
                    work.VmUuid = Guid.NewGuid().ToString("N");
                    work.DeploymentStatus = "deployed";
                    work.DeploymentMessage = "模拟虚拟机已就绪";
                    ready++;
                }
                else
                {
                    work.DeploymentStatus = "failed";
                    work.DeploymentMessage = failures.GetValueOrDefault(project.Id, "无法创建实操环境");
                }
            }
            if (ready == projects.Count)
            {
                student.DeploymentStatus = "deployed";
                student.DeploymentMessage = $"已部署 {ready} 个模拟环境";
                student.DeployedAt = now;
                deployed++;
            }
            else
            {
                student.DeploymentStatus = "failed";
                student.DeploymentMessage = $"已部署 {ready}/{projects.Count} 个。{string.Join(" ", failureMessages.Take(2))}";
            }
        }
        if (pending.Length > 0)
        {
            Audit.Add(db, ctx, "部署考试环境", exam.Name, $"就绪 {deployed} 人，失败 {pending.Length - deployed} 人");
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Results.Ok(new { deployed, existing = targets.Count - pending.Length, failed = pending.Length - deployed, failures = failureMessages });
    }

    static async Task<IResult> ListProjects(string id, string userId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var exam = await ExamManagementEndpoints.Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return Missing();
        var enrollment = await Enrollment(db, id, userId, ct);
        if (enrollment == null) return Missing();
        var rows = await (from project in Projects(db, exam.PaperId).AsNoTracking()
                          join env in db.ExamPracticalEnvironments.AsNoTracking().Where(item => item.EnrollmentId == enrollment.Id)
                              on project.Id equals env.PaperQuestionId into environments
                          from env in environments.DefaultIfEmpty()
                          orderby project.SortOrder
                          select new
                          {
                              paperQuestionId = project.Id, project.Stem, project.SortOrder,
                              deployed = env != null && env.DeploymentStatus == "deployed",
                              status = env == null ? "pending" : env.DeploymentStatus,
                              message = env == null ? "" : env.DeploymentMessage,
                              attemptedAt = env == null ? (DateTime?)null : env.DeployedAt,
                              deployedAt = env != null && env.DeploymentStatus == "deployed" ? (DateTime?)env.DeployedAt : null,
                              restartedAt = env == null ? null : env.RestartedAt
                          }).ToListAsync(ct);
        return Results.Ok(new { items = rows, canRestart = await CanRestart(db, exam, userId, ct) });
    }

    static async Task<IResult> StaffEnvironment(string id, string userId, string questionId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var exam = await ExamManagementEndpoints.Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return Missing();
        var work = await FindWork(db, exam, userId, questionId, ct);
        return work == null ? Missing() : Results.Ok(View(work, await CanRestart(db, exam, userId, ct)));
    }

    static async Task<IResult> Restart(string id, string userId, string questionId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var exam = await ExamManagementEndpoints.Scoped(db, ctx).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (exam == null) return Missing();
        var work = await FindWork(db, exam, userId, questionId, ct);
        if (work == null) return Missing();
        if (!await CanRestart(db, exam, userId, ct)) return Conflict("作答已结束，不能重启实操环境。");
        work.ConfigurationJson = work.BaselineJson;
        work.HasWork = false;
        work.Revision++;
        work.RestartedAt = DateTime.UtcNow;
        Audit.Add(db, ctx, "重启考试实操环境", exam.Name, $"考生 {userId}，题目 {questionId}");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(View(work));
    }

    static async Task<IResult> StudentEnvironment(string id, string questionId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var (exam, userId) = await FindActiveExam(db, ctx, id, ct);
        if (exam == null) return Conflict("请在考试作答期间查看实操环境。");
        var work = await FindWork(db, exam, userId, questionId, ct);
        return work == null ? Missing() : Results.Ok(View(work));
    }

    static async Task<IResult> SaveDevice(string id, string questionId, string deviceId, ExamDeviceUpdate input,
        HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (exam, userId) = await FindActiveExam(db, ctx, id, ct);
        if (exam == null) return Conflict("请在考试作答期间保存实操配置。");
        var work = await FindWork(db, exam, userId, questionId, ct);
        if (work == null) return Missing();
        if (work.Revision != input.Revision) return Conflict("环境已被更新或重启，请刷新后继续作答。");
        var devices = ClassroomCaseScoring.Devices(work.ConfigurationJson);
        var index = Array.FindIndex(devices, device => device.Id == deviceId);
        if (index < 0) return Missing();
        if (!ClassroomCaseScoring.ValidConfiguration(devices[index].Configuration, input.Configuration))
            return Bad("设备名称、接口或接口说明无效。");
        devices[index] = devices[index] with { Configuration = input.Configuration };
        work.ConfigurationJson = ClassroomCaseScoring.Write(devices);
        work.Revision++;
        work.HasWork = true;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(View(work));
    }

    static object View(ExamPracticalEnvironment work, bool canConnect = true) => new
    {
        work.PaperQuestionId, work.Revision, work.HasWork, work.DeployedAt, work.RestartedAt,
        virtualMachine = canConnect && work.VmUuid != null ? new { id = work.VmUuid, name = "考试实操虚拟机" } : null,
        devices = ClassroomCaseScoring.Devices(work.ConfigurationJson)
    };

    internal static async Task<TrainingVirtualMachine?> ResolveMachine(string uuid, SchoolDbContext db, HttpContext ctx, CancellationToken ct)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var isStudent = ctx.User.IsInRole("Student");
        var now = DateTime.UtcNow;
        var exams = isStudent
            ? db.TrainingExams.Where(item => item.StartAt <= now && item.Status == "open")
            : ExamManagementEndpoints.Scoped(db, ctx);
        var row = await (from work in db.ExamPracticalEnvironments.AsNoTracking()
                         join enrollment in db.ExamStudents on work.EnrollmentId equals enrollment.Id
                         join exam in exams on enrollment.ExamId equals exam.Id
                         join question in db.ExamPaperQuestions on work.PaperQuestionId equals question.Id
                         join node in db.Nodes on question.PracticalNodeId equals node.Id
                         where work.VmUuid == uuid && work.DeploymentStatus == "deployed"
                            && exam.PaperId == question.PaperId && exam.Status != "closed" && exam.EndAt > now
                            && node.Provider == ResourceProviders.Simulated && node.Enabled
                            && (!isStudent || (enrollment.StudentUserId == userId && db.ExamAttempts.Any(attempt =>
                                attempt.ExamId == exam.Id && attempt.UserId == userId && attempt.Status == "inProgress" && attempt.Deadline > now)))
                            && !db.ExamAttempts.Any(attempt => attempt.ExamId == exam.Id && attempt.UserId == enrollment.StudentUserId
                                && (attempt.Status != "inProgress" || attempt.Deadline <= now))
                         select new { work.DeployedAt, work.RestartedAt, NodeId = node.Id }).FirstOrDefaultAsync(ct);
        return row == null ? null : new TrainingVirtualMachine
        {
            Id = uuid,
            Uuid = Guid.ParseExact(uuid, "N").ToString(),
            IpAddress = "192.168.56.2",
            NodeId = row.NodeId,
            Name = "考试实操虚拟机",
            Status = "running",
            StartedAt = row.RestartedAt ?? row.DeployedAt
        };
    }

    static async Task<(TrainingExam? Exam, string UserId)> FindActiveExam(SchoolDbContext db, HttpContext ctx, string id, CancellationToken ct)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var now = DateTime.UtcNow;
        var exam = await db.TrainingExams.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id
            && item.Status == "open" && item.StartAt <= now && item.EndAt > now
            && db.ExamAttempts.Any(attempt => attempt.ExamId == id && attempt.UserId == userId
                && attempt.Status == "inProgress" && attempt.Deadline > now), ct);
        return (exam, userId);
    }

    static async Task<bool> CanRestart(SchoolDbContext db, TrainingExam exam, string userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return exam.Status != "closed" && exam.EndAt > now
            && !await db.ExamAttempts.AnyAsync(item => item.ExamId == exam.Id && item.UserId == userId
                && (item.Status != "inProgress" || item.Deadline <= now), ct);
    }

    static Task<ExamStudent?> Enrollment(SchoolDbContext db, string examId, string userId, CancellationToken ct)
        => db.ExamStudents.AsNoTracking().FirstOrDefaultAsync(item => item.ExamId == examId && item.StudentUserId == userId, ct);

    static Task<ExamPracticalEnvironment?> FindWork(SchoolDbContext db, TrainingExam exam, string userId, string questionId, CancellationToken ct)
        => (from work in db.ExamPracticalEnvironments
            join enrollment in db.ExamStudents on work.EnrollmentId equals enrollment.Id
            join question in Projects(db, exam.PaperId) on work.PaperQuestionId equals question.Id
            where enrollment.ExamId == exam.Id && enrollment.StudentUserId == userId && work.PaperQuestionId == questionId
                && work.DeploymentStatus == "deployed"
            select work).SingleOrDefaultAsync(ct);

    static IResult Bad(string message) => Results.BadRequest(new { message });
    static IResult Conflict(string message) => Results.Conflict(new { message });
    static IResult Missing() => Results.NotFound(new { message = "考试或实操环境不存在。" });
}
