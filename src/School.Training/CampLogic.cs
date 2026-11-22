using System.Text.Json;

namespace School.Training;

public static class CampLogic
{
    public static readonly string[] QuestionTypes = ExamLogic.QuestionTypes;

    public static string? ValidatePosition(CampPositionInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "岗位名称不能为空";
        if (input.Name.Trim().Length > 100) return "岗位名称不能超过 100 个字符";
        if ((input.Description ?? "").Length > 2000) return "岗位描述不能超过 2000 个字符";
        if (input.DurationMinutes is < 1 or > 600) return "测评时长应在 1–600 分钟之间";
        var skills = input.Skills ?? [];
        if (skills.Length == 0) return "请至少配置 1 项技能";
        if (skills.Length > 100) return "技能项最多 100 条";
        for (var index = 0; index < skills.Length; index++)
        {
            var skill = skills[index];
            if (string.IsNullOrWhiteSpace(skill.Name)) return $"第 {index + 1} 项技能的名称不能为空";
            if (skill.Name.Trim().Length > 100) return $"第 {index + 1} 项技能的名称不能超过 100 个字符";
            if ((skill.SubName ?? "").Length > 100) return $"第 {index + 1} 项技能的子技能不能超过 100 个字符";
            if ((skill.Ability ?? "").Length > 200) return $"第 {index + 1} 项技能的具体能力不能超过 200 个字符";
        }
        var questions = input.Questions ?? [];
        if (questions.Length == 0) return "请至少配置 1 道测评题";
        if (questions.Length > 200) return "单个岗位最多 200 道测评题";
        for (var index = 0; index < questions.Length; index++)
        {
            var question = questions[index];
            var label = $"第 {index + 1} 题";
            var examInput = new ExamQuestionInput(question.Type, question.Stem, question.Options, question.Answers, question.Score, question.Analysis);
            var error = ExamLogic.ValidateQuestion(examInput);
            if (error != null) return $"{label}：{error}";
            if (question.SkillIndex is null || question.SkillIndex < 0 || question.SkillIndex >= skills.Length)
                return $"{label}：请选择对应技能";
            if (question.Type != "practical") continue;
            if (question.PracticalRubric is { Length: > 0 } practical)
            {
                if ((question.Rubric?.Length ?? 0) > 0) return $"{label}：请选择一种评分方式";
                if (practical.Any(item => item == null) || practical.Sum(item => (long)item.Score) != question.Score)
                    return $"{label}：设备评分项分值合计必须等于题目分值";
                continue;
            }
            var rubric = question.Rubric ?? [];
            if (rubric.Length is < 1 or > 20) return $"{label}：实操题需要 1–20 条评分项";
            var total = 0;
            foreach (var item in rubric)
            {
                if (string.IsNullOrWhiteSpace(item.Item)) return $"{label}：评分项名称不能为空";
                if (item.Item.Trim().Length > 100) return $"{label}：评分项名称不能超过 100 个字符";
                if (string.IsNullOrWhiteSpace(item.Keyword)) return $"{label}：评分关键词不能为空";
                if (item.Keyword.Trim().Length > 200) return $"{label}：评分关键词不能超过 200 个字符";
                if (item.Keyword.Any(char.IsControl)) return $"{label}：评分关键词不能包含控制字符";
                if (item.Score is < 1 or > 100) return $"{label}：评分项分值应在 1–100 之间";
                total += item.Score;
            }
            if (total != question.Score) return $"{label}：评分项合计 {total} 分，应等于题目分值 {question.Score} 分";
        }
        return null;
    }

    public static List<JobSkill> BuildSkills(string positionId, IEnumerable<CampSkillInput>? inputs)
    {
        var order = 0;
        return (inputs ?? []).Select(input => new JobSkill
        {
            PositionId = positionId,
            Name = input.Name.Trim(),
            SubName = (input.SubName ?? "").Trim(),
            Ability = (input.Ability ?? "").Trim(),
            SortOrder = order++
        }).ToList();
    }

    public static void AddQuestions(SchoolDbContext db, string positionId, List<JobSkill> skills, CampQuestionInput[]? inputs)
    {
        var order = 0;
        foreach (var question in inputs ?? [])
        {
            var examInput = new ExamQuestionInput(question.Type, question.Stem, question.Options, question.Answers, question.Score, question.Analysis);
            db.JobQuestions.Add(new JobQuestion
            {
                PositionId = positionId,
                SkillId = skills[question.SkillIndex!.Value].Id,
                Type = question.Type,
                Stem = question.Stem.Trim(),
                OptionsJson = JsonSerializer.Serialize(ExamLogic.NormalizeOptions(examInput)),
                AnswerJson = JsonSerializer.Serialize(question.Type == "practical" ? [] : ExamLogic.NormalizeSelection(question.Answers)),
                Analysis = (question.Analysis ?? "").Trim(),
                RubricJson = question.Type == "practical" ? SerializeRubric(question.Rubric) : "[]",
                PracticalRubricJson = ClassroomCaseScoring.Write(question.Type == "practical" ? question.PracticalRubric ?? [] : []),
                Score = question.Score,
                SortOrder = order++
            });
        }
    }

    public static string SerializeRubric(IEnumerable<CampRubricItemInput>? items)
        => JsonSerializer.Serialize((items ?? []).Select(item => new CampRubricItem(item.Item.Trim(), item.Keyword.Trim(), item.Score)));

