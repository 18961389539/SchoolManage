using System.Text;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class TeachingImportEndpoints
{
    public static void MapTeachingImportEndpoints(this RouteGroupBuilder all)
    {
        var admin = all.MapGroup("").RequireAuthorization("Admin");
        admin.MapGet("/courses/questions/template", Template);
        admin.MapPost("/courses/questions/parse", ParseQuestions);
        admin.MapGet("/sections/{id}/importable-resources", ListImportable);
        admin.MapPost("/sections/{id}/resources/import", ImportResources);
    }

    static IResult Template()
    {
        static string Csv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        var rows = new[]
        {
            ExamImport.TemplateHeaders,
            string.Join(',', new[] { "单选题", "下列属于网络互连设备的是？", "路由器|交换机|显示器|集线器", "A,B", "5", "路由器和交换机参与网络互连。" }.Select(Csv)),
            string.Join(',', new[] { "多选题", "以下属于局域网特点的有？", "覆盖范围小|传输速率高|跨城市覆盖|误码率低", "A,B,D", "10", "局域网覆盖范围通常较小。" }.Select(Csv)),
            string.Join(',', new[] { "判断题", "交换机工作在物理层。", "正确|错误", "B", "5", "交换机工作在数据链路层。" }.Select(Csv))
        };
        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", rows) + "\r\n")).ToArray(),
            "text/csv; charset=utf-8", "课程练习导入模板.csv");
    }

    static async Task<IResult> ParseQuestions(HttpContext ctx, CancellationToken ct)
    {
        var (form, sheet, fileError) = await ImportFiles.ReadSheetAsync(ctx, ct);
        if (fileError != null) return Bad(fileError);
        if (form == null || sheet == null) return Bad("请选择 Excel 或 CSV 文件");
        List<ExamImportRow> rows;
        try { rows = ExamImport.BuildRows(sheet); }
        catch (ImportSheetException exception) { return Bad(exception.Message); }
        var valid = new List<CourseQuestionInput>();
        var errors = new List<object>();
        foreach (var row in rows)
        {
            var messages = row.Errors.Select(error => (error.Column, error.Message)).ToList();
            if (row.Data.Type == "practical")
            {
                messages.Add(("题型", "课程练习只支持单选题、多选题和判断题"));
            }
            else if (messages.Count == 0)
            {
                var courseQuestion = new CourseQuestionInput(row.Data.Type, row.Data.Stem, row.Data.Options, row.Data.Answers, row.Data.Score, row.Data.Analysis);
                var validation = TeachingLogic.ValidateQuestion(courseQuestion);
                if (validation != null) messages.Add((ColumnOf(validation), validation));
            }
            if (messages.Count > 0)
            {
                foreach (var (column, message) in messages)
                    errors.Add(new { rowNumber = row.RowNumber, column, message, stem = Truncate(row.Data.Stem, 60) });
                continue;
            }
            valid.Add(new CourseQuestionInput(row.Data.Type, row.Data.Stem, row.Data.Options, row.Data.Answers, row.Data.Score, row.Data.Analysis));
        }
        var removeInvalidRows = bool.TryParse(form["removeInvalidRows"], out var remove) && remove;
        if (errors.Count > 0 && !removeInvalidRows)
        {
            return Results.BadRequest(new
            {
                message = $"发现 {errors.Count} 处问题，请修正文件后重试，或选择忽略问题行",
                errorCount = errors.Count, validCount = valid.Count, errors
            });
        }
        if (valid.Count == 0) return Bad("没有可导入的有效题目");
        if (valid.Count > 100) return Bad("单次最多导入 100 道题目");
        return Results.Ok(new
        {
            validCount = valid.Count, invalidCount = errors.Count, errors,
            questions = valid.Select(question => new
            {
                question.Type, question.Stem, question.Options, question.Answers, question.Score, question.Analysis
            })
        });
    }

    static async Task<IResult> ListImportable(string id, HttpRequest request, SchoolDbContext db, CancellationToken ct)
    {
        var target = await db.CourseSections.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (target == null) return NotFound("小节不存在");
        var search = request.Query["search"].ToString().Trim();
        var query = from resource in db.CourseResources.AsNoTracking()
                    join section in db.CourseSections.AsNoTracking() on resource.SectionId equals section.Id
                    join chapter in db.CourseChapters.AsNoTracking() on section.ChapterId equals chapter.Id
                    join course in db.Courses.AsNoTracking() on chapter.CourseId equals course.Id
                    where section.Id != id
                    select new
                    {
                        resource.Id, resource.Name, resource.Kind, resource.DurationMinutes, resource.SortOrder,
                        SectionId = section.Id, SectionName = section.Name, SectionSort = section.SortOrder,
                        ChapterName = chapter.Name, ChapterSort = chapter.SortOrder,
                        CourseId = course.Id, CourseName = course.Name
                    };
        if (search.Length > 0) query = query.Where(item => item.Name.Contains(search) || item.CourseName.Contains(search));
        var rows = await query.OrderBy(item => item.CourseName).ThenBy(item => item.ChapterSort).ThenBy(item => item.SectionSort)
            .ThenBy(item => item.SortOrder).Take(500).ToListAsync(ct);
        var ids = rows.Select(item => item.Id).ToArray();
        var questionCounts = await db.CourseQuestions.AsNoTracking().Where(item => ids.Contains(item.ResourceId))
            .GroupBy(item => item.ResourceId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var fileCounts = await db.CourseResourceFiles.AsNoTracking().Where(item => ids.Contains(item.ResourceId))
            .GroupBy(item => item.ResourceId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        return Results.Ok(new
        {
            target = new { target.Id, target.Name },
            items = rows.Select(item => new
            {
                item.Id, item.Name, item.Kind, item.DurationMinutes, item.SectionId, item.SectionName,
                item.ChapterName, item.CourseId, item.CourseName,
                questionCount = questionCounts.GetValueOrDefault(item.Id),
                fileCount = fileCounts.GetValueOrDefault(item.Id)
            })
        });
    }

    static async Task<IResult> ImportResources(string id, CourseResourceImportInput input, HttpContext ctx, SchoolDbContext db, AppPaths paths, CancellationToken ct)
    {
        var target = await db.CourseSections.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (target == null) return NotFound("小节不存在");
        var ids = (input.ResourceIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        if (ids.Length is < 1 or > 100) return Bad("每次选择 1–100 个资源");
        var sources = await db.CourseResources.AsNoTracking().Where(item => ids.Contains(item.Id)).ToListAsync(ct);
        if (sources.Count != ids.Length) return Bad("部分资源不存在，请刷新后重试");
        if (sources.Any(item => item.SectionId == id)) return Bad("不能从当前小节导入资源");
        var sourceQuestions = await db.CourseQuestions.AsNoTracking().Where(item => ids.Contains(item.ResourceId)).ToListAsync(ct);
        var sourceFiles = await db.CourseResourceFiles.AsNoTracking().Where(item => ids.Contains(item.ResourceId)).ToListAsync(ct);
        var nextSort = await db.CourseResources.Where(item => item.SectionId == id)
            .Select(item => (int?)item.SortOrder).MaxAsync(ct) ?? -1;
        var copied = new List<StoredCourseFile>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var resourceCount = 0;
            var questionCount = 0;
            var fileCount = 0;
            foreach (var source in sources.OrderBy(item => item.SortOrder))
            {
                var clone = new CourseResource
                {
                    SectionId = id, Name = source.Name, Kind = source.Kind, DurationMinutes = source.DurationMinutes,
                    Content = source.Content, Topology = source.Topology, StartupNodeId = source.StartupNodeId,
                    TopologyBlueprintJson = source.TopologyBlueprintJson,
                    CaseRubricJson = source.CaseRubricJson, SortOrder = ++nextSort
                };
                db.CourseResources.Add(clone);
                var order = 0;
                foreach (var question in sourceQuestions.Where(item => item.ResourceId == source.Id).OrderBy(item => item.SortOrder))
                {
                    db.CourseQuestions.Add(new CourseQuestion
                    {
                        ResourceId = clone.Id, Type = question.Type, Stem = question.Stem, OptionsJson = question.OptionsJson,
                        AnswerJson = question.AnswerJson, Analysis = question.Analysis, Score = question.Score, SortOrder = order++
                    });
                    questionCount++;
                }
                var fileOrder = sourceFiles.Where(item => item.ResourceId == source.Id).Select(item => item.SortOrder).DefaultIfEmpty(-1).Max() + 1;
                foreach (var file in sourceFiles.Where(item => item.ResourceId == source.Id).OrderBy(item => item.SortOrder))
                {
                    var sourcePath = CourseFileStore.PathFor(paths, source.Id, file.StoredName);
                    if (!File.Exists(sourcePath)) continue;
                    var extension = Path.GetExtension(file.StoredName);
                    var storedName = Guid.NewGuid().ToString("N") + extension;
                    var directory = CourseFileStore.DirectoryFor(paths, clone.Id);
                    Directory.CreateDirectory(directory);
                    var targetPath = CourseFileStore.PathFor(paths, clone.Id, storedName);
                    await using (var sourceStream = File.OpenRead(sourcePath))
                    await using (var targetStream = File.Create(targetPath))
                        await sourceStream.CopyToAsync(targetStream, ct);
                    copied.Add(new StoredCourseFile(clone.Id, storedName));
                    db.CourseResourceFiles.Add(new CourseResourceFile
                    {
                        ResourceId = clone.Id, Name = file.Name, StoredName = storedName, Size = file.Size,
                        IsPinned = file.IsPinned, SortOrder = fileOrder++
                    });
                    fileCount++;
                }
                resourceCount++;
            }
            Audit.Add(db, ctx, "导入课程资源", target.Name, $"从其他课程导入 {resourceCount} 个资源");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { resources = resourceCount, questions = questionCount, files = fileCount });
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            CourseFileStore.Delete(paths, copied);
            throw;
        }
    }

    static string ColumnOf(string message)
        => message.Contains("题型") ? "题型"
        : message.Contains("题干") ? "题干"
        : message.Contains("选项") ? "选项"
        : message.Contains("答案") || message.Contains("正确") ? "正确答案"
        : message.Contains("分值") ? "分值"
        : message.Contains("解析") ? "解析"
        : "题目";

    static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";

    static IResult Bad(string message) => Results.BadRequest(new { message });
    static IResult NotFound(string message = "记录不存在") => Results.NotFound(new { message });
}
