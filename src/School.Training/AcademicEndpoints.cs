using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class AcademicEndpoints
{
    public static void MapAcademicEndpoints(this RouteGroupBuilder all)
    {
        var api = all.MapGroup("").RequireAuthorization("Admin");
        api.MapGet("/professions", async (HttpRequest req, SchoolDbContext db, CancellationToken ct) =>
        {
            var q = db.Professions.AsNoTracking(); var search = req.Query["search"].ToString().Trim();
            if (search.Length > 0) q = q.Where(x => x.Name.Contains(search) || x.Code.Contains(search));
            var (page, size) = Validation.Page(req);
            return Results.Ok(new { total = await q.CountAsync(ct), items = await q.OrderBy(x => x.Code).Skip((page - 1) * size).Take(size).ToListAsync(ct) });
        });
        api.MapPost("/professions", async (ProfessionRequest input, HttpContext ctx, SchoolDbContext db) =>
        {
            if (!ValidProfession(input)) return Results.BadRequest(new { message = "专业名称、代码必填，名称最多100字，代码最多64字，备注最多2000字" });
            var code = input.Code.Trim(); if (await db.Professions.AnyAsync(x => x.Code == code, ctx.RequestAborted)) return Results.Conflict(new { message = "专业代码已存在" });
            var entity = new Profession { Name = input.Name.Trim(), Code = code, Remark = input.Remark.Trim() }; db.Professions.Add(entity);
            Audit.Add(db, ctx, "新增专业", entity.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(entity);
        });
        api.MapPut("/professions/{id}", async (string id, ProfessionRequest input, HttpContext ctx, SchoolDbContext db) =>
        {
            var entity = await db.Professions.FindAsync([id], ctx.RequestAborted); if (entity == null) return Results.NotFound(new { message = "专业不存在" });
            if (!ValidProfession(input)) return Results.BadRequest(new { message = "专业名称、代码必填，且字段不能超过长度限制" });
            var code = input.Code.Trim(); if (await db.Professions.AnyAsync(x => x.Code == code && x.Id != id, ctx.RequestAborted)) return Results.Conflict(new { message = "专业代码已存在" });
            entity.Name = input.Name.Trim(); entity.Code = code; entity.Remark = input.Remark.Trim(); Audit.Add(db, ctx, "编辑专业", entity.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(entity);
        });
        api.MapDelete("/professions/{id}", async (string id, HttpContext ctx, SchoolDbContext db) =>
        {
            var entity = await db.Professions.FindAsync([id], ctx.RequestAborted); if (entity == null) return Results.NotFound(new { message = "专业不存在" });
            if (await db.Classes.AnyAsync(x => x.ProfessionId == id, ctx.RequestAborted)) return Results.Conflict(new { message = "专业下存在班级，请先调整班级关联" });
            db.Professions.Remove(entity); Audit.Add(db, ctx, "删除专业", entity.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { message = "已删除" });
        });
        api.MapGet("/classes", async (HttpRequest req, SchoolDbContext db, CancellationToken ct) =>
        {
            var search = req.Query["search"].ToString().Trim();
            var q = from c in db.Classes.AsNoTracking() join p in db.Professions on c.ProfessionId equals p.Id select new { c.Id, c.Name, c.ProfessionId, professionName = p.Name, c.Remark };
            if (search.Length > 0) q = q.Where(x => x.Name.Contains(search) || x.professionName.Contains(search));
            if (!string.IsNullOrEmpty(req.Query["professionId"])) { var pid = req.Query["professionId"].ToString(); q = q.Where(x => x.ProfessionId == pid); }
            var (page, size) = Validation.Page(req); return Results.Ok(new { total = await q.CountAsync(ct), items = await q.OrderBy(x => x.Name).Skip((page - 1) * size).Take(size).ToListAsync(ct) });
        });
        api.MapPost("/classes", async (ClassRequest input, HttpContext ctx, SchoolDbContext db) =>
        {
            var error = await CheckClass(input, db, cancellationToken: ctx.RequestAborted); if (error != null) return ClassError(error);
            var entity = new SchoolClass { Name = input.Name.Trim(), ProfessionId = input.ProfessionId, Remark = input.Remark.Trim() }; db.Classes.Add(entity); Audit.Add(db, ctx, "新增班级", entity.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(entity);
        });
        api.MapPut("/classes/{id}", async (string id, ClassRequest input, HttpContext ctx, SchoolDbContext db) =>
        {
            var entity = await db.Classes.FindAsync([id], ctx.RequestAborted); if (entity == null) return Results.NotFound(new { message = "班级不存在" });
            var error = await CheckClass(input, db, id, ctx.RequestAborted); if (error != null) return ClassError(error);
            entity.Name = input.Name.Trim(); entity.ProfessionId = input.ProfessionId; entity.Remark = input.Remark.Trim(); Audit.Add(db, ctx, "编辑班级", entity.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(entity);
        });
        api.MapDelete("/classes/{id}", async (string id, HttpContext ctx, SchoolDbContext db) =>
        {
            var entity = await db.Classes.FindAsync([id], ctx.RequestAborted); if (entity == null) return Results.NotFound(new { message = "班级不存在" });
            if (await db.People.AnyAsync(x => x.ClassId == id, ctx.RequestAborted)) return Results.Conflict(new { message = "班级下存在师生，请先调整其所属班级" });
            db.Classes.Remove(entity); Audit.Add(db, ctx, "删除班级", entity.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { message = "已删除" });
        });
        api.MapGet("/people", async (HttpRequest req, SchoolDbContext db, CancellationToken ct) =>
        {
            var kind = req.Query["kind"].ToString(); if (kind is not ("teacher" or "student")) return Results.BadRequest(new { message = "师生类型无效" });
            var q = from p in db.People.AsNoTracking()
                    join u in db.Users on p.UserId equals u.Id
                    join c in db.Classes on p.ClassId equals c.Id into classes
                    from c in classes.DefaultIfEmpty()
                    where p.Kind == kind
                    select new { p.Id, p.Kind, p.Name, u.UserName, p.Number, p.ClassId, className = c == null ? "" : c.Name, p.Gender, p.Phone, p.Email, u.Role };
            var search = req.Query["search"].ToString().Trim(); if (search.Length > 0) q = q.Where(x => x.Name.Contains(search) || x.UserName.Contains(search) || x.Number.Contains(search) || x.Phone.Contains(search));
            var classId = req.Query["classId"].ToString(); if (classId.Length > 0) q = q.Where(x => x.ClassId == classId);
            var role = req.Query["role"].ToString(); if (role.Length > 0) q = q.Where(x => x.Role == role);
            var (page, size) = Validation.Page(req); return Results.Ok(new { total = await q.CountAsync(ct), items = await q.OrderBy(x => x.Number).Skip((page - 1) * size).Take(size).ToListAsync(ct) });
        });
        api.MapPost("/people", async (PersonRequest input, HttpContext ctx, SchoolDbContext db, IPasswordHasher<AppUser> hasher) =>
        {
            var error = await CheckPerson(input, db, cancellationToken: ctx.RequestAborted) ?? Validation.Password(input.Password); if (error != null) return Results.BadRequest(new { message = error });
            var user = new AppUser { UserName = input.UserName.Trim().ToLowerInvariant(), DisplayName = input.Name.Trim(), Role = input.Kind == "student" ? "Student" : input.Role, MustChangePassword = true };
            user.PasswordHash = hasher.HashPassword(user, input.Password); db.Users.Add(user);
            var p = CreatePerson(input, user.Id); db.People.Add(p); Audit.Add(db, ctx, "新增" + (p.Kind == "teacher" ? "教师" : "学生"), p.Name); await db.SaveChangesAsync(ctx.RequestAborted); return Results.Ok(new { p.Id });
        });
        api.MapPut("/people/{id}", (string id, PersonRequest input, HttpContext ctx, SchoolDbContext db) => AdminInvariant.SerializeAsync(async () =>
        {
            var p = await db.People.FindAsync([id], ctx.RequestAborted); if (p == null) return Results.NotFound(new { message = "师生记录不存在" });
            if (p.Kind != input.Kind) return Results.BadRequest(new { message = "不能修改师生类型" });
            var error = await CheckPerson(input, db, p.Id, p.UserId, ctx.RequestAborted); if (error != null) return Results.BadRequest(new { message = error });
            var user = await db.Users.SingleAsync(x => x.Id == p.UserId, ctx.RequestAborted); var role = input.Kind == "student" ? "Student" : input.Role;
            if (user.Role == "Admin" && role != "Admin" && await db.Users.CountAsync(x => x.Role == "Admin", ctx.RequestAborted) <= 1) return Results.Conflict(new { message = "必须保留至少一个管理员" });
            p.Name = input.Name.Trim(); p.Number = input.Number.Trim(); p.ClassId = string.IsNullOrWhiteSpace(input.ClassId) ? null : input.ClassId; p.Gender = input.Gender.Trim(); p.Phone = input.Phone.Trim(); p.Email = input.Email.Trim();
            if (user.UserName != input.UserName.Trim().ToLowerInvariant() || user.Role != role) user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.UserName = input.UserName.Trim().ToLowerInvariant(); user.DisplayName = p.Name; user.Role = role;
            Audit.Add(db, ctx, "编辑师生", p.Name); await db.SaveChangesAsync(ctx.RequestAborted); ctx.RequestServices.GetRequiredService<SimulationSessions>().RevokeUser(user.Id); return Results.Ok(new { p.Id });
        }, ctx.RequestAborted));
        api.MapDelete("/people/{id}", async (string id, HttpContext ctx, SchoolDbContext db) => await DeletePeople([id], ctx, db));
        api.MapPost("/people/batch-delete", async (IdsRequest input, HttpContext ctx, SchoolDbContext db) => await DeletePeople(input.Ids, ctx, db));
        api.MapPost("/people/{id}/reset-password", async (string id, ResetPasswordRequest input, HttpContext ctx, SchoolDbContext db, IPasswordHasher<AppUser> hasher) =>
        {
            var error = Validation.Password(input.NewPassword); if (error != null) return Results.BadRequest(new { message = error });
            var p = await db.People.FindAsync([id], ctx.RequestAborted); if (p == null) return Results.NotFound(new { message = "师生记录不存在" });
            var user = await db.Users.SingleAsync(x => x.Id == p.UserId, ctx.RequestAborted); user.PasswordHash = hasher.HashPassword(user, input.NewPassword); user.SecurityStamp = Guid.NewGuid().ToString("N"); user.MustChangePassword = true;
            Audit.Add(db, ctx, "重置密码", p.Name); await db.SaveChangesAsync(ctx.RequestAborted); ctx.RequestServices.GetRequiredService<SimulationSessions>().RevokeUser(user.Id); return Results.Ok(new { message = "密码已重置，原登录会话已失效" });
        });
    }
    const string DuplicateClassMessage = "该专业下已存在同名班级";
    static bool ValidProfession(ProfessionRequest input) => !string.IsNullOrWhiteSpace(input.Name) && !string.IsNullOrWhiteSpace(input.Code) && input.Name.Length <= 100 && input.Code.Length <= 64 && input.Remark.Length <= 2000;
    static IResult ClassError(string message) => message == DuplicateClassMessage
        ? Results.Conflict(new { message })
        : Results.BadRequest(new { message });
    static async Task<string?> CheckClass(ClassRequest input, SchoolDbContext db, string? id = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || input.Remark.Length > 2000) return "班级名称必填，名称最多100字，备注最多2000字";
        if (!await db.Professions.AnyAsync(x => x.Id == input.ProfessionId, cancellationToken)) return "请选择有效专业";
        if (await db.Classes.AnyAsync(x => x.Id != id && x.Name == input.Name.Trim() && x.ProfessionId == input.ProfessionId, cancellationToken)) return DuplicateClassMessage;
        return null;
    }
    public static async Task<string?> CheckPerson(PersonRequest input, SchoolDbContext db, string? personId = null, string? userId = null, CancellationToken cancellationToken = default)
    {
        if (input.Kind is not ("teacher" or "student")) return "师生类型无效";
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || string.IsNullOrWhiteSpace(input.Number) || input.Number.Length > 64) return "姓名与学号/工号必填，且不能超过长度限制";
        var err = Validation.UserName(input.UserName); if (err != null) return err;
        if (input.Kind == "teacher" && input.Role is not ("Admin" or "Teacher")) return "教师角色无效";
        if (!string.IsNullOrWhiteSpace(input.ClassId) && !await db.Classes.AnyAsync(x => x.Id == input.ClassId, cancellationToken)) return "所属班级不存在";
        if (input.Gender.Length > 20 || input.Phone.Length > 30 || input.Email.Length > 254) return "性别、手机号或邮箱超过长度限制";
        if (input.Email.Length > 0 && (!System.Net.Mail.MailAddress.TryCreate(input.Email, out var email) || email.Address != input.Email)) return "邮箱格式不正确";
        var userName = input.UserName.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Id != userId && x.UserName == userName, cancellationToken)) return "账号已存在";
        if (await db.People.AnyAsync(x => x.Id != personId && x.Kind == input.Kind && x.Number == input.Number.Trim(), cancellationToken)) return input.Kind == "teacher" ? "工号已存在" : "学号已存在";
        return null;
    }
    public static Person CreatePerson(PersonRequest input, string userId) => new() { UserId = userId, Kind = input.Kind, Name = input.Name.Trim(), Number = input.Number.Trim(), ClassId = string.IsNullOrWhiteSpace(input.ClassId) ? null : input.ClassId, Gender = input.Gender.Trim(), Phone = input.Phone.Trim(), Email = input.Email.Trim() };
    static Task<IResult> DeletePeople(string[] ids, HttpContext ctx, SchoolDbContext db) => AdminInvariant.SerializeAsync(async () =>
    {
        if (ids.Length is < 1 or > 1000) return Results.BadRequest(new { message = "每次选择1–1000条记录" });
        var distinct = ids.Distinct().ToArray(); var people = await db.People.Where(x => distinct.Contains(x.Id)).ToListAsync(ctx.RequestAborted);
        if (people.Count != distinct.Length) return Results.NotFound(new { message = "部分记录不存在，请刷新后重试" });
        var userIds = people.Select(x => x.UserId).ToArray();
        if (userIds.Contains(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier))) return Results.Conflict(new { message = "不能删除当前登录账号" });
        var users = await db.Users.Where(x => userIds.Contains(x.Id)).ToListAsync(ctx.RequestAborted);
        var admins = users.Count(x => x.Role == "Admin"); if (admins > 0 && await db.Users.CountAsync(x => x.Role == "Admin", ctx.RequestAborted) <= admins) return Results.Conflict(new { message = "必须保留至少一个管理员" });
        var ownedClassrooms = await db.Classrooms.CountAsync(x => userIds.Contains(x.TeacherUserId), ctx.RequestAborted);
        if (ownedClassrooms > 0) return Results.Conflict(new { message = "所选教师仍负责课堂，请先转移或删除相关课堂" });
        db.People.RemoveRange(people); db.Users.RemoveRange(users); Audit.Add(db, ctx, "删除师生", string.Join(",", people.Select(x => x.Name))); await db.SaveChangesAsync(ctx.RequestAborted);
        foreach (var id in userIds) ctx.RequestServices.GetRequiredService<SimulationSessions>().RevokeUser(id);
        return Results.Ok(new { deletedCount = people.Count });
    }, ctx.RequestAborted);
}
public record ProfessionRequest(string Name, string Code, string Remark = "");
public record ClassRequest(string Name, string ProfessionId, string Remark = "");
public record PersonRequest(string Kind, string Name, string UserName, string Number, string? ClassId = null, string Gender = "", string Phone = "", string Email = "", string Role = "Teacher", string Password = "");
public record IdsRequest(string[] Ids);
public record ResetPasswordRequest(string NewPassword);