    public static CampRubricItem[] DeserializeRubric(string json)
    {
        try { return JsonSerializer.Deserialize<CampRubricItem[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static bool KeywordMatches(string keyword, string text)
        => keyword.Split(['|', '｜'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => text.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    public static (int Score, List<CampRubricMatch> Items) ScorePractical(string rubricJson, string text)
    {
        var items = DeserializeRubric(rubricJson);
        var matches = items.Select(item => new CampRubricMatch(item, text.Length > 0 && KeywordMatches(item.Keyword, text))).ToList();
        return (matches.Where(match => match.Matched).Sum(match => match.Item.Score), matches);
    }

    public static string Evaluation(int score, int maxScore)
    {
        if (maxScore <= 0) return "待评估";
        var percent = ExamLogic.Percent(score, maxScore);
        return percent >= 80 ? "掌握" : percent >= 60 ? "基本掌握" : "待提升";
    }

    public static string Level(int percent)
        => percent >= 85 ? "优秀" : percent >= 70 ? "良好" : percent >= 60 ? "合格" : "待提升";

    public static List<CampSkillRow> SkillRows(IEnumerable<JobSkill> skills, IEnumerable<JobQuestion> questions, IReadOnlyDictionary<string, int> awarded)
    {
        var rows = new List<CampSkillRow>();
        foreach (var skill in skills.OrderBy(item => item.SortOrder))
        {
            var own = questions.Where(question => question.SkillId == skill.Id).ToArray();
            var score = own.Sum(question => awarded.GetValueOrDefault(question.Id));
            var maxScore = own.Sum(question => question.Score);
            rows.Add(new CampSkillRow(skill.Name, skill.SubName, skill.Ability, own.Length, score, maxScore,
                maxScore <= 0 ? 0 : ExamLogic.Percent(score, maxScore), Evaluation(score, maxScore)));
        }
        return rows;
    }

    public static List<CampRadarAxis> Radar(IEnumerable<CampSkillRow> rows)
        => rows.GroupBy(row => row.Skill).Select(group =>
        {
            var score = group.Sum(row => row.Score);
            var maxScore = group.Sum(row => row.MaxScore);
            return new CampRadarAxis(group.Key, score, maxScore, maxScore <= 0 ? 0 : ExamLogic.Percent(score, maxScore));
        }).ToList();

    public static bool StructureMatches(
        JobPosition position, IEnumerable<JobSkill> skills, IEnumerable<JobQuestion> questions, CampPositionInput input)
    {
        if (position.DurationMinutes != input.DurationMinutes) return false;
        if (!string.Equals(position.StartupNodeId, string.IsNullOrWhiteSpace(input.StartupNodeId) ? null : input.StartupNodeId, StringComparison.Ordinal)) return false;
        var skillInputs = input.Skills ?? [];
        var existingSkills = skills.OrderBy(item => item.SortOrder).ToArray();
        if (existingSkills.Length != skillInputs.Length) return false;
        for (var index = 0; index < existingSkills.Length; index++)
        {
            var existing = existingSkills[index];
            var incoming = skillInputs[index];
            if (existing.Name != incoming.Name.Trim()
                || existing.SubName != (incoming.SubName ?? "").Trim()
                || existing.Ability != (incoming.Ability ?? "").Trim()) return false;
        }
        var questionInputs = input.Questions ?? [];
        var existingQuestions = questions.OrderBy(item => item.SortOrder).ToArray();
        if (existingQuestions.Length != questionInputs.Length) return false;
        for (var index = 0; index < existingQuestions.Length; index++)
        {
            var existing = existingQuestions[index];
            var incoming = questionInputs[index];
            var skillIndex = incoming.SkillIndex is int value && value >= 0 && value < existingSkills.Length ? value : -1;
            if (skillIndex < 0) return false;
            if (existing.Type != incoming.Type
                || existing.Stem != incoming.Stem.Trim()
                || existing.Score != incoming.Score
                || existing.Analysis != (incoming.Analysis ?? "").Trim()
                || existing.SkillId != existingSkills[skillIndex].Id
                || !ExamLogic.DeserializeOptions(existing.OptionsJson).SequenceEqual(ExamLogic.NormalizeOptions(
                    new ExamQuestionInput(incoming.Type, incoming.Stem, incoming.Options, incoming.Answers, incoming.Score, incoming.Analysis)))
                || !ExamLogic.DeserializeAnswers(existing.AnswerJson).SequenceEqual(
                    incoming.Type == "practical" ? [] : ExamLogic.NormalizeSelection(incoming.Answers))
                || !DeserializeRubric(existing.RubricJson).SequenceEqual((incoming.Rubric ?? []).Select(item => new CampRubricItem(item.Item.Trim(), item.Keyword.Trim(), item.Score)))
                || !ClassroomCaseScoring.Rubric(existing.PracticalRubricJson).SequenceEqual(incoming.PracticalRubric ?? []))
                return false;
        }
        return true;
    }
}

public sealed record CampPracticalProject(int Revision, bool HasWork, CaseDeviceState[] Devices);

public sealed record CampRubricMatch(CampRubricItem Item, bool Matched);
public sealed record CampSkillRow(string Skill, string SubSkill, string Ability, int QuestionCount, int Score, int MaxScore, int Percent, string Result);
public sealed record CampRadarAxis(string Name, int Score, int MaxScore, int Percent);
