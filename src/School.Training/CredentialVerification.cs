using Microsoft.AspNetCore.Identity;

namespace School.Training;

/// <summary>
/// 账号是否存在不改变密码校验的工作量：未知账号同样会对一个真实的占位哈希执行完整校验，
/// 避免通过登录响应时间枚举有效账号。超过长度上限的密码先截断再校验，
/// 使长度检查也不再提前短路哈希计算。
/// </summary>
public static class CredentialVerification
{
    public const int MaxPasswordLength = 128;

    static readonly AppUser Placeholder = new() { UserName = "__absent__", DisplayName = "__absent__", Role = "Student" };
    static readonly Lazy<string> PlaceholderHash = new(
        () => new PasswordHasher<AppUser>().HashPassword(Placeholder, Guid.NewGuid().ToString("N")),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool TryVerify(IPasswordHasher<AppUser> hasher, AppUser? candidate, string password, out AppUser authenticated)
    {
        authenticated = candidate ?? Placeholder;
        var supplied = password.Length > MaxPasswordLength ? password[..MaxPasswordLength] : password;
        var result = hasher.VerifyHashedPassword(authenticated, candidate?.PasswordHash ?? PlaceholderHash.Value, supplied);
        return candidate != null && password.Length <= MaxPasswordLength && result != PasswordVerificationResult.Failed;
    }
}
