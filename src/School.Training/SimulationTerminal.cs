using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class SimulationTerminal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task Handle(HttpContext ctx, string deviceId, SchoolDbContext db, SimulationSessions sessions)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { await Error(ctx, 400, "请使用 WebSocket 连接终端。"); return; }
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !string.Equals(originUri.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase) || !string.Equals(originUri.Scheme, ctx.Request.Scheme, StringComparison.OrdinalIgnoreCase))
        { await Error(ctx, 403, "终端只允许来自本系统页面的同源连接。"); return; }
        var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId, ctx.RequestAborted);
        if (device is null) { await Error(ctx, 404, "设备不存在。"); return; }
        var cabinet = await db.Cabinets.FindAsync([device.CabinetId], ctx.RequestAborted);
        if (cabinet is null) { await Error(ctx, 409, "设备关联机柜不存在。"); return; }
        if (device.ConnectionType is not ("simulated" or "tcp" or "telnet"))
        { await Error(ctx, 501, "当前交付环境已验证模拟终端。SSH 终端尚未适配，不能以模拟会话代替真实连接。"); return; }
        if (device.ConnectionType != "simulated" && !string.IsNullOrWhiteSpace(cabinet.JumpHost))
        { await Error(ctx, 501, "真实跳板机链路尚未适配；当前终端不会绕过跳板机直接连接设备。"); return; }
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = ctx.User.FindFirstValue("stamp");
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(stamp)) { await Error(ctx, 401, "登录状态已失效，请重新登录。"); return; }
        if (!sessions.TryAcquire(deviceId, userId, ctx.User.Identity?.Name ?? "unknown", stamp, ctx.RequestAborted, out var lease))
        { await Error(ctx, 409, "设备正在被其他终端会话占用，请先关闭该会话或回收机柜。"); return; }
        WebSocket? socket = null;
        var scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
        Task authorizationMonitor = Task.CompletedTask;
        try
        {
            if (!await IsAuthorized(lease, scopeFactory, lease.Token)) { await Error(ctx, 403, "账号或终端权限已变更，请重新登录。"); return; }
            socket = await ctx.WebSockets.AcceptWebSocketAsync();
            authorizationMonitor = MonitorAuthorization(lease, scopeFactory, ctx.RequestServices.GetRequiredService<ILogger<SimulationSessions>>());
            Audit.Add(db, ctx, "打开设备终端", device.Name, device.ConnectionType == "simulated" ? "内置模拟器会话。" : $"真实 {device.ConnectionType} 会话。");
            await db.SaveChangesAsync(ctx.RequestAborted);
            if (device.ConnectionType == "simulated") await RunSimulation(socket, device, db, lease, scopeFactory);
            else await RunTcp(socket, device, cabinet, lease, scopeFactory);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (SocketException) { if (socket is not null) await SafeSend(socket, "\r\n连接失败：目标不可达或已断开。\r\n"); }
        catch (IOException) { if (socket is not null) await SafeSend(socket, "\r\n连接已中断。\r\n"); }
        finally
        {
            lease.Cancel();
            try { await authorizationMonitor; } catch (OperationCanceledException) { }
            sessions.Release(lease);
            try
            {
                Audit.Add(db, ctx, "关闭设备终端", device.Name);
                await db.SaveChangesAsync(CancellationToken.None);
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

    private static async Task RunSimulation(WebSocket socket, TrainingDevice device, SchoolDbContext db, TerminalLease lease, IServiceScopeFactory scopeFactory)
    {
        var ct = lease.Token;
        var state = Load(device.ConfigurationJson, device.Name.Contains("路由") ? "RT-01" : "SW-01");
        var mode = "exec";
        var selectedInterface = "";
        var line = new StringBuilder();
        var escape = false;
        var lastWasCr = false;
        string Prompt() => mode switch { "config" => $"{state.Hostname}(config)# ", "interface" => $"{state.Hostname}(config-if)# ", _ => $"{state.Hostname}# " };
        await Send(socket, "\r\n实训设备模拟器 / SIMULATED DEVICE\r\n当前会话不连接物理硬件。输入 help 查看命令。\r\n配置会持久保存；回收机柜仅释放会话。\r\n\r\n" + Prompt(), ct);
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var input = await Receive(socket, ct); if (input is null) break;
            foreach (var c in input)
            {
                if (c == '\u001b') { escape = true; continue; }
                if (escape) { if (c is >= '@' and <= '~' && c != '[') escape = false; continue; }
                if (c == '\u0003') { line.Clear(); await Send(socket, "^C\r\n" + Prompt(), ct); continue; }
                if (c is '\b' or '\u007f')
                {
                    if (line.Length > 0) { line.Length--; await Send(socket, "\b \b", ct); }
                    continue;
                }
                if (c is '\r' or '\n')
                {
                    if (c == '\n' && lastWasCr) { lastWasCr = false; continue; }
                    lastWasCr = c == '\r';
                    var command = line.ToString().Trim(); line.Clear();
                    if (!await IsAuthorized(lease, scopeFactory, ct)) { lease.Cancel(); return; }
                    ct.ThrowIfCancellationRequested();
                    await Send(socket, "\r\n", ct);
                    var result = Execute(command, state, device, ref mode, ref selectedInterface);
                    if (result.Output.Length > 0) await Send(socket, result.Output.Replace("\n", "\r\n") + "\r\n", ct);
                    if (result.Changed)
                    {
                        device.ConfigurationJson = JsonSerializer.Serialize(state, Json);
                        await db.SaveChangesAsync(ct);
                    }
                    if (result.Close) return;
                    await Send(socket, Prompt(), ct);
                    continue;
                }
                lastWasCr = false;
                if (!char.IsControl(c) && line.Length < 2048) { line.Append(c); await Send(socket, c.ToString(), ct); }
            }
        }
    }

    private static CliResult Execute(string command, SimulatedDeviceState state, TrainingDevice device, ref string mode, ref string selectedInterface)
    {
        if (command.Length == 0) return new("");
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lower = command.ToLowerInvariant();
        if (lower is "help" or "?") return new("模拟器命令：\n  show version                 查看模拟设备信息\n  show interfaces              查看接口状态\n  show running-config          查看当前配置\n  show startup-config          查看已保存配置\n  configure terminal           进入配置模式\n  hostname <name>              修改模拟主机名（配置模式）\n  interface ethernet0/0        进入接口配置\n  shutdown / no shutdown       关闭 / 开启所选模拟接口\n  description <text>           设置接口说明\n  write memory                 保存启动配置\n  reload                       从启动配置重新载入\n  ping <host>                  模拟连通性检查（不发送网络包）\n  end / exit / quit            退出配置 / 关闭会话");
        if (lower is "show version" or "display version") return new($"School Training Simulator 1.0\n设备：{device.Name}\n主机名：{state.Hostname}\n运行模式：SIMULATED（无真实网络硬件）");
        if (lower == "enable") { mode = "exec"; return new("已处于模拟特权模式；实际访问权限由平台管理员授权。"); }
        if (lower is "show interfaces" or "show interface brief" or "display interface brief") return new("Interface           Status       Description\n" + string.Join("\n", state.Interfaces.Select(x => $"{x.Key.PadRight(19)} {(x.Value.Enabled ? "up" : "admin-down").PadRight(12)} {x.Value.Description}")));
        if (lower is "show running-config" or "display current-configuration") return new(FormatConfiguration(state));
        if (lower == "show startup-config") return new(FormatConfiguration(Load(device.SavedConfigurationJson, device.Name.Contains("路由") ? "RT-01" : "SW-01")));
        if (lower is "configure terminal" or "conf t" or "system-view") { mode = "config"; return new("进入模拟配置模式。"); }
        if (lower == "end") { mode = "exec"; selectedInterface = ""; return new(""); }
        if (lower is "exit" or "quit")
        {
            if (mode == "interface") { mode = "config"; selectedInterface = ""; return new(""); }
            if (mode == "config") { mode = "exec"; return new(""); }
            return new("模拟会话已关闭。", Close: true);
        }
        if (lower.StartsWith("hostname "))
        {
            if (mode == "exec") return new("请先执行 configure terminal。");
            if (words.Length != 2 || words[1].Length > 63 || words[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) return new("主机名须为 1 至 63 个字母、数字、连字符或下划线。");
            state.Hostname = words[1]; return new("模拟主机名已更新。", Changed: true);
        }
        if (lower.StartsWith("interface "))
        {
            if (mode == "exec") return new("请先执行 configure terminal。");
            if (words.Length != 2 || !state.Interfaces.ContainsKey(words[1].ToLowerInvariant())) return new("接口不存在，可先执行 show interfaces 查看。");
            selectedInterface = words[1].ToLowerInvariant(); mode = "interface"; return new("");
        }
        if (lower is "shutdown" or "no shutdown")
        {
            if (mode != "interface") return new("请先选择接口：interface ethernet0/0。");
            state.Interfaces[selectedInterface].Enabled = lower == "no shutdown";
            return new("模拟接口状态已更新。", Changed: true);
        }
        if (lower.StartsWith("description "))
        {
            if (mode != "interface") return new("请先进入接口配置模式。");
            var description = command[12..].Trim(); if (description.Length > 100) return new("接口说明不能超过 100 字。");
            state.Interfaces[selectedInterface].Description = description; return new("模拟接口说明已更新。", Changed: true);
        }
        if (lower is "write memory" or "wr" or "save")
        {
            device.SavedConfigurationJson = JsonSerializer.Serialize(state, Json);
            return new("模拟启动配置已保存。", Changed: true);
        }
        if (lower == "reload")
        {
            var saved = Load(device.SavedConfigurationJson, device.Name.Contains("路由") ? "RT-01" : "SW-01");
            state.Hostname = saved.Hostname; state.Interfaces = saved.Interfaces; mode = "exec"; selectedInterface = "";
            return new("已从模拟启动配置重新载入。", Changed: true);
        }
        if (lower.StartsWith("ping ")) return new(words.Length == 2 ? $"[SIMULATED] 对 {words[1]} 的模拟检查：4/4 响应。未发送实际网络报文。" : "用法：ping <host>");
        if (lower is "show clock" or "display clock") return new($"模拟器时钟：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        return new("未知或尚未支持的模拟命令。输入 help 查看可用命令。");
    }

    private static SimulatedDeviceState Load(string json, string hostname)
    {
        try
        {
            var value = JsonSerializer.Deserialize<SimulatedDeviceState>(json, Json);
            if (value is not null && !string.IsNullOrEmpty(value.Hostname) && value.Interfaces.Count > 0) return value;
        }
        catch (JsonException) { }
        return new() { Hostname = hostname, Interfaces = Enumerable.Range(0, 4).ToDictionary(x => $"ethernet0/{x}", _ => new SimulatedInterface()) };
    }
    private static string FormatConfiguration(SimulatedDeviceState state)
        => $"! SIMULATED CONFIGURATION\nhostname {state.Hostname}\n" + string.Join("\n", state.Interfaces.Select(x => $"interface {x.Key}\n {(x.Value.Enabled ? "no shutdown" : "shutdown")}\n description {x.Value.Description}\n exit")) + "\nend";

    private static async Task RunTcp(WebSocket socket, TrainingDevice device, Cabinet cabinet, TerminalLease lease, IServiceScopeFactory scopeFactory)
    {
        var ct = lease.Token;
        var host = string.IsNullOrWhiteSpace(device.Host) ? cabinet.Host : device.Host;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); await client.ConnectAsync(host, device.Port, timeout.Token);
        await Send(socket, $"\r\n真实 {device.ConnectionType.ToUpperInvariant()} 连接：{host}:{device.Port}\r\n", ct);
        using var network = client.GetStream();
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var decoder = Encoding.UTF8.GetDecoder();
        var telnet = new TelnetFilter();
        var read = Task.Run(async () =>
        {
            var buffer = new byte[8192]; var chars = new char[8192];
            while (!combined.IsCancellationRequested)
            {
                var count = await network.ReadAsync(buffer, combined.Token); if (count == 0) break;
                var bytes = device.ConnectionType == "telnet" ? telnet.Filter(buffer.AsSpan(0, count), out var replies) : buffer.AsSpan(0, count).ToArray();
                if (device.ConnectionType == "telnet" && telnet.Replies.Length > 0) await network.WriteAsync(telnet.Replies, combined.Token);
                var charCount = decoder.GetChars(bytes, chars, false);
                if (charCount > 0) await Send(socket, new string(chars, 0, charCount), combined.Token);
            }
        }, combined.Token);
        var write = Task.Run(async () =>
        {
            while (!combined.IsCancellationRequested)
            {
                var text = await Receive(socket, combined.Token); if (text is null) break;
                if (!await IsAuthorized(lease, scopeFactory, combined.Token)) { lease.Cancel(); return; }
                combined.Token.ThrowIfCancellationRequested();
                await network.WriteAsync(Encoding.UTF8.GetBytes(text), combined.Token);
            }
        }, combined.Token);
        await Task.WhenAny(read, write); combined.Cancel();
        try { await Task.WhenAll(read, write); } catch (OperationCanceledException) { }
    }

    private static async Task<bool> IsAuthorized(TerminalLease lease, IServiceScopeFactory scopeFactory, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var checkDb = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
        return await checkDb.Users.AsNoTracking().AnyAsync(x => x.Id == lease.UserId && x.SecurityStamp == lease.SecurityStamp && x.Role == "Admin" && !x.MustChangePassword, ct);
    }

    private static async Task MonitorAuthorization(TerminalLease lease, IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(lease.Token))
            {
                if (await IsAuthorized(lease, scopeFactory, lease.Token)) continue;
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

    private sealed class TelnetFilter
    {
        private int _state;
        private byte _command;
        public byte[] Replies { get; private set; } = [];
        public byte[] Filter(ReadOnlySpan<byte> bytes, out byte[] replies)
        {
            var output = new List<byte>(); var response = new List<byte>();
            foreach (var b in bytes)
            {
                switch (_state)
                {
                    case 0: if (b == 255) _state = 1; else output.Add(b); break;
                    case 1:
                        if (b == 255) { output.Add(255); _state = 0; }
                        else if (b is 251 or 252 or 253 or 254) { _command = b; _state = 2; }
                        else if (b == 250) _state = 3;
                        else _state = 0;
                        break;
                    case 2:
                        if (_command == 251) response.AddRange([255, 254, b]);
                        if (_command == 253) response.AddRange([255, 252, b]);
                        _state = 0; break;
                    case 3: if (b == 255) _state = 4; break;
                    case 4: _state = b == 240 ? 0 : 3; break;
                }
            }
            Replies = replies = response.ToArray(); return output.ToArray();
        }
    }

    private static async Task<string?> Receive(WebSocket socket, CancellationToken ct)
    {
        var bytes = new byte[4096]; using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 16384) throw new WebSocketException("终端消息无效或过长。");
            message.Write(bytes, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }
    private static Task Send(WebSocket socket, string text, CancellationToken ct) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);
    private static async Task SafeSend(WebSocket socket, string text) { if (socket.State != WebSocketState.Open) return; try { await Send(socket, text, CancellationToken.None); } catch (WebSocketException) { } }
    private static async Task Error(HttpContext ctx, int status, string message) { ctx.Response.StatusCode = status; await ctx.Response.WriteAsJsonAsync(new { message }); }
    private sealed record CliResult(string Output, bool Changed = false, bool Close = false);
    private sealed class SimulatedDeviceState { public string Hostname { get; set; } = ""; public Dictionary<string, SimulatedInterface> Interfaces { get; set; } = new(); }
    private sealed class SimulatedInterface { public bool Enabled { get; set; } = true; public string Description { get; set; } = ""; }
}
