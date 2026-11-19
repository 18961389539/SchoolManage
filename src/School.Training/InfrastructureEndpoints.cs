using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static partial class InfrastructureEndpoints
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
        => services.AddSingleton<SimulationSessions>();

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
        group.MapPost("/cabinets", async (CabinetInput input, SchoolDbContext db, IDataProtectionProvider protection, HttpContext ctx) =>
        {
            if (ValidateCabinet(input) is { } error) return Bad(error);
            if (await db.Cabinets.AnyAsync(x => x.Name == input.Name.Trim())) return Conflict("机柜名称已存在。");
            var row = new Cabinet(); Apply(row, input, protection); db.Cabinets.Add(row);
            Audit.Add(db, ctx, "新增机柜", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(CabinetView(row));
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
        group.MapPost("/cabinets/test-connections", async (InfrastructureIds input, SchoolDbContext db, CancellationToken ct) =>
        {
            if (input.Ids is not { Length: > 0 and <= 50 }) return Bad("请选择 1 至 50 个机柜。");
            var rows = await db.Cabinets.AsNoTracking().Where(x => input.Ids.Contains(x.Id)).ToListAsync(ct);
            if (rows.Count != input.Ids.Distinct().Count()) return Bad("部分机柜已不存在，请刷新列表。");
            var tasks = rows.Select(async row =>
            {
                if (row.IsSimulated) return new ConnectionProbe(row.Id, true, "模拟控制器响应正常。", true, "simulation");
                var host = string.IsNullOrWhiteSpace(row.JumpHost) ? row.Host : row.JumpHost;
                var port = string.IsNullOrWhiteSpace(row.JumpHost) ? row.Port : row.JumpPort;
                var result = await Probe(host, port, ct);
                return new ConnectionProbe(row.Id, result.Success, result.Success ? (string.IsNullOrWhiteSpace(row.JumpHost) ? "TCP 握手成功；未验证串口协议或认证。" : "跳板机 TCP 握手成功；未验证到设备的后续链路。") : result.Message, false, "tcp");
            });
            return Results.Ok(new { items = await Task.WhenAll(tasks) });
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
            if (request.Query["cabinetId"].FirstOrDefault() is { Length: > 0 } cabinetId) q = q.Where(x => x.CabinetId == cabinetId);
            var total = await q.CountAsync(ct); var rows = await Slice(q.OrderBy(x => x.Name), request).ToListAsync(ct);
            var cabinets = await db.Cabinets.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var types = await db.DeviceTypes.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            return Results.Ok(new { total, items = rows.Select(x => DeviceView(x, cabinets.GetValueOrDefault(x.CabinetId, ""), types.GetValueOrDefault(x.DeviceTypeId, ""), sessions.Owner(x.Id))) });
        });
        group.MapPost("/devices", async (DeviceInput input, SchoolDbContext db, IDataProtectionProvider protection, HttpContext ctx) =>
        {
            if (await ValidateDevice(input, db) is { } error) return Bad(error);
            var row = new TrainingDevice(); Apply(row, input, protection); db.Devices.Add(row);
            Audit.Add(db, ctx, "新增设备", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(DeviceView(row));
        });
        group.MapPut("/devices/{id}", async (string id, DeviceInput input, SchoolDbContext db, IDataProtectionProvider protection, SimulationSessions sessions, HttpContext ctx) =>
        {
            if (await ValidateDevice(input, db) is { } error) return Bad(error);
            var row = await db.Devices.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (sessions.Owner(id) is not null) return Conflict("设备存在活动会话，请先关闭终端或回收机柜。");
            Apply(row, input, protection); Audit.Add(db, ctx, "编辑设备", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(DeviceView(row));
        });
        group.MapDelete("/devices/{id}", async (string id, SchoolDbContext db, SimulationSessions sessions, HttpContext ctx) =>
        {
            var row = await db.Devices.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (sessions.Owner(id) is not null) return Conflict("设备存在活动会话，请先关闭终端或回收机柜。");
            db.Devices.Remove(row); Audit.Add(db, ctx, "删除设备", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { message = "已删除。" });
        });
        group.MapPost("/devices/{id}/connect-test", async (string id, SchoolDbContext db, CancellationToken ct) =>
        {
            var row = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct); if (row is null) return Missing();
            if (row.ConnectionType == "simulated") return Results.Ok(new ConnectionProbe(id, true, "模拟设备响应正常。", true, "simulation"));
            var cabinet = await db.Cabinets.FindAsync([row.CabinetId], ct);
            if (!string.IsNullOrEmpty(cabinet?.JumpHost)) return Results.Ok(new ConnectionProbe(id, false, "真实跳板机链路尚未配置验证；不能将直连探测作为设备连接成功。", false, "unsupported"));
            var probe = await Probe(string.IsNullOrWhiteSpace(row.Host) ? cabinet?.Host ?? "" : row.Host, row.Port, ct);
            return Results.Ok(new ConnectionProbe(id, probe.Success, probe.Success ? "仅 TCP 握手成功，未验证设备认证与控制协议。" : probe.Message, false, "tcp"));
        });
        group.MapGet("/terminal/{deviceId}", SimulationTerminal.Handle);
        group.MapPost("/simulation/bootstrap", BootstrapSimulation);
        group.MapGet("/server/info", () => Results.Ok(InfrastructureHostInfo.Collect()));

        MapNodes(group);
        return api;
    }

    private static void MapNodes(RouteGroupBuilder group)
    {
        group.MapGet("/nodes", async (SchoolDbContext db, HttpRequest request, CancellationToken ct) =>
        {
            var q = db.Nodes.AsNoTracking();
            if (Search(request) is { Length: > 0 } s) q = q.Where(x => x.Name.Contains(s) || x.Host.Contains(s));
            var total = await q.CountAsync(ct); var rows = await Slice(q.OrderBy(x => x.Name), request).ToListAsync(ct);
            var nodeIds = rows.Select(x => x.Id).ToArray();
            var vmCounts = await db.VirtualMachines.AsNoTracking().Where(x => nodeIds.Contains(x.NodeId)).GroupBy(x => x.NodeId).Select(g => new { NodeId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.NodeId, x => x.Count, ct);
            var accountCounts = await db.NodeAccountAssignments.AsNoTracking().Where(x => nodeIds.Contains(x.NodeId)).GroupBy(x => x.NodeId).Select(g => new { NodeId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.NodeId, x => x.Count, ct);
            return Results.Ok(new { total, items = rows.Select(x => NodeView(x, vmCounts.GetValueOrDefault(x.Id), accountCounts.GetValueOrDefault(x.Id))) });
        });
        group.MapPost("/nodes", async (NodeInput input, SchoolDbContext db, IDataProtectionProvider protection, HttpContext ctx) =>
        {
            if (ValidateNode(input) is { } error) return Bad(error);
            if (await db.Nodes.AnyAsync(x => x.Name == input.Name.Trim())) return Conflict("节点名称已存在。");
            var row = new TrainingNode(); Apply(row, input, protection); db.Nodes.Add(row);
            Audit.Add(db, ctx, "新增节点", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(NodeView(row));
        });
        group.MapPut("/nodes/{id}", async (string id, NodeInput input, SchoolDbContext db, IDataProtectionProvider protection, HttpContext ctx) =>
        {
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                if (ValidateNode(input) is { } error) return Bad(error);
                var row = await db.Nodes.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
                if (await db.Nodes.AnyAsync(x => x.Id != id && x.Name == input.Name.Trim())) return Conflict("节点名称已存在。");
                var vms = await db.VirtualMachines.Where(x => x.NodeId == id).ToListAsync(ctx.RequestAborted);
                var accountCount = await db.NodeAccountAssignments.CountAsync(x => x.NodeId == id, ctx.RequestAborted);
                if ((vms.Count > 0 || accountCount > 0) && input.Provider != row.Provider) return Conflict("节点仍有关联虚拟机或师生账号，不能更改提供程序。");
                if (input.AccountQuota < accountCount || input.VirtualMachineQuota < vms.Count) return Conflict("账号或虚拟机配额不能低于已有分配数量，请先释放关联资源。");
                if (vms.Where(x => x.Status == "running").Sum(x => x.CpuCount) > input.TotalCpu || vms.Where(x => x.Status == "running").Sum(x => x.MemoryMb) > input.TotalMemoryMb || vms.Sum(x => x.DiskGb) > input.TotalDiskGb)
                    return Conflict("节点容量不能低于已分配资源。");
                Apply(row, input, protection); Audit.Add(db, ctx, "编辑节点", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(NodeView(row, vms.Count, accountCount));
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
        group.MapGet("/nodes/{id}/status", async (string id, SchoolDbContext db, CancellationToken ct) =>
        {
            var row = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct); if (row is null) return Missing();
            if (row.Provider != "simulated") return Results.Json(new { message = "当前交付环境采用内置模拟器；真实虚拟化提供程序尚未配置与验证。", provider = row.Provider, available = false, isSimulated = false }, statusCode: 501);
            var vms = await db.VirtualMachines.AsNoTracking().Where(x => x.NodeId == id).ToListAsync(ct);
            var running = vms.Where(x => x.Status == "running").ToArray();
            var accountCount = await db.NodeAccountAssignments.CountAsync(x => x.NodeId == id, ct);
            return Results.Ok(new { row.Id, row.Name, row.Host, provider = row.Provider, isSimulated = true, enabled = row.Enabled, healthStatus = "healthy", status = row.Enabled ? "ready" : "disabled", cpuUsagePercent = Math.Round(100.0 * running.Sum(x => x.CpuCount) / row.TotalCpu, 1), memoryUsagePercent = Math.Round(100.0 * running.Sum(x => x.MemoryMb) / row.TotalMemoryMb, 1), diskUsagePercent = Math.Round(100.0 * vms.Sum(x => x.DiskGb) / row.TotalDiskGb, 1), row.TotalCpu, row.TotalMemoryMb, row.TotalDiskGb, virtualMachineCount = vms.Count, row.VirtualMachineQuota, runningCount = running.Length, accountCount, row.AccountQuota, metricsSource = "simulator-resource-reservation", message = "模拟指标按资源分配计算；账号数量表示分配到节点的现有师生账号，不创建操作系统账户。", collectedAt = DateTime.UtcNow });
        });
        MapNodeAccounts(group);
        group.MapPost("/nodes/{id}/enable", (string id, SchoolDbContext db, HttpContext ctx) => SetNodeEnabled(id, true, db, ctx));
        group.MapPost("/nodes/{id}/disable", (string id, SchoolDbContext db, HttpContext ctx) => SetNodeEnabled(id, false, db, ctx));
        group.MapGet("/nodes/{id}/configuration", async (string id, SchoolDbContext db, CancellationToken ct) =>
        {
            var row = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct); if (row is null) return Missing();
            if (row.Provider != "simulated") return Results.Json(new { message = "当前系统仅支持模拟节点配置；不会修改实际 Windows 主机。" }, statusCode: 501);
            return Results.Ok(ConfigurationView(row));
        });
        group.MapPut("/nodes/{id}/configuration", async (string id, NodeConfigurationInput input, SchoolDbContext db, HttpContext ctx) =>
        {
            var row = await db.Nodes.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (row.Provider != "simulated") return Results.Json(new { message = "当前系统仅支持模拟节点配置；不会修改实际 Windows 主机。" }, statusCode: 501);
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
            if (Search(request) is { Length: > 0 } s) q = q.Where(x => x.Name.Contains(s));
            if (request.Query["nodeId"].FirstOrDefault() is { Length: > 0 } nodeId) q = q.Where(x => x.NodeId == nodeId);
            var total = await q.CountAsync(ct); var rows = await Slice(q.OrderBy(x => x.Name), request).ToListAsync(ct);
            var nodes = await db.Nodes.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            return Results.Ok(new { total, items = rows.Select(x => VmView(x, nodes.GetValueOrDefault(x.NodeId))) });
        });
        group.MapPost("/virtual-machines", async (VirtualMachineInput input, SchoolDbContext db, HttpContext ctx) =>
        {
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                if (await ValidateVm(input, db) is { } error) return Bad(error);
                var node = await db.Nodes.FindAsync([input.NodeId], ctx.RequestAborted);
                if (node!.Provider != "simulated") return Bad("当前版本仅支持创建模拟虚拟机。");
                if (!node.Enabled) return Conflict("节点已禁止分配，请先启用节点。");
                if (await db.VirtualMachines.CountAsync(x => x.NodeId == node.Id, ctx.RequestAborted) >= node.VirtualMachineQuota) return Conflict("节点虚拟机数量已达到可分配上限。");
                if (await db.VirtualMachines.Where(x => x.NodeId == node.Id).SumAsync(x => x.DiskGb, ctx.RequestAborted) + input.DiskGb > node.TotalDiskGb) return Conflict("模拟节点磁盘容量不足。");
                var row = new TrainingVirtualMachine { NodeId = input.NodeId, Name = input.Name.Trim(), CpuCount = input.CpuCount, MemoryMb = input.MemoryMb, DiskGb = input.DiskGb, Remark = input.Remark?.Trim() ?? "" };
                db.VirtualMachines.Add(row); Audit.Add(db, ctx, "创建模拟虚拟机", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(VmView(row, node));
            }
            finally { VmPowerLock.Release(); }
        });
        group.MapPut("/virtual-machines/{id}", async (string id, VirtualMachineInput input, SchoolDbContext db, HttpContext ctx) =>
        {
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                var row = await db.VirtualMachines.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
                if (row.Status == "running") return Conflict("请先停止模拟虚拟机再修改配置。");
                if (await ValidateVm(input, db) is { } error) return Bad(error);
                var node = await db.Nodes.FindAsync([input.NodeId], ctx.RequestAborted);
                if (node!.Provider != "simulated") return Bad("当前版本仅支持模拟虚拟机配置。");
                if (row.NodeId != node.Id && !node.Enabled) return Conflict("目标节点已禁止分配，不能迁入虚拟机。");
                if (await db.VirtualMachines.CountAsync(x => x.NodeId == node.Id && x.Id != id, ctx.RequestAborted) >= node.VirtualMachineQuota) return Conflict("目标节点虚拟机数量已达到可分配上限。");
                if (await db.VirtualMachines.Where(x => x.NodeId == node.Id && x.Id != id).SumAsync(x => x.DiskGb, ctx.RequestAborted) + input.DiskGb > node.TotalDiskGb) return Conflict("模拟节点磁盘容量不足。");
                row.NodeId = input.NodeId; row.Name = input.Name.Trim(); row.CpuCount = input.CpuCount; row.MemoryMb = input.MemoryMb; row.DiskGb = input.DiskGb; row.Remark = input.Remark?.Trim() ?? "";
                Audit.Add(db, ctx, "编辑模拟虚拟机", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(VmView(row, node));
            }
            finally { VmPowerLock.Release(); }
        });
        group.MapDelete("/virtual-machines/{id}", async (string id, SchoolDbContext db, HttpContext ctx) =>
        {
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                var row = await db.VirtualMachines.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
                if (row.Status == "running") return Conflict("请先停止模拟虚拟机。");
                db.VirtualMachines.Remove(row); Audit.Add(db, ctx, "删除模拟虚拟机", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { message = "已删除。" });
            }
            finally { VmPowerLock.Release(); }
        });
        group.MapPost("/virtual-machines/{id}/start", (string id, SchoolDbContext db, HttpContext ctx) => SetVmPower(id, true, db, ctx));
        group.MapPost("/virtual-machines/{id}/stop", (string id, SchoolDbContext db, HttpContext ctx) => SetVmPower(id, false, db, ctx));
    }

    private static async Task<IResult> SetNodeEnabled(string id, bool enabled, SchoolDbContext db, HttpContext ctx)
    {
        await VmPowerLock.WaitAsync(ctx.RequestAborted);
        try
        {
            var row = await db.Nodes.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            if (row.Provider != "simulated") return Results.Json(new { message = "真实节点启停尚未适配；当前操作仅适用于模拟调度。" }, statusCode: 501);
            row.Enabled = enabled; Audit.Add(db, ctx, enabled ? "启用模拟节点分配" : "禁用模拟节点分配", row.Name, "仅控制模拟器是否接受新的资源分配，不修改 Windows 虚拟化角色或 BIOS。");
            await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(NodeView(row, await db.VirtualMachines.CountAsync(x => x.NodeId == id, ctx.RequestAborted), await db.NodeAccountAssignments.CountAsync(x => x.NodeId == id, ctx.RequestAborted)));
        }
        finally { VmPowerLock.Release(); }
    }

    private static readonly SemaphoreSlim VmPowerLock = new(1, 1);
    private static async Task<IResult> SetVmPower(string id, bool start, SchoolDbContext db, HttpContext ctx)
    {
        await VmPowerLock.WaitAsync(ctx.RequestAborted);
        try
        {
            var row = await db.VirtualMachines.FindAsync([id], ctx.RequestAborted); if (row is null) return Missing();
            var node = await db.Nodes.FindAsync([row.NodeId], ctx.RequestAborted); if (node is null) return Missing();
            if (node.Provider != "simulated") return Bad("当前版本仅支持模拟虚拟机电源操作。");
            if (start)
            {
                if (!node.Enabled) return Conflict("节点已禁止分配，无法启动新的模拟虚拟机。");
                var running = await db.VirtualMachines.Where(x => x.NodeId == node.Id && x.Status == "running" && x.Id != id).ToListAsync(ctx.RequestAborted);
                if (running.Sum(x => x.CpuCount) + row.CpuCount > node.TotalCpu || running.Sum(x => x.MemoryMb) + row.MemoryMb > node.TotalMemoryMb) return Conflict("模拟节点 CPU 或内存容量不足。");
                row.Status = "running"; row.StartedAt = DateTime.UtcNow;
            }
            else { row.Status = "stopped"; row.StartedAt = null; }
            Audit.Add(db, ctx, start ? "启动模拟虚拟机" : "停止模拟虚拟机", row.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(VmView(row, node));
        }
        finally { VmPowerLock.Release(); }
    }

    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);
    private static async Task<IResult> BootstrapSimulation(SchoolDbContext db, HttpContext ctx)
    {
        await BootstrapLock.WaitAsync(ctx.RequestAborted);
        try
        {
            await VmPowerLock.WaitAsync(ctx.RequestAborted);
            try
            {
                var created = 0;
                var skippedVirtualMachines = 0;
                var type = await db.DeviceTypes.FirstOrDefaultAsync(x => x.Name == "模拟网络设备", ctx.RequestAborted);
                if (type is null) { type = new DeviceType { Name = "模拟网络设备", Remark = "内置模拟器；无需真实硬件。" }; db.DeviceTypes.Add(type); created++; }
                var cabinet = await db.Cabinets.FindAsync(["simulation-cabinet"], ctx.RequestAborted);
                if (cabinet is null) { cabinet = new Cabinet { Id = "simulation-cabinet", Name = "模拟实训机柜", Host = "simulator.local", IsSimulated = true, Remark = "平台内置模拟环境，不连接物理串口控制器。" }; db.Cabinets.Add(cabinet); created++; }
                foreach (var (id, name, port) in new[] { ("simulation-switch", "模拟交换机 SW-01", 1), ("simulation-router", "模拟路由器 RT-01", 2) })
                    if (!await db.Devices.AnyAsync(x => x.Id == id, ctx.RequestAborted)) { db.Devices.Add(new TrainingDevice { Id = id, Name = name, CabinetId = cabinet.Id, DeviceTypeId = type.Id, ConnectionType = "simulated", SerialPortNumber = port, Remark = "内置模拟 CLI；支持接口配置、查看和保存。" }); created++; }
                var node = await db.Nodes.FindAsync(["simulation-node"], ctx.RequestAborted);
                if (node is null) { node = new TrainingNode { Id = "simulation-node", Name = "模拟计算节点 NODE-01", Host = "simulator.local", Provider = "simulated", Remark = "内置资源状态机，未启用 Hyper-V 或实际虚拟化。" }; db.Nodes.Add(node); created++; }
                var allocatedVmCount = await db.VirtualMachines.CountAsync(x => x.NodeId == node.Id, ctx.RequestAborted);
                var allocatedDiskGb = await db.VirtualMachines.Where(x => x.NodeId == node.Id).SumAsync(x => x.DiskGb, ctx.RequestAborted);
                foreach (var (id, name) in new[] { ("simulation-vm-linux", "模拟 Linux 实训环境"), ("simulation-vm-network", "模拟网络实验环境") })
                {
                    if (await db.VirtualMachines.AnyAsync(x => x.Id == id, ctx.RequestAborted)) continue;
                    if (node.Provider != "simulated" || !node.Enabled || allocatedVmCount >= node.VirtualMachineQuota || allocatedDiskGb + 20 > node.TotalDiskGb || node.TotalCpu < 2 || node.TotalMemoryMb < 2048) { skippedVirtualMachines++; continue; }
                    db.VirtualMachines.Add(new TrainingVirtualMachine { Id = id, NodeId = node.Id, Name = name }); created++; allocatedVmCount++; allocatedDiskGb += 20;
                }
                Audit.Add(db, ctx, "初始化模拟环境", "内置模拟器", $"新增 {created} 项资源；重复操作保留原有配置和状态。");
                await db.SaveChangesAsync(ctx.RequestAborted);
                var message = created == 0 ? "模拟环境已存在，原有配置和状态已保留。" : "模拟环境已创建，可进行终端和资源管理操作。";
                if (skippedVirtualMachines > 0) message += $" 按节点配额、容量或停用状态跳过 {skippedVirtualMachines} 台虚拟机。";
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
    private static async Task<string?> ValidateDevice(DeviceInput i, SchoolDbContext db)
    {
        if (!NameValid(i.Name)) return "请输入 1 至 100 字的设备名称。";
        if (i.ConnectionType is not ("simulated" or "tcp" or "telnet" or "ssh")) return "连接类型应为 simulated、tcp、telnet 或 ssh。";
        if (!PortValid(i.Port) || !PortValid(i.WebPort) || i.SerialPortNumber < 1 || i.SerialPortNumber > 65535) return "端口和串口编号应为 1 至 65535。";
        if (i.WebProtocol is not ("http" or "https")) return "Web 协议应为 http 或 https。";
        if (!string.IsNullOrWhiteSpace(i.Host) && !HostValid(i.Host)) return "设备主机名或 IP 地址无效。";
        if (!await db.Cabinets.AnyAsync(x => x.Id == i.CabinetId)) return "所选机柜不存在。";
        if (!await db.DeviceTypes.AnyAsync(x => x.Id == i.DeviceTypeId)) return "所选设备类型不存在。";
        return TextTooLong(i.Remark) ? "备注不能超过 2000 字。" : null;
    }
    private static string? ValidateNode(NodeInput i)
        => !NameValid(i.Name) ? "请输入 1 至 100 字的节点名称。" : i.Provider is not ("simulated" or "hyperv" or "proxmox") ? "节点类型无效。" : i.Provider != "simulated" && !HostValid(i.Host) ? "请输入有效的节点主机名或 IP 地址。" : !PortValid(i.Port) ? "端口应为 1 至 65535。" : i.TotalCpu is < 1 or > 4096 || i.TotalMemoryMb is < 128 or > 16777216 || i.TotalDiskGb is < 1 or > 1048576 ? "模拟节点资源容量超出支持范围。" : i.AccountQuota is < 0 or > 100000 || i.VirtualMachineQuota is < 0 or > 100000 ? "账号和虚拟机配额须为 0 至 100000 的整数，0 表示禁止新增分配。" : TextTooLong(i.Remark) ? "备注不能超过 2000 字。" : null;
    private static async Task<string?> ValidateVm(VirtualMachineInput i, SchoolDbContext db)
    {
        if (!NameValid(i.Name)) return "请输入 1 至 100 字的虚拟机名称。";
        var node = await db.Nodes.FindAsync(i.NodeId); if (node is null) return "所选节点不存在。";
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
    private static void Apply(TrainingDevice x, DeviceInput i, IDataProtectionProvider p) { x.Name = i.Name.Trim(); x.CabinetId = i.CabinetId; x.DeviceTypeId = i.DeviceTypeId; x.Host = i.Host?.Trim() ?? ""; x.Port = i.Port; x.ConnectionType = i.ConnectionType; x.SerialPortNumber = i.SerialPortNumber; x.WebPort = i.WebPort; x.WebProtocol = i.WebProtocol; x.UserName = i.UserName?.Trim() ?? ""; if (!string.IsNullOrEmpty(i.Password)) x.PasswordProtected = p.CreateProtector("EquipmentCredentials.v1").Protect(i.Password); x.Remark = i.Remark?.Trim() ?? ""; }
    private static void Apply(TrainingNode x, NodeInput i, IDataProtectionProvider p) { x.Name = i.Name.Trim(); x.Host = i.Host?.Trim() ?? ""; x.Provider = i.Provider; x.Port = i.Port; x.UserName = i.UserName?.Trim() ?? ""; if (!string.IsNullOrEmpty(i.Password)) x.PasswordProtected = p.CreateProtector("EquipmentCredentials.v1").Protect(i.Password); x.Enabled = i.Enabled; x.Remark = i.Remark?.Trim() ?? ""; x.TotalCpu = i.TotalCpu; x.TotalMemoryMb = i.TotalMemoryMb; x.TotalDiskGb = i.TotalDiskGb; x.AccountQuota = i.AccountQuota; x.VirtualMachineQuota = i.VirtualMachineQuota; }
    private static object CabinetView(Cabinet x, int deviceCount = 0, int activeSessionCount = 0) => new { x.Id, x.Name, x.Host, x.Port, x.JumpHost, x.JumpPort, x.JumpUserName, hasJumpPassword = x.JumpPasswordProtected.Length > 0, x.Remark, x.IsSimulated, deviceCount, activeSessionCount };
    private static object DeviceView(TrainingDevice x, string cabinetName = "", string deviceTypeName = "", string? owner = null) => new { x.Id, x.Name, x.CabinetId, cabinetName, x.DeviceTypeId, deviceTypeName, x.Host, x.Port, x.ConnectionType, x.SerialPortNumber, x.WebPort, x.WebProtocol, x.UserName, hasPassword = x.PasswordProtected.Length > 0, x.Remark, isSimulated = x.ConnectionType == "simulated", sessionActive = owner is not null, occupiedBy = owner };
    private static object NodeView(TrainingNode x, int virtualMachineCount = 0, int accountCount = 0) => new { x.Id, x.Name, x.Host, x.Provider, x.Port, x.UserName, hasPassword = x.PasswordProtected.Length > 0, x.Enabled, x.Remark, x.TotalCpu, x.TotalMemoryMb, x.TotalDiskGb, isSimulated = x.Provider == "simulated", virtualMachineCount, x.VirtualMachineQuota, accountCount, x.AccountQuota };
    private static object VmView(TrainingVirtualMachine x, TrainingNode? node) => new { x.Id, x.Name, x.NodeId, nodeName = node?.Name ?? "", x.Status, x.CpuCount, x.MemoryMb, x.DiskGb, x.CreatedAt, x.StartedAt, x.Remark, isSimulated = node?.Provider == "simulated" };
    private static string? Search(HttpRequest r) => r.Query["search"].FirstOrDefault()?.Trim();
    private static IQueryable<T> Slice<T>(IQueryable<T> q, HttpRequest r) { var page = int.TryParse(r.Query["page"], out var p) ? Math.Clamp(p, 1, 100000) : 1; var size = int.TryParse(r.Query["pageSize"], out var s) ? Math.Clamp(s, 1, 500) : 20; return q.Skip((page - 1) * size).Take(size); }
    private static async Task<object> Page<T>(IQueryable<T> q, HttpRequest r, CancellationToken ct = default) => new { total = await q.CountAsync(ct), items = await Slice(q, r).ToListAsync(ct) };
    private static IResult Bad(string m) => Results.BadRequest(new { message = m });
    private static IResult Conflict(string m) => Results.Conflict(new { message = m });
    private static IResult Missing() => Results.NotFound(new { message = "记录不存在或已被删除。" });
    private static async Task<(bool Success, string Message)> Probe(string host, int port, CancellationToken ct)
    {
        if (!HostValid(host) || !PortValid(port)) return (false, "目标主机或端口配置无效。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try { using var client = new TcpClient(); await client.ConnectAsync(host, port, timeout.Token); return (true, "TCP 可达。"); }
        catch (OperationCanceledException) { return (false, "连接超时或已取消。"); }
        catch (SocketException) { return (false, "无法建立 TCP 连接，请检查地址、端口及网络。"); }
    }
    private sealed record ConnectionProbe(string Id, bool Success, string Message, bool IsSimulated, string Level);
}
