namespace School.Training.Tests;

public sealed class SessionPolicyTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Session_inside_the_absolute_window_is_valid()
        => Assert.False(SessionPolicy.IsExpired(Now.AddHours(-23).ToUnixTimeSeconds().ToString(), Now, 24));

    [Fact]
    public void Session_beyond_the_absolute_window_is_expired()
        => Assert.True(SessionPolicy.IsExpired(Now.AddHours(-25).ToUnixTimeSeconds().ToString(), Now, 24));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void Cookie_without_a_valid_start_claim_is_expired(string? started)
        => Assert.True(SessionPolicy.IsExpired(started, Now, 24));
}
