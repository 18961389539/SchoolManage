using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class ExamQuestionEndpoints
{
    public static void MapExamQuestionEndpoints(this RouteGroupBuilder all)
    {
        var staff = all.MapGroup("").RequireAuthorization(policy => policy.RequireRole("Admin", "Teacher"));
        var admin = all.MapGroup("").RequireAuthorization("Admin");
        staff.MapExamQuestionBanks(admin);

        staff.MapGet("/exam-questions", ListQuestions);
        staff.MapGet("/exam-questions/template", Template);
        staff.MapGet("/exam-questions/{id}", GetQuestion);
        admin.MapPost("/exam-questions", CreateQuestion);
        admin.MapPut("/exam-questions/{id}", UpdateQuestion);
        admin.MapDelete("/exam-questions/{id}", DeleteQuestion);
        admin.MapPost("/exam-questions/batch-delete", BatchDeleteQuestions);
        admin.MapPost("/exam-questions/import", ImportQuestions);

        staff.MapGet("/exam-papers", ListPapers);
        staff.MapGet("/exam-papers/{id}", GetPaper);
        admin.MapPost("/exam-papers", CreatePaper);
        admin.MapPut("/exam-papers/{id}", UpdatePaper);
        admin.MapDelete("/exam-papers/{id}", DeletePaper);
        admin.MapPost("/exam-papers/batch-delete", BatchDeletePapers);
        admin.MapPost("/exam-papers/{id}/questions", AddPaperQuestions);
        admin.MapPut("/exam-papers/{id}/questions/{paperQuestionId}", UpdatePaperQuestionScore);
        admin.MapDelete("/exam-papers/{id}/questions/{paperQuestionId}", DeletePaperQuestion);
        admin.MapPost("/exam-papers/{id}/questions/remove", RemovePaperQuestions);
    }

    static async Task<IResult> ListQuestions(HttpRequest req, SchoolDbContext db, CancellationToken ct)
    {
        var query = QuestionsInScope(req, db).AsNoTracking();
        var search = req.Query["search"].ToString().Trim();
        if (search.Length > 0) query = query.Where(item => item.Stem.Contains(search) || item.Analysis.Contains(search));
        var type = req.Query["type"].ToString();
        if (ExamLogic.QuestionTypes.Contains(type)) query = query.Where(item => item.Type == type);
        var (page, size) = Validation.Page(req);
        var total = await query.CountAsync(ct);
        var rows = await (from item in query
                          join bank in db.ExamQuestionBanks on item.BankId equals bank.Id
                          orderby item.CreatedAt descending, item.Stem, item.Id
                          select new
                          {
                              item.Id, item.BankId, BankName = bank.Name, item.Type, item.Stem, item.OptionsJson, item.AnswerJson,
                              item.Analysis, item.Score, item.CreatedAt, item.UpdatedAt, item.PracticalNodeId
                          }).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(item => item.Id).ToArray();
        var usage = await db.ExamPaperQuestions.AsNoTracking().Where(item => ids.Contains(item.QuestionId))
            .GroupBy(item => item.QuestionId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        return Results.Ok(new
        {
            total, page, pageSize = size,
            items = rows.Select(item => new
            {
                item.Id, item.BankId, item.BankName, item.Type, item.Stem,
                options = ExamLogic.DeserializeOptions(item.OptionsJson),
                answers = ExamLogic.DeserializeAnswers(item.AnswerJson),
                item.Analysis, item.Score, item.CreatedAt, item.UpdatedAt,
                automaticPractical = item.PracticalNodeId != "",
                paperCount = usage.GetValueOrDefault(item.Id)
            })
        });
    }

    static async Task<IResult> GetQuestion(string id, HttpRequest request, SchoolDbContext db, CancellationToken ct)
    {
        var item = await QuestionsInScope(request, db).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item == null) return NotFound("题目不存在");
        var bankName = await db.ExamQuestionBanks.Where(bank => bank.Id == item.BankId).Select(bank => bank.Name).SingleAsync(ct);
        var papers = await (from link in db.ExamPaperQuestions.AsNoTracking()
                            join paper in db.ExamPapers.AsNoTracking() on link.PaperId equals paper.Id
                            where link.QuestionId == id
                            select new { paper.Id, paper.Name }).Distinct().ToListAsync(ct);
        return Results.Ok(new
        {
            item.Id, item.BankId, bankName, item.Type, item.Stem,
            options = ExamLogic.DeserializeOptions(item.OptionsJson),
            answers = ExamLogic.DeserializeAnswers(item.AnswerJson),
            item.Analysis, item.Score, item.CreatedAt, item.UpdatedAt, papers, item.PracticalNodeId, item.PracticalResources,
            automaticPractical = item.PracticalNodeId != "",
            practicalRubric = ClassroomCaseScoring.Rubric(item.PracticalRubricJson)
        });
    }

    static async Task<IResult> CreateQuestion(ExamQuestionInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var error = ExamLogic.ValidateQuestion(input);
        if (error != null) return Bad(error);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var bank = await db.ExamQuestionBanks.FindAsync([input.BankId ?? ExamQuestionBank.DefaultId], ct);
        if (bank == null) return NotFound("题库不存在");
        if (!ExamQuestionBankEndpoints.Allows(bank.Type, input.Type)) return Bad("此题型不适用于所选题库。");
        if (await ExamPracticalEndpoints.ValidateQuestion(db, input, ct) is { } practicalError) return Bad(practicalError);
        var entity = BuildQuestion(input);
        entity.BankId = bank.Id;
        db.ExamQuestions.Add(entity);
        Audit.Add(db, ctx, "新增题库题目", ExamLogic.QuestionTypeLabel(entity.Type), Truncate(entity.Stem));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { id = entity.Id });
    }

    static async Task<IResult> UpdateQuestion(string id, ExamQuestionInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var error = ExamLogic.ValidateQuestion(input);
        if (error != null) return Bad(error);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var entity = await QuestionsInScope(ctx.Request, db).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity == null) return NotFound("题目已移出此题库或不存在");
        var bank = await db.ExamQuestionBanks.FindAsync([input.BankId ?? entity.BankId], ct);
        if (bank == null) return NotFound("题库不存在");
        if (!ExamQuestionBankEndpoints.Allows(bank.Type, input.Type)) return Bad("此题型不适用于所选题库。");
        if (await ExamPracticalEndpoints.ValidateQuestion(db, input, ct) is { } practicalError) return Bad(practicalError);
        entity.BankId = bank.Id;
        entity.Type = input.Type;
        entity.Stem = input.Stem.Trim();
        entity.OptionsJson = JsonSerializer.Serialize(ExamLogic.NormalizeOptions(input));
        entity.AnswerJson = JsonSerializer.Serialize(input.Type == "practical" ? [] : ExamLogic.NormalizeSelection(input.Answers));
        entity.Analysis = (input.Analysis ?? "").Trim();
        entity.PracticalNodeId = input.PracticalNodeId?.Trim() ?? "";
        entity.PracticalRubricJson = ClassroomCaseScoring.Write(input.PracticalRubric ?? []);
        entity.PracticalResources = input.Type == "practical" ? (input.PracticalResources ?? entity.PracticalResources).Trim() : "";
        entity.Score = input.Score;
        entity.UpdatedAt = DateTime.UtcNow;
        Audit.Add(db, ctx, "编辑题库题目", ExamLogic.QuestionTypeLabel(entity.Type), Truncate(entity.Stem));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> DeleteQuestion(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var entity = await QuestionsInScope(ctx.Request, db).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity == null) return NotFound("题目不存在");
        var used = await db.ExamPaperQuestions.CountAsync(x => x.QuestionId == id, ct);
        if (used > 0) return Conflict($"该题目已被 {used} 份试卷引用，请先从试卷中移除");
        db.ExamQuestions.Remove(entity);
        Audit.Add(db, ctx, "删除题库题目", ExamLogic.QuestionTypeLabel(entity.Type), Truncate(entity.Stem));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> BatchDeleteQuestions(IdListInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var ids = (input.Ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length == 0) return Bad("请选择要删除的题目");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var items = await QuestionsInScope(ctx.Request, db).Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (items.Count != ids.Length) return NotFound("部分题目已移出此题库或不存在");
        var used = await db.ExamPaperQuestions.AsNoTracking().Where(x => ids.Contains(x.QuestionId))
            .Select(x => x.QuestionId).Distinct().CountAsync(ct);
        if (used > 0) return Conflict($"所选题目中有 {used} 道已被试卷引用，请先从试卷中移除");
        db.ExamQuestions.RemoveRange(items);
        Audit.Add(db, ctx, "批量删除题库题目", "题库", $"删除{items.Count}道题目");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { deleted = items.Count });
    }

    static IResult Template()
        => Results.File(
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ExamImport.Template())).ToArray(),
            "text/csv; charset=utf-8", "题目导入模板.csv");

    static async Task<IResult> ImportQuestions(HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var (form, sheet, fileError) = await ImportFiles.ReadSheetAsync(ctx, ct);
        if (fileError != null) return Bad(fileError);
        if (form == null || sheet == null) return Bad("请选择 Excel 或 CSV 文件");
        List<ExamImportRow> rows;
        try { rows = ExamImport.BuildRows(sheet); }
        catch (ImportSheetException exception) { return Bad(exception.Message); }
        var preview = bool.TryParse(form["preview"], out var previewOnly) && previewOnly;
        var removeInvalidRows = bool.TryParse(form["removeInvalidRows"], out var remove) && remove;
        await using var tx = preview ? null : await db.Database.BeginTransactionAsync(ct);
        var bankId = form["bankId"].FirstOrDefault() ?? ExamQuestionBank.DefaultId;
        var bank = await db.ExamQuestionBanks.FindAsync([bankId], ct);
        if (bank == null) return NotFound("要导入的题库不存在");
        if (!preview && form["expectedBankType"].FirstOrDefault() is { } expectedType && expectedType != bank.Type)
            return Conflict("题库类型已更改，请重新预览校验。");
        foreach (var row in rows)
            if (!ExamQuestionBankEndpoints.Allows(bank.Type, row.Data.Type))
                row.Errors.Add(new ImportError("题型", "此题型不适用于所选题库。"));
        var invalid = rows.Where(row => row.Errors.Count > 0).ToArray();
        var valid = rows.Where(row => row.Errors.Count == 0).ToArray();
        if (preview)
        {
            var (page, size) = Validation.Page(ctx.Request);
            size = Math.Min(size, 100);
            var visibleRows = removeInvalidRows ? valid : rows.ToArray();
            return Results.Ok(new
            {
                bankType = bank.Type, total = visibleRows.Length, validCount = valid.Length, invalidCount = invalid.Length,
                items = visibleRows.Skip((page - 1) * size).Take(size).Select(row => new
                {
                    row.RowNumber, row.Data.Type, stem = Truncate(row.Data.Stem, 120), row.Data.Score,
                    errors = row.Errors.Take(8).Select(error => new { error.Column, message = Truncate(error.Message, 200) })
                })
            });
        }
        if (invalid.Length > 0 && !removeInvalidRows)
        {
            return Results.BadRequest(new
            {
                message = $"发现 {invalid.Length} 行问题数据，请修正文件、移除问题行后重试",
                errorCount = invalid.Length,
                validCount = rows.Count - invalid.Length,
                errors = invalid.SelectMany(row => row.Errors.Select(error => new
                {
                    rowNumber = row.RowNumber, error.Column, error.Message, stem = Truncate(row.Data.Stem, 60)
                }))
            });
        }
        if (valid.Length == 0) return Bad("没有可导入的有效题目");
        var now = DateTime.UtcNow;
        foreach (var row in valid)
        {
            var entity = BuildQuestion(row.Data);
            entity.BankId = bank.Id;
            entity.CreatedAt = now;
            entity.UpdatedAt = now;
            db.ExamQuestions.Add(entity);
        }
        Audit.Add(db, ctx, "导入题库题目", bank.Name, $"导入{valid.Length}道题目，跳过{invalid.Length}行");
        await db.SaveChangesAsync(ct);
        await tx!.CommitAsync(ct);
        return Results.Ok(new { imported = valid.Length, skipped = invalid.Length });
    }

    static async Task<IResult> ListPapers(HttpRequest req, SchoolDbContext db, CancellationToken ct)
    {
        var query = db.ExamPapers.AsNoTracking();
        var search = req.Query["search"].ToString().Trim();
        if (search.Length > 0) query = query.Where(item => item.Name.Contains(search) || item.Description.Contains(search));
        var (page, size) = Validation.Page(req);
        var total = await query.CountAsync(ct);
        var papers = await query.OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Name)
            .Skip((page - 1) * size).Take(size)
            .Select(item => new { item.Id, item.Name, item.Description, item.Remark, item.CreatedAt, item.UpdatedAt })
            .ToListAsync(ct);
        var ids = papers.Select(item => item.Id).ToArray();
        var questions = await db.ExamPaperQuestions.AsNoTracking().Where(item => ids.Contains(item.PaperId))
            .Select(item => new { item.PaperId, item.Type, item.Score }).ToListAsync(ct);
        var examLinks = await db.TrainingExams.AsNoTracking().Where(item => ids.Contains(item.PaperId))
            .Select(item => new { item.Id, item.Name, item.PaperId }).ToListAsync(ct);
        return Results.Ok(new
        {
            total, page, pageSize = size,
            items = papers.Select(paper =>
            {
                var own = questions.Where(item => item.PaperId == paper.Id).ToArray();
                return new
                {
                    paper.Id, paper.Name, paper.Description, paper.Remark, paper.CreatedAt, paper.UpdatedAt,
                    questionCount = own.Length,
                    totalScore = own.Sum(item => item.Score),
                    typeCounts = own.GroupBy(item => item.Type).ToDictionary(group => group.Key, group => group.Count()),
                    examCount = examLinks.Count(item => item.PaperId == paper.Id)
                };
            })
        });
    }

    static async Task<IResult> GetPaper(string id, SchoolDbContext db, CancellationToken ct)
    {
        var paper = await db.ExamPapers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (paper == null) return NotFound("试卷不存在");
        var questions = await db.ExamPaperQuestions.AsNoTracking().Where(item => item.PaperId == id)
            .OrderBy(item => item.SortOrder).ToListAsync(ct);
        var exams = await db.TrainingExams.AsNoTracking().Where(item => item.PaperId == id)
            .OrderByDescending(item => item.CreatedAt).Select(item => new { item.Id, item.Name, item.Status }).ToListAsync(ct);
        return Results.Ok(new
        {
            paper.Id, paper.Name, paper.Description, paper.Remark, paper.CreatedAt, paper.UpdatedAt,
            questions = questions.Select(item => new
            {
                item.Id, item.QuestionId, item.Type, item.Stem,
                options = ExamLogic.DeserializeOptions(item.OptionsJson),
                answers = ExamLogic.DeserializeAnswers(item.AnswerJson),
                item.Analysis, item.Score, item.SortOrder
            }),
            questionCount = questions.Count,
            totalScore = questions.Sum(item => item.Score),
            typeCounts = questions.GroupBy(item => item.Type).ToDictionary(group => group.Key, group => group.Count()),
            exams,
            questionEditable = exams.Count == 0
        });
    }

    static async Task<IResult> CreatePaper(ExamPaperInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var error = ExamLogic.ValidatePaper(input);
        if (error != null) return Bad(error);
        var name = input.Name.Trim();
        if (await db.ExamPapers.AnyAsync(item => item.Name == name, ct)) return Conflict("试卷名称已存在");
        var entity = new ExamPaper
        {
            Name = name,
            Description = (input.Description ?? "").Trim(),
            Remark = (input.Remark ?? "").Trim()
        };
        db.ExamPapers.Add(entity);
        Audit.Add(db, ctx, "新增试卷", "试卷", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = entity.Id });
    }

    static async Task<IResult> UpdatePaper(string id, ExamPaperInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var error = ExamLogic.ValidatePaper(input);
        if (error != null) return Bad(error);
        var entity = await db.ExamPapers.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity == null) return NotFound("试卷不存在");
        var name = input.Name.Trim();
        if (await db.ExamPapers.AnyAsync(item => item.Id != id && item.Name == name, ct)) return Conflict("试卷名称已存在");
        entity.Name = name;
        entity.Description = (input.Description ?? "").Trim();
        entity.Remark = (input.Remark ?? "").Trim();
        entity.UpdatedAt = DateTime.UtcNow;
        Audit.Add(db, ctx, "编辑试卷", "试卷", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> DeletePaper(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var entity = await db.ExamPapers.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity == null) return NotFound("试卷不存在");
        var exams = await db.TrainingExams.AsNoTracking().Where(item => item.PaperId == id)
            .Select(item => item.Name).Take(5).ToListAsync(ct);
        if (exams.Count > 0) return Conflict($"试卷已被考试“{string.Join("、", exams)}”使用，不能删除");
        db.ExamPapers.Remove(entity);
        Audit.Add(db, ctx, "删除试卷", "试卷", entity.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> BatchDeletePapers(IdListInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var ids = (input.Ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length == 0) return Bad("请选择要删除的试卷");
        var used = await db.TrainingExams.AsNoTracking().Where(item => ids.Contains(item.PaperId))
            .Select(item => item.Name).Distinct().Take(5).ToListAsync(ct);
        if (used.Count > 0) return Conflict($"部分试卷已被考试“{string.Join("、", used)}”使用，不能删除");
        var items = await db.ExamPapers.Where(item => ids.Contains(item.Id)).ToListAsync(ct);
        if (items.Count == 0) return NotFound("试卷不存在");
        db.ExamPapers.RemoveRange(items);
        Audit.Add(db, ctx, "批量删除试卷", "试卷", $"删除{items.Count}份试卷");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { deleted = items.Count });
    }

    static async Task<IResult> AddPaperQuestions(string id, ExamPaperQuestionAddInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var paper = await db.ExamPapers.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (paper == null) return NotFound("试卷不存在");
        if (await db.TrainingExams.AnyAsync(item => item.PaperId == id, ct))
            return Conflict("试卷已用于考试，不能再调整题目；请新建试卷");
        var requested = (input.Items ?? []).Where(item => item != null && !string.IsNullOrWhiteSpace(item.QuestionId)).ToArray();
        if (requested.Length == 0) return Bad("请选择要添加的题目");
        var questionIds = requested.Select(item => item.QuestionId).Distinct().ToArray();
        var questions = await db.ExamQuestions.AsNoTracking().Where(item => questionIds.Contains(item.Id)).ToListAsync(ct);
        if (questions.Count != questionIds.Length) return Bad("部分题目不存在，请刷新后重试");
        var existing = (await db.ExamPaperQuestions.Where(item => item.PaperId == id)
            .Select(item => item.QuestionId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var existingCount = await db.ExamPaperQuestions.CountAsync(item => item.PaperId == id, ct);
        if (existingCount + questionIds.Count(questionId => !existing.Contains(questionId)) > 500)
            return Bad("单份试卷最多 500 道题目");
        var nextOrder = (await db.ExamPaperQuestions.Where(item => item.PaperId == id)
            .MaxAsync(item => (int?)item.SortOrder, ct) ?? -1) + 1;
        var bank = questions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var added = 0;
        foreach (var item in requested)
        {
            if (!existing.Add(item.QuestionId)) continue;
            var question = bank[item.QuestionId];
            var score = item.Score ?? question.Score;
            if (score is < 1 or > 100) return Bad($"题目“{Truncate(question.Stem, 30)}”的分值应在 1–100 之间");
            db.ExamPaperQuestions.Add(new ExamPaperQuestion
            {
                PaperId = id, QuestionId = question.Id, Type = question.Type, Stem = question.Stem,
                OptionsJson = question.OptionsJson, AnswerJson = question.AnswerJson, Analysis = question.Analysis,
                PracticalNodeId = question.PracticalNodeId, PracticalRubricJson = question.PracticalRubricJson,
                PracticalResources = question.PracticalResources,
                Score = score, SortOrder = nextOrder + added
            });
            added++;
        }
        paper.UpdatedAt = DateTime.UtcNow;
        Audit.Add(db, ctx, "试卷添加题目", "试卷", $"{paper.Name}：添加{added}道题目");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { added, skipped = requested.Length - added });
    }

    static async Task<IResult> UpdatePaperQuestionScore(string id, string paperQuestionId, ExamPaperQuestionScoreInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        if (input.Score is < 1 or > 100) return Bad("题目分值应在 1–100 之间");
        if (await db.TrainingExams.AnyAsync(item => item.PaperId == id, ct))
            return Conflict("试卷已用于考试，不能再调整题目分值");
        var link = await db.ExamPaperQuestions.FirstOrDefaultAsync(item => item.Id == paperQuestionId && item.PaperId == id, ct);
        if (link == null) return NotFound("试卷题目不存在");
        link.Score = input.Score;
        var paper = await db.ExamPapers.FirstAsync(item => item.Id == id, ct);
        paper.UpdatedAt = DateTime.UtcNow;
        Audit.Add(db, ctx, "调整试卷题目分值", "试卷", $"{paper.Name}：{Truncate(link.Stem)} → {input.Score}分");
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    static Task<IResult> DeletePaperQuestion(string id, string paperQuestionId, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
        => RemovePaperQuestionLinks(id, [paperQuestionId], ctx, db, ct);

    static Task<IResult> RemovePaperQuestions(string id, PaperQuestionRemoval input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
        => RemovePaperQuestionLinks(id, input.PaperQuestionIds ?? [], ctx, db, ct);

    static async Task<IResult> RemovePaperQuestionLinks(string id, string[] requested, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        var ids = requested.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        if (ids.Length is < 1 or > 500) return Bad("请选择 1–500 道试卷题目");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.TrainingExams.AnyAsync(item => item.PaperId == id, ct))
            return Conflict("试卷已用于考试，不能再调整题目");
        var links = await db.ExamPaperQuestions.Where(item => item.PaperId == id && ids.Contains(item.Id)).ToListAsync(ct);
        if (links.Count != ids.Length) return NotFound("部分试卷题目不存在，请刷新后重试");
        db.ExamPaperQuestions.RemoveRange(links);
        var paper = await db.ExamPapers.FirstAsync(item => item.Id == id, ct);
        paper.UpdatedAt = DateTime.UtcNow;
        Audit.Add(db, ctx, "移除试卷题目", "试卷", $"{paper.Name}：移除 {links.Count} 道题目");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    sealed record PaperQuestionRemoval(string[]? PaperQuestionIds);

    static ExamQuestion BuildQuestion(ExamQuestionInput input) => new()
    {
        Type = input.Type,
        Stem = input.Stem.Trim(),
        OptionsJson = JsonSerializer.Serialize(ExamLogic.NormalizeOptions(input)),
        AnswerJson = JsonSerializer.Serialize(input.Type == "practical" ? [] : ExamLogic.NormalizeSelection(input.Answers)),
        Analysis = (input.Analysis ?? "").Trim(),
        PracticalNodeId = input.PracticalNodeId?.Trim() ?? "",
        PracticalRubricJson = ClassroomCaseScoring.Write(input.PracticalRubric ?? []),
        PracticalResources = input.PracticalResources?.Trim() ?? "",
        Score = input.Score
    };

    static IQueryable<ExamQuestion> QuestionsInScope(HttpRequest request, SchoolDbContext db)
    {
        var bankId = request.Query["bankId"].ToString();
        return bankId.Length == 0 ? db.ExamQuestions : db.ExamQuestions.Where(item => item.BankId == bankId);
    }

    static string Truncate(string value, int length = 40)
        => value.Length <= length ? value : value[..length] + "…";

    static IResult Bad(string message) => Results.BadRequest(new { message });
    static IResult Conflict(string message) => Results.Conflict(new { message });
    static IResult NotFound(string message = "记录不存在") => Results.NotFound(new { message });
}
