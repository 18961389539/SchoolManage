using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace School.Training;

/// <summary>
/// Remembers a verified account stamp for five seconds so ordinary API calls skip the user table.
/// Logout, password changes and deletion bump an epoch, so a stamp remembered just before
/// the change cannot be accepted afterward.
/// </summary>
public sealed class SecurityStampCache : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);
    readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 4096 });
    readonly ConcurrentDictionary<string, int> epochs = new(StringComparer.Ordinal);

    public bool TryResolve(string userId, string? presented, out bool accepted)
    {
        accepted = false;
        if (!cache.TryGetValue(Key(userId), out Entry? entry) || entry == null) return false;
        if (entry.Epoch != Epoch(userId))
        {
            cache.Remove(Key(userId));
            return false;
        }
        accepted = entry.Stamp == presented;
        return true;
    }

    public void Remember(string userId, string stamp)
    {
        var epoch = Epoch(userId);
        cache.Set(Key(userId), new Entry(stamp, epoch), Options());
    }

    /// <summary>
    /// Forgets the cached stamp. <paramref name="replacement"/> is the stamp now stored for the
    /// account; a null replacement means the account is gone.
    /// </summary>
    public void Revoke(string userId, string? replacement = null)
    {
        var epoch = epochs.AddOrUpdate(userId, 1, (_, value) => value + 1);
        if (replacement == null) cache.Remove(Key(userId));
        else cache.Set(Key(userId), new Entry(replacement, epoch), Options());
    }

    public void Dispose() => cache.Dispose();

    int Epoch(string userId) => epochs.TryGetValue(userId, out var value) ? value : 0;

    static string Key(string userId) => "stamp:" + userId;

    static MemoryCacheEntryOptions Options() => new()
    {
        AbsoluteExpirationRelativeToNow = Lifetime,
        Size = 1
    };

    sealed record Entry(string Stamp, int Epoch);
}
