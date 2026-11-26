using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

/// <summary>
/// A byte-stream or simulated CLI behind an already acquired terminal lease.
/// <see cref="SimulationSessions"/> keeps the quota and exclusive reclaim; a transport never allocates them.
/// </summary>
public interface IDeviceTerminalTransport
{
    bool CanHandle(string connectionType);
    TransportReadiness Readiness(TrainingDevice device, Cabinet cabinet);
    Task RunAsync(DeviceTerminalSession session);
}

public readonly record struct TransportReadiness(bool Ready, int StatusCode, string Message)
{
    public static TransportReadiness Available { get; } = new(true, 200, "");
    public static TransportReadiness Unavailable(int statusCode, string message) => new(false, statusCode, message);
}

/// <summary>
/// Opens the byte stream that reaches a device when the cabinet stores a jump host.
/// A direct TCP transport uses <see cref="JumpTunnel.Stream"/> and does not read jump-host fields itself.
/// </summary>
public interface IJumpHostTunnel
{
    bool Applies(Cabinet cabinet, string connectionType);
    Task<JumpTunnel> OpenAsync(Cabinet cabinet, TrainingDevice device, CancellationToken cancellationToken);
}

public sealed record JumpTunnel(bool Ready, Stream? Stream, int StatusCode, string Message)
{
    public static JumpTunnel Open(Stream stream) => new(true, stream, 200, "");
    public static JumpTunnel Unavailable(int statusCode, string message) => new(false, null, statusCode, message);
}

/// <summary>The registered jump-host factory until a real tunnel implementation is added.</summary>
public sealed class UnavailableJumpHostTunnel : IJumpHostTunnel
{
    public const string UnavailableMessage = "真实跳板机链路尚未适配；当前终端不会绕过跳板机直接连接设备。";

    public bool Applies(Cabinet cabinet, string connectionType)
        => !ResourceProviders.IsSimulated(connectionType) && !string.IsNullOrWhiteSpace(cabinet.JumpHost);

    public Task<JumpTunnel> OpenAsync(Cabinet cabinet, TrainingDevice device, CancellationToken cancellationToken)
        => Task.FromResult(JumpTunnel.Unavailable(501, UnavailableMessage));
}

public sealed class DeviceTerminalCatalog(IEnumerable<IDeviceTerminalTransport> transports)
{
    public IDeviceTerminalTransport? Find(string connectionType)
        => transports.FirstOrDefault(transport => transport.CanHandle(connectionType));
}

public sealed class DeviceTerminalSession(
    WebSocket socket,
    TrainingDevice device,
    DeviceType deviceType,
    Cabinet cabinet,
    TerminalLease lease,
    SchoolDbContext db,
    IServiceScopeFactory scopeFactory,
    Stream? tunnel)
{
    public WebSocket Socket { get; } = socket;
    public TrainingDevice Device { get; } = device;
    public DeviceType DeviceType { get; } = deviceType;
    public Cabinet Cabinet { get; } = cabinet;
    public TerminalLease Lease { get; } = lease;
    public SchoolDbContext Db { get; } = db;
    public IServiceScopeFactory ScopeFactory { get; } = scopeFactory;
    public Stream? Tunnel { get; } = tunnel;

    public async Task<bool> StillAuthorized(CancellationToken cancellationToken)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var checkDb = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
        return await Devices(checkDb, Lease).AnyAsync(cancellationToken);
    }

    public static IQueryable<TrainingDevice> Devices(SchoolDbContext db, TerminalLease lease)
    {
        var devices = TrainingResourceAccess.AccessibleDevices(db, lease.UserId, lease.SecurityStamp)
            .Where(device => device.Id == lease.DeviceId);
        return lease.LearnerNodeId is null ? devices : devices.Where(device => device.NodeId == lease.LearnerNodeId);
    }
}

public static class DeviceTerminalSocket
{
    public static async Task<string?> Receive(WebSocket socket, CancellationToken cancellationToken)
    {
        var bytes = new byte[4096];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 16384) throw new WebSocketException("终端消息无效或过长。");
            message.Write(bytes, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }

    public static Task Send(WebSocket socket, string text, CancellationToken cancellationToken)
        => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, cancellationToken);

    public static async Task SafeSend(WebSocket socket, string text)
    {
        if (socket.State != WebSocketState.Open) return;
        try { await Send(socket, text, CancellationToken.None); }
        catch (WebSocketException) { }
    }
}
