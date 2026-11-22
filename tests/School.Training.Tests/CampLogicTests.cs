using System.Text.Json;

namespace School.Training.Tests;

public class CampLogicTests
{
    static CampPositionInput ValidInput() => new(
        "网络运维工程师", "岗位说明", 60, null,
        [
            new CampSkillInput("网络基础", "路由交换", "能够划分 VLAN 并配置链路"),
            new CampSkillInput("安全防护", "访问控制", "能够配置 ACL 与防火墙策略")
        ],
        [
            new CampQuestionInput("single", "VLAN 的主要作用是？", ["隔离广播域", "提高带宽", "加密数据", "备份配置"], [0], 5, "", 0),
            new CampQuestionInput("practical", "请描述 VLAN 划分与验证步骤。", null, null, 10, "", 1,
                [new CampRubricItemInput("划分规划", "VLAN|划分", 6), new CampRubricItemInput("连通验证", "ping|测试", 4)])
        ]);

    [Fact]
    public void Accepts_a_complete_position()
        => Assert.Null(CampLogic.ValidatePosition(ValidInput()));

    [Fact]
    public void Requires_skills_and_questions()
    {
        Assert.Equal("请至少配置 1 项技能", CampLogic.ValidatePosition(ValidInput() with { Skills = [] }));
        Assert.Equal("请至少配置 1 道测评题", CampLogic.ValidatePosition(ValidInput() with { Questions = [] }));
    }

    [Fact]
    public void Rejects_practical_rubric_sum_mismatch_and_missing_skill()
    {
        var input = ValidInput();
        var practical = input.Questions![1];
        var wrongSum = input with
        {
            Questions = [input.Questions![0], practical with
            {
                Rubric = [new CampRubricItemInput("划分规划", "VLAN", 3), new CampRubricItemInput("连通验证", "ping", 4)]
            }]
        };
        Assert.Contains("评分项合计 7 分", CampLogic.ValidatePosition(wrongSum));

        var missingSkill = input with { Questions = [input.Questions![0] with { SkillIndex = null }] };
        Assert.Contains("请选择对应技能", CampLogic.ValidatePosition(missingSkill));
    }

    [Theory]
    [InlineData("VLAN|划分", "已完成 vlan 划分并验证", true)]
    [InlineData("ping|测试", "使用 PING 检查连通性", true)]
    [InlineData("防火墙|ACL", "配置了访问列表", false)]
    public void Matches_keyword_alternatives_case_insensitively(string keyword, string text, bool expected)
        => Assert.Equal(expected, CampLogic.KeywordMatches(keyword, text));

    [Fact]
    public void Scores_practical_answers_by_matched_rubric_items()
    {
        var rubric = CampLogic.SerializeRubric([
            new CampRubricItemInput("划分规划", "VLAN|划分", 6),
            new CampRubricItemInput("连通验证", "ping|测试", 4)
        ]);
        var result = CampLogic.ScorePractical(rubric, "先按部门划分 VLAN，再用 ping 验证。");
        Assert.Equal(10, result.Score);
        Assert.All(result.Items, item => Assert.True(item.Matched));

        var partial = CampLogic.ScorePractical(rubric, "只完成了 VLAN 划分。");
        Assert.Equal(6, partial.Score);
        Assert.Equal([true, false], partial.Items.Select(item => item.Matched));
    }

    [Theory]
    [InlineData(85, 100, "掌握")]
    [InlineData(79, 100, "基本掌握")]
    [InlineData(59, 100, "待提升")]
    [InlineData(0, 0, "待评估")]
    public void Maps_scores_to_evaluation_results(int score, int maxScore, string expected)
        => Assert.Equal(expected, CampLogic.Evaluation(score, maxScore));

    [Fact]
    public void Builds_skill_rows_and_radar_grouped_by_top_level_skill()
    {
        var skills = new[]
        {
            new JobSkill { Id = "s1", Name = "网络基础", SubName = "路由交换", Ability = "划分 VLAN" },
            new JobSkill { Id = "s2", Name = "网络基础", SubName = "地址规划", Ability = "规划网段" }
        };
        var questions = new[]
        {
            new JobQuestion { Id = "q1", SkillId = "s1", Score = 10 },
            new JobQuestion { Id = "q2", SkillId = "s2", Score = 10 }
        };
        var awarded = new Dictionary<string, int> { ["q1"] = 8 };
        var rows = CampLogic.SkillRows(skills, questions, awarded);
        Assert.Equal(2, rows.Count);
        Assert.Equal("掌握", rows[0].Result);
        Assert.Equal(8, rows[0].Score);
        Assert.Equal(80, rows[0].Percent);
        Assert.Equal("待提升", rows[1].Result);

        var radar = CampLogic.Radar(rows);
        var axis = Assert.Single(radar);
        Assert.Equal("网络基础", axis.Name);
        Assert.Equal(8, axis.Score);
        Assert.Equal(20, axis.MaxScore);
        Assert.Equal(40, axis.Percent);
    }

    [Fact]
    public void Structure_comparison_ignores_name_but_detects_question_changes()
    {
        var input = ValidInput();
        var position = new JobPosition { Id = "p1", Name = "旧名称", Description = "旧", DurationMinutes = 60 };
        var skills = CampLogic.BuildSkills("p1", input.Skills);
        skills[0].Id = "skill-1";
        skills[1].Id = "skill-2";
        var questions = new[]
        {
            new JobQuestion
            {
                Id = "q1", PositionId = "p1", SkillId = "skill-1", Type = "single",
                Stem = input.Questions![0].Stem, OptionsJson = JsonSerializer.Serialize(input.Questions![0].Options),
                AnswerJson = "[0]", Score = 5, SortOrder = 0
            },
            new JobQuestion
            {
                Id = "q2", PositionId = "p1", SkillId = "skill-2", Type = "practical",
                Stem = input.Questions![1].Stem, OptionsJson = "[]", AnswerJson = "[]",
                RubricJson = CampLogic.SerializeRubric(input.Questions![1].Rubric), Score = 10, SortOrder = 1
            }
        };
        var renamed = input with { Name = "新名称", Description = "新简介" };
        Assert.True(CampLogic.StructureMatches(position, skills, questions, renamed));
        Assert.False(CampLogic.StructureMatches(position, skills, questions, input with { DurationMinutes = 90 }));
        Assert.False(CampLogic.StructureMatches(position, skills, questions,
            input with { Questions = [input.Questions![0] with { Options = ["A", "B", "C", "D"] }, input.Questions![1]] }));
    }
}
