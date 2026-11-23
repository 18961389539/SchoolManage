using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Identity;

namespace School.Training;

/// <summary>
/// 账号是否存在不改变密码校验的工作量：未知账号同样会对一个真实的占位哈希执行完整校验，
/// 避免通过登录响应时间枚举有效账号。超过长度上限的密码先截断再校验，
/// 使长度检查也不再提前短路哈希计算。占位哈希按当前哈希器生成，确保登录迭代参数
/// 调整后，未知账号与真实账号的校验耗时仍然一致。
/// </summary>
public static class CredentialVerification
{
    public const int MaxPasswordLength = 128;

    /// <summary>
    /// Login failures are audited, but a mistyped password in the account field must not be
    /// written verbatim. Keep just enough prefix for an operator to recognise the attempt.
    /// </summary>
    public static string MaskAccount(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return "(空)";
        return trimmed.Length <= 2 ? trimmed[..1] + "***" : trimmed[..2] + "***";
    }

    static readonly AppUser Placeholder = new() { UserName = "__absent__", DisplayName = "__absent__", Role = "Student" };
    static readonly ConditionalWeakTable<IPasswordHasher<AppUser>, string> PlaceholderHashes = new();

    public static bool TryVerify(IPasswordHasher<AppUser> hasher, AppUser? candidate, string password,
        out AppUser authenticated, out bool needsRehash)
    {
        authenticated = candidate ?? Placeholder;
        needsRehash = false;
        var supplied = password.Length > MaxPasswordLength ? password[..MaxPasswordLength] : password;
        var hash = candidate?.PasswordHash ?? PlaceholderHashes.GetValue(hasher,
            static owner => owner.HashPassword(Placeholder, Guid.NewGuid().ToString("N")));
        var result = hasher.VerifyHashedPassword(authenticated, hash, supplied);
        if (candidate == null || password.Length > MaxPasswordLength) return false;
        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }
}
