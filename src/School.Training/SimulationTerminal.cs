using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class SimulationTerminal
{
    public static async Task Handle(HttpContext ctx, string deviceId, SchoolDbContext db, SimulationSessions sessions, DeviceTerminalCatalog transports, IJumpHostTunnel jumpHosts)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { await Error(ctx, 400, "请使用 WebSocket 连接终端。"); return; }
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !string.Equals(originUri.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase) || !string.Equals(originUri.Scheme, ctx.Request.Scheme, StringComparison.OrdinalIgnoreCase))
        { await Error(ctx, 403, "终端只允许来自本系统页面的同源连接。"); return; }
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = ctx.User.FindFirstValue("stamp");
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(stamp)) { await Error(ctx, 401, "登录状态已失效，请重新登录。"); return; }
        var target = await TrainingResourceAccess.AccessibleDevices(db, userId, stamp).AsNoTracking()
            .Where(device => device.Id == deviceId).Select(device => new { device.NodeId }).FirstOrDefaultAsync(ctx.RequestAborted);
        if (target is null) { await Error(ctx, 404, "设备不存在或无访问权限。"); return; }
        var learnerNodeId = ctx.User.IsInRole("Admin") ? null : target.NodeId;
        if (!sessions.TryAcquire(deviceId, userId, ctx.User.Identity?.Name ?? "unknown", stamp, ctx.RequestAborted, out var lease, learnerNodeId))
        { await Error(ctx, 409, "设备正在被其他终端会话占用，请先关闭该会话或回收机柜。"); return; }
        WebSocket? socket = null;
        TrainingDevice? device = null;
        Stream? tunnel = null;
        var transportStarted = false;
        var scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
        Task authorizationMonitor = Task.CompletedTask;
        try
        {
            // A Web configuration write may have completed while this request competed for the same lease.
            device = await DeviceTerminalSession.Devices(db, lease).AsNoTracking().FirstOrDefaultAsync(lease.Token);
            if (device is null) { await Error(ctx, 404, "设备不存在或无访问权限。"); return; }
            var cabinet = await db.Cabinets.FindAsync([device.CabinetId], lease.Token);
            if (cabinet is null) { await Error(ctx, 409, "设备关联机柜不存在。"); return; }
            var transport = transports.Find(device.ConnectionType);
            var readiness = transport?.Readiness(device, cabinet) ?? TransportReadiness.Unavailable(501, SshChannelTransport.UnavailableMessage);
            if (!readiness.Ready) { await Error(ctx, readiness.StatusCode, readiness.Message); return; }
            if (jumpHosts.Applies(cabinet, device.ConnectionType))
            {
                var opened = await jumpHosts.OpenAsync(cabinet, device, lease.Token);
                if (!opened.Ready) { await Error(ctx, opened.StatusCode, opened.Message); return; }
                tunnel = opened.Stream;
            }
            var deviceType = await db.DeviceTypes.FindAsync([device.DeviceTypeId], lease.Token);
            if (deviceType is null) { await Error(ctx, 409, "关联设备类型不存在。"); return; }
            lease.Token.ThrowIfCancellationRequested();
            socket = await ctx.WebSockets.AcceptWebSocketAsync();
            authorizationMonitor = MonitorAuthorization(lease, scopeFactory, ctx.RequestServices.GetRequiredService<ILogger<SimulationSessions>>());
            Audit.Add(db, ctx, "打开设备终端", device.Name, ResourceProviders.IsSimulated(device.ConnectionType) ? "内置模拟器会话。" : $"真实 {device.ConnectionType} 会话。");
            await db.SaveChangesAsync(ctx.RequestAborted);
            var session = new DeviceTerminalSession(socket, device, deviceType, cabinet, lease, db, scopeFactory, tunnel);
            transportStarted = true;
            await transport!.RunAsync(session);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (SocketException) { if (socket is not null) await DeviceTerminalSocket.SafeSend(socket, "\r\n连接失败：目标不可达或已断开。\r\n"); }
        catch (IOException) { if (socket is not null) await DeviceTerminalSocket.SafeSend(socket, "\r\n连接已中断。\r\n"); }
        finally
        {
            if (!transportStarted) tunnel?.Dispose();
            lease.Cancel();
            try { await authorizationMonitor; } catch (OperationCanceledException) { }
            sessions.Release(lease);
            try
            {
                if (socket is not null && device is not null)
                {
                    Audit.Add(db, ctx, "关闭设备终端", device.Name);
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (DbUpdateException) { }
            if (socket is not null && (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "会话结束或机柜已回收", timeout.Token); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { socket.Abort(); }
            }
            socket?.Dispose();
        }
    }

    private static async Task MonitorAuthorization(TerminalLease lease, IServiceScopeFactory scopeFactory, ILogger logger)
    {
        // Revalidation is a safety net, not the primary revocation path: password resets,
        // role changes and user deletion cancel the lease immediately through
        // SimulationSessions.RevokeUser. Five seconds keeps that net without turning every
        // open terminal into one database query per second.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(lease.Token))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var checkDb = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
                if (await DeviceTerminalSession.Devices(checkDb, lease).AnyAsync(lease.Token)) continue;
                lease.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Fail closed if authorization cannot be checked. Never keep an unverified shell open.
            logger.LogWarning(ex, "Terminal authorization revalidation failed; revoking session for user {UserId}", lease.UserId);
            lease.Cancel();
        }
    }

    private static async Task Error(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { message });
    }
}
