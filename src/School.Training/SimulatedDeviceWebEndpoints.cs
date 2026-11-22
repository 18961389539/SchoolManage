using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class SimulatedDeviceWebEndpoints
{
    public static void MapSimulatedDeviceWebEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/devices/{deviceId}/web-console").RequireAuthorization();
        group.MapGet("", Get);
        group.MapPut("", Update);
        group.MapPut("/startup", SaveStartup);
    }

    private static async Task<IResult> Get(string deviceId, HttpContext ctx, SchoolDbContext db, SimulationSessions sessions)
    {
        var device = await FindDevice(db, ctx.User, deviceId, ctx.RequestAborted);
        if (device is null) return NotAvailable();
        return Results.Ok(View(device, busy: sessions.Owner(deviceId) is not null));
    }

    private static Task<IResult> Update(string deviceId, WebConfigurationInput input, HttpContext ctx, SchoolDbContext db, SimulationSessions sessions)
        => Edit(deviceId, input.Version, input, ctx, db, sessions);

    private static Task<IResult> SaveStartup(string deviceId, WebVersionInput input, HttpContext ctx, SchoolDbContext db, SimulationSessions sessions)
        => Edit(deviceId, input.Version, null, ctx, db, sessions);

    private static async Task<IResult> Edit(string deviceId, string version, WebConfigurationInput? input, HttpContext ctx, SchoolDbContext db, SimulationSessions sessions)
    {
        var device = await FindDevice(db, ctx.User, deviceId, ctx.RequestAborted);
        if (device is null) return NotAvailable();
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var stamp = ctx.User.FindFirstValue("stamp")!;
        var learnerNodeId = ctx.User.IsInRole("Admin") ? null : device.NodeId;
        if (!sessions.TryAcquire(deviceId, userId, ctx.User.Identity?.Name ?? "unknown", stamp, ctx.RequestAborted, out var lease, learnerNodeId))
            return Conflict("设备正由终端或其他配置会话使用。请等待会话结束后刷新再试。");

        try
        {
            var ct = lease.Token;
            // Read inside the lease so the last CLI or Web write is visible before validating the edit.
            device = await FindDevice(db, ctx.User, deviceId, ct);
            if (device is null) return NotAvailable();
            if (learnerNodeId is not null && device.NodeId != learnerNodeId) return NotAvailable();
            if (!string.Equals(SimulationDeviceConfiguration.Version(device), version, StringComparison.Ordinal))
                return Conflict("设备配置已变更。请刷新后根据当前配置重新编辑。");

            var running = SimulationDeviceConfiguration.Read(device);
            if (input is not null && Apply(input, running) is { } invalid) return invalid;

            var configurationJson = input is null ? device.ConfigurationJson : SimulationDeviceConfiguration.Serialize(running);
            var savedConfigurationJson = input is null ? SimulationDeviceConfiguration.Serialize(running) : device.SavedConfigurationJson;
            ct.ThrowIfCancellationRequested();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Authorization and snapshot match are predicates in the write itself, so revocation or
            // a concurrent change to device settings cannot slip between validation and persistence.
            var updated = await TrainingResourceAccess.AccessibleDevices(db, userId, stamp)
                .Where(row => row.Id == deviceId && row.ConnectionType == ResourceProviders.Simulated
                    && row.NodeId == device.NodeId && row.Name == device.Name
                    && row.ConfigurationJson == device.ConfigurationJson && row.SavedConfigurationJson == device.SavedConfigurationJson)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ConfigurationJson, configurationJson)
                    .SetProperty(row => row.SavedConfigurationJson, savedConfigurationJson), ct);
            if (updated == 0) return Conflict("设备配置或访问权限已变更。请刷新后重试。");

            Audit.Add(db, ctx, input is null ? "保存模拟设备启动配置" : "更新模拟设备运行配置", device.Name, "模拟 Web 管理。");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            device.ConfigurationJson = configurationJson;
            device.SavedConfigurationJson = savedConfigurationJson;
            return Results.Ok(View(device, busy: false));
        }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            return Conflict("设备会话或访问权限已撤销。请刷新后重试。");
        }
        finally
        {
            sessions.Release(lease);
        }
    }

    private static IResult? Apply(WebConfigurationInput input, SimulatedDeviceState running)
    {
        if (!SimulationDeviceConfiguration.IsValidHostname(input.Hostname))
            return Results.BadRequest(new { message = "主机名须为 1 至 63 个字母、数字、连字符或下划线。" });
        if (input.Interfaces is null || input.Interfaces.Length != running.Interfaces.Count)
            return Results.BadRequest(new { message = "接口列表不完整，请刷新后重试。" });
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in input.Interfaces)
        {
            if (item is null || string.IsNullOrEmpty(item.Name) || !names.Add(item.Name) || !running.Interfaces.TryGetValue(item.Name, out var current))
                return Results.BadRequest(new { message = "接口不存在或重复，请刷新后重试。" });
            var description = item.Description?.Trim();
            if (item.Enabled is null || !SimulationDeviceConfiguration.IsValidDescription(description))
                return Results.BadRequest(new { message = "请选择接口状态，接口说明须在 100 字以内且不含控制字符。" });
            current.Enabled = item.Enabled.Value;
            current.Description = description!;
        }
        running.Hostname = input.Hostname;
        return null;
    }

    private static Task<TrainingDevice?> FindDevice(SchoolDbContext db, ClaimsPrincipal user, string deviceId, CancellationToken ct)
        => TrainingResourceAccess.AccessibleDevices(db, user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", user.FindFirstValue("stamp") ?? "")
            .AsNoTracking().SingleOrDefaultAsync(row => row.Id == deviceId && row.ConnectionType == ResourceProviders.Simulated, ct);

    private static object View(TrainingDevice device, bool busy)
    {
        var running = SimulationDeviceConfiguration.Read(device);
        var startup = SimulationDeviceConfiguration.Read(device, startup: true);
        var hasStartupConfiguration = SimulationDeviceConfiguration.HasStartupConfiguration(device);
        var interfaces = running.Interfaces.Select(pair => new { name = pair.Key, pair.Value.Enabled, pair.Value.Description }).ToArray();
        return new
        {
            device.Id,
            device.Name,
            isSimulated = true,
            running.Hostname,
            interfaces,
            enabledInterfaceCount = interfaces.Count(@interface => @interface.Enabled),
            busy,
            hasStartupConfiguration,
            startupHostname = hasStartupConfiguration ? startup.Hostname : null,
            unsavedChanges = !string.Equals(running.Hostname, startup.Hostname, StringComparison.Ordinal)
                || running.Interfaces.Count != startup.Interfaces.Count
                || running.Interfaces.Any(pair => !startup.Interfaces.TryGetValue(pair.Key, out var saved)
                    || pair.Value.Enabled != saved.Enabled || pair.Value.Description != saved.Description),
            version = SimulationDeviceConfiguration.Version(device)
        };
    }

    private static IResult NotAvailable() => Results.NotFound(new { message = "设备不存在或无 Web 管理权限。" });
    private static IResult Conflict(string message) => Results.Conflict(new { message });

    public sealed record WebConfigurationInput(string Version, string Hostname, WebInterfaceInput?[] Interfaces);
    public sealed record WebInterfaceInput(string Name, bool? Enabled, string? Description);
    public sealed record WebVersionInput(string Version);
}
