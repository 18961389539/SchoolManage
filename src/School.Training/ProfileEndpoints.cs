using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this RouteGroupBuilder all)
    {
        all.MapGet("/profile", Get);
        all.MapPut("/profile", Update);
    }

    static async Task<IResult> Get(HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == CurrentUserId(ctx), ct);
        if (user == null) return Results.Unauthorized();
        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(item => item.UserId == user.Id, ct);
        return Results.Ok(View(user, person));
    }

    static async Task<IResult> Update(ProfileInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(item => item.Id == CurrentUserId(ctx), ct);
        if (user == null) return Results.Unauthorized();
        var person = await db.People.FirstOrDefaultAsync(item => item.UserId == user.Id, ct);
        var kind = person?.Kind ?? RoleKind(user.Role);
        var error = ProfileLogic.Validate(input, requireNumber: kind != "admin");
        if (error != null) return Results.BadRequest(new { message = error });

        var name = input.Name.Trim();
        var number = (input.Number ?? "").Trim();
        if (person == null)
        {
            person = new Person { UserId = user.Id, Kind = kind, Name = name, Number = number };
            db.People.Add(person);
        }
        else
        {
            person.Name = name;
            if (number.Length > 0) person.Number = number;
        }
        if (person.Kind == "admin" && person.Number.Length == 0) person.Number = await UniqueAdminNumber(db, user, ct);
        if (person.Number.Length > 0 && await db.People.AnyAsync(item => item.Id != person.Id
            && item.Kind == person.Kind && item.Number == person.Number, ct))
            return Results.Conflict(new { message = person.Kind == "admin" ? "工号已存在" : "学号/工号已存在" });
        person.Gender = (input.Gender ?? "").Trim();
        person.Phone = (input.Phone ?? "").Trim();
        person.Email = (input.Email ?? "").Trim();
        user.DisplayName = name;
        Audit.Add(db, ctx, "修改个人信息", user.UserName);
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(user, person));
    }

    static object View(AppUser user, Person? person) => new
    {
        user.Id, user.UserName, user.Role, user.DisplayName,
        kind = person?.Kind ?? RoleKind(user.Role),
        name = person?.Name ?? user.DisplayName,
        number = person?.Number ?? "",
        gender = person?.Gender ?? "",
        phone = person?.Phone ?? "",
        email = person?.Email ?? "",
        ClassId = person?.ClassId
    };

    static string RoleKind(string role) => role switch { "Student" => "student", "Admin" => "admin", _ => "teacher" };
    static string CurrentUserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    static async Task<string> UniqueAdminNumber(SchoolDbContext db, AppUser user, CancellationToken ct)
    {
        var stem = ("ADM-" + user.UserName).Trim();
        if (stem.Length > 56) stem = stem[..56];
        var candidate = stem;
        for (var index = 2; await db.People.AnyAsync(item => item.Kind == "admin" && item.Number == candidate, ct); index++)
            candidate = $"{stem}-{index}";
        return candidate;
    }
}

public static class ProfileLogic
{
    public static string? Validate(ProfileInput input, bool requireNumber)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100) return "姓名必填，且最多 100 个字符";
        var number = (input.Number ?? "").Trim();
        if (requireNumber && number.Length == 0) return "学号/工号不能为空";
        if (number.Length > 64) return "学号/工号最多 64 个字符";
        if ((input.Gender ?? "").Trim().Length > 20) return "性别最多 20 个字符";
        if ((input.Phone ?? "").Trim().Length > 30) return "手机号最多 30 个字符";
        var email = (input.Email ?? "").Trim();
        if (email.Length > 254) return "邮箱最多 254 个字符";
        if (email.Length > 0 && (!System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email))
            return "邮箱格式不正确";
        return null;
    }
}

public sealed record ProfileInput(string Name, string? Number = null, string? Gender = null, string? Phone = null, string? Email = null);
