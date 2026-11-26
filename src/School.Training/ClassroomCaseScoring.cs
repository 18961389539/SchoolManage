using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public sealed record CaseRubricItem(string Name, string DeviceId, string Field, string? InterfaceName,
    string Expected, int Score, string? Guidance = null);
public sealed record CaseDeviceState(string Id, string Name, SimulatedDeviceState Configuration);
public sealed record CaseScoreItem(string Name, string DeviceName, string Field, string? InterfaceName,
    string Expected, string? Actual, int Score, bool Matched, string? Guidance);
public sealed record CaseWorkVersion(string WorkId, int Revision);
public sealed record CaseDeviceUpdate(string WorkId, int Revision, SimulatedDeviceState Configuration);

public static class ClassroomCaseScoring
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxDevices = 100;

    public static void MapClassroomCaseScoring(this RouteGroupBuilder admin, RouteGroupBuilder staff, RouteGroupBuilder learners)
    {
        admin.MapGet("/nodes/{id}/case-devices", ListDevices);
        staff.MapGet("/classrooms/case-sessions/{id}/work", Monitor);
        staff.MapPut("/classrooms/case-sessions/{id}/work/devices/{deviceId}", Assist);
        var work = learners.MapGroup("/classrooms/{id}/resources/{resourceId}/case-work");
        work.MapGet("", GetWork);
        work.MapPut("/devices/{deviceId}", UpdateDevice);
        work.MapPost("/save", Save);
        work.MapPost("/trial", Trial);
        work.MapPost("/finish", Finish);
    }

    public static Task<List<TrainingDevice>> LoadDevices(SchoolDbContext db, string? nodeId, CancellationToken ct)
        => db.Devices.AsNoTracking().Where(device => nodeId != null && device.NodeId == nodeId
            && device.ConnectionType == ResourceProviders.Simulated)
            .OrderBy(device => device.Name).ThenBy(device => device.Id).Take(MaxDevices + 1).ToListAsync(ct);

    static async Task<IResult> ListDevices(string id, SchoolDbContext db, CancellationToken ct)
    {
        var devices = await LoadDevices(db, id, ct);
        if (devices.Count > MaxDevices) return Bad($"单个实操节点最多支持 {MaxDevices} 台模拟设备。");
        return Results.Ok(devices.Select(device => new
        {
            device.Id, device.Name, interfaces = SimulationDeviceConfiguration.Read(device).Interfaces.Keys
        }));
    }

    public static async Task<string?> ValidateRubric(SchoolDbContext db, CourseResourceInput input, CancellationToken ct)
    {
        var rules = input.CaseRubric ?? [];
        if (rules.Length == 0) return null;
        if (input.Kind != "casePractice") return "只有案例实操可以配置评分表。";
        return await ValidateRubric(db, input.StartupNodeId, rules, ct);
    }

    public static async Task<string?> ValidateRubric(SchoolDbContext db, string? nodeId, CaseRubricItem[] rules, CancellationToken ct)
    {
        if (rules.Length > 30) return "每个评分表最多 30 项。";
        if (!await db.Nodes.AnyAsync(node => node.Id == nodeId
            && node.Provider == ResourceProviders.Simulated, ct)) return "请为评分表选择模拟启动节点。";
        var devices = await LoadDevices(db, nodeId, ct);
        if (devices.Count > MaxDevices) return $"单个实操节点最多支持 {MaxDevices} 台模拟设备。";
        var configs = devices.ToDictionary(device => device.Id, device => SimulationDeviceConfiguration.Read(device));
        var targets = new HashSet<(string, string, string?)>();
        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > 100
                || rule.Score is < 1 or > 100 || (rule.Guidance?.Length ?? 0) > 500)
                return "评分项需填写名称（1–100 字）、分值（1–100 分），排错指导最多 500 字。";
            if (string.IsNullOrWhiteSpace(rule.DeviceId) || !configs.TryGetValue(rule.DeviceId, out var config))
                return "评分项设备必须属于所选模拟节点。";
            var valid = rule.Field switch
            {
                "hostname" => rule.InterfaceName == null && SimulationDeviceConfiguration.IsValidHostname(rule.Expected),
                "enabled" => config.Interfaces.ContainsKey(rule.InterfaceName ?? "") && rule.Expected is "true" or "false",
                "description" => config.Interfaces.ContainsKey(rule.InterfaceName ?? "") && SimulationDeviceConfiguration.IsValidDescription(rule.Expected),
                _ => false
            };
            if (!valid) return "评分项的配置字段、接口或预期值无效。";
            if (!targets.Add((rule.DeviceId, rule.Field, rule.InterfaceName))) return "同一设备的同一配置项只能评分一次。";
        }
        return null;
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static CaseRubricItem[] Rubric(string json) => ReadArray<CaseRubricItem>(json);
    public static CaseDeviceState[] Devices(string json) => ReadArray<CaseDeviceState>(json);
    public static CaseScoreItem[] ScoreDetails(string json) => ReadArray<CaseScoreItem>(json);
    static T[] ReadArray<T>(string json) => JsonSerializer.Deserialize<T[]>(json, Json) ?? [];

    public static bool ValidConfiguration(SimulatedDeviceState original, SimulatedDeviceState config)
        => SimulationDeviceConfiguration.IsValidHostname(config.Hostname) && config.Interfaces != null
            && original.Interfaces.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(config.Interfaces.Keys)
            && config.Interfaces.Values.All(port => port != null && SimulationDeviceConfiguration.IsValidDescription(port.Description));

    public static object GradeView(ClassroomCaseGrade grade) => new
    {
        grade.Id, grade.IsFinal, grade.StartedAt, grade.ScoredAt, grade.Score, grade.MaxScore, grade.Percent,
        passed = MeetsTarget(grade.Score, grade.MaxScore),
        details = ReadArray<CaseScoreItem>(grade.DetailsJson)
    };

    static bool MeetsTarget(int score, int maxScore) => maxScore > 0 && score * 100 >= maxScore * AnalyticsLogic.PassScorePercent;

    public static CaseScoreItem[] Evaluate(IReadOnlyList<CaseRubricItem> rules, IReadOnlyList<CaseDeviceState> devices)
    {
        var byId = devices.ToDictionary(device => device.Id);
        return rules.Select(rule =>
        {
            var device = byId.GetValueOrDefault(rule.DeviceId);
            var port = device?.Configuration.Interfaces.GetValueOrDefault(rule.InterfaceName ?? "");
            var actual = rule.Field switch
            {
                "hostname" => device?.Configuration.Hostname,
                "enabled" => port == null ? null : port.Enabled ? "true" : "false",
                "description" => port?.Description,
                _ => null
            };
            var matched = string.Equals(actual, rule.Expected, StringComparison.Ordinal);
            return new CaseScoreItem(rule.Name, device?.Name ?? "设备不可用", rule.Field, rule.InterfaceName,
                rule.Expected, actual, rule.Score, matched, matched ? null : rule.Guidance);
        }).ToArray();
    }

    public static void Initialize(ClassroomCaseSession session, CourseResource resource, IReadOnlyList<TrainingDevice> devices)
    {
        session.WorkId = Guid.NewGuid().ToString("N");
        session.Revision = 0;
        session.RubricJson = resource.CaseRubricJson;
        session.EnvironmentJson = Write(devices.Select(device =>
            new CaseDeviceState(device.Id, device.Name, SimulationDeviceConfiguration.Read(device))));
    }

    public static void Discard(ClassroomCaseSession session, DateTime now)
    {
        session.Status = "ended";
        session.EndedAt = now;
        session.VirtualMachineId = null;
        session.EnvironmentJson = "[]";
        session.RubricJson = "[]";
    }

    public static void Suspend(ClassroomCaseSession session, ClassroomLiveShare shares)
    {
        shares.Revoke(session.WorkId);
        session.Status = "saved";
        session.Revision++;
        session.VirtualMachineId = null;
    }

    static async Task<IResult> GetWork(string id, string resourceId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var (session, _, error) = await FindWork(id, resourceId, ctx, db, ct);
        if (error != null) return error;
        var grades = await db.ClassroomCaseGrades.AsNoTracking().Where(grade => grade.SessionId == session!.Id)
            .OrderByDescending(grade => grade.ScoredAt).ToListAsync(ct);
        return Results.Ok(new
        {
            sessionId = session!.Id, session.Status, session.WorkId, session.Revision, session.StartedAt,
            topology = session.Status == "active" ? CaseTopology.Read(session.TopologyBlueprintJson) : null,
            virtualMachine = await CaseMachine(db, session, ct),
            devices = session.Status == "active" ? ReadArray<CaseDeviceState>(session.EnvironmentJson) : [],
            grades = grades.Select(GradeView)
        });
    }

    static async Task<IResult> Monitor(string id, HttpContext ctx, SchoolDbContext db, ClassroomLiveShare shares, CancellationToken ct)
    {
        var session = await db.ClassroomCaseSessions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (session == null) return Results.NotFound(new { message = "实操会话不存在。" });
        var userId = UserId(ctx);
        if (!ctx.User.IsInRole("Admin") &&
            !await db.Classrooms.AnyAsync(item => item.Id == session.ClassroomId && item.TeacherUserId == userId, ct))
            return Results.Forbid();
        return Results.Ok(new
        {
            sessionId = session.Id, session.Status, session.WorkId, session.Revision,
            topology = session.Status == "ended" ? null : CaseTopology.Read(session.TopologyBlueprintJson),
            allowAssist = session.Status == "active" && shares.Read(session.WorkId)?.AllowAssist == true
                && await ClassroomLiveShareEndpoints.ActiveSessions(db).AnyAsync(item => item.Id == id, ct),
            virtualMachine = await CaseMachine(db, session, ct),
            devices = session.Status == "ended" ? [] : ReadArray<CaseDeviceState>(session.EnvironmentJson)
        });
    }

    sealed record CaseMachineSummary(string Id, string Name, string Status);

    static Task<CaseMachineSummary?> CaseMachine(SchoolDbContext db, ClassroomCaseSession session, CancellationToken ct)
        => db.VirtualMachines.AsNoTracking().Where(vm => vm.Id == session.VirtualMachineId)
            .Select(vm => new CaseMachineSummary(vm.Id, vm.Name, vm.Status)).FirstOrDefaultAsync(ct);

    static async Task<IResult> UpdateDevice(string id, string resourceId, string deviceId, CaseDeviceUpdate input,
        HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (session, _, error) = await FindWork(id, resourceId, ctx, db, ct, mustBeOpen: true);
        if (error != null) return error;
        var result = ApplyUpdate(session!, deviceId, input);
        if (result != null) return result;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { session!.Revision });
    }

    static async Task<IResult> Assist(string id, string deviceId, CaseDeviceUpdate input, HttpContext ctx,
        SchoolDbContext db, ClassroomLiveShare shares, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var session = await ClassroomLiveShareEndpoints.StaffSessions(db, ctx).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (session == null) return Results.NotFound();
        if (shares.Read(session.WorkId)?.AllowAssist != true
            || !await ClassroomLiveShareEndpoints.ActiveSessions(db).AnyAsync(item => item.Id == id, ct))
            return Results.Conflict(new { message = "学生未授权协助或实操已结束" });
        var result = ApplyUpdate(session, deviceId, input);
        if (result != null) return result;
        Audit.Add(db, ctx, "协助配置课堂实操", "实操会话", session.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { session.Revision });
    }

    static IResult? ApplyUpdate(ClassroomCaseSession session, string deviceId, CaseDeviceUpdate input)
    {
        if (!IsCurrent(session, input.WorkId, input.Revision)) return Stale();
        var devices = ReadArray<CaseDeviceState>(session.EnvironmentJson);
        var index = Array.FindIndex(devices, device => device.Id == deviceId);
        if (index < 0) return Results.NotFound(new { message = "该设备不在你的实操环境中。" });
        var original = devices[index].Configuration;
        var config = input.Configuration;
        if (config == null || !ValidConfiguration(original, config))
            return Bad("设备名称、接口或接口说明无效。");
        devices[index] = devices[index] with { Configuration = config };
        session.EnvironmentJson = Write(devices);
        session.Revision++;
        return null;
    }

    static async Task<IResult> Save(string id, string resourceId, CaseWorkVersion input, HttpContext ctx,
        SchoolDbContext db, ClassroomLiveShare shares, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (session, _, error) = await FindWork(id, resourceId, ctx, db, ct, mustBeOpen: true);
        if (error != null) return error;
        if (session!.Status == "saved" && session.WorkId == input.WorkId && session.Revision == input.Revision + 1)
            return Results.Ok(new { session.Status });
        if (!IsCurrent(session, input.WorkId, input.Revision)) return Stale();
        Suspend(session, shares);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { session.Status });
    }

    static Task<IResult> Trial(string id, string resourceId, CaseWorkVersion input, HttpContext ctx,
        SchoolDbContext db, ClassroomLiveShare shares, CancellationToken ct)
        => Score(id, resourceId, input, ctx, db, shares, isFinal: false, ct);

    static Task<IResult> Finish(string id, string resourceId, CaseWorkVersion input, HttpContext ctx,
        SchoolDbContext db, ClassroomLiveShare shares, CancellationToken ct)
        => Score(id, resourceId, input, ctx, db, shares, isFinal: true, ct);

    static async Task<IResult> Score(string id, string resourceId, CaseWorkVersion input, HttpContext ctx,
        SchoolDbContext db, ClassroomLiveShare shares, bool isFinal, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (session, record, error) = await FindWork(id, resourceId, ctx, db, ct, mustBeOpen: true);
        if (error != null) return error;
        var gradeId = isFinal ? input.WorkId : $"{input.WorkId}:{input.Revision}";
        var existing = await db.ClassroomCaseGrades.AsNoTracking()
            .FirstOrDefaultAsync(grade => grade.Id == gradeId && grade.SessionId == session!.Id && grade.IsFinal == isFinal, ct);
        if (existing != null) return Results.Ok(GradeView(existing));
        if (!IsCurrent(session!, input.WorkId, input.Revision)) return Stale();
        var rules = Rubric(session!.RubricJson);
        if (rules.Length == 0)
        {
            var current = await db.CourseResources.Where(item => item.Id == resourceId)
                .Select(item => item.CaseRubricJson).SingleAsync(ct);
            rules = Rubric(current);
            var deviceIds = ReadArray<CaseDeviceState>(session.EnvironmentJson).Select(device => device.Id).ToHashSet();
            if (rules.Length == 0 || !rules.All(rule => deviceIds.Contains(rule.DeviceId)))
                return Results.Conflict(new { message = "本环境没有可用的评分表，请联系教师检查实操资源设置。" });
            session.RubricJson = current;
        }
        var details = Evaluate(rules, ReadArray<CaseDeviceState>(session.EnvironmentJson));
        var score = details.Where(item => item.Matched).Sum(item => item.Score);
        var maxScore = details.Sum(item => item.Score);
        var grade = new ClassroomCaseGrade
        {
            Id = gradeId, SessionId = session.Id, IsFinal = isFinal, StartedAt = session.StartedAt, ScoredAt = DateTime.UtcNow,
            Score = score, MaxScore = maxScore, Percent = score * 100 / maxScore, DetailsJson = Write(details)
        };
        if (isFinal)
        {
            record!.Status = "completed";
            record.CompletedAt = grade.ScoredAt;
            record.AttemptCount++;
            record.BestScore = Math.Max(record.BestScore, grade.Percent);
            shares.Revoke(session.WorkId);
            Discard(session, grade.ScoredAt);
        }
        db.ClassroomCaseGrades.Add(grade);
        Audit.Add(db, ctx, isFinal ? "提交实操评分" : "尝试实操评分", resourceId, $"{score}/{maxScore}");
        await db.SaveChangesAsync(ct);
        if (isFinal) await ClassroomProgress.RefreshStudentAsync(db, record!.ClassroomId, record.UserId, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(GradeView(grade));
    }

    static async Task<(ClassroomCaseSession? Session, LearningRecord? Record, IResult? Error)> FindWork(
        string classroomId, string resourceId, HttpContext ctx, SchoolDbContext db, CancellationToken ct, bool mustBeOpen = false)
    {
        var userId = UserId(ctx);
        if (!await db.ClassroomStudents.AnyAsync(row => row.ClassroomId == classroomId && row.StudentUserId == userId, ct))
            return (null, null, Results.Forbid());
        if (mustBeOpen && !await TeachingLogic.OpenClassrooms(db, DateTime.UtcNow).AnyAsync(row => row.Id == classroomId, ct))
            return (null, null, Results.Conflict(new { message = "课堂尚未开放或已结课。" }));
        var work = await (from session in db.ClassroomCaseSessions
                          join record in db.LearningRecords on session.LearningRecordId equals record.Id
                          join resource in db.CourseResources on session.ResourceId equals resource.Id
                          where session.ClassroomId == classroomId && session.ResourceId == resourceId && session.UserId == userId
                              && resource.Kind == "casePractice"
                          select new { session, record }).SingleOrDefaultAsync(ct);
        return work == null
            ? (null, null, Results.NotFound(new { message = "请先开始实操。" }))
            : (work.session, work.record, null);
    }

    static bool IsCurrent(ClassroomCaseSession session, string workId, int revision)
        => session.Status == "active" && session.WorkId.Length > 0 && session.WorkId == workId && session.Revision == revision;
    static string UserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    static IResult Stale() => Results.Conflict(new { message = "实操状态已改变，请刷新后再操作。" });
    static IResult Bad(string message) => Results.BadRequest(new { message });
}
