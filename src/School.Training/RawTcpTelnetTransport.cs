using System.Net.Sockets;
using System.Text;

namespace School.Training;

public sealed class RawTcpTelnetTransport : IDeviceTerminalTransport
{
    public bool CanHandle(string connectionType) => connectionType is "tcp" or "telnet";

    public TransportReadiness Readiness(TrainingDevice device, Cabinet cabinet) => TransportReadiness.Available;

    public async Task RunAsync(DeviceTerminalSession session)
    {
        var socket = session.Socket;
        var device = session.Device;
        var ct = session.Lease.Token;
        if (session.Tunnel is Stream tunnel)
        {
            await Pump(session, tunnel, ct);
            return;
        }
        var host = string.IsNullOrWhiteSpace(device.Host) ? session.Cabinet.Host : device.Host;
        var target = await NetworkTargets.ResolveAllowedAsync(host, ct);
        if (!target.Allowed)
        {
            // Same policy as the probe endpoint: the service never opens a session to the
            // host itself, the link-local segment or a multicast group.
            await DeviceTerminalSocket.Send(socket, $"\r\n无法连接 {(string.IsNullOrWhiteSpace(host) ? "（未配置连接地址）" : $"{host}:{device.Port}")}：{target.Message}\r\n", ct);
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        try { await client.ConnectAsync(target.Addresses[0], device.Port, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await DeviceTerminalSocket.SafeSend(socket, $"\r\n连接 {host}:{device.Port} 超时：5 秒内未能建立连接，请检查设备地址、端口和网络。\r\n");
            return;
        }
        await DeviceTerminalSocket.Send(socket, $"\r\n真实 {device.ConnectionType.ToUpperInvariant()} 连接：{host}:{device.Port}\r\n", ct);
        await Pump(session, client.GetStream(), ct);
    }

    private static async Task Pump(DeviceTerminalSession session, Stream network, CancellationToken ct)
    {
        using (network)
        {
            var socket = session.Socket;
            var device = session.Device;
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var decoder = Encoding.UTF8.GetDecoder();
            var telnet = new TelnetFilter();
            var encoder = new TerminalInputEncoder(session.DeviceType.Newline, session.DeviceType.Backspace);
            var read = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                var chars = new char[8192];
                while (!combined.IsCancellationRequested)
                {
                    var count = await network.ReadAsync(buffer, combined.Token);
                    if (count == 0) break;
                    var bytes = device.ConnectionType == "telnet" ? telnet.Filter(buffer.AsSpan(0, count)) : buffer.AsSpan(0, count).ToArray();
                    if (device.ConnectionType == "telnet" && telnet.Replies.Length > 0) await network.WriteAsync(telnet.Replies, combined.Token);
                    var charCount = decoder.GetChars(bytes, chars, false);
                    if (charCount > 0) await DeviceTerminalSocket.Send(socket, new string(chars, 0, charCount), combined.Token);
                }
            }, combined.Token);
            var write = Task.Run(async () =>
            {
                while (!combined.IsCancellationRequested)
                {
                    var text = await DeviceTerminalSocket.Receive(socket, combined.Token);
                    if (text is null) break;
                    if (!await session.StillAuthorized(combined.Token)) { session.Lease.Cancel(); return; }
                    combined.Token.ThrowIfCancellationRequested();
                    var translated = encoder.Normalize(text);
                    if (translated.Length > 0) await network.WriteAsync(Encoding.UTF8.GetBytes(translated), combined.Token);
                }
            }, combined.Token);
            await Task.WhenAny(read, write);
            combined.Cancel();
            try { await Task.WhenAll(read, write); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class TelnetFilter
    {
        private int _state;
        private byte _command;
        public byte[] Replies { get; private set; } = [];
        public byte[] Filter(ReadOnlySpan<byte> bytes)
        {
            var output = new List<byte>();
            var response = new List<byte>();
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
            Replies = response.ToArray();
            return output.ToArray();
        }
    }
}
