using System.Net;
using School.Training;

namespace School.Training.Tests;

public sealed class NetworkTargetsTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("255.255.255.255")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    public void IsBlocked_RejectsHostAndNonUnicastTargets(string address)
        => Assert.True(NetworkTargets.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.31.7")]
    [InlineData("192.168.10.20")]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::10")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:192.168.10.20")]
    public void IsBlocked_AllowsRoutableUnicastTargets(string address)
        => Assert.False(NetworkTargets.IsBlocked(IPAddress.Parse(address)));

    [Fact]
    public async Task ResolveAllowed_RejectsLiteralLoopbackWithoutDns()
    {
        var result = await NetworkTargets.ResolveAllowedAsync("127.0.0.1", CancellationToken.None);
        Assert.False(result.Allowed);
        Assert.Equal(NetworkTargets.BlockedMessage, result.Message);
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public async Task ResolveAllowed_RejectsMetadataEndpoint()
    {
        var result = await NetworkTargets.ResolveAllowedAsync("169.254.169.254", CancellationToken.None);
        Assert.False(result.Allowed);
        Assert.Equal(NetworkTargets.BlockedMessage, result.Message);
    }

    [Fact]
    public async Task ResolveAllowed_ReturnsValidatedAddressForAllowedLiteral()
    {
        var result = await NetworkTargets.ResolveAllowedAsync("192.168.10.20", CancellationToken.None);
        Assert.True(result.Allowed);
        Assert.Equal("", result.Message);
        Assert.Equal(IPAddress.Parse("192.168.10.20"), Assert.Single(result.Addresses));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAllowed_RejectsEmptyHost(string host)
    {
        var result = await NetworkTargets.ResolveAllowedAsync(host, CancellationToken.None);
        Assert.False(result.Allowed);
        Assert.Equal(NetworkTargets.UnresolvableMessage, result.Message);
    }
}
