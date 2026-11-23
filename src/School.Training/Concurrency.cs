namespace School.Training;

/// <summary>
/// Optimistic concurrency for editable content. The client echoes the version it loaded and a
/// write built on a stale copy is refused with 409 + <c>stale</c> instead of silently replacing
/// whatever the other editor saved in the meantime.
/// </summary>
public static class Concurrency
{
    public const string Code = "stale";
    public const string Message = "内容已被其他人修改，请刷新后重试";

    /// <summary>Both sides must speak about the same revision; a missing version is not "latest".</summary>
    public static bool Matches(int? submitted, int current) => submitted == current;

    public static IResult MissingVersion() => Results.BadRequest(new { message = "请求缺少版本号，请刷新页面后重试" });

    public static IResult Conflict(string subject) =>
        Results.Json(new { message = $"{subject}已被其他人修改，请刷新后重试", code = Code }, statusCode: StatusCodes.Status409Conflict);
}
