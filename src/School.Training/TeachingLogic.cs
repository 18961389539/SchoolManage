using System.Text.Json;

namespace School.Training;

public static class TeachingLogic
{
    public static string EffectiveStatus(Classroom classroom, DateTime now)
        => classroom.Status == "closed" || now >= classroom.CloseAt ? "closed"
            : classroom.Status == "open" && now >= classroom.OpenAt ? "open" : "scheduled";

    public static IQueryable<Classroom> OpenClassrooms(SchoolDbContext db, DateTime now)
        => db.Classrooms.Where(room => room.Status == "open" && room.OpenAt <= now && room.CloseAt > now);
    public static readonly string[] CourseTypes = ["general", "core", "open"];
    public static readonly string[] ResourceKinds = ["theory", "case", "theoryPractice", "casePractice"];
    public static readonly string[] QuestionTypes = ["single", "multiple", "judge"];

    public static string CourseTypeLabel(string value) => value switch
    {
        "general" => "通识课程",
        "core" => "专业核心",
        "open" => "开放课程",
        _ => value
    };

    public static string ResourceKindLabel(string value) => value switch
    {
        "theory" => "理论知识",
        "case" => "案例教学",
        "theoryPractice" => "理论练习",
        "casePractice" => "案例实操",
        _ => value
    };

    public static string QuestionTypeLabel(string value) => value switch
    {
        "single" => "单选题",
        "multiple" => "多选题",
        "judge" => "判断题",
        _ => value
    };

    public static string? ValidateCourse(CourseInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "课程名称不能为空";
        if (input.Name.Trim().Length > 100) return "课程名称不能超过 100 个字符";
        if (!CourseTypes.Contains(input.Type)) return "课程类型不合法";
        if ((input.Description ?? "").Length > 2000) return "课程简介不能超过 2000 个字符";
        if ((input.Remark ?? "").Length > 2000) return "备注不能超过 2000 个字符";
        return null;
    }

    public static string? ValidateChapterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "大章名称不能为空";
        return name.Trim().Length > 100 ? "大章名称不能超过 100 个字符" : null;
    }

    public static string? ValidateSectionName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "小节名称不能为空";
        return name.Trim().Length > 100 ? "小节名称不能超过 100 个字符" : null;
    }

    public static string? ValidateSectionDuration(int minutes)
        => minutes is < 1 or > 600 ? "小节时长应在 1–600 分钟之间" : null;

    public static string? ValidateResource(CourseResourceInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "资源名称不能为空";
        if (input.Name.Trim().Length > 100) return "资源名称不能超过 100 个字符";
        if (!ResourceKinds.Contains(input.Kind)) return "资源类型不合法";
        if (input.DurationMinutes is < 0 or > 600) return "资源时长应在 0–600 分钟之间";
        if ((input.Content ?? "").Length > 20000) return "资源正文不能超过 20000 个字符";
        if ((input.Topology ?? "").Length > 2000) return "拓扑说明不能超过 2000 个字符";
        if (input.Kind is "theory" or "case" && string.IsNullOrWhiteSpace(input.Content)) return "理论知识或案例教学资源需要填写正文";
        var questions = input.Questions ?? [];
        if (input.Kind == "theoryPractice")
        {
            if (questions.Length == 0) return "理论练习至少需要 1 道题目";
            if (questions.Length > 100) return "单个练习最多 100 道题目";
        }
        if (input.Kind != "theoryPractice" && questions.Length > 0) return "只有理论练习可以配置题目";
        foreach (var question in questions)
        {
            var error = ValidateQuestion(question);
            if (error != null) return error;
        }
        return null;
    }

    public static string? ValidateQuestion(CourseQuestionInput question)
    {
        if (!QuestionTypes.Contains(question.Type)) return "题目类型不合法";
        if (string.IsNullOrWhiteSpace(question.Stem)) return "题干不能为空";
        if (question.Stem.Trim().Length > 500) return "题干不能超过 500 个字符";
        if (question.Score is < 1 or > 100) return "题目分值应在 1–100 之间";
        if ((question.Analysis ?? "").Length > 1000) return "题目解析不能超过 1000 个字符";
        var options = question.Options ?? [];
        if (question.Type == "judge")
        {
            if (options.Length != 2) return "判断题需要两个选项";
            if (options.Any(string.IsNullOrWhiteSpace)) return "选项内容不能为空";
        }
        else
        {
            if (options.Length is < 2 or > 10) return "选择题需要 2–10 个选项";
            if (options.Any(option => string.IsNullOrWhiteSpace(option))) return "选项内容不能为空";
            if (options.Any(option => option.Trim().Length > 200)) return "选项不能超过 200 个字符";
        }
        var answers = NormalizeSelection(question.Answers);
        if (answers.Length == 0) return "请设置正确答案";
        if (answers.Length != (question.Answers ?? []).Length) return "正确答案不能重复";
        if (answers.Any(index => index < 0 || index >= options.Length)) return "正确答案超出选项范围";
        if (question.Type is "single" or "judge" && answers.Length != 1) return "单选题和判断题只能设置一个正确答案";
        return null;
    }

    public static int[] NormalizeSelection(IEnumerable<int>? values)
        => (values ?? []).Distinct().OrderBy(value => value).ToArray();

    public static bool IsCorrect(CourseQuestion question, IEnumerable<int>? selection)
    {
        var expected = NormalizeSelection(DeserializeAnswers(question.AnswerJson));
        var actual = NormalizeSelection(selection);
        return expected.SequenceEqual(actual);
    }

    public static (int Score, int MaxScore) ScoreAttempt(IEnumerable<CourseQuestion> questions, IReadOnlyDictionary<string, int[]> answers)
    {
        var score = 0;
        var maxScore = 0;
        foreach (var question in questions)
        {
            maxScore += question.Score;
            if (answers.TryGetValue(question.Id, out var selection) && IsCorrect(question, selection)) score += question.Score;
        }
        return (score, maxScore);
    }

    public static int[] DeserializeAnswers(string json)
    {
        try { return JsonSerializer.Deserialize<int[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string[] DeserializeOptions(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
