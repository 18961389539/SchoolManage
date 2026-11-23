using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using School.Training;

namespace School.Training.Tests;

public sealed class CredentialVerificationTests
{
    const string Password = "CorrectHorse123";
    static readonly IPasswordHasher<AppUser> Hasher = HasherWith(2000);
    static readonly IPasswordHasher<AppUser> OlderHasher = HasherWith(1000);

    static IPasswordHasher<AppUser> HasherWith(int iterations)
        => new PasswordHasher<AppUser>(Options.Create(new PasswordHasherOptions { IterationCount = iterations }));

    static AppUser UserWith(IPasswordHasher<AppUser> hasher)
    {
        var user = new AppUser { UserName = "teacher01", DisplayName = "教师", Role = "Teacher" };
        user.PasswordHash = hasher.HashPassword(user, Password);
        return user;
    }

    [Fact]
    public void CurrentHash_VerifiesWithoutRehash()
    {
        var user = UserWith(Hasher);
        Assert.True(CredentialVerification.TryVerify(Hasher, user, Password, out var authenticated, out var needsRehash));
        Assert.Same(user, authenticated);
        Assert.False(needsRehash);
    }

    [Fact]
    public void OlderIterationCount_RequestsRehashAfterSuccess()
    {
        var user = UserWith(OlderHasher);
        Assert.True(CredentialVerification.TryVerify(Hasher, user, Password, out _, out var needsRehash));
        Assert.True(needsRehash);

        user.PasswordHash = Hasher.HashPassword(user, Password);
        Assert.True(CredentialVerification.TryVerify(Hasher, user, Password, out _, out var rehashedAgain));
        Assert.False(rehashedAgain);
    }

    [Fact]
    public void WrongPassword_FailsWithoutRehash()
    {
        var user = UserWith(OlderHasher);
        Assert.False(CredentialVerification.TryVerify(Hasher, user, "WrongHorse123", out _, out var needsRehash));
        Assert.False(needsRehash);
    }

    [Fact]
    public void UnknownAccount_FailsWithoutRehash()
    {
        Assert.False(CredentialVerification.TryVerify(Hasher, null, Password, out var authenticated, out var needsRehash));
        Assert.False(needsRehash);
        Assert.NotEqual("teacher01", authenticated.UserName);
    }

    [Fact]
    public void OverlongPassword_FailsEvenWhenPrefixMatches()
    {
        var user = UserWith(Hasher);
        Assert.False(CredentialVerification.TryVerify(Hasher, user, Password + new string('x', 200), out _, out var needsRehash));
        Assert.False(needsRehash);
    }

    [Theory]
    [InlineData("alice", "al***")]
    [InlineData("ab", "a***")]
    [InlineData("a", "a***")]
    [InlineData("  bob  ", "bo***")]
    [InlineData("", "(空)")]
    [InlineData(null, "(空)")]
    public void MaskAccount_keeps_only_a_short_prefix(string? value, string expected)
        => Assert.Equal(expected, CredentialVerification.MaskAccount(value));
}
