using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace School.Training;

public sealed record VmConsoleCommand(string Command, string ConnectionVersion);
public sealed record VmConsoleLine(int Sequence, DateTime At, string Actor, string Command, string Output);
public sealed record VmConsoleSnapshot(string Id, string Name, string Uuid, string IpAddress,
    int CpuCount, int MemoryMb, int DiskGb, DateTime? StartedAt, string ConnectionVersion,
    bool CanWrite, bool LinkUp, VmConsoleLine[] Lines);

public sealed class SimulatedVmConsole : IDisposable
{
    const int MaxLines = 80;
    readonly MemoryCache buffers = new(new MemoryCacheOptions { SizeLimit = 512 });
    readonly object gate = new();

    sealed class Buffer
    {
        public bool LinkUp { get; set; } = true;
        public int NextSequence { get; set; }
        public Queue<VmConsoleLine> Lines { get; } = new();
    }

    public VmConsoleSnapshot Read(TrainingVirtualMachine vm, string scope, bool canWrite)
    {
        lock (gate)
        {
            var version = Version(vm, scope);
            buffers.TryGetValue<Buffer>(version, out var buffer);
            return Snapshot(vm, version, canWrite, buffer);
        }
    }

    public VmConsoleSnapshot Run(TrainingVirtualMachine vm, string scope, string actor, string command)
    {
        lock (gate)
        {
            var version = Version(vm, scope);
            var buffer = buffers.GetOrCreate(version, entry =>
            {
                entry.SetSize(1).SetSlidingExpiration(TimeSpan.FromMinutes(30));
                return new Buffer();
            })!;
            var now = DateTime.UtcNow;
            var output = Execute(vm, buffer, command, now);
            buffer.Lines.Enqueue(new VmConsoleLine(++buffer.NextSequence, now, actor, command, output));
            if (buffer.Lines.Count > MaxLines) buffer.Lines.Dequeue();
            return Snapshot(vm, version, true, buffer);
        }
    }

    static string Execute(TrainingVirtualMachine vm, Buffer buffer, string command, DateTime now)
    {
        switch (string.Join(' ', command.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant())
        {
            case "help": return "help · status · hostname · ip · uptime · free · date · link up · link down";
            case "hostname": return vm.Name;
            case "ip":
            case "ip addr": return $"eth0: {(buffer.LinkUp ? "UP" : "DOWN")}\nIPv4: {vm.IpAddress}";
            case "uptime":
                var seconds = Math.Max(0, (long)(now - (vm.StartedAt ?? now)).TotalSeconds);
                return $"已运行 {seconds} 秒";
            case "free": return $"模拟内存总量：{vm.MemoryMb} MB";
            case "date": return $"{SchoolTime.Format(now)} (UTC+8)";
            case "status": return $"运行中 · {vm.CpuCount} 核 · {vm.MemoryMb} MB 内存 · {vm.DiskGb} GB 磁盘";
            case "link up": buffer.LinkUp = true; return "已启用模拟网卡 eth0。";
            case "link down": buffer.LinkUp = false; return "已停用模拟网卡 eth0。";
            default: return "未知命令。输入 help 查看可用命令。";
        }
    }

    public static string Version(TrainingVirtualMachine vm, string scope) => $"{vm.Id}:{vm.StartedAt?.Ticks}:{scope}";

    static VmConsoleSnapshot Snapshot(TrainingVirtualMachine vm, string version, bool canWrite, Buffer? buffer)
        => new(vm.Id, vm.Name, vm.Uuid, vm.IpAddress, vm.CpuCount, vm.MemoryMb, vm.DiskGb, vm.StartedAt, version,
            canWrite, buffer?.LinkUp ?? true, buffer?.Lines.ToArray() ?? []);

    public void Dispose() => buffers.Dispose();
}

public static class VmConsoleEndpoints
{
    public static void MapVmConsole(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/vm-console/{scope}/{id}")
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Teacher", "Student"));
        group.MapGet("", Read);
        group.MapPost("/commands", Run);
    }

    sealed record Target(TrainingVirtualMachine Vm, string Scope, bool CanWrite);

    static async Task<IResult> Read(string scope, string id, HttpContext ctx, SchoolDbContext db, SimulatedVmConsole console, CancellationToken ct)
    {
        var target = await Resolve(scope, id, ctx, db, ct);
        if (target == null) return Missing();
        if (target.Vm.Status != "running") return Offline();
        return Results.Ok(console.Read(target.Vm, target.Scope, target.CanWrite));
    }

