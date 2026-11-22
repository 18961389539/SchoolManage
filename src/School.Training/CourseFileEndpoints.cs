using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class CourseFileEndpoints
{
    const int MaxFilesPerResource = 50;

    public static void MapCourseFileEndpoints(this RouteGroupBuilder all)
    {
        var reader = all.MapGroup("");
        var admin = all.MapGroup("").RequireAuthorization("Admin");

        reader.MapGet("/course-files/{id}/download", Download);
        reader.MapGet("/classrooms/{classroomId}/files/{id}", OpenClassroomFile);
        admin.MapPost("/resources/{id}/files", Upload);
        admin.MapPut("/course-files/{id}", Rename);
        admin.MapDelete("/course-files/{id}", Delete);
        admin.MapPost("/course-files/{id}/pin", Pin);
    }

    static async Task<IResult> OpenClassroomFile(string classroomId, string id, bool? preview, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == classroomId, ct);
        if (classroom == null || (!ctx.User.IsInRole("Admin")
            && !(ctx.User.IsInRole("Teacher") && classroom.TeacherUserId == userId)
            && !(ctx.User.IsInRole("Student") && await db.ClassroomStudents.AnyAsync(
                item => item.ClassroomId == classroomId && item.StudentUserId == userId, ct))))
            return NotFound("附件不存在");
        var file = await (from attachment in db.CourseResourceFiles.AsNoTracking()
                          join resource in db.CourseResources on attachment.ResourceId equals resource.Id
                          join section in db.CourseSections on resource.SectionId equals section.Id
                          join chapter in db.CourseChapters on section.ChapterId equals chapter.Id
                          where attachment.Id == id && chapter.CourseId == classroom.CourseId
                          select attachment).FirstOrDefaultAsync(ct);
        if (file == null) return NotFound("附件不存在");
        var path = CourseFileStore.PathFor(paths, file.ResourceId, file.StoredName);
        if (!File.Exists(path)) return NotFound("附件文件缺失，请联系管理员");
        if (preview != true) return Results.File(path, "application/octet-stream", file.Name);
        var contentType = PreviewContentType(file.StoredName);
        if (contentType == null) return Bad("此附件格式仅支持下载");
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self'; script-src 'none'; base-uri 'none'";
        return Results.File(path, contentType, enableRangeProcessing: true);
    }

    static string? PreviewContentType(string storedName) => Path.GetExtension(storedName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".txt" or ".md" or ".log" => "text/plain; charset=utf-8",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        _ => null
    };

    static async Task<IResult> Download(string id, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var row = await (from file in db.CourseResourceFiles.AsNoTracking()
                         join resource in db.CourseResources.AsNoTracking() on file.ResourceId equals resource.Id
                         join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                         join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                         join course in db.Courses.AsNoTracking() on chapter.CourseId equals course.Id
                         where file.Id == id
                         select new { File = file, course.Status }).FirstOrDefaultAsync(ct);
        if (row == null) return NotFound("附件不存在");
        if (!ctx.User.IsInRole("Admin") && row.Status != "published") return NotFound("附件不存在或课程尚未发布");
        var path = CourseFileStore.PathFor(paths, row.File.ResourceId, row.File.StoredName);
        if (!File.Exists(path)) return NotFound("附件文件缺失，请联系管理员");
        return Results.File(path, "application/octet-stream", row.File.Name);
    }

    static async Task<IResult> Upload(string id, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var resource = await db.CourseResources.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (resource == null) return NotFound("资源不存在");
        if (!ctx.Request.HasFormContentType) return Bad("请选择要上传的附件");
        var form = await ctx.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0) return Bad("请选择要上传的附件");
        if (file.Length > Limits.MaxUploadBytes) return Bad($"附件不能超过 {Limits.MaxUploadMegabytes}MB");
        if (await db.CourseResourceFiles.CountAsync(item => item.ResourceId == id, ct) >= MaxFilesPerResource)
            return Conflict($"单个资源最多上传 {MaxFilesPerResource} 个附件");
        var originalName = file.FileName.Replace('\\', '/').Split('/').Last().Trim();
        if (originalName.Length == 0) originalName = "附件";
        if (originalName.Length > 100) originalName = originalName[..100];
        var requestedName = form["name"].ToString().Trim();
        var displayName = requestedName.Length > 0 ? requestedName : originalName;
        var nameError = ValidateName(displayName);
        if (nameError != null) return Bad(nameError);
        var extension = Path.GetExtension(originalName);
        if (extension.Length > 20 || extension.Any(ch => !char.IsLetterOrDigit(ch) && ch != '.')) extension = "";
        var storedName = Guid.NewGuid().ToString("N") + extension;
        var directory = CourseFileStore.DirectoryFor(paths, id);
        Directory.CreateDirectory(directory);
        var path = CourseFileStore.PathFor(paths, id, storedName);
        try
        {
            await using (var stream = File.Create(path)) await file.CopyToAsync(stream, ct);
            var next = await db.CourseResourceFiles.Where(item => item.ResourceId == id)
                .Select(item => (int?)item.SortOrder).MaxAsync(ct) ?? -1;
            var entity = new CourseResourceFile
            {
                ResourceId = id, Name = displayName, StoredName = storedName, Size = file.Length, SortOrder = next + 1
            };
            db.CourseResourceFiles.Add(entity);
            Audit.Add(db, ctx, "上传课程附件", resource.Name, displayName);
            await db.SaveChangesAsync(ct);
            return Results.Ok(View(entity));
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    static async Task<IResult> Rename(string id, CourseFileRenameInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.CourseResourceFiles.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity == null) return NotFound("附件不存在");
        var name = (input.Name ?? "").Trim();
        var error = ValidateName(name);
        if (error != null) return Bad(error);
        entity.Name = name;
        Audit.Add(db, ctx, "重命名课程附件", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(entity));
    }

    static async Task<IResult> Delete(string id, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var entity = await db.CourseResourceFiles.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity == null) return NotFound("附件不存在");
        db.CourseResourceFiles.Remove(entity);
        Audit.Add(db, ctx, "删除课程附件", entity.Name);
        await db.SaveChangesAsync(ct);
        CourseFileStore.Delete(paths, [new StoredCourseFile(entity.ResourceId, entity.StoredName)]);
        return Results.Ok(new { message = "已删除" });
    }

    static async Task<IResult> Pin(string id, CourseFilePinInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.CourseResourceFiles.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity == null) return NotFound("附件不存在");
        entity.IsPinned = input.Pinned;
        Audit.Add(db, ctx, input.Pinned ? "置顶课程附件" : "取消置顶课程附件", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(entity));
    }

    static string? ValidateName(string name)
        => name.Length is 0 or > 100 || name.Any(char.IsControl) ? "附件名称需为 1–100 个字符且不能包含控制字符" : null;

    public static object View(CourseResourceFile file)
        => new { file.Id, file.ResourceId, file.Name, file.Size, file.IsPinned, file.SortOrder, file.CreatedAt,
            canPreview = PreviewContentType(file.StoredName) != null };

    static IResult Bad(string message) => Results.BadRequest(new { message });
    static IResult Conflict(string message) => Results.Conflict(new { message });
    static IResult NotFound(string message = "记录不存在") => Results.NotFound(new { message });
}
