using System.Text.Json;

namespace School.Training;

public static class ExamLogic
{
    public static readonly string[] QuestionTypes = ["single", "multiple", "judge", "practical"];
    public static readonly string[] ExamStatuses = ["scheduled", "open", "closed"];
    public static readonly string[] AttemptStatuses = ["inProgress", "submitted", "graded"];

    public static string QuestionTypeLabel(string value) => value switch
    {
        "single" => "单选题",
        "multiple" => "多选题",
        "judge" => "判断题",
        "practical" => "实操题",
        _ => value
    };

    public static string ExamStatusLabel(string value) => value switch
    {
        "scheduled" => "未开始",
        "open" => "进行中",
        "closed" => "已结束",
        _ => value
    };

    public static string AttemptStatusLabel(string value) => value switch
    {
        "notStarted" => "未参加",
        "inProgress" => "作答中",
        "submitted" => "待批阅",
        "graded" => "已批阅",
        _ => value
    };

    public static bool IsObjective(string type) => type is "single" or "multiple" or "judge";

    public static bool HasAnswer(ExamAnswer answer)
        => !string.IsNullOrWhiteSpace(answer.TextAnswer) || DeserializeSelection(answer.SelectedJson).Length > 0
            || (answer.PracticalDetailsJson != "[]" && answer.AnsweredAt != null);

    public static string EffectiveStatus(string status, DateTime startAt, DateTime endAt, DateTime now)
        => status == "closed" || now >= endAt ? "closed" : status == "open" && now >= startAt ? "open" : "scheduled";

    public static string EffectiveStatus(TrainingExam exam, DateTime now)
        => EffectiveStatus(exam.Status, exam.StartAt, exam.EndAt, now);

    public static string? ValidateQuestion(ExamQuestionInput question)
    {
        if (!QuestionTypes.Contains(question.Type)) return "题型不合法";
        if (string.IsNullOrWhiteSpace(question.Stem)) return "题干不能为空";
        if (question.PracticalResources is { Length: > 6000 }) return "答题资源不能超过 6000 个字符";
        if (question.Type != "practical" && !string.IsNullOrWhiteSpace(question.PracticalResources)) return "只有实操题可配置答题资源";
        if (question.Stem.Trim().Length > 1000) return "题干不能超过 1000 个字符";
        if (question.Score is < 1 or > 100) return "题目分值应在 1–100 之间";
        if ((question.Analysis ?? "").Length > 2000) return "题目解析不能超过 2000 个字符";
        if (question.Type == "practical")
        {
            if (NormalizeOptions(question).Length > 0 || NormalizeSelection(question.Answers).Length > 0) return "实操题不需要设置选项和正确答案";
            return null;
        }
        var options = NormalizeOptions(question);
        if (question.Type == "judge")
        {
            if (options.Length != 2) return "判断题需要两个选项";
        }
        else if (options.Length is < 2 or > 10) return "选择题需要 2–10 个选项";
        if (options.Any(string.IsNullOrWhiteSpace)) return "选项内容不能为空";
        if (options.Any(option => option.Trim().Length > 200)) return "选项不能超过 200 个字符";
        var answers = NormalizeSelection(question.Answers);
        if (answers.Length == 0) return "请设置正确答案";
        if (answers.Length != (question.Answers ?? []).Length) return "正确答案不能重复";
        if (answers.Any(index => index < 0 || index >= options.Length)) return "正确答案超出选项范围";
        if (question.Type is "single" or "judge" && answers.Length != 1) return "单选题和判断题只能设置一个正确答案";
        return null;
    }

    public static string[] NormalizeOptions(ExamQuestionInput question)
        => question.Type == "practical"
            ? []
            : question.Type == "judge" && (question.Options ?? []).Length == 0
                ? ["正确", "错误"]
                : (question.Options ?? []).Select(option => (option ?? "").Trim()).ToArray();

    public static string[] NormalizeOptions(ExamPaperQuestion question) => DeserializeOptions(question.OptionsJson);

    public static int[] NormalizeSelection(IEnumerable<int>? values)
        => (values ?? []).Distinct().OrderBy(value => value).ToArray();

    public static string? ValidatePaper(ExamPaperInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "试卷名称不能为空";
        if (input.Name.Trim().Length > 100) return "试卷名称不能超过 100 个字符";
        if ((input.Description ?? "").Length > 2000) return "试卷说明不能超过 2000 个字符";
        if ((input.Remark ?? "").Length > 2000) return "备注不能超过 2000 个字符";
        return null;
    }

    public static string? ValidateExam(TrainingExamInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "考试名称不能为空";
        if (input.Name.Trim().Length > 100) return "考试名称不能超过 100 个字符";
        if (string.IsNullOrWhiteSpace(input.PaperId)) return "请选择试卷";
        if ((input.Description ?? "").Length > 2000) return "考试说明不能超过 2000 个字符";
        if (input.StartAt.Year is < 2000 or > 2100 || input.EndAt.Year is < 2000 or > 2100) return "考试时间不合法";
        if (input.EndAt <= input.StartAt) return "结束时间必须晚于开始时间";
        if (input.DurationMinutes is < 1 or > 600) return "考试时长应在 1–600 分钟之间";
        return null;
    }

    public static int Percent(int score, int maxScore)
        => maxScore <= 0 ? 0 : (int)Math.Round(score * 100d / maxScore, MidpointRounding.AwayFromZero);

    public static ScoreBand[] ScoreBands(decimal[] percentages, int expected, int absent, int pending)
    {
        ScoreBand Band(string label, int count) => new(label, count,
            expected == 0 ? 0 : Math.Round(count * 100m / expected, 1));
        ScoreBand[] bands =
        [
            Band("缺考", absent),
            Band("60% 以下", percentages.Count(value => value < 60)),
            Band("60%–不足 80%", percentages.Count(value => value is >= 60 and < 80)),
            Band("80%–100%", percentages.Count(value => value >= 80))
        ];
        return pending == 0 ? bands : [.. bands, Band("作答中／待批阅", pending)];
    }

    public static bool IsCorrect(string answerJson, IEnumerable<int>? selection)
    {
        var expected = NormalizeSelection(DeserializeAnswers(answerJson));
        var actual = NormalizeSelection(selection);
        return expected.SequenceEqual(actual);
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

    public static int[] DeserializeSelection(string json) => DeserializeAnswers(json);
}

public sealed record ScoreBand(string Label, int Count, decimal Percent);
