using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace School.Training;

public sealed class ClassroomLiveShare : IDisposable
{
    public const int MaxFrameLength = 160_000;
    readonly MemoryCache shares = new(new MemoryCacheOptions { SizeLimit = 256 });
    readonly object gate = new();

    public sealed record State(string Token, bool AllowAssist, string? Frame);

    public string Begin(string workId, bool allowAssist)
    {
        lock (gate)
        {
            var token = Guid.NewGuid().ToString("N");
            Store(workId, new State(token, allowAssist, null));
            return token;
        }
    }

    public State? Read(string workId)
    {
        lock (gate) return shares.Get<State>(workId);
    }

    public bool Update(string workId, string token, string frame)
    {
        lock (gate)
        {
            var current = shares.Get<State>(workId);
            if (current?.Token != token) return false;
            Store(workId, current with { Frame = frame });
            return true;
        }
    }

    public void Stop(string workId, string token)
    {
        lock (gate)
            if (shares.Get<State>(workId)?.Token == token) shares.Remove(workId);
    }

    public void Revoke(string workId)
    {
        lock (gate) shares.Remove(workId);
    }

    void Store(string key, State state) => shares.Set(key, state, new MemoryCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(12),
        Size = 1
    });

    public void Dispose() => shares.Dispose();
}

public static class ClassroomLiveShareEndpoints
{
    public static void MapClassroomLiveShare(this RouteGroupBuilder staff, RouteGroupBuilder learners)
    {
        staff.MapGet("/classrooms/case-sessions/{id}/live", Watch);
        var share = learners.MapGroup("/classrooms/case-sessions/{id}/live");
        share.MapPost("", Begin);
        share.MapPost("/frame", Frame).WithMetadata(new RequestSizeLimitAttribute(ClassroomLiveShare.MaxFrameLength + 512));
        share.MapPost("/stop", Stop);
    }

    sealed record ShareInput(bool AllowAssist);
    sealed record FrameInput(string Token, string Image);
    sealed record StopInput(string Token);

    internal static IQueryable<ClassroomCaseSession> ActiveSessions(SchoolDbContext db)
    {
        var rooms = TeachingLogic.OpenClassrooms(db, DateTime.UtcNow);
        return db.ClassroomCaseSessions.Where(session => session.Status == "active" && session.WorkId != ""
            && rooms.Any(room => room.Id == session.ClassroomId)
            && db.ClassroomStudents.Any(student => student.ClassroomId == session.ClassroomId && student.StudentUserId == session.UserId));
    }

    static IQueryable<ClassroomCaseSession> StudentSessions(SchoolDbContext db, HttpContext ctx)
        => ActiveSessions(db).Where(session => session.UserId == ctx.User.FindFirstValue(ClaimTypes.NameIdentifier));

    internal static IQueryable<ClassroomCaseSession> StaffSessions(SchoolDbContext db, HttpContext ctx)
    {
        var user = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ctx.User.IsInRole("Admin") ? db.ClassroomCaseSessions
            : db.ClassroomCaseSessions.Where(session => db.Classrooms.Any(room => room.Id == session.ClassroomId && room.TeacherUserId == user));
    }

    static async Task<IResult> Begin(string id, ShareInput input, HttpContext ctx, SchoolDbContext db, ClassroomLiveShare shares, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var workId = await StudentSessions(db, ctx).Where(item => item.Id == id).Select(item => item.WorkId).FirstOrDefaultAsync(ct);
        if (workId == null) return Missing();
        var token = shares.Begin(workId, input.AllowAssist);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { token });
    }

    static async Task<IResult> Frame(string id, FrameInput input, HttpContext ctx, SchoolDbContext db, ClassroomLiveShare shares, CancellationToken ct)
    {
        var image = input.Image;
        if (string.IsNullOrEmpty(image) || image.Length > ClassroomLiveShare.MaxFrameLength)
            return Results.BadRequest(new { message = "共享画面大小无效" });
        var data = new byte[image.Length * 3 / 4];
        if (!Convert.TryFromBase64String(image, data, out var length) || length < 4 || data[0] != 0xff || data[1] != 0xd8
            || data[length - 2] != 0xff || data[length - 1] != 0xd9)
            return Results.BadRequest(new { message = "共享画面需为 JPEG 图像" });
        var workId = await StudentSessions(db, ctx).Where(item => item.Id == id).Select(item => item.WorkId).FirstOrDefaultAsync(ct);
        return workId != null && shares.Update(workId, input.Token, image) ? Results.NoContent() : Missing();
    }

    static async Task<IResult> Stop(string id, StopInput input, HttpContext ctx, SchoolDbContext db, ClassroomLiveShare shares, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var workId = await StudentSessions(db, ctx).Where(item => item.Id == id).Select(item => item.WorkId).FirstOrDefaultAsync(ct);
        if (workId != null) shares.Stop(workId, input.Token);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> Watch(string id, HttpContext ctx, SchoolDbContext db, ClassroomLiveShare shares, CancellationToken ct)
    {
        if (!await StaffSessions(db, ctx).AnyAsync(item => item.Id == id, ct)) return Missing();
        var workId = await ActiveSessions(db).Where(item => item.Id == id).Select(item => item.WorkId).FirstOrDefaultAsync(ct);
        var state = workId == null ? null : shares.Read(workId);
        return Results.Ok(new { sharing = state != null, allowAssist = state?.AllowAssist == true, image = state?.Frame });
    }

    static IResult Missing() => Results.NotFound(new { message = "实操共享已结束或当前无权访问" });
}