    static async Task<IResult> Run(string scope, string id, VmConsoleCommand input, HttpContext ctx, SchoolDbContext db,
        SimulatedVmConsole console, CancellationToken ct)
    {
        var command = input.Command.Trim();
        if (command.Length is < 1 or > 100 || command.Any(char.IsControl))
            return Results.BadRequest(new { message = "请输入 1 至 100 个字符的模拟控制台命令。" });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var target = await Resolve(scope, id, ctx, db, ct);
        if (target == null) return Missing();
        if (target.Vm.Status != "running") return Offline();
        if (!target.CanWrite)
            return Results.Json(new { message = "请从课堂或岗位监控入口操作这台虚拟机。" }, statusCode: 403);
        if (input.ConnectionVersion != SimulatedVmConsole.Version(target.Vm, target.Scope))
            return Results.Conflict(new { message = "虚拟机已更换或重启，请刷新控制台。" });
        var actor = ctx.User.IsInRole("Admin") ? "管理员" : ctx.User.IsInRole("Teacher") ? "教师" : "学生";
        var result = console.Run(target.Vm, target.Scope, actor, command);
        await transaction.CommitAsync(ct);
        return Results.Ok(result);
    }

    static async Task<Target?> Resolve(string scope, string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var isAdmin = ctx.User.IsInRole("Admin");
        var isTeacher = ctx.User.IsInRole("Teacher");
        var isStudent = ctx.User.IsInRole("Student");
        var now = DateTime.UtcNow;
        var classrooms = SimulatedVirtualMachines.ActiveClassrooms(db).AsNoTracking();
        var camps = SimulatedVirtualMachines.ActiveCamp(db, now).AsNoTracking();
        string? vmId;
        string key;
        var canWrite = true;
        switch (scope)
        {
            case "exam":
                var examVm = await ExamPracticalEndpoints.ResolveMachine(id, db, ctx, ct);
                return examVm == null ? null : new Target(examVm, $"exam:{id}", true);
            case "machine":
                vmId = id;
                var teaching = await classrooms.Where(item => item.VirtualMachineId == id).OrderBy(item => item.Id)
                    .Select(item => new { item.Id, item.WorkId }).FirstOrDefaultAsync(ct);
                var campId = teaching == null
                    ? await camps.Where(item => item.VirtualMachineId == id).OrderBy(item => item.Id).Select(item => item.Id).FirstOrDefaultAsync(ct)
                    : null;
                if (teaching != null || campId != null)
                {
                    if (!isAdmin) return null;
                    canWrite = false;
                }
                key = teaching != null ? ClassroomScope(teaching.Id, teaching.WorkId) : campId != null ? CampScope(campId) : "machine";
                break;
            case "classroom":
                var session = await classrooms.Where(item => item.Id == id && (isAdmin
                    || (isTeacher && db.Classrooms.Any(room => room.Id == item.ClassroomId && room.TeacherUserId == userId))
                    || (isStudent && item.UserId == userId && db.ClassroomStudents.Any(student =>
                        student.ClassroomId == item.ClassroomId && student.StudentUserId == userId))))
                    .Select(item => new { item.WorkId, item.VirtualMachineId }).FirstOrDefaultAsync(ct);
                if (session == null) return null;
                vmId = session.VirtualMachineId;
                key = ClassroomScope(id, session.WorkId);
                break;
            case "camp":
                var campScope = isAdmin
                    ? camps
                    : isTeacher
                        ? CampAccess.TeacherControlScope(camps, db, userId)
                        : camps.Where(item => item.UserId == userId);
                vmId = await campScope.Where(item => item.Id == id).Select(item => item.VirtualMachineId).FirstOrDefaultAsync(ct);
                key = CampScope(id);
                break;
            default:
                return null;
        }
        if (vmId == null) return null;
        var target = await (from vm in db.VirtualMachines.AsNoTracking()
                            join node in db.Nodes.AsNoTracking() on vm.NodeId equals node.Id
                            where vm.Id == vmId && node.Provider == ResourceProviders.Simulated
                                && (!isStudent || node.Enabled)
                                && (scope != "machine" || isAdmin || (node.Enabled &&
                                    db.NodeAccountAssignments.Any(item => item.NodeId == node.Id && item.UserId == userId)))
                            select vm).FirstOrDefaultAsync(ct);
        return target == null ? null : new Target(target, key, canWrite);
    }

    static string ClassroomScope(string sessionId, string workId) => $"classroom:{sessionId}:{workId}";
    static string CampScope(string attemptId) => $"camp:{attemptId}";
    static IResult Missing() => Results.NotFound(new { message = "虚拟机不存在或当前无权连接。" });
    static IResult Offline() => Results.Conflict(new { message = "虚拟机已停止，启动后可连接。" });
}
