using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static partial class InfrastructureEndpoints
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
        => services.AddSingleton<SimulationSessions>().AddSingleton<SimulatedVmConsole>().AddSingleton<ClassroomLiveShare>().AddSingleton<HyperVProvider>();

    public static RouteGroupBuilder MapInfrastructureEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("").RequireAuthorization("Admin");
        group.MapGet("/device-types", async (SchoolDbContext db, HttpRequest request, CancellationToken ct) =>
        {
            var q = db.DeviceTypes.AsNoTracking();
            if (Search(request) is { Length: > 0 } s) q = q.Where(x => x.Name.Contains(s) || x.Remark.Contains(s));
            return Results.Ok(await Page(q.OrderBy(x => x.Name), request, ct));
        });
        group.MapPost("/device-types", async (DeviceTypeInput input, SchoolDbContext db, HttpContext ctx) =>
        {
            if (ValidateType(input) is { } error) return Bad(error);
            if (await db.DeviceTypes.AnyAsync(x => x.Name == input.Name.Trim())) return Conflict("设备类型名称已存在。");
            var row = new DeviceType(); Apply(row, input); db.DeviceTypes.Add(row);
            Audit.Add(db, ctx, "新增设备类型", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(row);
        });
        group.MapPut("/device-types/{id}", async (string id, DeviceTypeInput input, SchoolDbContext db, HttpContext ctx) =>
        {
            if (ValidateType(input) is { } error) return Bad(error);
            var row = await db.DeviceTypes.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (await db.DeviceTypes.AnyAsync(x => x.Id != id && x.Name == input.Name.Trim())) return Conflict("设备类型名称已存在。");
            Apply(row, input); Audit.Add(db, ctx, "编辑设备类型", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(row);
        });
        group.MapDelete("/device-types/{id}", async (string id, SchoolDbContext db, HttpContext ctx) =>
        {
            var row = await db.DeviceTypes.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (await db.Devices.AnyAsync(x => x.DeviceTypeId == id)) return Conflict("该类型仍被设备使用，请先调整设备类型。");
            db.DeviceTypes.Remove(row); Audit.Add(db, ctx, "删除设备类型", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { message = "已删除。" });
        });

        group.MapGet("/cabinets", async (SchoolDbContext db, HttpRequest request, SimulationSessions sessions, CancellationToken ct) =>
        {
            var q = db.Cabinets.AsNoTracking();
            if (Search(request) is { Length: > 0 } s) q = q.Where(x => x.Name.Contains(s) || x.Host.Contains(s));
            var total = await q.CountAsync(ct); var rows = await Slice(q.OrderBy(x => x.Name), request).ToListAsync(ct);
            var ids = rows.Select(x => x.Id).ToArray(); var devices = await db.Devices.AsNoTracking().Where(x => ids.Contains(x.CabinetId)).Select(x => new { x.Id, x.CabinetId }).ToListAsync(ct);
            return Results.Ok(new { total, items = rows.Select(x => CabinetView(x, devices.Count(d => d.CabinetId == x.Id), sessions.Count(devices.Where(d => d.CabinetId == x.Id).Select(d => d.Id)))) });
        });
        group.MapPost("/cabinets", async (CabinetInput input, SchoolDbContext db, IDataProtectionProvider protection, HttpContext ctx, LicenseQuotaPolicy quota) =>
        {
            if (ValidateCabinet(input) is { } error) return Bad(error);
            var ct = ctx.RequestAborted;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await db.Cabinets.AnyAsync(x => x.Name == input.Name.Trim(), ct)) return Conflict("机柜名称已存在。");
            if (await quota.CheckAllocationAsync(db, LicensedResource.Cabinets, 1, ct) is { } denied) return denied;
            var row = new Cabinet(); Apply(row, input, protection); db.Cabinets.Add(row);
            Audit.Add(db, ctx, "新增机柜", row.Name);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(CabinetView(row));
        });
        group.MapPut("/cabinets/{id}", async (string id, CabinetInput input, SchoolDbContext db, IDataProtectionProvider protection, SimulationSessions sessions, HttpContext ctx) =>
        {
            if (ValidateCabinet(input) is { } error) return Bad(error);
            var row = await db.Cabinets.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (await db.Cabinets.AnyAsync(x => x.Id != id && x.Name == input.Name.Trim())) return Conflict("机柜名称已存在。");
            var deviceIds = await db.Devices.Where(x => x.CabinetId == id).Select(x => x.Id).ToListAsync(ctx.RequestAborted);
            if (sessions.Count(deviceIds) > 0) return Conflict("机柜存在活动会话，请先回收机柜再编辑连接参数。");
            Apply(row, input, protection); Audit.Add(db, ctx, "编辑机柜", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(CabinetView(row, deviceIds.Count));
        });
        group.MapDelete("/cabinets/{id}", async (string id, SchoolDbContext db, HttpContext ctx) =>
        {
            var row = await db.Cabinets.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (await db.Devices.AnyAsync(x => x.CabinetId == id)) return Conflict("机柜中仍有设备，请先删除或移走设备。");
            db.Cabinets.Remove(row); Audit.Add(db, ctx, "删除机柜", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { message = "已删除。" });
        });
        group.MapPost("/cabinets/test-connections", async (InfrastructureIds input, SchoolDbContext db, HttpContext ctx, CancellationToken ct) =>
        {
            if (input.Ids is not { Length: > 0 and <= 50 }) return Bad("请选择 1 至 50 个机柜。");
            var rows = await db.Cabinets.AsNoTracking().Where(x => input.Ids.Contains(x.Id)).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
            if (rows.Count != input.Ids.Distinct().Count()) return Bad("部分机柜已不存在，请刷新列表。");
            var tasks = rows.Select(async row =>
            {
                if (row.IsSimulated) return new ConnectionProbe(row.Id, row.Name, true, "模拟控制器响应正常。", true, "simulation");
                var host = string.IsNullOrWhiteSpace(row.JumpHost) ? row.Host : row.JumpHost;
                var port = string.IsNullOrWhiteSpace(row.JumpHost) ? row.Port : row.JumpPort;
                var result = await Probe(host, port, ct);
                return new ConnectionProbe(row.Id, row.Name, result.Success, result.Success ? (string.IsNullOrWhiteSpace(row.JumpHost) ? "TCP 握手成功；未验证串口协议或认证。" : "跳板机 TCP 握手成功；未验证到设备的后续链路。") : result.Message, false, "tcp");
            });
            var items = await Task.WhenAll(tasks);
            var names = string.Join(",", rows.Take(10).Select(x => x.Name)) + (rows.Count > 10 ? $" 等 {rows.Count} 个" : "");
            var scope = items.All(x => x.IsSimulated) ? "使用内置模拟控制器响应。" : "真实机柜仅检查 TCP 可达性。";
            Audit.Add(db, ctx, "测试机柜连接", names, $"共 {items.Length} 个机柜，失败 {items.Count(x => !x.Success)} 个。{scope}");
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { items });
        });
        group.MapPost("/cabinets/{id}/reclaim", async (string id, SchoolDbContext db, SimulationSessions sessions, HttpContext ctx) =>
        {
            var row = await db.Cabinets.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            var deviceIds = await db.Devices.Where(x => x.CabinetId == id).Select(x => x.Id).ToListAsync(ctx.RequestAborted);
            var released = sessions.Reclaim(deviceIds);
            Audit.Add(db, ctx, "回收机柜", row.Name, $"关闭 {released} 个平台终端会话；不执行设备配置重置。");
            await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { releasedSessions = released, message = $"已请求关闭 {released} 个平台终端会话。回收不会重置设备配置。" });
        });

        group.MapGet("/devices", async (SchoolDbContext db, HttpRequest request, SimulationSessions sessions, CancellationToken ct) =>
        {
            var q = db.Devices.AsNoTracking();
            if (Search(request) is { Length: > 0 } s) q = q.Where(x => x.Name.Contains(s) || x.Host.Contains(s) || x.UserName.Contains(s));
            var cabinetId = request.Query["cabinetId"].FirstOrDefault();
            if (!string.IsNullOrEmpty(cabinetId)) q = q.Where(x => x.CabinetId == cabinetId);
            var total = await q.CountAsync(ct); var rows = await Slice(q.OrderBy(x => x.Name), request).ToListAsync(ct);
            var cabinets = await db.Cabinets.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var types = await db.DeviceTypes.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var nodeIds = rows.Select(x => x.NodeId).OfType<string>().Distinct().ToArray();
            var nodes = await db.Nodes.AsNoTracking().Where(x => nodeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var cabinet = !string.IsNullOrEmpty(cabinetId) && cabinets.TryGetValue(cabinetId, out var cabinetName) ? new { id = cabinetId, name = cabinetName } : null;
            return Results.Ok(new { total, cabinet, items = rows.Select(x => DeviceView(x, cabinets.GetValueOrDefault(x.CabinetId, ""), types.GetValueOrDefault(x.DeviceTypeId, ""), sessions.Owner(x.Id), x.NodeId is null ? "" : nodes.GetValueOrDefault(x.NodeId, ""))) });
        });
        group.MapPost("/devices", async (DeviceInput input, SchoolDbContext db, IDataProtectionProvider protection, HttpContext ctx, LicenseQuotaPolicy quota) =>
        {
            var ct = ctx.RequestAborted;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await ValidateDevice(input, db, ct) is { } error) return Bad(error);
            if (await quota.CheckAllocationAsync(db, LicensedResource.Devices, 1, ct) is { } denied) return denied;
            var row = new TrainingDevice(); Apply(row, input, protection); db.Devices.Add(row);
            Audit.Add(db, ctx, "新增设备", row.Name);
            await db.SaveChangesAsync(ct);
            var nodeName = await DeviceNodeName(db, row.NodeId, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(DeviceView(row, nodeName: nodeName));
        });
        group.MapPut("/devices/{id}", async (string id, DeviceInput input, SchoolDbContext db, IDataProtectionProvider protection, SimulationSessions sessions, HttpContext ctx) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
            if (await ValidateDevice(input, db, ctx.RequestAborted) is { } error) return Bad(error);
            var row = await db.Devices.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (sessions.Owner(id) is not null) return Conflict("设备存在活动会话，请先关闭终端或回收机柜。");
            Apply(row, input, protection); Audit.Add(db, ctx, "编辑设备", row.Name); await db.SaveChangesAsync(ctx.RequestAborted);
            await transaction.CommitAsync(ctx.RequestAborted);
            return Results.Ok(DeviceView(row, nodeName: await DeviceNodeName(db, row.NodeId, ctx.RequestAborted)));
        });
        group.MapDelete("/devices/{id}", async (string id, SchoolDbContext db, SimulationSessions sessions, HttpContext ctx) =>
        {
            var row = await db.Devices.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (sessions.Owner(id) is not null) return Conflict("设备存在活动会话，请先关闭终端或回收机柜。");
            db.Devices.Remove(row); Audit.Add(db, ctx, "删除设备", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { message = "已删除。" });
        });
        group.MapPost("/devices/{id}/connect-test", async (string id, SchoolDbContext db, HttpContext ctx, CancellationToken ct) =>
        {
            var row = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct); if (row is null) return Missing();
            if (ResourceProviders.IsSimulated(row.ConnectionType)) return Results.Ok(new ConnectionProbe(id, row.Name, true, "模拟设备响应正常。", true, "simulation"));
            var cabinet = await db.Cabinets.FindAsync([row.CabinetId], ct);
            if (!string.IsNullOrEmpty(cabinet?.JumpHost)) return Results.Ok(new ConnectionProbe(id, row.Name, false, "真实跳板机链路尚未配置验证；不能将直连探测作为设备连接成功。", false, "unsupported"));
            var target = string.IsNullOrWhiteSpace(row.Host) ? cabinet?.Host ?? "" : row.Host;
            var probe = await Probe(target, row.Port, ct);
            Audit.Add(db, ctx, "测试设备连接", row.Name, $"{target}:{row.Port}；" + (probe.Success ? "TCP 握手成功，未验证设备认证与控制协议。" : probe.Message));
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new ConnectionProbe(id, row.Name, probe.Success, probe.Success ? "仅 TCP 握手成功，未验证设备认证与控制协议。" : probe.Message, false, "tcp"));
        });
        api.MapGet("/terminal/{deviceId}", SimulationTerminal.Handle)
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Teacher", "Student"));
        api.MapMyResources();
        api.MapSimulatedDeviceWebEndpoints();
        api.MapVmConsole();
        group.MapPost("/simulation/bootstrap", BootstrapSimulation);
        group.MapGet("/server/info", () => Results.Ok(InfrastructureHostInfo.Collect()));

        MapNodes(group);
        group.MapHyperV();
        return api;
    }

    private static void MapNodes(RouteGroupBuilder group)
    {
        group.MapGet("/nodes", async (SchoolDbContext db, HttpRequest request, HyperVProvider hyperV, CancellationToken ct) =>
        {
            var q = db.Nodes.AsNoTracking();
            if (request.Query["provider"].FirstOrDefault() is { Length: > 0 } provider) q = q.Where(x => x.Provider == provider);
            if (Search(request) is { Length: > 0 } s) q = q.Where(x => x.Name.Contains(s) || x.Host.Contains(s));
            var total = await q.CountAsync(ct); var rows = await Slice(q.OrderBy(x => x.Name), request).ToListAsync(ct);
            var nodeIds = rows.Select(x => x.Id).ToArray();
            var usage = await ReadNodeUsageAsync(db, nodeIds, ct);
            var accountCounts = await db.NodeAccountAssignments.AsNoTracking().Where(x => nodeIds.Contains(x.NodeId)).GroupBy(x => x.NodeId).Select(g => new { NodeId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.NodeId, x => x.Count, ct);
            var inspections = new ConcurrentDictionary<string, HyperVResult?>(StringComparer.OrdinalIgnoreCase);
            var hyperVNodes = rows.Where(row => row.Provider == HyperVProvider.Name).ToArray();
            if (hyperVNodes.Length > 0)
            {
                await Parallel.ForEachAsync(hyperVNodes, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    async (row, token) => inspections[row.Id] = await hyperV.InspectAsync(row.Host, token));
            }
            var items = rows.Select(row => NodeView(
                row, usage.GetValueOrDefault(row.Id), accountCounts.GetValueOrDefault(row.Id),
                inspections.GetValueOrDefault(row.Id)?.Metrics)).ToList();
            return Results.Ok(new { total, items });
        });
        group.MapPost("/nodes", async (NodeInput input, SchoolDbContext db, IDataProtectionProvider protection, HttpContext ctx, LicenseQuotaPolicy quota) =>
        {
            if (ValidateNode(input) is { } error) return Bad(error);
            var ct = ctx.RequestAborted;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await db.Nodes.AnyAsync(x => x.Name == input.Name.Trim(), ct)) return Conflict("节点名称已存在。");
            if (input.Provider == HyperVProvider.Name && await db.Nodes.AnyAsync(x => x.Provider == HyperVProvider.Name && x.Host.ToLower() == input.Host!.Trim().ToLower(), ct))
                return Conflict("该 Hyper-V 主机已登记。");
            if (await quota.CheckAllocationAsync(db, LicensedResource.Nodes, 1, ct) is { } denied) return denied;
            var row = new TrainingNode(); Apply(row, input, protection); db.Nodes.Add(row);
            Audit.Add(db, ctx, "新增节点", row.Name);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(NodeView(row));
        });
        group.MapPut("/nodes/{id}", async (string id, NodeInput input, SchoolDbContext db, IDataProtectionProvider protection, SimulationSessions sessions, HttpContext ctx) =>
        {
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                if (ValidateNode(input) is { } error) return Bad(error);
                await using var transaction = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
                var row = await db.Nodes.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
                if (await db.Nodes.AnyAsync(x => x.Id != id && x.Name == input.Name.Trim(), ctx.RequestAborted)) return Conflict("节点名称已存在。");
                if (input.Provider == HyperVProvider.Name && await db.Nodes.AnyAsync(x => x.Id != id && x.Provider == HyperVProvider.Name && x.Host.ToLower() == input.Host!.Trim().ToLower(), ctx.RequestAborted))
                    return Conflict("该 Hyper-V 主机已登记。");
                var usage = (await ReadNodeUsageAsync(db, [id], ctx.RequestAborted)).GetValueOrDefault(id);
                var accountCount = await db.NodeAccountAssignments.CountAsync(x => x.NodeId == id, ctx.RequestAborted);
                if (input.Provider != row.Provider && (usage.VirtualMachineCount > 0 || accountCount > 0 || await db.Devices.AnyAsync(x => x.NodeId == id, ctx.RequestAborted)))
                    return Conflict("节点仍有关联虚拟机、设备或师生账号，不能更改提供程序。");
                if (input.AccountQuota < accountCount || input.VirtualMachineQuota < usage.VirtualMachineCount) return Conflict("账号或虚拟机配额不能低于已有分配数量，请先释放关联资源。");
                if (usage.RunningCpuCount > input.TotalCpu || usage.RunningMemoryMb > input.TotalMemoryMb || usage.DiskGb > input.TotalDiskGb)
                    return Conflict("节点容量不能低于已分配资源。");
                Apply(row, input, protection); Audit.Add(db, ctx, "编辑节点", row.Name); await db.SaveChangesAsync(ctx.RequestAborted);
                await transaction.CommitAsync(ctx.RequestAborted);
                if (!row.Enabled) sessions.RevokeNodeLearners(id);
                return Results.Ok(NodeView(row, usage, accountCount));
            }
            finally { VmPowerLock.Release(); }
        });
        group.MapDelete("/nodes/{id}", async (string id, SchoolDbContext db, HttpContext ctx) =>
        {
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                var row = await db.Nodes.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
                if (await db.VirtualMachines.AnyAsync(x => x.NodeId == id)) return Conflict("节点仍有关联虚拟机，请先删除或迁移虚拟机。");
                if (await db.NodeAccountAssignments.AnyAsync(x => x.NodeId == id)) return Conflict("节点仍有已分配师生账号，请先在账号分配中移除全部关联。");
                db.Nodes.Remove(row); Audit.Add(db, ctx, "删除节点", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { message = "已删除。" });
            }
            finally { VmPowerLock.Release(); }
        });
        group.MapGet("/nodes/{id}/status", async (string id, SchoolDbContext db, HyperVProvider hyperV, CancellationToken ct) =>
        {
            var row = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct); if (row is null) return Missing();
            if (row.Provider == HyperVProvider.Name && hyperV.IsConfigured(row.Host))
            {
                var real = await hyperV.InspectAsync(row.Host, ct);
                if (real?.Metrics is not { } host) return HyperVEndpoints.Unavailable();
                var accounts = await db.NodeAccountAssignments.CountAsync(x => x.NodeId == id, ct);
                return Results.Ok(new
                {
                    row.Id, row.Name, row.Host, row.Provider, isSimulated = false, row.Enabled,
                    status = row.Enabled ? "ready" : "disabled", healthStatus = "healthy",
                    host.CpuUsagePercent, host.MemoryUsagePercent, host.DiskUsagePercent,
                    row.TotalCpu, row.TotalMemoryMb, row.TotalDiskGb, virtualMachineCount = host.TotalVms,
                    row.VirtualMachineQuota, runningCount = host.RunningVms,
                    accountCount = accounts, row.AccountQuota, metricsSource = "hyperv-host",
                    message = "Hyper-V 主机实测。账号数量表示平台分配的账号。",
                    collectedAt = DateTime.UtcNow
                });
            }
            if (!ResourceProviders.IsSimulated(row.Provider)) return Results.Json(new { message = "此节点的虚拟化管理尚未配置。", provider = row.Provider, available = false, isSimulated = false }, statusCode: 501);
            var usage = (await ReadNodeUsageAsync(db, [id], ct)).GetValueOrDefault(id);
            var metrics = GetNodeMetrics(row, usage);
            var accountCount = await db.NodeAccountAssignments.CountAsync(x => x.NodeId == id, ct);
            return Results.Ok(new
            {
                row.Id,
                row.Name,
                row.Host,
                row.Provider,
                isSimulated = true,
                row.Enabled,
                status = row.Enabled ? "ready" : "disabled",
                metrics.HealthStatus,
                metrics.CpuUsagePercent,
                metrics.MemoryUsagePercent,
                metrics.DiskUsagePercent,
                row.TotalCpu,
                row.TotalMemoryMb,
                row.TotalDiskGb,
                usage.VirtualMachineCount,
                row.VirtualMachineQuota,
                usage.RunningCount,
                accountCount,
                row.AccountQuota,
                metricsSource = "simulator-resource-reservation",
                message = "模拟指标按资源分配计算；账号数量表示分配到节点的现有师生账号，不创建操作系统账户。",
                collectedAt = DateTime.UtcNow
            });
        });
        MapNodeAccounts(group);
        group.MapPost("/nodes/{id}/enable", (string id, SchoolDbContext db, SimulationSessions sessions, HttpContext ctx) => SetNodeEnabled(id, true, db, sessions, ctx));
        group.MapPost("/nodes/{id}/disable", (string id, SchoolDbContext db, SimulationSessions sessions, HttpContext ctx) => SetNodeEnabled(id, false, db, sessions, ctx));
        group.MapGet("/nodes/{id}/configuration", async (string id, SchoolDbContext db, CancellationToken ct) =>
        {
            var row = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct); if (row is null) return Missing();
            if (!ResourceProviders.IsSimulated(row.Provider)) return ResourceProviders.Unsupported("当前系统仅支持模拟节点配置；不会修改实际 Windows 主机。");
            return Results.Ok(ConfigurationView(row));
        });
        group.MapPut("/nodes/{id}/configuration", async (string id, NodeConfigurationInput input, SchoolDbContext db, HttpContext ctx) =>
        {
            var row = await db.Nodes.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (!ResourceProviders.IsSimulated(row.Provider)) return ResourceProviders.Unsupported("当前系统仅支持模拟节点配置；不会修改实际 Windows 主机。");
            if (ValidateNetwork(input) is { } error) return Bad(error);
            DateTimeOffset? time = null;
            if (!string.IsNullOrWhiteSpace(input.SystemTime))
            {
                if (!(input.SystemTime.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(input.SystemTime, @"[+-]\d{2}:\d{2}$")) || !DateTimeOffset.TryParse(input.SystemTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) || parsed.Year < 2000 || parsed.Year > 2100)
                    return Bad("模拟系统时间须为 2000 至 2100 年的 ISO 时间，包含 Z 或时区偏移。");
                time = parsed;
            }
            row.IpAddress = input.IpAddress.Trim(); row.SubnetMask = input.SubnetMask.Trim(); row.Gateway = input.Gateway?.Trim() ?? "";
            if (time is { } setTime) row.SimulatedClockOffsetSeconds = (long)Math.Round((setTime - DateTimeOffset.UtcNow).TotalSeconds);
            Audit.Add(db, ctx, "配置模拟节点系统", row.Name, "更改模拟器时间与网络参数；实际 Windows 主机未修改。");
            await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(ConfigurationView(row));
        });
        group.MapGet("/virtual-machines", async (SchoolDbContext db, HttpRequest request, CancellationToken ct) =>
        {
            var q = db.VirtualMachines.AsNoTracking();
            if (Search(request) is { Length: > 0 } s)
            {
                var uuidSearch = Guid.TryParse(s, out var uuid) ? uuid.ToString() : s.ToLowerInvariant();
                q = q.Where(x => x.Name.Contains(s) || x.IpAddress.Contains(s) || x.Uuid.Contains(uuidSearch) || x.Id == s);
            }
            if (request.Query["nodeId"].FirstOrDefault() is { Length: > 0 } nodeId) q = q.Where(x => x.NodeId == nodeId);
            var total = await q.CountAsync(ct); var rows = await Slice(q.OrderBy(x => x.Name).ThenBy(x => x.Id), request).ToListAsync(ct);
            var nodeIds = rows.Select(row => row.NodeId).Distinct().ToArray();
            var vmIds = rows.Select(row => row.Id).ToArray();
            var nodes = await db.Nodes.AsNoTracking().Where(x => nodeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var reserved = (await SimulatedVirtualMachines.ReservedIds(db, DateTime.UtcNow)
                .Where(id => vmIds.Contains(id)).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
            return Results.Ok(new { total, items = rows.Select(x => VmView(x, nodes.GetValueOrDefault(x.NodeId), reserved.Contains(x.Id))) });
        });
        group.MapPost("/virtual-machines", async (VirtualMachineInput input, SchoolDbContext db, HttpContext ctx, LicenseQuotaPolicy quota) =>
        {
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
                if (await ValidateVm(input, db, ctx.RequestAborted) is { } error) return Bad(error);
                var node = await db.Nodes.FindAsync([input.NodeId], ctx.RequestAborted);
                if (node is null) return Conflict("所选节点已不存在，请刷新后重试。");
                if (!ResourceProviders.IsSimulated(node.Provider)) return Bad("当前版本仅支持创建模拟虚拟机。");
                if (!node.Enabled) return Conflict("节点已禁止分配，请先启用节点。");
                if (await db.VirtualMachines.CountAsync(x => x.NodeId == node.Id, ctx.RequestAborted) >= node.VirtualMachineQuota) return Conflict("节点虚拟机数量已达到可分配上限。");
                if (await quota.CheckAllocationAsync(db, LicensedResource.VirtualMachines, 1, ctx.RequestAborted) is { } denied) return denied;
                if (await db.VirtualMachines.Where(x => x.NodeId == node.Id).SumAsync(x => x.DiskGb, ctx.RequestAborted) + input.DiskGb > node.TotalDiskGb) return Conflict("模拟节点磁盘容量不足。");
                var row = new TrainingVirtualMachine { NodeId = input.NodeId, Name = input.Name.Trim(), CpuCount = input.CpuCount, MemoryMb = input.MemoryMb, DiskGb = input.DiskGb, Remark = input.Remark?.Trim() ?? "" };
                row.IpAddress = await SimulatedVirtualMachines.AllocateAddress(db, ctx.RequestAborted);
                db.VirtualMachines.Add(row);
                Audit.Add(db, ctx, "创建模拟虚拟机", row.Name);
                await db.SaveChangesAsync(ctx.RequestAborted);
                await transaction.CommitAsync(ctx.RequestAborted);
                return Results.Ok(VmView(row, node));
            }
            finally { VmPowerLock.Release(); }
        });
        group.MapPut("/virtual-machines/{id}", async (string id, VirtualMachineInput input, SchoolDbContext db, HttpContext ctx) =>
        {
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
                var row = await db.VirtualMachines.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
                if (await SimulatedVirtualMachines.ReservedIds(db, DateTime.UtcNow).ContainsAsync(id, ctx.RequestAborted))
                    return Conflict("虚拟机正在用于课堂或岗位测评。");
                if (row.Status == "running") return Conflict("请先停止模拟虚拟机再修改配置。");
                if (await ValidateVm(input, db, ctx.RequestAborted) is { } error) return Bad(error);
                var node = await db.Nodes.FindAsync([input.NodeId], ctx.RequestAborted);
                if (node is null) return Conflict("所选节点已不存在，请刷新后重试。");
                if (!ResourceProviders.IsSimulated(node.Provider)) return Bad("当前版本仅支持模拟虚拟机配置。");
                if (row.NodeId != node.Id && !node.Enabled) return Conflict("目标节点已禁止分配，不能迁入虚拟机。");
                if (await db.VirtualMachines.CountAsync(x => x.NodeId == node.Id && x.Id != id, ctx.RequestAborted) >= node.VirtualMachineQuota) return Conflict("目标节点虚拟机数量已达到可分配上限。");
                if (await db.VirtualMachines.Where(x => x.NodeId == node.Id && x.Id != id).SumAsync(x => x.DiskGb, ctx.RequestAborted) + input.DiskGb > node.TotalDiskGb) return Conflict("模拟节点磁盘容量不足。");
                row.NodeId = input.NodeId; row.Name = input.Name.Trim(); row.CpuCount = input.CpuCount; row.MemoryMb = input.MemoryMb; row.DiskGb = input.DiskGb; row.Remark = input.Remark?.Trim() ?? "";
                Audit.Add(db, ctx, "编辑模拟虚拟机", row.Name);
                await db.SaveChangesAsync(ctx.RequestAborted);
                await transaction.CommitAsync(ctx.RequestAborted);
                return Results.Ok(VmView(row, node));
            }
            finally { VmPowerLock.Release(); }
        });
        group.MapDelete("/virtual-machines/{id}", (string id, SchoolDbContext db, HttpContext ctx) => ChangeVm(id, "delete", db, ctx));
        group.MapPost("/virtual-machines/{id}/destroy", (string id, SchoolDbContext db, HttpContext ctx) => ChangeVm(id, "destroy", db, ctx));
        group.MapPost("/virtual-machines/{id}/start", (string id, SchoolDbContext db, HttpContext ctx) => ChangeVm(id, "start", db, ctx));
        group.MapPost("/virtual-machines/{id}/stop", (string id, SchoolDbContext db, HttpContext ctx) => ChangeVm(id, "stop", db, ctx));
        group.MapPost("/virtual-machines/{id}/restart", (string id, SchoolDbContext db, HttpContext ctx) => ChangeVm(id, "restart", db, ctx));
    }

    private static async Task<IResult> SetNodeEnabled(string id, bool enabled, SchoolDbContext db, SimulationSessions sessions, HttpContext ctx)
    {
        await VmPowerLock.WaitAsync(ctx.RequestAborted);
        try
        {
            var row = await db.Nodes.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (!ResourceProviders.IsSimulated(row.Provider) && row.Provider != HyperVProvider.Name)
                return ResourceProviders.Unsupported("此节点的虚拟化管理尚未配置。");
            row.Enabled = enabled;
            Audit.Add(db, ctx, enabled ? "启用节点分配" : "禁用节点分配", row.Name, "控制平台内的资源分配，不修改宿主机系统配置。");
            await db.SaveChangesAsync(ctx.RequestAborted);
            if (!enabled) sessions.RevokeNodeLearners(id);
            var usage = (await ReadNodeUsageAsync(db, [id], ctx.RequestAborted)).GetValueOrDefault(id);
            return Results.Ok(NodeView(row, usage, await db.NodeAccountAssignments.CountAsync(x => x.NodeId == id, ctx.RequestAborted)));
        }
        finally { VmPowerLock.Release(); }
    }

    private static readonly SemaphoreSlim VmPowerLock = new(1, 1);
    private static async Task<IResult> ChangeVm(string id, string action, SchoolDbContext db, HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        await VmPowerLock.WaitAsync(ct);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var row = await db.VirtualMachines.FindAsync([id], ct);
            if (row == null) return Missing();
            var node = await db.Nodes.FindAsync([row.NodeId], ct);
            if (node == null) return Missing();
            if (!ResourceProviders.IsSimulated(node.Provider)) return ResourceProviders.Unsupported("仅支持控制模拟虚拟机。");
            if (await SimulatedVirtualMachines.ReservedIds(db, DateTime.UtcNow).ContainsAsync(id, ct))
                return Conflict("虚拟机正在用于课堂或岗位测评，请在对应监控页面操作。");
            if (action == "delete" && row.Status == "running") return Conflict("请先停止模拟虚拟机。");
            if (action is "delete" or "destroy")
            {
                db.VirtualMachines.Remove(row);
                Audit.Add(db, ctx, "销毁模拟虚拟机", row.Name, row.Uuid);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Ok(new { message = "已销毁虚拟机。" });
            }
            var alreadyRunning = row.Status == "running";
            if ((action == "start" && alreadyRunning) || (action == "stop" && !alreadyRunning))
                return Results.Ok(VmView(row, node));
            if (action == "restart" && !alreadyRunning) return Conflict("请先启动虚拟机。");
            if (action != "stop")
            {
                if (!node.Enabled) return Conflict("节点已禁止分配，无法启动模拟虚拟机。");
                if (!await SimulatedVirtualMachines.CanBoot(db, node, row, ct)) return Conflict("模拟节点 CPU 或内存容量不足。");
            }
            row.Status = action == "stop" ? "stopped" : "running";
            row.StartedAt = action == "stop" ? null : DateTime.UtcNow;
            var label = action switch { "start" => "启动", "restart" => "重启", _ => "停止" };
            Audit.Add(db, ctx, label + "模拟虚拟机", row.Name, row.Uuid);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(VmView(row, node));
        }
        finally { VmPowerLock.Release(); }
    }

    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);
    private static async Task<IResult> BootstrapSimulation(SchoolDbContext db, HttpContext ctx, LicenseQuotaPolicy quota)
    {
        await BootstrapLock.WaitAsync(ctx.RequestAborted);
        try
        {
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                var ct = ctx.RequestAborted;
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var limits = await quota.ReadLimitsAsync(db, ct);
                var created = 0;
                var addedDevices = 0;
                var addedVirtualMachines = 0;
                var skippedVirtualMachines = 0;
                var type = await db.DeviceTypes.FirstOrDefaultAsync(x => x.Name == "模拟网络设备", ctx.RequestAborted);
                if (type is null) { type = new DeviceType { Name = "模拟网络设备", Remark = "内置模拟器；无需真实硬件。" }; db.DeviceTypes.Add(type); created++; }
                var cabinet = await db.Cabinets.FindAsync(["simulation-cabinet"], ctx.RequestAborted);
                if (cabinet is null && await LicenseQuotaPolicy.CheckAllocationAsync(db, limits, LicensedResource.Cabinets, 1, ct) is { } cabinetDenied) return cabinetDenied;
                if (cabinet is null) { cabinet = new Cabinet { Id = "simulation-cabinet", Name = "模拟实训机柜", Host = "simulator.local", IsSimulated = true, Remark = "平台内置模拟环境，不连接物理串口控制器。" }; db.Cabinets.Add(cabinet); created++; }
                var node = await db.Nodes.FindAsync(["simulation-node"], ctx.RequestAborted);
                if (node is null && await LicenseQuotaPolicy.CheckAllocationAsync(db, limits, LicensedResource.Nodes, 1, ct) is { } nodeDenied) return nodeDenied;
                if (node is null) { node = new TrainingNode { Id = "simulation-node", Name = "模拟计算节点 NODE-01", Host = "simulator.local", Provider = "simulated", Remark = "内置资源状态机，未启用 Hyper-V 或实际虚拟化。" }; db.Nodes.Add(node); created++; }
                foreach (var (id, name, port) in new[] { ("simulation-switch", "模拟交换机 SW-01", 1), ("simulation-router", "模拟路由器 RT-01", 2) })
                {
                    if (await db.Devices.AnyAsync(x => x.Id == id, ctx.RequestAborted)) continue;
                    if (await LicenseQuotaPolicy.CheckAllocationAsync(db, limits, LicensedResource.Devices, addedDevices + 1, ct) is { } deviceDenied) return deviceDenied;
                    db.Devices.Add(new TrainingDevice
                    {
                        Id = id,
                        Name = name,
                        CabinetId = cabinet.Id,
                        DeviceTypeId = type.Id,
                        ConnectionType = ResourceProviders.Simulated,
                        SerialPortNumber = port,
                        NodeId = ResourceProviders.IsSimulated(node.Provider) ? node.Id : null,
                        Remark = "内置模拟 CLI；支持接口配置、查看和保存。"
                    });
                    created++;
                    addedDevices++;
                }
                var allocatedVmCount = await db.VirtualMachines.CountAsync(x => x.NodeId == node.Id, ctx.RequestAborted);
                var allocatedDiskGb = await db.VirtualMachines.Where(x => x.NodeId == node.Id).SumAsync(x => x.DiskGb, ctx.RequestAborted);
                foreach (var (id, name) in new[] { ("simulation-vm-linux", "模拟 Linux 实训环境"), ("simulation-vm-network", "模拟网络实验环境") })
                {
                    if (await db.VirtualMachines.AnyAsync(x => x.Id == id, ctx.RequestAborted)) continue;
                    if (!ResourceProviders.IsSimulated(node.Provider) || !node.Enabled || allocatedVmCount >= node.VirtualMachineQuota || allocatedDiskGb + 20 > node.TotalDiskGb || node.TotalCpu < 2 || node.TotalMemoryMb < 2048) { skippedVirtualMachines++; continue; }
                    if (await LicenseQuotaPolicy.CheckAllocationAsync(db, limits, LicensedResource.VirtualMachines, addedVirtualMachines + 1, ct) is not null) { skippedVirtualMachines++; continue; }
                    var address = await SimulatedVirtualMachines.AllocateAddress(db, ct);
                    db.VirtualMachines.Add(new TrainingVirtualMachine { Id = id, NodeId = node.Id, Name = name, IpAddress = address });
                    created++; allocatedVmCount++; allocatedDiskGb += 20; addedVirtualMachines++;
                }
                Audit.Add(db, ctx, "初始化模拟环境", "内置模拟器", $"新增 {created} 项资源；重复操作保留原有配置和状态。");
                await db.SaveChangesAsync(ctx.RequestAborted);
                await transaction.CommitAsync(ct);
                var message = created == 0 ? "模拟环境已存在，原有配置和状态已保留。" : "模拟环境已创建，可进行终端和资源管理操作。";
                if (skippedVirtualMachines > 0) message += $" 按许可证额度、节点配额、容量或停用状态跳过 {skippedVirtualMachines} 台虚拟机。";
                return Results.Ok(new { created, skippedVirtualMachines, isSimulated = true, message });
            }
            finally { VmPowerLock.Release(); }
        }
        finally { BootstrapLock.Release(); }
    }

    private static string? ValidateType(DeviceTypeInput i)
        => !NameValid(i.Name) ? "请输入 1 至 100 字的设备类型名称。" : i.Newline is not (null or "" or "CRLF" or "CR" or "LF" or "\r\n" or "\r" or "\n") ? "换行符应为 CRLF、CR 或 LF。" : i.Backspace is not (null or "" or "DEL" or "BS" or "\b" or "\u007f") ? "回退键应为 DEL 或 BS。" : TextTooLong(i.Remark) ? "备注不能超过 2000 字。" : null;
    private static string? ValidateCabinet(CabinetInput i)
        => !NameValid(i.Name) ? "请输入 1 至 100 字的机柜名称。" : !i.IsSimulated && !HostValid(i.Host) ? "请输入有效的机柜主机名或 IP 地址。" : !PortValid(i.Port) || !PortValid(i.JumpPort) ? "端口应为 1 至 65535。" : !string.IsNullOrWhiteSpace(i.JumpHost) && !HostValid(i.JumpHost) ? "跳板机主机名或 IP 地址无效。" : TextTooLong(i.Remark) ? "备注不能超过 2000 字。" : null;
    private static async Task<string?> ValidateDevice(DeviceInput i, SchoolDbContext db, CancellationToken ct)
    {
        if (!NameValid(i.Name)) return "请输入 1 至 100 字的设备名称。";
        if (i.ConnectionType is not ("simulated" or "tcp" or "telnet" or "ssh")) return "连接类型应为 simulated、tcp、telnet 或 ssh。";
        if (!PortValid(i.Port) || !PortValid(i.WebPort) || i.SerialPortNumber < 1 || i.SerialPortNumber > 65535) return "端口和串口编号应为 1 至 65535。";
        if (i.WebProtocol is not ("http" or "https")) return "Web 协议应为 http 或 https。";
        if (!string.IsNullOrWhiteSpace(i.Host) && !HostValid(i.Host)) return "设备主机名或 IP 地址无效。";
        if (!await db.Cabinets.AnyAsync(x => x.Id == i.CabinetId, ct)) return "所选机柜不存在。";
        if (!await db.DeviceTypes.AnyAsync(x => x.Id == i.DeviceTypeId, ct)) return "所选设备类型不存在。";
        if (!string.IsNullOrWhiteSpace(i.NodeId))
        {
            if (!ResourceProviders.IsSimulated(i.ConnectionType)) return "仅模拟设备可以关联实训节点。";
            var id = i.NodeId.Trim();
            if (!await db.Nodes.AnyAsync(x => x.Id == id && x.Provider == ResourceProviders.Simulated, ct)) return "所选模拟节点不存在。";
        }
        return TextTooLong(i.Remark) ? "备注不能超过 2000 字。" : null;
    }
    private static string? ValidateNode(NodeInput i)
        => !NameValid(i.Name) ? "请输入 1 至 100 字的节点名称。" : i.Provider is not (ResourceProviders.Simulated or "hyperv" or "proxmox") ? "节点类型无效。" : !ResourceProviders.IsSimulated(i.Provider) && !HostValid(i.Host) ? "请输入有效的节点主机名或 IP 地址。" : !PortValid(i.Port) ? "端口应为 1 至 65535。" : i.TotalCpu is < 1 or > 4096 || i.TotalMemoryMb is < 128 or > 16777216 || i.TotalDiskGb is < 1 or > 1048576 ? "模拟节点资源容量超出支持范围。" : i.AccountQuota is < 0 or > 100000 || i.VirtualMachineQuota is < 0 or > 100000 ? "账号和虚拟机配额须为 0 至 100000 的整数，0 表示禁止新增分配。" : TextTooLong(i.Remark) ? "备注不能超过 2000 字。" : null;
    private static async Task<string?> ValidateVm(VirtualMachineInput i, SchoolDbContext db, CancellationToken ct)
    {
        if (!NameValid(i.Name)) return "请输入 1 至 100 字的虚拟机名称。";
        var node = await db.Nodes.FindAsync([i.NodeId], ct); if (node is null) return "所选节点不存在。";
        if (i.CpuCount < 1 || i.CpuCount > node.TotalCpu || i.MemoryMb < 128 || i.MemoryMb > node.TotalMemoryMb || i.DiskGb < 1 || i.DiskGb > node.TotalDiskGb) return "虚拟机资源配置超出节点容量。";
        return TextTooLong(i.Remark) ? "备注不能超过 2000 字。" : null;
    }
    private static object ConfigurationView(TrainingNode row) => new { nodeId = row.Id, nodeName = row.Name, isSimulated = true, row.IpAddress, row.SubnetMask, row.Gateway, systemTime = DateTimeOffset.UtcNow.AddSeconds(row.SimulatedClockOffsetSeconds), message = "仅更新内置模拟节点，不修改实际 Windows 主机的时间或网络。" };
    private static string? ValidateNetwork(NodeConfigurationInput input)
    {
        static bool ParseV4(string? value, out uint number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var segments = value.Split('.');
            if (segments.Length != 4 || segments.Any(x => x.Length == 0 || x.Any(c => c < '0' || c > '9') || !byte.TryParse(x, out _))) return false;
            var bytes = segments.Select(byte.Parse).ToArray();
            number = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            return true;
        }
        if (!ParseV4(input.IpAddress?.Trim(), out var ip) || ip == 0 || (ip >> 24) >= 224 || (ip >> 24) == 127) return "请输入有效的单播 IPv4 地址（不含回环或组播地址）。";
        if (!ParseV4(input.SubnetMask?.Trim(), out var mask)) return "请输入有效的 IPv4 子网掩码。";
        var inverse = ~mask;
        if (mask == 0 || inverse == 0 || (inverse & (inverse + 1)) != 0 || inverse < 3) return "子网掩码须连续，且应保留网络、广播及主机地址。";
        var network = ip & mask; var broadcast = network | inverse;
        if (ip == network || ip == broadcast) return "节点 IP 不能是网络地址或广播地址。";
        if (!string.IsNullOrWhiteSpace(input.Gateway))
        {
            if (!ParseV4(input.Gateway.Trim(), out var gateway) || (gateway & mask) != network || gateway == network || gateway == broadcast || gateway == ip) return "网关须为同一子网中的其他有效主机地址。";
        }
        return null;
    }
    private static bool NameValid(string? s) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= 100 && !s.Any(char.IsControl);
    private static bool TextTooLong(string? s) => s?.Length > 2000;
    private static bool PortValid(int p) => p is > 0 and <= 65535;
    private static bool HostValid(string? host) => !string.IsNullOrWhiteSpace(host) && host.Length <= 253 && Uri.CheckHostName(host.Trim()) != UriHostNameType.Unknown;
    private static void Apply(DeviceType x, DeviceTypeInput i) { x.Name = i.Name.Trim(); x.Newline = i.Newline switch { "CR" or "\r" => "CR", "LF" or "\n" => "LF", _ => "CRLF" }; x.Backspace = i.Backspace is "BS" or "\b" ? "BS" : "DEL"; x.Remark = i.Remark?.Trim() ?? ""; }
    private static void Apply(Cabinet x, CabinetInput i, IDataProtectionProvider p) { x.Name = i.Name.Trim(); x.Host = i.Host?.Trim() ?? ""; x.Port = i.Port; x.JumpHost = i.JumpHost?.Trim() ?? ""; x.JumpPort = i.JumpPort; x.JumpUserName = i.JumpUserName?.Trim() ?? ""; if (!string.IsNullOrEmpty(i.JumpPassword)) x.JumpPasswordProtected = p.CreateProtector("EquipmentCredentials.v1").Protect(i.JumpPassword); x.Remark = i.Remark?.Trim() ?? ""; x.IsSimulated = i.IsSimulated; }
    private static void Apply(TrainingDevice x, DeviceInput i, IDataProtectionProvider p) { x.Name = i.Name.Trim(); x.CabinetId = i.CabinetId; x.DeviceTypeId = i.DeviceTypeId; x.NodeId = string.IsNullOrWhiteSpace(i.NodeId) ? null : i.NodeId.Trim(); x.Host = i.Host?.Trim() ?? ""; x.Port = i.Port; x.ConnectionType = i.ConnectionType; x.SerialPortNumber = i.SerialPortNumber; x.WebPort = i.WebPort; x.WebProtocol = i.WebProtocol; x.UserName = i.UserName?.Trim() ?? ""; if (!string.IsNullOrEmpty(i.Password)) x.PasswordProtected = p.CreateProtector("EquipmentCredentials.v1").Protect(i.Password); x.Remark = i.Remark?.Trim() ?? ""; }
    private static void Apply(TrainingNode x, NodeInput i, IDataProtectionProvider p) { x.Name = i.Name.Trim(); x.Host = i.Host?.Trim() ?? ""; x.Provider = i.Provider; x.Port = i.Port; x.UserName = i.UserName?.Trim() ?? ""; if (!string.IsNullOrEmpty(i.Password)) x.PasswordProtected = p.CreateProtector("EquipmentCredentials.v1").Protect(i.Password); x.Enabled = i.Enabled; x.Remark = i.Remark?.Trim() ?? ""; x.TotalCpu = i.TotalCpu; x.TotalMemoryMb = i.TotalMemoryMb; x.TotalDiskGb = i.TotalDiskGb; x.AccountQuota = i.AccountQuota; x.VirtualMachineQuota = i.VirtualMachineQuota; }
    private static object CabinetView(Cabinet x, int deviceCount = 0, int activeSessionCount = 0) => new { x.Id, x.Name, x.Host, x.Port, x.JumpHost, x.JumpPort, x.JumpUserName, hasJumpPassword = x.JumpPasswordProtected.Length > 0, x.Remark, x.IsSimulated, deviceCount, activeSessionCount };
    private static async Task<string> DeviceNodeName(SchoolDbContext db, string? nodeId, CancellationToken ct)
        => nodeId is null ? "" : await db.Nodes.Where(x => x.Id == nodeId).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "";
    private static object DeviceView(TrainingDevice x, string cabinetName = "", string deviceTypeName = "", string? owner = null, string nodeName = "") => new { x.Id, x.Name, x.CabinetId, cabinetName, x.DeviceTypeId, deviceTypeName, x.NodeId, nodeName, x.Host, x.Port, x.ConnectionType, x.SerialPortNumber, x.WebPort, x.WebProtocol, x.UserName, hasPassword = x.PasswordProtected.Length > 0, x.Remark, isSimulated = ResourceProviders.IsSimulated(x.ConnectionType), sessionActive = owner is not null, occupiedBy = owner };
    private static object NodeView(TrainingNode node, NodeUsage usage = default, int accountCount = 0, HyperVMetrics? host = null)
    {
        var metrics = host == null ? GetNodeMetrics(node, usage)
            : new NodeMetrics("healthy", host.CpuUsagePercent, host.MemoryUsagePercent, host.DiskUsagePercent);
        return new
        {
            node.Id,
            node.Name,
            node.Host,
            node.Provider,
            node.Port,
            node.UserName,
            hasPassword = node.PasswordProtected.Length > 0,
            node.Enabled,
            node.Remark,
            node.TotalCpu,
            node.TotalMemoryMb,
            node.TotalDiskGb,
            isSimulated = ResourceProviders.IsSimulated(node.Provider),
            metrics.HealthStatus,
            metrics.CpuUsagePercent,
            metrics.MemoryUsagePercent,
            metrics.DiskUsagePercent,
            virtualMachineCount = host?.TotalVms ?? usage.VirtualMachineCount,
            node.VirtualMachineQuota,
            accountCount,
            node.AccountQuota
        };
    }

    private static Task<Dictionary<string, NodeUsage>> ReadNodeUsageAsync(SchoolDbContext db, string[] nodeIds, CancellationToken ct)
        => db.VirtualMachines.AsNoTracking().Where(vm => nodeIds.Contains(vm.NodeId)).GroupBy(vm => vm.NodeId)
            .Select(group => new
            {
                NodeId = group.Key,
                Usage = new NodeUsage(group.Count(), group.Count(vm => vm.Status == "running"),
                    group.Sum(vm => vm.Status == "running" ? (long)vm.CpuCount : 0L),
                    group.Sum(vm => vm.Status == "running" ? (long)vm.MemoryMb : 0L),
                    group.Sum(vm => (long)vm.DiskGb))
            })
            .ToDictionaryAsync(row => row.NodeId, row => row.Usage, ct);

    private static NodeMetrics GetNodeMetrics(TrainingNode node, NodeUsage usage)
        => ResourceProviders.IsSimulated(node.Provider)
            ? new("healthy", Math.Round(100.0 * usage.RunningCpuCount / node.TotalCpu, 1),
                Math.Round(100.0 * usage.RunningMemoryMb / node.TotalMemoryMb, 1),
                Math.Round(100.0 * usage.DiskGb / node.TotalDiskGb, 1))
            : default;

    private readonly record struct NodeUsage(int VirtualMachineCount, int RunningCount, long RunningCpuCount, long RunningMemoryMb, long DiskGb);
    private readonly record struct NodeMetrics(string? HealthStatus, double? CpuUsagePercent, double? MemoryUsagePercent, double? DiskUsagePercent);

    private static object VmView(TrainingVirtualMachine vm, TrainingNode? node, bool isReserved = false)
    {
        var simulated = ResourceProviders.IsSimulated(node?.Provider);
        return new
        {
            vm.Id, vm.Name, vm.Uuid, vm.IpAddress, vm.NodeId, nodeName = node?.Name ?? "", nodeEnabled = node?.Enabled ?? false,
            vm.Status, vm.CpuCount, vm.MemoryMb, vm.DiskGb, vm.CreatedAt, vm.StartedAt, vm.Remark,
            isSimulated = simulated, isReserved, detectionStatus = SimulatedVirtualMachines.DetectionStatus(vm, simulated)
        };
    }
    private static string? Search(HttpRequest r) => r.Query["search"].FirstOrDefault()?.Trim();
    private static IQueryable<T> Slice<T>(IQueryable<T> q, HttpRequest r) { var (page, size) = Validation.Page(r); return q.Skip((page - 1) * size).Take(size); }
    private static async Task<object> Page<T>(IQueryable<T> q, HttpRequest r, CancellationToken ct = default) => new { total = await q.CountAsync(ct), items = await Slice(q, r).ToListAsync(ct) };
    private static IResult Bad(string m) => Results.BadRequest(new { message = m });
    private static IResult Conflict(string m) => Results.Conflict(new { message = m });
    private static IResult Missing() => Results.NotFound(new { message = "记录不存在或已被删除。" });
    internal static async Task<(bool Success, string Message)> Probe(string host, int port, CancellationToken ct)
    {
        if (!HostValid(host) || !PortValid(port)) return (false, "目标主机或端口配置无效。");
        var target = await NetworkTargets.ResolveAllowedAsync(host, ct);
        if (!target.Allowed) return (false, target.Message);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try { using var client = new TcpClient(); await client.ConnectAsync(target.Addresses[0], port, timeout.Token); return (true, "TCP 可达。"); }
        catch (OperationCanceledException) { return (false, "连接超时或已取消。"); }
        catch (SocketException) { return (false, "无法建立 TCP 连接，请检查地址、端口及网络。"); }
    }
    private sealed record ConnectionProbe(string Id, string Name, bool Success, string Message, bool IsSimulated, string Level);
}
