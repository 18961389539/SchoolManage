using System.Text.Json;

namespace School.Training.Tests;

public class TeachingLogicTests
{
    static CourseQuestion Question(string id, string type, int[] answers, int score = 10)
        => new()
        {
            Id = id, Type = type, OptionsJson = JsonSerializer.Serialize(new[] { "A", "B", "C", "D" }),
            AnswerJson = JsonSerializer.Serialize(answers), Score = score
        };

    [Fact]
    public void Validates_course_fields_and_type()
    {
        Assert.Null(TeachingLogic.ValidateCourse(new CourseInput("网络基础", "core", "简介", "备注")));
        Assert.Equal("课程名称不能为空", TeachingLogic.ValidateCourse(new CourseInput(" ", "core")));
        Assert.Equal("课程类型不合法", TeachingLogic.ValidateCourse(new CourseInput("课程", "unknown")));
        Assert.Equal("课程简介不能超过 2000 个字符", TeachingLogic.ValidateCourse(new CourseInput("课程", "general", new string('x', 2001))));
    }

    [Fact]
    public void Practice_resources_need_questions_and_other_resources_reject_them()
    {
        var question = new CourseQuestionInput("single", "题干", ["A", "B"], [0]);
        Assert.Null(TeachingLogic.ValidateResource(new CourseResourceInput("练习", "theoryPractice", Questions: [question])));

        var noQuestions = TeachingLogic.ValidateResource(new CourseResourceInput("练习", "theoryPractice"));
        Assert.Equal("理论练习至少需要 1 道题目", noQuestions);

        var theoryWithQuestion = TeachingLogic.ValidateResource(new CourseResourceInput("理论", "theory", Content: "正文", Questions: [question]));
        Assert.Equal("只有理论练习可以配置题目", theoryWithQuestion);

        Assert.Equal("理论知识或案例教学资源需要填写正文", TeachingLogic.ValidateResource(new CourseResourceInput("理论", "theory")));
    }

    [Theory]
    [InlineData("single", new[] { 0 }, 10, null)]
    [InlineData("multiple", new[] { 0, 2 }, 10, null)]
    [InlineData("judge", new[] { 1 }, 5, null)]
    [InlineData("single", new[] { 0, 1 }, 10, "单选题和判断题只能设置一个正确答案")]
    [InlineData("single", new[] { 4 }, 10, "正确答案超出选项范围")]
    [InlineData("single", new[] { 0 }, 0, "题目分值应在 1–100 之间")]
    public void Validates_question_answers_and_scores(string type, int[] answers, int score, string? expected)
    {
        var options = type == "judge" ? new[] { "正确", "错误" } : new[] { "A", "B", "C", "D" };
        var error = TeachingLogic.ValidateQuestion(new CourseQuestionInput(type, "题干", options, answers, score));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Rejects_duplicate_answers()
    {
        var error = TeachingLogic.ValidateQuestion(new CourseQuestionInput("multiple", "题干", ["A", "B", "C"], [1, 1]));
        Assert.Equal("正确答案不能重复", error);
    }

    [Fact]
    public void Normalizes_selection_order_and_duplicates()
    {
        Assert.Equal([1, 3], TeachingLogic.NormalizeSelection([3, 1, 3, 1]));
        Assert.Empty(TeachingLogic.NormalizeSelection(null));
    }

    [Fact]
    public void Scores_only_exact_answer_sets()
    {
        var questions = new[]
        {
            Question("single", "single", [1], 10),
            Question("multiple", "multiple", [0, 2], 20),
            Question("judge", "judge", [1], 5)
        };
        var answers = new Dictionary<string, int[]>
        {
            ["single"] = [1],
            ["multiple"] = [2, 0, 0],
            ["judge"] = [0]
        };
        var (score, maxScore) = TeachingLogic.ScoreAttempt(questions, answers);
        Assert.Equal(30, score);
        Assert.Equal(35, maxScore);
    }

    [Fact]
    public void Missing_or_malformed_answers_do_not_award_points()
    {
        var questions = new[] { Question("single", "single", [2], 10) };
        var (score, maxScore) = TeachingLogic.ScoreAttempt(questions, new Dictionary<string, int[]>());
        Assert.Equal(0, score);
        Assert.Equal(10, maxScore);
        Assert.Empty(TeachingLogic.DeserializeAnswers("not-json"));
        Assert.Empty(TeachingLogic.DeserializeOptions("not-json"));
        Assert.Equal([0, 1], TeachingLogic.DeserializeAnswers("[0,1]"));
    }
}
