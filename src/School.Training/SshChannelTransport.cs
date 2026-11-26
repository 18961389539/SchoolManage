namespace School.Training;

/// <summary>
/// The SSH slot in the transport catalog. It refuses the session before a socket is accepted,
/// so a simulated CLI cannot stand in for a real secure shell.
/// </summary>
public sealed class SshChannelTransport : IDeviceTerminalTransport
{
    public const string UnavailableMessage = "当前交付环境已验证模拟终端。SSH 终端尚未适配，不能以模拟会话代替真实连接。";

    public bool CanHandle(string connectionType) => connectionType == "ssh";

    public TransportReadiness Readiness(TrainingDevice device, Cabinet cabinet)
        => TransportReadiness.Unavailable(501, UnavailableMessage);

    public Task RunAsync(DeviceTerminalSession session)
        => throw new InvalidOperationException(UnavailableMessage);
}
