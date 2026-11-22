namespace School.Training.Tests;

public class ExamLogicTests
{
    [Theory]
    [InlineData("single", new[] { 0 }, "", null)]
    [InlineData("multiple", new[] { 0, 2 }, "", null)]
    [InlineData("judge", new[] { 1 }, "", null)]
    [InlineData("practical", new int[0], "参考答案", null)]
    [InlineData("single", new[] { 0, 1 }, "", "单选题和判断题只能设置一个正确答案")]
    [InlineData("single", new[] { 3 }, "", "正确答案超出选项范围")]
    [InlineData("multiple", new int[0], "", "请设置正确答案")]
    [InlineData("practical", new[] { 0 }, "", "实操题不需要设置选项和正确答案")]
    public void Validates_question_shapes(string type, int[] answers, string analysis, string? expected)
    {
        var options = type == "judge" ? null : type == "practical" ? null : new[] { "A", "B", "C" };
        var error = ExamLogic.ValidateQuestion(new ExamQuestionInput(type, "题干", options, answers, 10, analysis));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Judge_questions_default_to_true_false_options()
    {
        var options = ExamLogic.NormalizeOptions(new ExamQuestionInput("judge", "题干", null, [0], 5));
        Assert.Equal(["正确", "错误"], options);
    }

    [Theory]
    [InlineData("scheduled", -2, -1, "closed")]
    [InlineData("scheduled", -1, 1, "scheduled")]
    [InlineData("open", 1, 2, "scheduled")]
    [InlineData("open", 0, 1, "open")]
    [InlineData("open", -1, 0, "closed")]
    [InlineData("closed", 1, 2, "closed")]
    public void Exams_are_accessible_only_within_the_open_window(string status, int start, int end, string expected)
    {
        var now = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, ExamLogic.EffectiveStatus(status, now.AddMinutes(start), now.AddMinutes(end), now));
    }

    [Fact]
    public void Scores_objective_answers_exactly()
    {
        const string answerJson = "[1,2]";
        Assert.True(ExamLogic.IsCorrect(answerJson, [2, 1, 2]));
        Assert.False(ExamLogic.IsCorrect(answerJson, [1]));
        Assert.False(ExamLogic.IsCorrect(answerJson, [0, 1, 2]));
    }

    [Fact]
    public void Percentages_and_bands_cover_boundaries()
    {
        Assert.Equal(60, ExamLogic.Percent(60, 100));
        Assert.Equal(0, ExamLogic.Percent(5, 0));
        var bands = ExamLogic.ScoreBands([100, 80, 79.99m, 60, 59.99m, 0], expected: 8, absent: 1, pending: 1);
        Assert.Equal(new[] { "缺考", "60% 以下", "60%–不足 80%", "80%–100%", "作答中／待批阅" }, bands.Select(item => item.Label));
        Assert.Equal(new[] { 1, 2, 2, 2, 1 }, bands.Select(item => item.Count));
        Assert.Equal(new[] { 12.5m, 25m, 25m, 25m, 12.5m }, bands.Select(item => item.Percent));
    }

    [Fact]
    public void Validates_exam_window_and_duration()
    {
        var start = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        Assert.Null(ExamLogic.ValidateExam(new TrainingExamInput("期末考试", "paper", "", start, start.AddHours(2), 90)));
        Assert.Equal("结束时间必须晚于开始时间", ExamLogic.ValidateExam(new TrainingExamInput("期末考试", "paper", "", start, start, 90)));
        Assert.Equal("考试时长应在 1–600 分钟之间", ExamLogic.ValidateExam(new TrainingExamInput("期末考试", "paper", "", start, start.AddHours(2), 0)));
        Assert.Equal("请选择试卷", ExamLogic.ValidateExam(new TrainingExamInput("期末考试", " ", "", start, start.AddHours(2), 60)));
    }
}

public class ExamImportTests
{
    static List<List<string>> Sheet(params string[][] rows) => rows.Select(row => row.ToList()).ToList();

    [Fact]
    public void Parses_all_four_question_types_from_alias_headers()
    {
        var rows = ExamImport.BuildRows(Sheet(
            ["题型", "题干", "选项", "正确答案", "分值", "解析"],
            ["单选题", "题目一", "A项|B项|C项", "A", "5", "解析一"],
            ["多选题", "题目二", "甲|乙|丙", "A,C", "10", ""],
            ["判断题", "题目三", "正确|错误", "错误", "5", ""],
            ["实操题", "题目四", "", "", "20", "按要点评分"]));
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Empty(row.Errors));
        Assert.Equal("single", rows[0].Data.Type);
        Assert.Equal([0], rows[0].Data.Answers!);
        Assert.Equal("multiple", rows[1].Data.Type);
        Assert.Equal([0, 2], rows[1].Data.Answers!);
        Assert.Equal("judge", rows[2].Data.Type);
        Assert.Equal([1], rows[2].Data.Answers!);
        Assert.Equal("practical", rows[3].Data.Type);
        Assert.Empty(rows[3].Data.Options!);
    }

    [Fact]
    public void Reports_row_and_column_for_invalid_cells()
    {
        var rows = ExamImport.BuildRows(Sheet(
            ["题型", "题干", "选项", "正确答案", "分值"],
            ["未知题型", "", "只有一个选项", "Z", "999"],
            ["单选题", "题目", "A|B", "D", "5"],
            ["单选题", "题目", "A", "A", "5"]));
        Assert.Equal(4, rows[0].Errors.Count);
        Assert.Contains(rows[0].Errors, error => error.Column == "题型");
        Assert.Contains(rows[0].Errors, error => error.Column == "题干");
        Assert.Contains(rows[0].Errors, error => error.Column == "正确答案");
        Assert.Contains(rows[0].Errors, error => error.Column == "分值");
        Assert.Equal(3, rows[1].RowNumber);
        Assert.Contains(rows[1].Errors, error => error.Column == "正确答案");
        Assert.Contains(rows[2].Errors, error => error.Column == "选项");
    }

    [Fact]
    public void Rejects_sheets_without_required_headers()
    {
        var error = Assert.Throws<ImportSheetException>(() => ExamImport.BuildRows(Sheet(["姓名", "账号"], ["张三", "zhangsan"])));
        Assert.Contains("缺少题型或题干表头", error.Message);
    }

    [Fact]
    public void Template_headers_round_trip_through_import_aliases()
    {
        var sheet = ImportCsv.Parse(ExamImport.Template());
        Assert.Equal(5, sheet.Count);
        var headers = ExamImport.MapHeaders(sheet[0]);
        Assert.Contains("type", headers);
        Assert.Contains("stem", headers);
        Assert.Contains("answer", headers);
        var rows = ExamImport.BuildRows(sheet);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Empty(row.Errors));
    }

    [Fact]
    public void Skips_blank_rows_and_accepts_named_judge_answers()
    {
        var rows = ExamImport.BuildRows(Sheet(
            ["题型", "题干", "选项", "正确答案"],
            ["判断题", "题干", "", "正确"],
            ["", " ", "", ""]));
        Assert.Single(rows);
        Assert.Equal([0], rows[0].Data.Answers!);
        Assert.Equal(["正确", "错误"], rows[0].Data.Options!);
    }
}
