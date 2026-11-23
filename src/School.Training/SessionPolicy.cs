namespace School.Training;

public static class SessionPolicy
{
    public const string StartedClaim = "sessionStarted";

    /// <summary>
    /// Sliding expiration keeps an active operator signed in, but a cookie must still be
    /// renewed by a real login after the absolute window. Old cookies have no claim and
    /// are treated as expired.
    /// </summary>
    public static bool IsExpired(string? started, DateTimeOffset now, int absoluteHours)
        => !long.TryParse(started, out var seconds)
            || now.ToUnixTimeSeconds() - seconds > absoluteHours * 3600L;
}
