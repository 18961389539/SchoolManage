using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class TeachingEndpoints
{
    public static void MapTeachingEndpoints(this RouteGroupBuilder all)
    {
        var reader = all.MapGroup("");
        var admin = all.MapGroup("").RequireAuthorization("Admin");
        var staff = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Admin", "Teacher"));
        var learners = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Student"));

        reader.MapGet("/courses", ListCourses);
        reader.MapGet("/courses/{id}", GetCourse);
        reader.MapGet("/course-series", ListCourseSeries);
        reader.MapGet("/teaching/availability", GetAvailability);
        reader.MapGet("/classrooms", ListClassrooms);
        reader.MapGet("/classrooms/{id}/content", GetClassroomContent);

        admin.MapPost("/courses", CreateCourse);
        admin.MapPut("/courses/{id}", UpdateCourse);
        admin.MapDelete("/courses/{id}", DeleteCourse);
        admin.MapPost("/courses/{id}/publish", PublishCourse);
        admin.MapPost("/courses/{id}/unpublish", UnpublishCourse);
        admin.MapPut("/courses/{id}/activation", SetCourseActivation);
        admin.MapPost("/course-series", (SeriesInput input, SchoolDbContext db, CancellationToken ct) => SaveCourseSeries(null, input, db, ct));
        admin.MapPut("/course-series/{id}", SaveCourseSeries);
        admin.MapDelete("/course-series/{id}", DeleteCourseSeries);
        admin.MapPost("/courses/{id}/chapters", CreateChapter);
        admin.MapPut("/chapters/{id}", UpdateChapter);
        admin.MapDelete("/chapters/{id}", DeleteChapter);
        admin.MapPost("/chapters/{id}/sections", CreateSection);
        admin.MapPut("/sections/{id}", UpdateSection);
        admin.MapDelete("/sections/{id}", DeleteSection);
        admin.MapPost("/sections/{id}/resources", CreateResource);
        admin.MapGet("/resources/{id}", GetResourceDetail);
        admin.MapGet("/course-topologies", ListTopologies);
        admin.MapCaseTopology();
        admin.MapPut("/resources/{id}", UpdateResource);
        admin.MapDelete("/resources/{id}", DeleteResource);

        staff.MapGet("/teaching/classes", GetTeachingClasses);
        admin.MapGet("/teaching/teachers", GetTeachers);
        staff.MapGet("/classrooms/{id}/students", GetClassroomStudents);
        staff.MapGet("/teaching/students", ListSelectableStudents);
        staff.MapGet("/classrooms/{id}/progress", GetClassroomProgress);
        staff.MapPost("/classrooms", CreateClassroom);
        staff.MapPut("/classrooms/{id}", UpdateClassroom);
        staff.MapDelete("/classrooms/{id}", DeleteClassroom);
        staff.MapPost("/classrooms/{id}/open", OpenClassroom);
        staff.MapPost("/classrooms/{id}/close", CloseClassroom);
        staff.MapPost("/classrooms/{id}/students", AddClassroomStudents);
        staff.MapPost("/classrooms/{id}/students/remove", RemoveClassroomStudents);

        learners.MapPost("/classrooms/{id}/resources/{resourceId}/start", StartLearning);
        learners.MapPost("/classrooms/{id}/resources/{resourceId}/complete", CompleteLearning);
        learners.MapPost("/classrooms/{id}/resources/{resourceId}/submit", SubmitPractice);
    }

    static async Task<IResult> ListTopologies(string? nodeId, SchoolDbContext db, CancellationToken ct)
        => Results.Ok(await db.CourseResources.AsNoTracking()
            .Where(item => item.Kind == "casePractice" && item.Topology != "" && item.StartupNodeId == nodeId)
            .OrderBy(item => item.Name).Select(item => new { item.Id, item.Name, item.Topology })
            .Take(100).ToListAsync(ct));

    static async Task<IResult> ListCourses(HttpRequest req, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var q = db.Courses.AsNoTracking();
        var isAdmin = ctx.User.IsInRole("Admin");
        if (!isAdmin) q = q.Where(course => course.Status == "published");
        var search = req.Query["search"].ToString().Trim();
        if (search.Length > 0) q = q.Where(course => course.Name.Contains(search) || course.Description.Contains(search));
        var type = req.Query["type"].ToString();
        if (TeachingLogic.CourseTypes.Contains(type)) q = q.Where(course => course.Type == type);
        var status = req.Query["status"].ToString();
        if (isAdmin && status is "draft" or "published") q = q.Where(course => course.Status == status);
        var seriesId = req.Query["seriesId"].ToString();
        if (seriesId.Length > 0) q = q.Where(course => course.SeriesId == seriesId);
        if (req.Query["active"] == "true") q = q.Where(course => course.Status == "published" && course.IsActive);
        var (page, size) = Validation.Page(req);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(course => course.CreatedAt).ThenBy(course => course.Name)
            .Skip((page - 1) * size).Take(size)
            .Select(course => new
            {
                course.Id, course.Name, course.Description, course.Type, course.Status, course.Remark, course.CreatedAt,
                course.PublishedAt, course.IsActive, course.SeriesId, course.Version
            })
            .ToListAsync(ct);
        var ids = rows.Select(row => row.Id).ToArray();
        var chapterCounts = await db.CourseChapters.AsNoTracking().Where(chapter => ids.Contains(chapter.CourseId))
            .GroupBy(chapter => chapter.CourseId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var sectionCounts = await (from section in db.CourseSections.AsNoTracking()
                                   join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                                   where ids.Contains(chapter.CourseId)
                                   group section by chapter.CourseId into grouped
                                   select new { CourseId = grouped.Key, Count = grouped.Count() })
            .ToDictionaryAsync(item => item.CourseId, item => item.Count, ct);
        var resourceStats = await LoadCourseResourceStats(db, ids, ct);
        return Results.Ok(new
        {
            total,
            items = rows.Select(row =>
            {
                var stats = SummarizeCourseResources(resourceStats[row.Id]);
                return new
                {
                    row.Id, row.Name, row.Description, row.Type, row.Status, row.Remark, row.CreatedAt, row.PublishedAt, row.IsActive, row.SeriesId,
                    row.Version,
                    chapterCount = chapterCounts.GetValueOrDefault(row.Id),
                    sectionCount = sectionCounts.GetValueOrDefault(row.Id),
                    stats.ResourceCount, stats.TheoryCount, stats.TheoryPracticeCount, stats.CaseCount,
                    stats.CasePracticeCount, stats.TotalMinutes
                };
            })
        });
    }

    static async Task<IResult> ListCourseSeries(bool? manage, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var includeDrafts = manage == true && ctx.User.IsInRole("Admin");
        var rows = await db.CoreCourseSeries.AsNoTracking().OrderBy(item => item.Name).Select(item => new
        {
            item.Id, item.Name, item.Description, courseCount = db.Courses.Count(course => course.SeriesId == item.Id
                && (includeDrafts || course.Status == "published"))
        }).Where(item => includeDrafts || item.courseCount > 0).ToListAsync(ct);
        return Results.Ok(rows);
    }

    sealed record SeriesInput(string? Name, string? Description);
    sealed record ActivationInput(bool Active);

    static async Task<IResult> SaveCourseSeries(string? id, SeriesInput input, SchoolDbContext db, CancellationToken ct)
    {
        var name = input.Name?.Trim() ?? "";
        var description = input.Description?.Trim() ?? "";
        if (name.Length is < 1 or > 100 || description.Length > 2000)
            return Bad("系列名称需为 1–100 字，介绍最多 2000 字");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.CoreCourseSeries.AnyAsync(item => item.Name == name && item.Id != id, ct)) return Conflict("系列名称已存在");
        var series = id == null ? new CoreCourseSeries() : await db.CoreCourseSeries.FindAsync([id], ct);
        if (series == null) return NotFound("课程系列不存在");
        series.Name = name;
        series.Description = description;
        if (id == null) db.CoreCourseSeries.Add(series);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { series.Id });
    }

    static async Task<IResult> DeleteCourseSeries(string id, SchoolDbContext db, CancellationToken ct)
    {
        var series = await db.CoreCourseSeries.FindAsync([id], ct);
        if (series == null) return NotFound("课程系列不存在");
        db.CoreCourseSeries.Remove(series);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> GetAvailability(HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx);
        var active = await db.Courses.AnyAsync(item => item.Status == "published" && item.IsActive, ct);
        var hasHistory = !active && await db.Classrooms.AnyAsync(item => ctx.User.IsInRole("Admin")
            || (ctx.User.IsInRole("Teacher") && item.TeacherUserId == userId)
            || (ctx.User.IsInRole("Student") && db.ClassroomStudents.Any(student => student.ClassroomId == item.Id && student.StudentUserId == userId)), ct);
        return Results.Ok(new { classrooms = active || hasHistory });
    }

    static async Task<IResult> SetCourseActivation(string id, ActivationInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var course = await db.Courses.FindAsync([id], ct);
        if (course == null) return NotFound("课程不存在");
        if (input.Active && course.Status != "published") return Conflict("请先发布课程");
        if (!input.Active && await db.Classrooms.AnyAsync(item => item.CourseId == id && item.Status != "closed", ct))
            return Conflict("课程仍有未结课的课堂");
        course.IsActive = input.Active;
        Audit.Add(db, ctx, input.Active ? "激活课程" : "停用课程", course.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { course.IsActive });
    }

    static async Task<bool> ValidSeries(CourseInput input, SchoolDbContext db, CancellationToken ct)
        => string.IsNullOrEmpty(input.SeriesId) || (input.Type == "core"
            && await db.CoreCourseSeries.AnyAsync(item => item.Id == input.SeriesId, ct));

    sealed record CourseResourceStat(string CourseId, string Kind, int DurationMinutes);
    sealed record CourseResourceCounts(int ResourceCount, int TheoryCount, int TheoryPracticeCount, int CaseCount,
        int CasePracticeCount, int TotalMinutes);

    static async Task<ILookup<string, CourseResourceStat>> LoadCourseResourceStats(SchoolDbContext db, string[] ids, CancellationToken ct)
        => (await (from resource in db.CourseResources.AsNoTracking()
                   join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                   join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                   where ids.Contains(chapter.CourseId)
                   select new CourseResourceStat(chapter.CourseId, resource.Kind, resource.DurationMinutes)).ToListAsync(ct))
            .ToLookup(item => item.CourseId);

    static CourseResourceCounts SummarizeCourseResources(IEnumerable<CourseResourceStat> resources)
    {
        var own = resources.ToArray();
        return new CourseResourceCounts(own.Length, own.Count(item => item.Kind == "theory"),
            own.Count(item => item.Kind == "theoryPractice"), own.Count(item => item.Kind == "case"),
            own.Count(item => item.Kind == "casePractice"), own.Sum(item => item.DurationMinutes));
    }

    static async Task<IResult> GetCourse(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (course == null) return NotFound("课程不存在");
        if (!ctx.User.IsInRole("Admin") && course.Status != "published") return NotFound("课程不存在或尚未发布");
        var chapters = await db.CourseChapters.AsNoTracking().Where(item => item.CourseId == id).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var chapterIds = chapters.Select(item => item.Id).ToArray();
        var sections = await db.CourseSections.AsNoTracking().Where(item => chapterIds.Contains(item.ChapterId)).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var sectionIds = sections.Select(item => item.Id).ToArray();
        var resources = await db.CourseResources.AsNoTracking().Where(item => sectionIds.Contains(item.SectionId)).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var resourceIds = resources.Select(item => item.Id).ToArray();
        var questionCounts = await db.CourseQuestions.AsNoTracking().Where(item => resourceIds.Contains(item.ResourceId))
            .GroupBy(item => item.ResourceId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var fileCounts = await db.CourseResourceFiles.AsNoTracking().Where(item => resourceIds.Contains(item.ResourceId))
            .GroupBy(item => item.ResourceId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var tree = chapters.Select(chapter => new
        {
            chapter.Id, chapter.Name, chapter.SortOrder, chapter.Version,
            sections = sections.Where(section => section.ChapterId == chapter.Id).Select(section => new
            {
                section.Id, section.Name, section.DurationMinutes, section.SortOrder, section.Version,
                resources = resources.Where(resource => resource.SectionId == section.Id).Select(resource => new
                {
                    resource.Id, resource.Name, resource.Kind, resource.DurationMinutes, resource.SortOrder, resource.Version,
                    resource.StartupNodeId,
                    questionCount = questionCounts.GetValueOrDefault(resource.Id),
                    fileCount = fileCounts.GetValueOrDefault(resource.Id)
                })
            })
        });
        return Results.Ok(new
        {
            course.Id, course.Name, course.Description, course.Type, course.Status, course.Remark, course.CreatedAt, course.PublishedAt,
            course.Version,
            chapterCount = chapters.Count, sectionCount = sections.Count, resourceCount = resources.Count,
            theoryCount = resources.Count(item => item.Kind == "theory"),
            theoryPracticeCount = resources.Count(item => item.Kind == "theoryPractice"),
            caseCount = resources.Count(item => item.Kind == "case"),
            casePracticeCount = resources.Count(item => item.Kind == "casePractice"),
            totalMinutes = resources.Sum(item => item.DurationMinutes),
            chapters = tree
        });
    }

    static async Task<IResult> CreateCourse(CourseInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var error = TeachingLogic.ValidateCourse(input);
        if (error != null) return Bad(error);
        if (!await ValidSeries(input, db, ct)) return Bad("请选择有效的专业核心课系列");
        var name = input.Name.Trim();
        if (await db.Courses.AnyAsync(course => course.Name == name, ct)) return Conflict("课程名称已存在");
        var entity = new TrainingCourse
        {
            Name = name, Type = input.Type, Description = (input.Description ?? "").Trim(), Remark = (input.Remark ?? "").Trim(),
            SeriesId = string.IsNullOrEmpty(input.SeriesId) ? null : input.SeriesId
        };
        db.Courses.Add(entity);
        Audit.Add(db, ctx, "新增课程", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id });
    }

    static async Task<IResult> UpdateCourse(string id, CourseInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.Courses.FindAsync([id], ct);
        if (entity == null) return NotFound("课程不存在");
        if (input.Version is not int version) return Concurrency.MissingVersion();
        if (!Concurrency.Matches(version, entity.Version)) return Concurrency.Conflict("课程");
        var error = TeachingLogic.ValidateCourse(input);
        if (error != null) return Bad(error);
        if (!await ValidSeries(input, db, ct)) return Bad("请选择有效的专业核心课系列");
        var name = input.Name.Trim();
        if (await db.Courses.AnyAsync(course => course.Id != id && course.Name == name, ct)) return Conflict("课程名称已存在");
        entity.Name = name;
        entity.Type = input.Type;
        entity.SeriesId = string.IsNullOrEmpty(input.SeriesId) ? null : input.SeriesId;
        entity.Description = (input.Description ?? "").Trim();
        entity.Remark = (input.Remark ?? "").Trim();
        entity.Version++;
        Audit.Add(db, ctx, "编辑课程", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id });
    }

    static async Task<IResult> DeleteCourse(string id, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var entity = await db.Courses.FindAsync([id], ct);
        if (entity == null) return NotFound("课程不存在");
        if (await db.Classrooms.AnyAsync(item => item.CourseId == id, ct)) return Conflict("课程已被课堂使用，请先删除或调整课堂");
        var resourceIds = await (from resource in db.CourseResources.AsNoTracking()
                                 join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                                 join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                                 where chapter.CourseId == id
                                 select resource.Id).ToListAsync(ct);
        var stored = await CourseFileStore.ListAsync(db, resourceIds, ct);
        db.Courses.Remove(entity);
        Audit.Add(db, ctx, "删除课程", entity.Name);
        await db.SaveChangesAsync(ct);
        CourseFileStore.Delete(paths, stored);
        return Results.Ok(new { message = "已删除" });
    }

    static async Task<IResult> PublishCourse(string id, bool? activate, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var course = await db.Courses.FindAsync([id], ct);
        if (course == null) return NotFound("课程不存在");
        var resources = await (from resource in db.CourseResources.AsNoTracking()
                               join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                               join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                               where chapter.CourseId == id
                               select new { resource.Id, resource.Name, resource.Kind }).ToListAsync(ct);
        if (resources.Count == 0) return Conflict("课程至少需要 1 个资源后才能发布");
        var practices = resources.Where(item => item.Kind == "theoryPractice").Select(item => item.Id).ToArray();
        var withQuestions = await db.CourseQuestions.AsNoTracking().Where(item => practices.Contains(item.ResourceId))
            .Select(item => item.ResourceId).Distinct().ToListAsync(ct);
        var missing = resources.Where(item => item.Kind == "theoryPractice" && !withQuestions.Contains(item.Id)).ToArray();
        if (missing.Length > 0) return Conflict("以下理论练习还没有题目：" + string.Join("、", missing.Select(item => item.Name)));
        if (course.Status != "published") course.IsActive = activate ?? true;
        course.Status = "published";
        course.PublishedAt ??= DateTime.UtcNow;
        Audit.Add(db, ctx, "发布课程", course.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { course.Id, course.Status, course.PublishedAt, course.IsActive });
    }

    static async Task<IResult> UnpublishCourse(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var course = await db.Courses.FindAsync([id], ct);
        if (course == null) return NotFound("课程不存在");
        if (await db.Classrooms.AnyAsync(item => item.CourseId == id && item.Status != "closed", ct))
            return Conflict("课程仍有未结课的课堂，暂不能下线");
        course.Status = "draft";
        course.IsActive = false;
        Audit.Add(db, ctx, "下线课程", course.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { course.Id, course.Status });
    }

    static async Task<IResult> CreateChapter(string id, ChapterInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        if (!await db.Courses.AnyAsync(item => item.Id == id, ct)) return NotFound("课程不存在");
        var error = TeachingLogic.ValidateChapterName(input.Name);
        if (error != null) return Bad(error);
        var next = await db.CourseChapters.Where(item => item.CourseId == id).Select(item => (int?)item.SortOrder).MaxAsync(ct) ?? -1;
        var entity = new CourseChapter { CourseId = id, Name = input.Name.Trim(), SortOrder = next + 1 };
        db.CourseChapters.Add(entity);
        Audit.Add(db, ctx, "新增大章", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id, entity.SortOrder });
    }

    static async Task<IResult> UpdateChapter(string id, ChapterInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.CourseChapters.FindAsync([id], ct);
        if (entity == null) return NotFound("大章不存在");
        if (input.Version is not int version) return Concurrency.MissingVersion();
        if (!Concurrency.Matches(version, entity.Version)) return Concurrency.Conflict("大章");
        var error = TeachingLogic.ValidateChapterName(input.Name);
        if (error != null) return Bad(error);
        entity.Name = input.Name.Trim();
        entity.Version++;
        Audit.Add(db, ctx, "编辑大章", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id });
    }

    static async Task<IResult> DeleteChapter(string id, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var entity = await db.CourseChapters.FindAsync([id], ct);
        if (entity == null) return NotFound("大章不存在");
        var resourceIds = await (from resource in db.CourseResources.AsNoTracking()
                                 join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                                 where section.ChapterId == id
                                 select resource.Id).ToListAsync(ct);
        if (await HasLearningHistory(db, resourceIds, ct)) return Conflict("该大章下的资源已有学习记录或实操会话，不能删除。");
        var stored = await CourseFileStore.ListAsync(db, resourceIds, ct);
        db.CourseChapters.Remove(entity);
        Audit.Add(db, ctx, "删除大章", entity.Name);
        await db.SaveChangesAsync(ct);
        CourseFileStore.Delete(paths, stored);
        return Results.Ok(new { message = "已删除" });
    }

    static async Task<IResult> CreateSection(string id, SectionInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        if (!await db.CourseChapters.AnyAsync(item => item.Id == id, ct)) return NotFound("大章不存在");
        var error = TeachingLogic.ValidateSectionName(input.Name) ?? TeachingLogic.ValidateSectionDuration(input.DurationMinutes);
        if (error != null) return Bad(error);
        var next = await db.CourseSections.Where(item => item.ChapterId == id).Select(item => (int?)item.SortOrder).MaxAsync(ct) ?? -1;
        var entity = new CourseSection { ChapterId = id, Name = input.Name.Trim(), DurationMinutes = input.DurationMinutes, SortOrder = next + 1 };
        db.CourseSections.Add(entity);
        Audit.Add(db, ctx, "新增小节", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id, entity.SortOrder });
    }

    static async Task<IResult> UpdateSection(string id, SectionInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.CourseSections.FindAsync([id], ct);
        if (entity == null) return NotFound("小节不存在");
        if (input.Version is not int version) return Concurrency.MissingVersion();
        if (!Concurrency.Matches(version, entity.Version)) return Concurrency.Conflict("小节");
        var error = TeachingLogic.ValidateSectionName(input.Name) ?? TeachingLogic.ValidateSectionDuration(input.DurationMinutes);
        if (error != null) return Bad(error);
        entity.Name = input.Name.Trim();
        entity.DurationMinutes = input.DurationMinutes;
        entity.Version++;
        Audit.Add(db, ctx, "编辑小节", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id });
    }

    static async Task<IResult> DeleteSection(string id, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var entity = await db.CourseSections.FindAsync([id], ct);
        if (entity == null) return NotFound("小节不存在");
        var resourceIds = await db.CourseResources.AsNoTracking().Where(item => item.SectionId == id).Select(item => item.Id).ToListAsync(ct);
        if (await HasLearningHistory(db, resourceIds, ct)) return Conflict("该小节下的资源已有学习记录或实操会话，不能删除。");
        var stored = await CourseFileStore.ListAsync(db, resourceIds, ct);
        db.CourseSections.Remove(entity);
        Audit.Add(db, ctx, "删除小节", entity.Name);
        await db.SaveChangesAsync(ct);
        CourseFileStore.Delete(paths, stored);
        return Results.Ok(new { message = "已删除" });
    }

    static async Task<IResult> CreateResource(string id, CourseResourceInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        if (!await db.CourseSections.AnyAsync(item => item.Id == id, ct)) return NotFound("小节不存在");
        var error = TeachingLogic.ValidateResource(input);
        if (error != null) return Bad(error);
        if (!await StartupNodeExists(db, input.StartupNodeId, ct)) return Bad("启动节点不存在");
        if (await ClassroomCaseScoring.ValidateRubric(db, input, ct) is { } rubricError) return Bad(rubricError);
        if (await CaseTopology.ValidateResource(db, input, ct) is { } topologyError) return Bad(topologyError);
        var next = await db.CourseResources.Where(item => item.SectionId == id).Select(item => (int?)item.SortOrder).MaxAsync(ct) ?? -1;
        var entity = BuildResource(id, input, next + 1);
        db.CourseResources.Add(entity);
        AddQuestions(db, entity.Id, input.Questions);
        Audit.Add(db, ctx, "新增课程资源", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id, entity.SortOrder });
    }

    static async Task<IResult> GetResourceDetail(string id, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.CourseResources.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity == null) return NotFound("资源不存在");
        var questions = await db.CourseQuestions.AsNoTracking().Where(item => item.ResourceId == id).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var files = await db.CourseResourceFiles.AsNoTracking().Where(item => item.ResourceId == id)
            .OrderByDescending(item => item.IsPinned).ThenBy(item => item.SortOrder).ToListAsync(ct);
        return Results.Ok(new
        {
            entity.Id, entity.SectionId, entity.Name, entity.Kind, entity.DurationMinutes, entity.Content, entity.Topology,
            entity.StartupNodeId, entity.SortOrder, entity.Version,
            topologyBlueprint = CaseTopology.Read(entity.TopologyBlueprintJson),
            caseRubric = ClassroomCaseScoring.Rubric(entity.CaseRubricJson),
            questions = questions.Select(question => new
            {
                question.Id, question.Type, question.Stem, options = TeachingLogic.DeserializeOptions(question.OptionsJson),
                answers = TeachingLogic.DeserializeAnswers(question.AnswerJson), question.Analysis, question.Score, question.SortOrder
            }),
            files = files.Select(CourseFileEndpoints.View)
        });
    }

    static async Task<IResult> UpdateResource(string id, CourseResourceInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.CourseResources.FindAsync([id], ct);
        if (entity == null) return NotFound("资源不存在");
        if (input.Version is not int version) return Concurrency.MissingVersion();
        if (!Concurrency.Matches(version, entity.Version)) return Concurrency.Conflict("资源");
        var error = TeachingLogic.ValidateResource(input);
        if (error != null) return Bad(error);
        if (!await StartupNodeExists(db, input.StartupNodeId, ct)) return Bad("启动节点不存在");
        if (await ClassroomCaseScoring.ValidateRubric(db, input, ct) is { } rubricError) return Bad(rubricError);
        if (await CaseTopology.ValidateResource(db, input, ct) is { } topologyError) return Bad(topologyError);
        if (entity.Kind != input.Kind && await db.LearningRecords.AnyAsync(item => item.ResourceId == id, ct))
            return Conflict("已有学习记录的资源不能更改类型。");
        entity.Name = input.Name.Trim();
        entity.Kind = input.Kind;
        entity.DurationMinutes = input.DurationMinutes;
        entity.Content = (input.Content ?? "").Trim();
        entity.Topology = (input.Topology ?? "").Trim();
        entity.TopologyBlueprintJson = ClassroomCaseScoring.Write(input.TopologyBlueprint);
        entity.CaseRubricJson = ClassroomCaseScoring.Write(input.CaseRubric ?? []);
        entity.StartupNodeId = string.IsNullOrWhiteSpace(input.StartupNodeId) ? null : input.StartupNodeId;
        var existing = await db.CourseQuestions.Where(item => item.ResourceId == id).ToListAsync(ct);
        db.CourseQuestions.RemoveRange(existing);
        AddQuestions(db, id, input.Questions);
        entity.Version++;
        Audit.Add(db, ctx, "编辑课程资源", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entity.Id });
    }

    static async Task<IResult> DeleteResource(string id, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var entity = await db.CourseResources.FindAsync([id], ct);
        if (entity == null) return NotFound("资源不存在");
        if (await HasLearningHistory(db, [id], ct)) return Conflict("该资源已有学习记录或实操会话，不能删除。");
        var stored = await CourseFileStore.ListAsync(db, [id], ct);
        db.CourseResources.Remove(entity);
        Audit.Add(db, ctx, "删除课程资源", entity.Name);
        await db.SaveChangesAsync(ct);
        CourseFileStore.Delete(paths, stored);
        return Results.Ok(new { message = "已删除" });
    }

    static async Task<IResult> GetTeachingClasses(SchoolDbContext db, CancellationToken ct)
    {
        var professions = await db.Professions.AsNoTracking().OrderBy(item => item.Code)
            .Select(item => new { item.Id, item.Name, item.Code }).ToListAsync(ct);
        var classes = await (from item in db.Classes.AsNoTracking()
                             join profession in db.Professions.AsNoTracking() on item.ProfessionId equals profession.Id
                             select new
                             {
                                 item.Id, item.Name, item.ProfessionId, professionName = profession.Name,
                                 studentCount = db.People.Count(person => person.Kind == "student" && person.ClassId == item.Id)
                             }).OrderBy(item => item.Name).ToListAsync(ct);
        return Results.Ok(new { professions, classes });
    }

    static async Task<IResult> ListSelectableStudents(HttpRequest request, SchoolDbContext db, CancellationToken ct)
    {
        var search = request.Query["search"].ToString().Trim();
        var classId = request.Query["classId"].ToString();
        var professionId = request.Query["professionId"].ToString();
        var rows = from person in db.People.AsNoTracking()
                   join user in db.Users.AsNoTracking() on person.UserId equals user.Id
                   join schoolClass in db.Classes.AsNoTracking() on person.ClassId equals schoolClass.Id into classes
                   from schoolClass in classes.DefaultIfEmpty()
                   where person.Kind == "student" && user.Role == "Student"
                       && (classId == "" || person.ClassId == classId)
                       && (professionId == "" || schoolClass.ProfessionId == professionId)
                       && (search == "" || person.Name.Contains(search) || person.Number.Contains(search) || user.UserName!.Contains(search))
                   orderby person.Number, person.UserId
                   select new { person.UserId, person.Name, person.Number, className = schoolClass == null ? "" : schoolClass.Name };
        var (page, size) = Validation.Page(request);
        return Results.Ok(new { items = await rows.Skip((page - 1) * size).Take(size).ToListAsync(ct), total = await rows.CountAsync(ct) });
    }

    static async Task<IResult> GetTeachers(SchoolDbContext db, CancellationToken ct)
    {
        var teachers = await (from user in db.Users.AsNoTracking()
                              join person in db.People.AsNoTracking() on user.Id equals person.UserId into people
                              from person in people.DefaultIfEmpty()
                              where user.Role == "Teacher" || user.Role == "Admin"
                              select new
                              {
                                  user.Id, user.UserName, user.DisplayName, user.Role,
                                  number = person == null ? "" : person.Number
                              }).OrderBy(item => item.DisplayName).ToListAsync(ct);
        return Results.Ok(teachers);
    }

    static async Task<IResult> ListClassrooms(HttpRequest req, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx) ?? "";
        var q = from classroom in db.Classrooms.AsNoTracking()
                join course in db.Courses.AsNoTracking() on classroom.CourseId equals course.Id
                join teacher in db.Users.AsNoTracking() on classroom.TeacherUserId equals teacher.Id
                select new { Classroom = classroom, CourseName = course.Name, CourseType = course.Type, TeacherName = teacher.DisplayName };
        if (ctx.User.IsInRole("Teacher")) q = q.Where(item => item.Classroom.TeacherUserId == userId);
        if (ctx.User.IsInRole("Student"))
            q = q.Where(item => db.ClassroomStudents.Any(student => student.ClassroomId == item.Classroom.Id && student.StudentUserId == userId));
        var search = req.Query["search"].ToString().Trim();
        if (search.Length > 0)
            q = q.Where(item => item.Classroom.Name.Contains(search) || item.CourseName.Contains(search) || item.TeacherName.Contains(search));
        var status = req.Query["status"].ToString();
        var now = DateTime.UtcNow;
        q = status switch
        {
            "open" => q.Where(item => item.Classroom.Status == "open" && item.Classroom.OpenAt <= now && item.Classroom.CloseAt > now),
            "closed" => q.Where(item => item.Classroom.Status == "closed" || item.Classroom.CloseAt <= now),
            "scheduled" => q.Where(item => item.Classroom.Status != "closed" && item.Classroom.CloseAt > now &&
                (item.Classroom.Status == "scheduled" || item.Classroom.OpenAt > now)),
            _ => q
        };
        var (page, size) = Validation.Page(req);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(item => item.Classroom.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(item => item.Classroom.Id).ToArray();
        var studentCounts = await db.ClassroomStudents.AsNoTracking().Where(item => ids.Contains(item.ClassroomId))
            .GroupBy(item => item.ClassroomId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var resourceStats = await LoadCourseResourceStats(db, rows.Select(item => item.Classroom.CourseId).Distinct().ToArray(), ct);
        var countsByCourse = rows.Select(item => item.Classroom.CourseId).Distinct()
            .ToDictionary(courseId => courseId, courseId => SummarizeCourseResources(resourceStats[courseId]));
        return Results.Ok(new
        {
            total,
            items = rows.Select(item => new
            {
                item.Classroom.Id, item.Classroom.Name, item.Classroom.CourseId, item.CourseName, item.CourseType,
                item.Classroom.TeacherUserId, item.TeacherName, item.Classroom.OpenAt, item.Classroom.CloseAt,
                status = TeachingLogic.EffectiveStatus(item.Classroom, now), storedStatus = item.Classroom.Status,
                item.Classroom.CreatedAt,
                studentCount = studentCounts.GetValueOrDefault(item.Classroom.Id),
                resources = countsByCourse[item.Classroom.CourseId]
            })
        });
    }

    static async Task<IResult> CreateClassroom(ClassroomInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var error = ValidateClassroom(input);
        if (error != null) return Bad(error);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.CourseId, ct);
        if (course == null || course.Status != "published" || !course.IsActive) return Bad("请选择已发布并激活的课程");
        var (teacherError, teacherUserId) = await ResolveTeacher(input.TeacherUserId, ctx, db, ct);
        if (teacherError != null) return teacherError;
        var entity = new Classroom
        {
            Name = input.Name.Trim(), CourseId = input.CourseId, TeacherUserId = teacherUserId,
            OpenAt = input.OpenAt, CloseAt = input.CloseAt, Status = "scheduled"
        };
        db.Classrooms.Add(entity);
        var (studentCount, enrollError) = await AddStudentsAsync(db, entity.Id, input.ClassIds, input.ProfessionIds, input.StudentUserIds, ct);
        if (enrollError != null) return Bad(enrollError);
        Audit.Add(db, ctx, "新增课堂", entity.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { entity.Id, studentCount });
    }

    static async Task<IResult> UpdateClassroom(string id, ClassroomInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var entity = await db.Classrooms.FindAsync([id], ct);
        if (entity == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, entity)) return Forbidden("无权管理该课堂");
        var error = ValidateClassroom(input);
        if (error != null) return Bad(error);
        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.CourseId, ct);
        if (course == null || course.Status != "published" || (!course.IsActive && entity.CourseId != course.Id))
            return Bad("请选择已发布并激活的课程");
        if (entity.CourseId != input.CourseId && await db.LearningRecords.AnyAsync(item => item.ClassroomId == id, ct))
            return Conflict("课堂已有学习记录，不能更换课程");
        var teacherUserId = entity.TeacherUserId;
        if (ctx.User.IsInRole("Admin"))
        {
            var (teacherError, resolvedTeacherId) = await ResolveTeacher(input.TeacherUserId ?? entity.TeacherUserId, ctx, db, ct);
            if (teacherError != null) return teacherError;
            teacherUserId = resolvedTeacherId;
        }
        entity.Name = input.Name.Trim();
        entity.CourseId = input.CourseId;
        entity.TeacherUserId = teacherUserId;
        entity.OpenAt = input.OpenAt;
        entity.CloseAt = input.CloseAt;
        Audit.Add(db, ctx, "编辑课堂", entity.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { entity.Id });
    }

    static async Task<IResult> DeleteClassroom(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.Classrooms.FindAsync([id], ct);
        if (entity == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, entity)) return Forbidden("无权管理该课堂");
        db.Classrooms.Remove(entity);
        Audit.Add(db, ctx, "删除课堂", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { message = "已删除" });
    }

    static async Task<IResult> OpenClassroom(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var entity = await db.Classrooms.FindAsync([id], ct);
        if (entity == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, entity)) return Forbidden("无权管理该课堂");
        if (entity.CloseAt <= DateTime.UtcNow) return Conflict("课堂时间已结束，请先调整开放时间");
        if (!await db.Courses.AnyAsync(item => item.Id == entity.CourseId && item.Status == "published" && item.IsActive, ct))
            return Conflict("请先发布并激活课程");
        entity.Status = "open";
        Audit.Add(db, ctx, "开放课堂", entity.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { entity.Id, status = TeachingLogic.EffectiveStatus(entity, DateTime.UtcNow), storedStatus = entity.Status });
    }

    static async Task<IResult> CloseClassroom(string id, HttpContext ctx, SchoolDbContext db, ClassroomLiveShare shares, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var entity = await db.Classrooms.FindAsync([id], ct);
        if (entity == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, entity)) return Forbidden("无权管理该课堂");
        entity.Status = "closed";
        await ClassroomCaseLogic.SuspendClassroomAsync(db, id, shares, ct);
        Audit.Add(db, ctx, "结课", entity.Name);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { entity.Id, entity.Status });
    }

    static async Task<IResult> GetClassroomStudents(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权查看该课堂学生");
        return Results.Ok(new { items = await LoadClassroomStudents(db, id, ct) });
    }

    static async Task<IResult> AddClassroomStudents(string id, ClassroomEnrollmentInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.FindAsync([id], ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂学生");
        var (added, error) = await AddStudentsAsync(db, id, input.ClassIds, input.ProfessionIds, input.StudentUserIds, ct);
        if (error != null) return Bad(error);
        if (added == 0) return Conflict("所选专业或班级没有新增学生");
        Audit.Add(db, ctx, "添加课堂学生", classroom.Name, $"新增 {added} 人");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { added });
    }

    static async Task<IResult> RemoveClassroomStudents(string id, ClassroomRemovalInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.FindAsync([id], ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权管理该课堂学生");
        var userIds = (input.UserIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        if (userIds.Length is < 1 or > 1000) return Bad("每次选择1–1000名学生");
        var enrollments = await db.ClassroomStudents.Where(item => item.ClassroomId == id && userIds.Contains(item.StudentUserId)).ToListAsync(ct);
        if (enrollments.Count == 0) return NotFound("所选学生不在该课堂中");
        var records = await db.LearningRecords.Where(item => item.ClassroomId == id && userIds.Contains(item.UserId)).ToListAsync(ct);
        var groupIds = await db.ClassroomGroups.Where(item => item.ClassroomId == id).Select(item => item.Id).ToListAsync(ct);
        var memberships = await db.ClassroomGroupMembers
            .Where(item => groupIds.Contains(item.GroupId) && userIds.Contains(item.StudentUserId)).ToListAsync(ct);
        db.ClassroomGroupMembers.RemoveRange(memberships);
        db.LearningRecords.RemoveRange(records);
        db.ClassroomStudents.RemoveRange(enrollments);
        Audit.Add(db, ctx, "移除课堂学生", classroom.Name, $"移除 {enrollments.Count} 人");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { removed = enrollments.Count });
    }

    static async Task<IResult> GetClassroomProgress(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!CanManage(ctx, classroom)) return Forbidden("无权查看该课堂学情");
        var students = await LoadClassroomStudents(db, id, ct);
        var resources = await (from resource in db.CourseResources.AsNoTracking()
                               join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                               join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                               where chapter.CourseId == classroom.CourseId
                               orderby chapter.SortOrder, section.SortOrder, resource.SortOrder
                               select new { resource.Id, resource.Name, resource.Kind }).ToListAsync(ct);
        var records = await db.LearningRecords.AsNoTracking()
            .Where(item => item.ClassroomId == id)
            .Select(item => new { item.UserId, item.ResourceId, item.Status, item.AttemptCount, item.BestScore, item.CompletedAt, item.StartedAt })
            .ToListAsync(ct);
        var byUser = records.GroupBy(item => item.UserId).ToDictionary(group => group.Key, group => group.ToList());
        var completedRecords = records.Count(item => item.Status == "completed");
        var items = students.Select(student =>
        {
            var own = byUser.GetValueOrDefault(student.UserId) ?? [];
            var scores = own.Where(item => item.AttemptCount > 0).Select(item => item.BestScore).ToArray();
            var lastActivity = own.Select(item => (DateTime?)(item.CompletedAt ?? item.StartedAt)).Max();
            return new
            {
                student.UserId, student.PersonId, student.Name, student.UserName, student.Number, student.ClassId, student.ClassName,
                completed = own.Count(item => item.Status == "completed"),
                total = resources.Count,
                averageScore = scores.Length == 0 ? 0 : Math.Round(scores.Average(), 1),
                lastActivity
            };
        }).OrderBy(item => item.Number).ToArray();
        return Results.Ok(new
        {
            classroomId = classroom.Id, classroom.Name, totalStudents = students.Count, totalResources = resources.Count, completedRecords,
            students = items,
            resources = resources.Select(resource => new
            {
                resource.Id, resource.Name, resource.Kind,
                completed = records.Count(record => record.ResourceId == resource.Id && record.Status == "completed")
            })
        });
    }

    static async Task<IResult> GetClassroomContent(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        var isAdmin = ctx.User.IsInRole("Admin");
        var isTeacher = ctx.User.IsInRole("Teacher") && classroom.TeacherUserId == CurrentUserId(ctx);
        var isStudent = ctx.User.IsInRole("Student") &&
                        await db.ClassroomStudents.AnyAsync(item => item.ClassroomId == id && item.StudentUserId == CurrentUserId(ctx), ct);
        if (!isAdmin && !isTeacher && !isStudent) return Forbidden("无权访问该课堂");
        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(item => item.Id == classroom.CourseId, ct);
        if (course == null) return NotFound("课堂关联课程不存在");
        var teacher = await db.Users.AsNoTracking().Where(item => item.Id == classroom.TeacherUserId)
            .Select(item => new { item.DisplayName, item.UserName }).FirstOrDefaultAsync(ct);
        var chapters = await db.CourseChapters.AsNoTracking().Where(item => item.CourseId == course.Id).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var chapterIds = chapters.Select(item => item.Id).ToArray();
        var sections = await db.CourseSections.AsNoTracking().Where(item => chapterIds.Contains(item.ChapterId)).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var sectionIds = sections.Select(item => item.Id).ToArray();
        var resources = await db.CourseResources.AsNoTracking().Where(item => sectionIds.Contains(item.SectionId)).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var resourceIds = resources.Select(item => item.Id).ToArray();
        var questions = await db.CourseQuestions.AsNoTracking().Where(item => resourceIds.Contains(item.ResourceId)).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var files = (await db.CourseResourceFiles.AsNoTracking().Where(item => resourceIds.Contains(item.ResourceId))
            .OrderByDescending(item => item.IsPinned).ThenBy(item => item.SortOrder).ToListAsync(ct)).ToLookup(item => item.ResourceId);
        var records = isStudent
            ? await db.LearningRecords.AsNoTracking().Where(item => item.ClassroomId == id && item.UserId == CurrentUserId(ctx)).ToListAsync(ct)
            : new List<LearningRecord>();
        var completionCounts = await db.LearningRecords.AsNoTracking().Where(item => item.ClassroomId == id && item.Status == "completed")
            .GroupBy(item => item.ResourceId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var tree = chapters.Select(chapter => new
        {
            chapter.Id, chapter.Name, chapter.SortOrder,
            sections = sections.Where(section => section.ChapterId == chapter.Id).Select(section => new
            {
                section.Id, section.Name, section.DurationMinutes, section.SortOrder,
                resources = resources.Where(resource => resource.SectionId == section.Id).Select(resource => new
                {
                    resource.Id, resource.Name, resource.Kind, resource.DurationMinutes, resource.SortOrder,
                    resource.Content, resource.Topology, resource.StartupNodeId,
                    questionCount = questions.Count(question => question.ResourceId == resource.Id),
                    maxScore = questions.Where(question => question.ResourceId == resource.Id).Sum(question => question.Score),
                    completedCount = completionCounts.GetValueOrDefault(resource.Id),
                    files = files[resource.Id].Select(CourseFileEndpoints.View),
                    questions = questions.Where(question => question.ResourceId == resource.Id)
                        .Select(question => QuestionView(question, !isStudent)).ToArray()
                })
            })
        });
        var status = TeachingLogic.EffectiveStatus(classroom, DateTime.UtcNow);
        return Results.Ok(new
        {
            classroom = new
            {
                classroom.Id, classroom.Name, classroom.CourseId, courseName = course.Name, courseType = course.Type,
                classroom.TeacherUserId, teacherName = teacher?.DisplayName ?? "", classroom.OpenAt, classroom.CloseAt,
                status, classroom.CreatedAt
            },
            course = new { course.Id, course.Name, course.Type, course.Description },
            canLearn = isStudent && status == "open",
            isPreview = !isStudent,
            tree,
            records = records.Select(RecordView)
        });
    }

    static async Task<IResult> StartLearning(string id, string resourceId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var userId = CurrentUserId(ctx);
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!await db.ClassroomStudents.AnyAsync(item => item.ClassroomId == id && item.StudentUserId == userId, ct))
            return Forbidden("你不在该课堂中");
        if (TeachingLogic.EffectiveStatus(classroom, DateTime.UtcNow) != "open") return Conflict("课堂尚未开放或已结课");
        var resource = await FindCourseResource(db, classroom.CourseId, resourceId, ct);
        if (resource == null) return NotFound("资源不属于该课堂课程");
        var record = await GetOrCreateRecord(db, id, userId!, resourceId, ct);
        var now = DateTime.UtcNow;
        // Keep a completed case-practice record as completed until a new final grade is submitted,
        // so classroom learning stats do not lose the previous result during a retry.
        if (record.Status == "notStarted")
        {
            record.Status = "inProgress";
            record.StartedAt = now;
            record.CompletedAt = null;
        }
        if (resource.Kind == "theoryPractice") record.PracticeStartedAt ??= now;
        if (resource.Kind == "casePractice"
            && await ClassroomCaseLogic.StartAsync(db, resource, record, userId!, now, ct) is { } caseError)
            return Conflict(caseError);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(RecordView(record));
    }

    static async Task<IResult> CompleteLearning(string id, string resourceId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var userId = CurrentUserId(ctx);
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!await db.ClassroomStudents.AnyAsync(item => item.ClassroomId == id && item.StudentUserId == userId, ct))
            return Forbidden("你不在该课堂中");
        if (TeachingLogic.EffectiveStatus(classroom, DateTime.UtcNow) != "open") return Conflict("课堂尚未开放或已结课");
        var resource = await FindCourseResource(db, classroom.CourseId, resourceId, ct);
        if (resource == null) return NotFound("资源不属于该课堂课程");
        if (resource.Kind == "theoryPractice") return Conflict("理论练习请提交答案后自动完成");
        if (resource.Kind == "casePractice") return Conflict("案例实操请通过「提交终评并回收环境」完成。");
        var record = await GetOrCreateRecord(db, id, userId!, resourceId, ct);
        var now = DateTime.UtcNow;
        record.Status = "completed";
        record.CompletedAt ??= now;
        await db.SaveChangesAsync(ct);
        return Results.Ok(RecordView(record));
    }

    static async Task<IResult> SubmitPractice(string id, string resourceId, PracticeSubmissionInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var userId = CurrentUserId(ctx);
        var classroom = await db.Classrooms.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (classroom == null) return NotFound("课堂不存在");
        if (!await db.ClassroomStudents.AnyAsync(item => item.ClassroomId == id && item.StudentUserId == userId, ct))
            return Forbidden("你不在该课堂中");
        if (TeachingLogic.EffectiveStatus(classroom, DateTime.UtcNow) != "open") return Conflict("课堂尚未开放或已结课");
        var resource = await FindCourseResource(db, classroom.CourseId, resourceId, ct);
        if (resource == null) return NotFound("资源不属于该课堂课程");
        if (resource.Kind != "theoryPractice") return Bad("该资源不是理论练习");
        var questions = await db.CourseQuestions.AsNoTracking().Where(item => item.ResourceId == resourceId)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id).ToListAsync(ct);
        if (questions.Count == 0) return Conflict("该练习尚未配置题目");
        var answers = (input.Answers ?? []).Where(answer => !string.IsNullOrWhiteSpace(answer.QuestionId))
            .GroupBy(answer => answer.QuestionId)
            .ToDictionary(group => group.Key, group => TeachingLogic.NormalizeSelection(group.Last().Selected));
        var (score, maxScore) = TeachingLogic.ScoreAttempt(questions, answers);
        var now = DateTime.UtcNow;
        var record = await GetOrCreateRecord(db, id, userId!, resourceId, ct);
        record.AttemptCount++;
        record.BestScore = Math.Max(record.BestScore, score);
        record.Status = "completed";
        record.CompletedAt ??= now;
        var questionResults = questions.Select(question => PracticeResult(question, answers.GetValueOrDefault(question.Id) ?? [])).ToArray();
        var attempt = new PracticeAttempt
        {
            LearningRecordId = record.Id,
            AnswersJson = ClassroomCaseScoring.Write(questionResults),
            Score = score, MaxScore = maxScore, StartedAt = record.PracticeStartedAt ?? now, SubmittedAt = now
        };
        record.PracticeStartedAt = null;
        db.PracticeAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        var averageScore = await db.PracticeAttempts.Where(item => item.LearningRecordId == record.Id)
            .AverageAsync(item => (double)item.Score, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new
        {
            score, maxScore, passed = maxScore > 0 && (long)score * 100 >= (long)maxScore * 60,
            attempt.StartedAt, attempt.SubmittedAt, record.AttemptCount, record.BestScore,
            averageScore = Math.Round(averageScore, 1),
            questions = questionResults
        });
    }

    internal static PracticeQuestionResult PracticeResult(CourseQuestion question, int[] selected)
        => new(question.Id, question.Type, question.Stem, TeachingLogic.DeserializeOptions(question.OptionsJson), selected,
            TeachingLogic.DeserializeAnswers(question.AnswerJson), TeachingLogic.IsCorrect(question, selected), question.Score, question.Analysis);

    static object QuestionView(CourseQuestion question, bool includeAnswers)
        => includeAnswers
            ? new
            {
                question.Id, question.Type, question.Stem,
                options = TeachingLogic.DeserializeOptions(question.OptionsJson),
                answers = TeachingLogic.DeserializeAnswers(question.AnswerJson),
                question.Analysis, question.Score, question.SortOrder
            }
            : new
            {
                question.Id, question.Type, question.Stem,
                options = TeachingLogic.DeserializeOptions(question.OptionsJson),
                question.Score, question.SortOrder
            };

    static object RecordView(LearningRecord record)
        => new { record.Id, record.ResourceId, record.Status, record.BestScore, record.AttemptCount, record.StartedAt, record.CompletedAt };

    static string? CurrentUserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);

    static bool CanManage(HttpContext ctx, Classroom classroom)
        => ctx.User.IsInRole("Admin") || (ctx.User.IsInRole("Teacher") && classroom.TeacherUserId == CurrentUserId(ctx));

    static string? ValidateClassroom(ClassroomInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "课堂名称不能为空";
        if (input.Name.Trim().Length > 100) return "课堂名称不能超过 100 个字符";
        if (input.OpenAt.Year is < 2000 or > 2100 || input.CloseAt.Year is < 2000 or > 2100) return "课堂时间不合法";
        if (input.CloseAt <= input.OpenAt) return "结课时间必须晚于开课时间";
        return null;
    }

    static async Task<(IResult? Error, string TeacherUserId)> ResolveTeacher(string? requestedTeacherId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var teacherUserId = string.IsNullOrWhiteSpace(requestedTeacherId) ? CurrentUserId(ctx) ?? "" : requestedTeacherId;
        if (!ctx.User.IsInRole("Admin") && teacherUserId != CurrentUserId(ctx)) return (Forbidden("教师只能创建自己的课堂"), "");
        var teacher = await db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == teacherUserId, ct);
        if (teacher == null || teacher.Role is not ("Admin" or "Teacher")) return (Bad("请选择有效的授课教师"), "");
        return (null, teacherUserId);
    }

    static async Task<(int Count, string? Error)> AddStudentsAsync(
        SchoolDbContext db, string classroomId, string[]? classIds, string[]? professionIds, string[]? studentUserIds, CancellationToken ct)
    {
        var requestedClasses = (classIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        var requestedProfessions = (professionIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        var requestedStudents = (studentUserIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        if (requestedStudents.Length > 1000) return (0, "每次最多选择 1000 名学生");
        if (requestedClasses.Length == 0 && requestedProfessions.Length == 0 && requestedStudents.Length == 0) return (0, null);
        if (requestedClasses.Length > 0)
        {
            var found = await db.Classes.AsNoTracking().Where(item => requestedClasses.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct);
            if (found.Count != requestedClasses.Length) return (0, "部分班级不存在");
        }
        if (requestedProfessions.Length > 0)
        {
            var found = await db.Professions.AsNoTracking().Where(item => requestedProfessions.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct);
            if (found.Count != requestedProfessions.Length) return (0, "部分专业不存在");
        }
        var targetClassIds = await db.Classes.AsNoTracking()
            .Where(item => requestedClasses.Contains(item.Id) || requestedProfessions.Contains(item.ProfessionId))
            .Select(item => item.Id).ToArrayAsync(ct);
        var students = await (from person in db.People.AsNoTracking()
                              join user in db.Users.AsNoTracking() on person.UserId equals user.Id
                              where person.Kind == "student" && user.Role == "Student"
                                && (requestedStudents.Contains(person.UserId) || (person.ClassId != null && targetClassIds.Contains(person.ClassId)))
                              select new { person.Id, person.UserId, person.ClassId }).ToListAsync(ct);
        if (students.Count(student => requestedStudents.Contains(student.UserId)) != requestedStudents.Length)
            return (0, "部分学生不存在或不是学生账号");
        var existing = (await db.ClassroomStudents.Where(item => item.ClassroomId == classroomId)
            .Select(item => item.StudentUserId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var student in students)
        {
            if (!existing.Add(student.UserId)) continue;
            db.ClassroomStudents.Add(new ClassroomStudent
            {
                ClassroomId = classroomId, StudentUserId = student.UserId,
                StudentPersonId = student.Id, ClassId = student.ClassId
            });
            added++;
        }
        return (added, null);
    }

    static async Task<CourseResource?> FindCourseResource(SchoolDbContext db, string courseId, string resourceId, CancellationToken ct)
        => await (from resource in db.CourseResources.AsNoTracking()
                  join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                  join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                  where resource.Id == resourceId && chapter.CourseId == courseId
                  select resource).FirstOrDefaultAsync(ct);

    static async Task<LearningRecord> GetOrCreateRecord(SchoolDbContext db, string classroomId, string userId, string resourceId, CancellationToken ct)
    {
        var record = await db.LearningRecords.FirstOrDefaultAsync(item =>
            item.ClassroomId == classroomId && item.UserId == userId && item.ResourceId == resourceId, ct);
        if (record != null) return record;
        // A double click or a second tab can both miss the read above. The unique index turns
        // the loser's insert into a no-op, so both requests continue with the winning row
        // instead of one failing with a duplicate-data conflict.
        var id = Guid.NewGuid().ToString("N");
        var startedAt = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "LearningRecords" ("Id", "ClassroomId", "UserId", "ResourceId", "Status", "BestScore", "AttemptCount", "StartedAt")
            VALUES ({id}, {classroomId}, {userId}, {resourceId}, 'notStarted', 0, 0, {startedAt})
            ON CONFLICT ("ClassroomId", "UserId", "ResourceId") DO NOTHING
            """, ct);
        return await db.LearningRecords.FirstOrDefaultAsync(item =>
            item.ClassroomId == classroomId && item.UserId == userId && item.ResourceId == resourceId, ct)
            ?? throw new InvalidOperationException("学习记录创建后未能读取");
    }

    static async Task<List<ClassroomStudentView>> LoadClassroomStudents(SchoolDbContext db, string classroomId, CancellationToken ct)
        => await (from enrollment in db.ClassroomStudents.AsNoTracking()
                  join person in db.People.AsNoTracking() on enrollment.StudentPersonId equals person.Id
                  join user in db.Users.AsNoTracking() on enrollment.StudentUserId equals user.Id
                  join schoolClass in db.Classes.AsNoTracking() on person.ClassId equals schoolClass.Id into classes
                  from schoolClass in classes.DefaultIfEmpty()
                  join profession in db.Professions.AsNoTracking() on schoolClass.ProfessionId equals profession.Id into professions
                  from profession in professions.DefaultIfEmpty()
                  where enrollment.ClassroomId == classroomId
                  select new ClassroomStudentView
                  {
                      UserId = enrollment.StudentUserId, PersonId = enrollment.StudentPersonId,
                      Name = person.Name, UserName = user.UserName, Number = person.Number,
                      ClassId = person.ClassId, ClassName = schoolClass == null ? "" : schoolClass.Name,
                      ProfessionName = profession == null ? "" : profession.Name
                  }).OrderBy(item => item.Number).ToListAsync(ct);

    static async Task<bool> StartupNodeExists(SchoolDbContext db, string? nodeId, CancellationToken ct)
        => string.IsNullOrWhiteSpace(nodeId) || await db.Nodes.AnyAsync(item => item.Id == nodeId, ct);

    static async Task<bool> HasLearningHistory(SchoolDbContext db, List<string> resourceIds, CancellationToken ct)
        => resourceIds.Count > 0
            && (await db.LearningRecords.AnyAsync(item => resourceIds.Contains(item.ResourceId), ct)
                || await db.ClassroomCaseSessions.AnyAsync(item => resourceIds.Contains(item.ResourceId), ct));

    static CourseResource BuildResource(string sectionId, CourseResourceInput input, int sortOrder) => new()
    {
        SectionId = sectionId, Name = input.Name.Trim(), Kind = input.Kind,
        DurationMinutes = input.DurationMinutes, Content = (input.Content ?? "").Trim(),
        Topology = (input.Topology ?? "").Trim(),
        TopologyBlueprintJson = ClassroomCaseScoring.Write(input.TopologyBlueprint),
        CaseRubricJson = ClassroomCaseScoring.Write(input.CaseRubric ?? []),
        StartupNodeId = string.IsNullOrWhiteSpace(input.StartupNodeId) ? null : input.StartupNodeId,
        SortOrder = sortOrder
    };

    static void AddQuestions(SchoolDbContext db, string resourceId, CourseQuestionInput[]? inputs)
    {
        var order = 0;
        foreach (var question in inputs ?? [])
        {
            db.CourseQuestions.Add(new CourseQuestion
            {
                ResourceId = resourceId, Type = question.Type, Stem = question.Stem.Trim(),
                OptionsJson = JsonSerializer.Serialize(question.Options ?? []),
                AnswerJson = JsonSerializer.Serialize(TeachingLogic.NormalizeSelection(question.Answers)),
                Analysis = (question.Analysis ?? "").Trim(), Score = question.Score, SortOrder = order++
            });
        }
    }

    static IResult Bad(string message) => Results.BadRequest(new { message });
    static IResult Conflict(string message) => Results.Conflict(new { message });
    static IResult NotFound(string message = "记录不存在") => Results.NotFound(new { message });
    static IResult Forbidden(string message) => Results.Json(new { message }, statusCode: StatusCodes.Status403Forbidden);

    sealed class ClassroomStudentView
    {
        public string UserId { get; set; } = "";
        public string PersonId { get; set; } = "";
        public string Name { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Number { get; set; } = "";
        public string? ClassId { get; set; }
        public string ClassName { get; set; } = "";
        public string ProfessionName { get; set; } = "";
    }
}
