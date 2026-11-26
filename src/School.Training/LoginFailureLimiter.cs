using System.Net;
using Microsoft.Extensions.Configuration;

namespace School.Training;

/// <summary>
/// Login limits that a shared lab NAT can live with. The request window is per client IP.
/// Password failures lock only that IP paired with one normalized account, so one student's
/// mistyped password does not block the rest of the classroom.
/// </summary>
public sealed class LoginThrottleOptions
{
    public int IpPermitLimit { get; init; } = 120;
    public int FailureLimit { get; init; } = 8;
    public TimeSpan Lockout { get; init; } = TimeSpan.FromMinutes(5);

    public static LoginThrottleOptions Load(IConfiguration configuration)
    {
        static int Read(IConfiguration configuration, string key, int fallback, int minimum, int maximum)
        {
            var value = configuration.GetValue<int?>(key) ?? fallback;
            return Math.Clamp(value, minimum, maximum);
        }

        return new LoginThrottleOptions
        {
            IpPermitLimit = Read(configuration, "School:Login:IpPermitLimit", 120, 1, 10_000),
            FailureLimit = Read(configuration, "School:Login:FailureLimit", 8, 1, 100),
            Lockout = TimeSpan.FromMinutes(Read(configuration, "School:Login:LockoutMinutes", 5, 1, 1_440))
        };
    }
}

public sealed class LoginFailureLimiter(LoginThrottleOptions options)
{
    readonly object gate = new();
    readonly Dictionary<Bucket, FailureWindow> windows = [];

    public bool IsLocked(string clientIp, string account, DateTimeOffset now, out TimeSpan retryAfter)
    {
        lock (gate)
        {
            Prune(now);
            if (windows.TryGetValue(new Bucket(clientIp, account), out var window)
                && window.LockedUntil is { } until && until > now)
            {
                retryAfter = until - now;
                return true;
            }
            retryAfter = default;
            return false;
        }
    }

    public void RecordFailure(string clientIp, string account, DateTimeOffset now)
    {
        lock (gate)
        {
            Prune(now);
            var key = new Bucket(clientIp, account);
            if (!windows.TryGetValue(key, out var window) || Stale(window, now))
            {
                window = new FailureWindow();
                windows[key] = window;
            }
            if (window.LockedUntil > now) return;
            window.Failures++;
            window.LastSeen = now;
            if (window.Failures >= options.FailureLimit)
            {
                window.LockedUntil = now.Add(options.Lockout);
                window.Failures = 0;
            }
        }
    }

    public void RecordSuccess(string clientIp, string account)
    {
        lock (gate) windows.Remove(new Bucket(clientIp, account));
    }

    public static string ClientKey(IPAddress? address)
    {
        if (address == null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    public static string AccountKey(string? userName)
    {
        var account = (userName ?? "").Trim().ToLowerInvariant();
        return account.Length <= Validation.UserNameMaxLength ? account : account[..Validation.UserNameMaxLength];
    }

    bool Stale(FailureWindow window, DateTimeOffset now)
    {
        if (window.LockedUntil is { } until) return until <= now;
        return window.LastSeen + options.Lockout <= now;
    }

    void Prune(DateTimeOffset now)
    {
        if (windows.Count < 4096) return;
        foreach (var key in windows.Where(pair => Stale(pair.Value, now)).Select(pair => pair.Key).ToArray())
            windows.Remove(key);
    }

    readonly record struct Bucket(string Ip, string Account);

    sealed class FailureWindow
    {
        public int Failures;
        public DateTimeOffset? LockedUntil;
        public DateTimeOffset LastSeen;
    }
}
