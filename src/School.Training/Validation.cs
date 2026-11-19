using System.Text.RegularExpressions;

namespace School.Training;

/// <summary>
/// Boundary validation shared by the HTTP endpoints. Kept free of ASP.NET types
/// (except the <see cref="HttpRequest"/> overload) so the rules can be unit tested.
/// </summary>
public static class Validation
{
    public const int UserNameMinLength = 3;
    public const int UserNameMaxLength = 64;
    public const int PasswordMinLength = 12;
    public const int PasswordMaxLength = 128;
    public const int MaxPage = 1_000_000;
    public const int MaxPageSize = 1000;
    public const int DefaultPageSize = 20;

    static readonly Regex UserNamePattern = new("^[a-zA-Z0-9][a-zA-Z0-9._@-]{2,63}$", RegexOptions.CultureInvariant);

    public static string? UserName(string? value)
        => !string.IsNullOrWhiteSpace(value) && UserNamePattern.IsMatch(value)
            ? null
            : "账号须为3–64位字母、数字或 . _ @ -";

    public static string? Password(string? value)
        => value is { Length: >= PasswordMinLength and <= PasswordMaxLength } && value.Any(char.IsLetter) && value.Any(char.IsDigit)
            ? null
            : "密码须为12–128位，且同时包含字母和数字";

    public static (int Page, int Size) Page(HttpRequest request)
        => Page(request.Query["page"], request.Query["pageSize"]);

    public static (int Page, int Size) Page(string? page, string? pageSize)
        => (Clamp(page, 1, MaxPage, 1), Clamp(pageSize, 1, MaxPageSize, DefaultPageSize));

    static int Clamp(string? text, int min, int max, int fallback)
        => int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;
}
