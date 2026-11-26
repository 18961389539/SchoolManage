using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public sealed class SimulatedCliTransport : IDeviceTerminalTransport
{
    public bool CanHandle(string connectionType) => ResourceProviders.IsSimulated(connectionType);

    public TransportReadiness Readiness(TrainingDevice device, Cabinet cabinet) => TransportReadiness.Available;

    public async Task RunAsync(DeviceTerminalSession session)
    {
        var socket = session.Socket;
        var device = session.Device;
        var ct = session.Lease.Token;
        var state = SimulationDeviceConfiguration.Read(device);
        var mode = "exec";
        var selectedInterface = "";
        var line = new StringBuilder();
        var escape = false;
        var lastWasCr = false;
        string Prompt() => mode switch { "config" => $"{state.Hostname}(config)# ", "interface" => $"{state.Hostname}(config-if)# ", _ => $"{state.Hostname}# " };
        await DeviceTerminalSocket.Send(socket, "\r\n实训设备模拟器 / SIMULATED DEVICE\r\n当前会话不连接物理硬件。输入 help 查看命令。\r\n配置会持久保存；回收机柜仅释放会话。\r\n\r\n" + Prompt(), ct);
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var input = await DeviceTerminalSocket.Receive(socket, ct);
            if (input is null) break;
            foreach (var c in input)
            {
                if (c == '\u001b') { escape = true; continue; }
                if (escape) { if (c is >= '@' and <= '~' && c != '[') escape = false; continue; }
                if (c == '\u0003') { line.Clear(); await DeviceTerminalSocket.Send(socket, "^C\r\n" + Prompt(), ct); continue; }
                if (c is '\b' or '\u007f')
                {
                    if (line.Length > 0) { line.Length--; await DeviceTerminalSocket.Send(socket, "\b \b", ct); }
                    continue;
                }
                if (c is '\r' or '\n')
                {
                    if (c == '\n' && lastWasCr) { lastWasCr = false; continue; }
                    lastWasCr = c == '\r';
                    var command = line.ToString().Trim();
                    line.Clear();
                    if (!await session.StillAuthorized(ct)) { session.Lease.Cancel(); return; }
                    ct.ThrowIfCancellationRequested();
                    await DeviceTerminalSocket.Send(socket, "\r\n", ct);
                    var result = Execute(command, state, device, ref mode, ref selectedInterface);
                    if (result.Changed)
                    {
                        device.ConfigurationJson = SimulationDeviceConfiguration.Serialize(state);
                        var updated = await DeviceTerminalSession.Devices(session.Db, session.Lease)
                            .Where(x => x.ConnectionType == ResourceProviders.Simulated)
                            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ConfigurationJson, device.ConfigurationJson)
                                .SetProperty(x => x.SavedConfigurationJson, device.SavedConfigurationJson), ct);
                        if (updated == 0) { session.Lease.Cancel(); return; }
                    }
                    if (result.Output.Length > 0) await DeviceTerminalSocket.Send(socket, result.Output.Replace("\n", "\r\n") + "\r\n", ct);
                    if (result.Close) return;
                    await DeviceTerminalSocket.Send(socket, Prompt(), ct);
                    continue;
                }
                lastWasCr = false;
                if (!char.IsControl(c) && line.Length < 2048) { line.Append(c); await DeviceTerminalSocket.Send(socket, c.ToString(), ct); }
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
        if (lower == "enable") { mode = "exec"; return new("已处于模拟特权模式；访问范围由平台授权决定。"); }
        if (lower is "show interfaces" or "show interface brief" or "display interface brief") return new("Interface           Status       Description\n" + string.Join("\n", state.Interfaces.Select(x => $"{x.Key.PadRight(19)} {(x.Value.Enabled ? "up" : "admin-down").PadRight(12)} {x.Value.Description}")));
        if (lower is "show running-config" or "display current-configuration") return new(FormatConfiguration(state));
        if (lower == "show startup-config") return new(FormatConfiguration(SimulationDeviceConfiguration.Read(device, startup: true)));
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
            if (words.Length != 2 || !SimulationDeviceConfiguration.IsValidHostname(words[1])) return new("主机名须为 1 至 63 个字母、数字、连字符或下划线。");
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
            var description = command[12..].Trim();
            if (!SimulationDeviceConfiguration.IsValidDescription(description)) return new("接口说明最多 100 字且不能包含控制字符。");
            state.Interfaces[selectedInterface].Description = description; return new("模拟接口说明已更新。", Changed: true);
        }
        if (lower is "write memory" or "wr" or "save")
        {
            device.SavedConfigurationJson = SimulationDeviceConfiguration.Serialize(state);
            return new("模拟启动配置已保存。", Changed: true);
        }
        if (lower == "reload")
        {
            var saved = SimulationDeviceConfiguration.Read(device, startup: true);
            state.Hostname = saved.Hostname; state.Interfaces = saved.Interfaces; mode = "exec"; selectedInterface = "";
            return new("已从模拟启动配置重新载入。", Changed: true);
        }
        if (lower.StartsWith("ping ")) return new(words.Length == 2 ? $"[SIMULATED] 对 {words[1]} 的模拟检查：4/4 响应。未发送实际网络报文。" : "用法：ping <host>");
        if (lower is "show clock" or "display clock") return new($"模拟器时钟：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        return new("未知或尚未支持的模拟命令。输入 help 查看可用命令。");
    }

    private static string FormatConfiguration(SimulatedDeviceState state)
        => $"! SIMULATED CONFIGURATION\nhostname {state.Hostname}\n" + string.Join("\n", state.Interfaces.Select(x => $"interface {x.Key}\n {(x.Value.Enabled ? "no shutdown" : "shutdown")}\n description {x.Value.Description}\n exit")) + "\nend";

    private sealed record CliResult(string Output, bool Changed = false, bool Close = false);
}
