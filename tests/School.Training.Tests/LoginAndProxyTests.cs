using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using School.Training;

namespace School.Training.Tests;

public sealed class LoginAndProxyTests
{
    static IConfiguration Config(params (string Key, string? Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(item => new KeyValuePair<string, string?>(item.Key, item.Value)))
            .Build();

    [Fact]
    public void Eight_consecutive_failures_lock_only_that_account_and_client()
    {
        var limiter = new LoginFailureLimiter(new LoginThrottleOptions());
        var now = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
        for (var attempt = 0; attempt < 7; attempt++)
            limiter.RecordFailure("203.0.113.7", "admin", now);

        Assert.False(limiter.IsLocked("203.0.113.7", "admin", now, out _));
        limiter.RecordFailure("203.0.113.7", "admin", now);

        Assert.True(limiter.IsLocked("203.0.113.7", "admin", now, out var retry));
        Assert.Equal(TimeSpan.FromMinutes(5), retry);
        Assert.False(limiter.IsLocked("203.0.113.7", "student01", now, out _));
        Assert.False(limiter.IsLocked("203.0.113.8", "admin", now, out _));
        Assert.False(limiter.IsLocked("203.0.113.7", "admin", now.AddMinutes(5), out _));
    }

    [Fact]
    public void A_gap_or_a_successful_login_resets_the_failure_streak()
    {
        var limiter = new LoginFailureLimiter(new LoginThrottleOptions { FailureLimit = 3, Lockout = TimeSpan.FromMinutes(5) });
        var now = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
        limiter.RecordFailure("10.0.0.4", "admin", now);
        limiter.RecordFailure("10.0.0.4", "admin", now.AddMinutes(6));
        limiter.RecordFailure("10.0.0.4", "admin", now.AddMinutes(6));
        Assert.False(limiter.IsLocked("10.0.0.4", "admin", now.AddMinutes(6), out _));

        limiter.RecordSuccess("10.0.0.4", "admin");
        limiter.RecordFailure("10.0.0.4", "admin", now.AddMinutes(6));
        limiter.RecordFailure("10.0.0.4", "admin", now.AddMinutes(6));
        Assert.False(limiter.IsLocked("10.0.0.4", "admin", now.AddMinutes(6), out _));
        limiter.RecordFailure("10.0.0.4", "admin", now.AddMinutes(6));
        Assert.True(limiter.IsLocked("10.0.0.4", "admin", now.AddMinutes(6), out _));
    }

    [Fact]
    public void Client_and_account_keys_are_normalized()
    {
        Assert.Equal("203.0.113.7", LoginFailureLimiter.ClientKey(IPAddress.Parse("::ffff:203.0.113.7")));
        Assert.Equal("unknown", LoginFailureLimiter.ClientKey(null));
        Assert.Equal("admin", LoginFailureLimiter.AccountKey(" Admin "));
        Assert.Equal(Validation.UserNameMaxLength, LoginFailureLimiter.AccountKey(new string('A', 80)).Length);
    }

    [Fact]
    public void Load_uses_the_classroom_defaults_and_clamps()
    {
        var defaults = LoginThrottleOptions.Load(Config());
        Assert.Equal(120, defaults.IpPermitLimit);
        Assert.Equal(8, defaults.FailureLimit);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.Lockout);

        var clamped = LoginThrottleOptions.Load(Config(
            ("School:Login:IpPermitLimit", "0"),
            ("School:Login:FailureLimit", "1000"),
            ("School:Login:LockoutMinutes", "9")));
        Assert.Equal(1, clamped.IpPermitLimit);
        Assert.Equal(100, clamped.FailureLimit);
        Assert.Equal(TimeSpan.FromMinutes(9), clamped.Lockout);
    }

    [Fact]
    public void Empty_trusted_proxy_list_leaves_forwarded_headers_disabled()
    {
        var options = ReverseProxyOptions.Load(Config(("School:ReverseProxy:TrustedProxies:0", "  ")));
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void Trusted_proxies_replace_the_loopback_default_and_reject_the_whole_internet()
    {
        var options = ReverseProxyOptions.Load(Config(
            ("School:ReverseProxy:TrustedProxies:0", "10.1.0.0/16"),
            ("School:ReverseProxy:TrustedProxies:1", "[::1]"),
            ("AllowedHosts", "training.school.example;*;localhost")));
        Assert.True(options.IsConfigured);
        Assert.Equal(IPAddress.IPv6Loopback, Assert.Single(options.TrustedProxies));
        var network = Assert.Single(options.TrustedNetworks);
        Assert.Equal(IPAddress.Parse("10.1.0.0"), network.BaseAddress);
        Assert.Equal(16, network.PrefixLength);

        var forwarded = new ForwardedHeadersOptions();
        options.Apply(forwarded);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost, forwarded.ForwardedHeaders);
        Assert.Equal(1, forwarded.ForwardLimit);
        Assert.Equal(IPAddress.IPv6Loopback, Assert.Single(forwarded.KnownProxies));
        Assert.DoesNotContain(IPAddress.Loopback, forwarded.KnownProxies);
        Assert.Contains("training.school.example", forwarded.AllowedHosts);
        Assert.Contains("localhost", forwarded.AllowedHosts);
        Assert.DoesNotContain("*", forwarded.AllowedHosts);

        var world = Assert.Throws<InvalidOperationException>(() => ReverseProxyOptions.Load(Config(
            ("School:ReverseProxy:TrustedProxies:0", "0.0.0.0/0"))));
        Assert.Contains("不能信任全部地址", world.Message);
        Assert.Throws<InvalidOperationException>(() => ReverseProxyOptions.Load(Config(
            ("School:ReverseProxy:TrustedProxies:0", "not-a-proxy"))));
    }
}
