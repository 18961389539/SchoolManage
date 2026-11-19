using School.Training;

namespace School.Training.Tests;

public sealed class ImportCsvTests
{
    [Fact]
    public void Parse_KeepsQuotedCommasAndEscapedQuotes()
    {
        var rows = ImportCsv.Parse("姓名,备注\r\n张三,\"a,b\"\r\n李四,\"他说\"\"你好\"\"\"");

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "姓名", "备注" }, rows[0]);
        Assert.Equal("a,b", rows[1][1]);
        Assert.Equal("他说\"你好\"", rows[2][1]);
    }

    [Fact]
    public void Parse_KeepsLineBreaksInsideQuotedField()
    {
        var rows = ImportCsv.Parse("a,b\n\"第一行\n第二行\",c");

        Assert.Equal(2, rows.Count);
        Assert.Equal("第一行\n第二行", rows[1][0]);
        Assert.Equal("c", rows[1][1]);
    }

    [Theory]
    [InlineData("a,b\n1,2\n")]
    [InlineData("a,b\r\n1,2\r\n")]
    [InlineData("a,b\r1,2\r")]
    public void Parse_AcceptsCommonLineEndings(string text)
    {
        var rows = ImportCsv.Parse(text);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "1", "2" }, rows[1]);
    }

    [Fact]
    public void Parse_DoesNotEmitTrailingEmptyRow()
        => Assert.Single(ImportCsv.Parse("a,b\n"));

    [Fact]
    public void Parse_KeepsEmptyCells()
    {
        var rows = ImportCsv.Parse("a,b,c\n1,,3");
        Assert.Equal(new[] { "1", "", "3" }, rows[1]);
    }

    [Fact]
    public void Parse_RejectsUnclosedQuote()
        => Assert.Throws<FormatException>(() => ImportCsv.Parse("a,b\n\"unterminated,2"));

    [Fact]
    public void Parse_RejectsSheetsLargerThanTheImportLimit()
    {
        var text = new string('\n', ImportCsv.MaxSheetRows + 1);
        Assert.Throws<FormatException>(() => ImportCsv.Parse(text));
    }

    [Fact]
    public void BuildRows_MapsChineseAliasesAndNormalizesValues()
    {
        var sheet = ImportCsv.Parse("\uFEFF姓名,账号,学号,班级,性别,手机号,电子邮箱\r\n 张三 ,ZhangSan ,2026001,计算机1班,男,13800000000,a@b.cn");

        var row = Assert.Single(ImportCsv.BuildRows(sheet));

        Assert.Equal(2, row.RowNumber);
        Assert.Equal("张三", row.Data.Name);
        Assert.Equal("zhangsan", row.Data.UserName);
        Assert.Equal("2026001", row.Data.Number);
        Assert.Equal("计算机1班", row.Data.ClassName);
        Assert.Equal("男", row.Data.Gender);
        Assert.Equal("13800000000", row.Data.Phone);
        Assert.Equal("a@b.cn", row.Data.Email);
        Assert.Empty(row.Errors);
    }

    [Fact]
    public void BuildRows_AcceptsEnglishHeaders()
    {
        var sheet = ImportCsv.Parse("name,userName,number,className\r\nLi Si,LiSi001,2026002,Class A");

        var row = Assert.Single(ImportCsv.BuildRows(sheet));

        Assert.Equal("Li Si", row.Data.Name);
        Assert.Equal("lisi001", row.Data.UserName);
        Assert.Equal("Class A", row.Data.ClassName);
        Assert.Equal("", row.Data.Phone);
    }

    [Fact]
    public void BuildRows_SkipsBlankRowsAndKeepsSpreadsheetRowNumbers()
    {
        var sheet = ImportCsv.Parse("姓名,账号,学号\r\n,,\r\n张三,zhangsan,2026001");

        var row = Assert.Single(ImportCsv.BuildRows(sheet));

        Assert.Equal(3, row.RowNumber);
    }

    [Fact]
    public void BuildRows_FillsMissingTrailingCellsWithEmptyStrings()
    {
        var sheet = ImportCsv.Parse("姓名,账号,学号,班级\r\n张三,zhangsan,2026001");

        var row = Assert.Single(ImportCsv.BuildRows(sheet));

        Assert.Equal("", row.Data.ClassName);
        Assert.Equal("", row.Data.Gender);
    }

    [Fact]
    public void BuildRows_RejectsMissingRequiredHeader()
    {
        var sheet = ImportCsv.Parse("姓名,班级\r\n张三,计算机1班");

        var error = Assert.Throws<ImportSheetException>(() => ImportCsv.BuildRows(sheet));
        Assert.Contains("缺少姓名、账号、学号/工号表头", error.Message);
    }

    [Fact]
    public void BuildRows_RejectsDuplicateColumns()
    {
        var sheet = ImportCsv.Parse("姓名,账号,学号,姓名\r\n张三,zhangsan,2026001,张三");

        var error = Assert.Throws<ImportSheetException>(() => ImportCsv.BuildRows(sheet));
        Assert.Contains("表头包含重复列", error.Message);
    }

    [Fact]
    public void BuildRows_RejectsHeaderWithoutData()
    {
        var sheet = ImportCsv.Parse("姓名,账号,学号");

        var error = Assert.Throws<ImportSheetException>(() => ImportCsv.BuildRows(sheet));
        Assert.Contains("文件没有可导入的数据", error.Message);
    }

    [Fact]
    public void BuildRows_RejectsEmptySheet()
        => Assert.Throws<ImportSheetException>(() => ImportCsv.BuildRows([]));
}
