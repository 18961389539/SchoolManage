namespace School.Training.Tests;

public sealed class DeviceTerminalTransportTests
{
    private static DeviceTerminalCatalog Catalog() => new(
    [
        new SimulatedCliTransport(),
        new RawTcpTelnetTransport(),
        new SshChannelTransport()
    ]);

    [Fact]
    public void Catalog_selects_each_protocol_without_the_session_lease()
    {
        var catalog = Catalog();
        Assert.IsType<SimulatedCliTransport>(catalog.Find(ResourceProviders.Simulated));
        Assert.IsType<RawTcpTelnetTransport>(catalog.Find("tcp"));
        Assert.IsType<RawTcpTelnetTransport>(catalog.Find("telnet"));
        Assert.IsType<SshChannelTransport>(catalog.Find("ssh"));
        Assert.Null(catalog.Find("serial"));
    }

    [Fact]
    public async Task Ssh_and_a_jump_host_stay_unavailable_before_a_socket_opens()
    {
        var ssh = new SshChannelTransport().Readiness(new TrainingDevice { ConnectionType = "ssh" }, new Cabinet());
        Assert.False(ssh.Ready);
        Assert.Equal(501, ssh.StatusCode);
        Assert.Equal(SshChannelTransport.UnavailableMessage, ssh.Message);

        var tunnel = new UnavailableJumpHostTunnel();
        var direct = new Cabinet();
        var jumped = new Cabinet { JumpHost = "10.0.0.8" };
        Assert.False(tunnel.Applies(jumped, ResourceProviders.Simulated));
        Assert.False(tunnel.Applies(direct, "tcp"));
        Assert.True(tunnel.Applies(jumped, "tcp"));
        var opened = await tunnel.OpenAsync(jumped, new TrainingDevice { ConnectionType = "tcp" }, CancellationToken.None);
        Assert.False(opened.Ready);
        Assert.Null(opened.Stream);
        Assert.Equal(501, opened.StatusCode);
        Assert.Equal(UnavailableJumpHostTunnel.UnavailableMessage, opened.Message);
    }

    [Fact]
    public void A_ready_transport_does_not_inspect_the_jump_host()
    {
        var cabinet = new Cabinet { JumpHost = "10.0.0.8" };
        Assert.True(new SimulatedCliTransport().Readiness(new TrainingDevice(), cabinet).Ready);
        Assert.True(new RawTcpTelnetTransport().Readiness(new TrainingDevice { ConnectionType = "tcp" }, cabinet).Ready);
    }
}
