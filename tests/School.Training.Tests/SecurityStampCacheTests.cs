namespace School.Training.Tests;

public sealed class SecurityStampCacheTests
{
    [Fact]
    public void Revoke_rejects_the_previous_stamp_immediately()
    {
        using var cache = new SecurityStampCache();
        cache.Remember("user", "old");
        Assert.True(cache.TryResolve("user", "old", out var accepted));
        Assert.True(accepted);

        cache.Revoke("user", "new");
        Assert.True(cache.TryResolve("user", "old", out accepted));
        Assert.False(accepted);
        Assert.True(cache.TryResolve("user", "new", out accepted));
        Assert.True(accepted);

        cache.Revoke("user");
        Assert.False(cache.TryResolve("user", "new", out _));
    }
}
