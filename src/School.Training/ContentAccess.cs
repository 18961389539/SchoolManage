using System.Security.Claims;

namespace School.Training;

/// <summary>
/// Ownership for courses, question banks and papers. Visibility is separate from a course's
/// draft/published status: private and shared resources stay out of the admin standard library.
/// </summary>
public static class ContentAccess
{
    public const string Private = "private";
    public const string Shared = "shared";
    public const string Published = "published";

    public static bool Known(string? value) => value is Private or Shared or Published;

    public static string UserId(ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    public static bool CanSee(ClaimsPrincipal user, string? ownerId, string visibility)
    {
        if (user.IsInRole("Admin")) return true;
        if (visibility == Published) return true;
        if (visibility == Shared) return user.IsInRole("Teacher");
        return user.IsInRole("Teacher") && ownerId == UserId(user);
    }

    public static bool CanEdit(ClaimsPrincipal user, string? ownerId, string visibility)
    {
        if (user.IsInRole("Admin")) return true;
        return user.IsInRole("Teacher") && ownerId == UserId(user) && visibility != Published;
    }

    public static string? TryNormalizeCreate(ClaimsPrincipal user, string? requested, out string visibility)
    {
        if (user.IsInRole("Admin"))
        {
            if (string.IsNullOrWhiteSpace(requested)) { visibility = Published; return null; }
            if (!Known(requested)) { visibility = Published; return "可见范围不合法"; }
            visibility = requested;
            return null;
        }
        if (requested == Published) { visibility = Private; return "只有管理员可以把内容纳入公共标准库"; }
        if (string.IsNullOrWhiteSpace(requested) || requested == Private) { visibility = Private; return null; }
        if (requested == Shared) { visibility = Shared; return null; }
        visibility = Private;
        return "可见范围不合法";
    }

    public static string? ApplyVisibility(ClaimsPrincipal user, string current, string? requested, out string next)
    {
        next = current;
        if (string.IsNullOrWhiteSpace(requested) || requested == current) return null;
        if (!Known(requested)) return "可见范围不合法";
        if (!user.IsInRole("Admin") && (requested == Published || current == Published))
            return "只有管理员可以把内容纳入公共标准库";
        next = requested;
        return null;
    }

    /// <summary>A published course a teacher may attach to a classroom. Standard-library courses still need activation.</summary>
    public static bool UsableForClassroom(string status, string visibility, bool isActive, string? ownerId, bool callerIsAdmin, string teacherUserId)
    {
        if (status != "published") return false;
        if (visibility == Published) return isActive;
        if (visibility == Shared) return true;
        return ownerId == teacherUserId || callerIsAdmin;
    }

    /// <summary>
    /// Draft courses stay with their owner. Colleagues see a shared course only after its status is published.
    /// </summary>
    public static IResult? RequireCourseEdit(ClaimsPrincipal user, string? ownerId, string visibility, string status)
    {
        if (user.IsInRole("Admin")) return null;
        var listed = CanSee(user, ownerId, visibility) && (status == "published" || CanEdit(user, ownerId, visibility));
        if (!listed) return Results.NotFound(new { message = "课程不存在或尚未发布" });
        if (!CanEdit(user, ownerId, visibility))
            return Results.Json(new { message = "只能编辑本人尚未纳入公共标准库的课程" }, statusCode: StatusCodes.Status403Forbidden);
        return null;
    }

    public static IResult? RequireEdit(ClaimsPrincipal user, string? ownerId, string visibility, string hidden, string locked)
    {
        if (user.IsInRole("Admin")) return null;
        if (!CanSee(user, ownerId, visibility)) return Results.NotFound(new { message = hidden });
        if (!CanEdit(user, ownerId, visibility))
            return Results.Json(new { message = locked }, statusCode: StatusCodes.Status403Forbidden);
        return null;
    }
}
