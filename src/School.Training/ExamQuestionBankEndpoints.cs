using Microsoft.EntityFrameworkCore;

namespace School.Training;

public static class ExamQuestionBankEndpoints
{
    public static void MapExamQuestionBanks(this RouteGroupBuilder staff, RouteGroupBuilder admin)
    {
        staff.MapGet("/exam-question-banks", List);
        admin.MapPost("/exam-question-banks", Create);
        admin.MapPut("/exam-question-banks/{id}", Update);
        admin.MapDelete("/exam-question-banks/{id}", Delete);
    }

    public static bool Allows(string bankType, string questionType) => bankType switch
    {
        "theory" => ExamLogic.IsObjective(questionType),
        "practical" => questionType == "practical",
        "mixed" => ExamLogic.QuestionTypes.Contains(questionType),
        _ => false
    };

    static async Task<IResult> List(HttpRequest request, SchoolDbContext db, CancellationToken ct)
    {
        var query = db.ExamQuestionBanks.AsNoTracking();
        var search = request.Query["search"].ToString().Trim();
        if (search.Length > 0) query = query.Where(bank => bank.Name.Contains(search));
        var type = request.Query["type"].ToString();
        if (type is "theory" or "practical" or "mixed") query = query.Where(bank => bank.Type == type);
        var (page, size) = Validation.Page(request);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(bank => bank.Id == ExamQuestionBank.DefaultId)
            .ThenByDescending(bank => bank.CreatedAt).ThenBy(bank => bank.Id)
            .Skip((page - 1) * size).Take(size)
            .Select(bank => new
            {
                bank.Id, bank.Name, bank.Type, bank.CreatedAt,
                isDefault = bank.Id == ExamQuestionBank.DefaultId,
                questionCount = db.ExamQuestions.Count(question => question.BankId == bank.Id)
            }).ToListAsync(ct);
        return Results.Ok(new { total, page, pageSize = size, items });
    }

    static async Task<IResult> Create(ExamQuestionBankInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        if (Validate(input) is { } error) return Bad(error);
        var name = input.Name.Trim();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.ExamQuestionBanks.AnyAsync(bank => bank.Name == name, ct)) return Conflict("题库名称已存在。");
        var bank = new ExamQuestionBank { Name = name, Type = input.Type };
        db.ExamQuestionBanks.Add(bank);
        Audit.Add(db, ctx, "新增考试题库", bank.Name);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { bank.Id });
    }

    static async Task<IResult> Update(string id, ExamQuestionBankInput input, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        if (Validate(input) is { } error) return Bad(error);
        var name = input.Name.Trim();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var bank = await db.ExamQuestionBanks.FindAsync([id], ct);
        if (bank == null) return Missing();
        if (bank.Id == ExamQuestionBank.DefaultId && input.Type != "mixed")
            return Conflict("默认题库须保留为综合题库。");
        if (await db.ExamQuestionBanks.AnyAsync(item => item.Id != id && item.Name == name, ct))
            return Conflict("题库名称已存在。");
        if (bank.Type != input.Type)
        {
            var types = await db.ExamQuestions.Where(question => question.BankId == id)
                .Select(question => question.Type).Distinct().ToListAsync(ct);
            if (!types.All(type => Allows(input.Type, type)))
                return Conflict("题库中有不符合新类型的题目，请先移出这些题目。");
        }
        bank.Name = name;
        bank.Type = input.Type;
        Audit.Add(db, ctx, "编辑考试题库", bank.Name);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> Delete(string id, HttpContext ctx, SchoolDbContext db, CancellationToken ct)
    {
        if (id == ExamQuestionBank.DefaultId) return Conflict("默认题库不能删除。");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var bank = await db.ExamQuestionBanks.FindAsync([id], ct);
        if (bank == null) return Missing();
        var questions = db.ExamQuestions.Where(question => question.BankId == id);
        if (await db.ExamPaperQuestions.AnyAsync(link => questions.Any(question => question.Id == link.QuestionId), ct))
            return Conflict("该题库有题目被试卷引用。请先移出这些题目或解除试卷引用。");
        if (await questions.AnyAsync(ct)) return Conflict("请先清空题库或将题目移到其他题库。");
        db.ExamQuestionBanks.Remove(bank);
        Audit.Add(db, ctx, "删除考试题库", bank.Name);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    static string? Validate(ExamQuestionBankInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 100 || input.Name.Any(char.IsControl))
            return "请输入 1 至 100 个字符的题库名称。";
        return input.Type is "theory" or "practical" or "mixed" ? null : "请选择有效的题库类型。";
    }

    static IResult Bad(string message) => Results.BadRequest(new { message });
    static IResult Conflict(string message) => Results.Conflict(new { message });
    static IResult Missing() => Results.NotFound(new { message = "题库不存在。" });
}
