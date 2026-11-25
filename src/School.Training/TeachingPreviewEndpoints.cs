using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class TeachingPreviewEndpoints
{
    public static void MapTeachingPreviewEndpoints(this RouteGroupBuilder all)
    {
        var reader = all.MapGroup("");
        reader.MapGet("/courses/{id}/preview", Preview);
        reader.MapPost("/courses/{id}/resources/{resourceId}/preview-practice", PreviewPractice);
        reader.MapGet("/courses/{id}/resources/{resourceId}/environment", Environment);
    }

    static async Task<IResult> Preview(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (course == null) return NotFound("课程不存在");
        var isAdmin = ctx.User.IsInRole("Admin");
        var isStaff = isAdmin || ctx.User.IsInRole("Teacher");
        var isStudent = ctx.User.IsInRole("Student");
        if (!isStaff && course.Status != "published") return NotFound("课程不存在或尚未发布");
        var chapters = await db.CourseChapters.AsNoTracking().Where(item => item.CourseId == id).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var chapterIds = chapters.Select(item => item.Id).ToArray();
        var sections = await db.CourseSections.AsNoTracking().Where(item => chapterIds.Contains(item.ChapterId)).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var sectionIds = sections.Select(item => item.Id).ToArray();
        var resources = await db.CourseResources.AsNoTracking().Where(item => sectionIds.Contains(item.SectionId)).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var resourceIds = resources.Select(item => item.Id).ToArray();
        var questions = await db.CourseQuestions.AsNoTracking().Where(item => resourceIds.Contains(item.ResourceId)).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var files = await db.CourseResourceFiles.AsNoTracking().Where(item => resourceIds.Contains(item.ResourceId))
            .OrderByDescending(item => item.IsPinned).ThenBy(item => item.SortOrder).ToListAsync(ct);
        var nodeIds = resources.Where(item => !string.IsNullOrWhiteSpace(item.StartupNodeId)).Select(item => item.StartupNodeId!).Distinct().ToArray();
        var nodes = await db.Nodes.AsNoTracking().Where(item => nodeIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Name }).ToDictionaryAsync(item => item.Id, item => item.Name, ct);
        var tree = chapters.Select(chapter => new
        {
            chapter.Id, chapter.Name, chapter.SortOrder,
            sections = sections.Where(section => section.ChapterId == chapter.Id).Select(section => new
            {
                section.Id, section.Name, section.DurationMinutes, section.SortOrder,
                resources = resources.Where(resource => resource.SectionId == section.Id).Select(resource =>
                {
                    var own = questions.Where(question => question.ResourceId == resource.Id).ToArray();
                    return new
                    {
                        resource.Id, resource.Name, resource.Kind, resource.DurationMinutes, resource.SortOrder,
                        resource.Content, resource.Topology, resource.StartupNodeId,
                        startupNodeName = resource.StartupNodeId == null ? "" : nodes.GetValueOrDefault(resource.StartupNodeId, ""),
                        questionCount = own.Length,
                        maxScore = own.Sum(question => question.Score),
                        files = files.Where(file => file.ResourceId == resource.Id).Select(CourseFileEndpoints.View).ToArray(),
                        questions = own.Select(question => new
                        {
                            question.Id, question.Type, question.Stem,
                            options = TeachingLogic.DeserializeOptions(question.OptionsJson),
                            answers = isStudent ? [] : TeachingLogic.DeserializeAnswers(question.AnswerJson),
                            analysis = isStudent ? "" : question.Analysis,
                            question.Score, question.SortOrder
                        }).ToArray()
                    };
                })
            })
        });
        return Results.Ok(new
        {
            course = new { course.Id, course.Name, course.Description, course.Type, course.Status, course.PublishedAt },
            preview = true,
            showAnswers = !isStudent,
            tree
        });
    }

    static async Task<IResult> PreviewPractice(string id, string resourceId, PracticeSubmissionInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var row = await FindResource(db, id, resourceId, ct);
        if (row == null) return NotFound("资源不属于该课程");
        if (!ctx.User.IsInRole("Admin") && !ctx.User.IsInRole("Teacher") && row.CourseStatus != "published")
            return NotFound("课程不存在或尚未发布");
        if (row.Kind != "theoryPractice") return Bad("该资源不是理论练习");
        var questions = await db.CourseQuestions.AsNoTracking().Where(item => item.ResourceId == resourceId).ToListAsync(ct);
        if (questions.Count == 0) return Conflict("该练习尚未配置题目");
        var answers = (input.Answers ?? []).Where(answer => !string.IsNullOrWhiteSpace(answer.QuestionId))
            .GroupBy(answer => answer.QuestionId)
            .ToDictionary(group => group.Key, group => TeachingLogic.NormalizeSelection(group.Last().Selected));
        var (score, maxScore) = TeachingLogic.ScoreAttempt(questions, answers);
        var revealAnswers = !ctx.User.IsInRole("Student");
        return Results.Ok(new
        {
            score, maxScore, passed = maxScore > 0 && (long)score * 100 >= (long)maxScore * 60,
            questions = revealAnswers ? questions.Select(question => new
            {
                questionId = question.Id,
                correct = TeachingLogic.IsCorrect(question, answers.GetValueOrDefault(question.Id)),
                correctAnswers = TeachingLogic.DeserializeAnswers(question.AnswerJson),
                question.Analysis, question.Score
            }) : []
        });
    }

    static async Task<IResult> Environment(string id, string resourceId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var row = await FindResource(db, id, resourceId, ct);
        if (row == null) return NotFound("资源不属于该课程");
        var isAdmin = ctx.User.IsInRole("Admin");
        if (!isAdmin && !ctx.User.IsInRole("Teacher") && row.CourseStatus != "published")
            return NotFound("课程不存在或尚未发布");
        var blueprint = CaseTopology.Read(row.TopologyBlueprintJson);
        var templateDevices = blueprint?.DeviceIds ?? [];
        var node = string.IsNullOrWhiteSpace(row.StartupNodeId) ? null
            : await db.Nodes.AsNoTracking().Where(item => item.Id == row.StartupNodeId)
                .Select(item => new { item.Id, item.Name, item.Host, item.Enabled, item.Remark }).FirstOrDefaultAsync(ct);
        var deviceQuery = isAdmin
            ? db.Devices.AsNoTracking().Where(item => item.ConnectionType == ResourceProviders.Simulated
                && (row.StartupNodeId == null || item.NodeId == row.StartupNodeId))
            : TrainingResourceAccess.AccessibleDevices(db, CurrentUserId(ctx), ctx.User.FindFirstValue("stamp") ?? "")
                .AsNoTracking().Where(item => row.StartupNodeId != null && item.NodeId == row.StartupNodeId);
        if (blueprint != null) deviceQuery = deviceQuery.Where(device => templateDevices.Contains(device.Id));
        var devices = await (from device in deviceQuery
                             join cabinet in db.Cabinets.AsNoTracking() on device.CabinetId equals cabinet.Id
                             join owner in db.Nodes.AsNoTracking() on device.NodeId equals owner.Id into nodes
                             from owner in nodes.DefaultIfEmpty()
                             orderby device.Name
                             select new
                             {
                                 device.Id, device.Name, cabinetName = cabinet.Name,
                                 nodeName = owner == null ? "" : owner.Name, isSimulated = true, canAccess = true
                             }).ToListAsync(ct);
        var vmQuery = isAdmin
            ? db.VirtualMachines.AsNoTracking().Where(item => row.StartupNodeId == null || item.NodeId == row.StartupNodeId)
            : from vm in db.VirtualMachines.AsNoTracking()
              join assigned in TrainingResourceAccess.AssignedNodes(db, CurrentUserId(ctx)) on vm.NodeId equals assigned.Id
              where row.StartupNodeId != null && vm.NodeId == row.StartupNodeId
              select vm;
        var reserved = SimulatedVirtualMachines.ReservedIds(db, DateTime.UtcNow);
        var virtualMachines = await (from vm in vmQuery
                                     join owner in db.Nodes.AsNoTracking() on vm.NodeId equals owner.Id
                                     orderby vm.Name
                                     select new
                                     {
                                         vm.Id, vm.Name, vm.Status, vm.CpuCount, vm.MemoryMb, vm.DiskGb,
                                         canConnect = owner.Provider == ResourceProviders.Simulated && owner.Enabled
                                             && vm.Status == "running" && !reserved.Contains(vm.Id)
                                     }).ToListAsync(ct);
        return Results.Ok(new
        {
            resource = new
            {
                row.Id, row.Name, row.Kind, row.Topology, row.StartupNodeId,
                topologyBlueprint = blueprint
            },
            topologyDevices = await db.Devices.AsNoTracking().Where(device => templateDevices.Contains(device.Id))
                .Select(device => new { device.Id, device.Name }).ToListAsync(ct),
            node,
            devices,
            virtualMachines
        });
    }

    static async Task<PreviewResourceRow?> FindResource(SchoolDbContext db, string courseId, string resourceId, CancellationToken ct)
        => await (from resource in db.CourseResources.AsNoTracking()
                  join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                  join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                  join course in db.Courses.AsNoTracking() on chapter.CourseId equals course.Id
                  where resource.Id == resourceId && course.Id == courseId
                  select new PreviewResourceRow(resource.Id, resource.Name, resource.Kind, resource.Topology, resource.StartupNodeId,
                      course.Status, resource.TopologyBlueprintJson))
            .FirstOrDefaultAsync(ct);

    static string CurrentUserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    static IResult Bad(string message) => Results.BadRequest(new { message });
    static IResult Conflict(string message) => Results.Conflict(new { message });
    static IResult NotFound(string message = "记录不存在") => Results.NotFound(new { message });
}

public sealed record PreviewResourceRow(string Id, string Name, string Kind, string Topology, string? StartupNodeId, string CourseStatus,
    string TopologyBlueprintJson);
