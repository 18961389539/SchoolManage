using Microsoft.AspNetCore.Http;
using School.Training;

namespace School.Training.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("a.b_c-d")]
    [InlineData("teacher001")]
    [InlineData("a@b.cn")]
    [InlineData("A123456789012345678901234567890123456789012345678901234567890123")] // 64 chars
    public void UserName_AcceptsAllowedShapes(string value)
        => Assert.Null(Validation.UserName(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]                 // too short
    [InlineData(".abc")]               // must start with a letter or digit
    [InlineData("张三")]                // non-ASCII
    [InlineData("has space")]
    [InlineData("a1234567890123456789012345678901234567890123456789012345678901234")] // 65 chars
    public void UserName_RejectsInvalidShapes(string? value)
        => Assert.NotNull(Validation.UserName(value));

    [Theory]
    [InlineData("abcd12345678")]       // 12 chars, letters + digits
    [InlineData("A1b2C3d4E5f6G7h8")]
    [InlineData("密码密码密码密码密码a1")] // non-ASCII letters still count as letters
    [InlineData("𝔸bcdefghij12")]        // letter outside the BMP (surrogate pair in UTF-16)
    public void Password_AcceptsPolicy(string value)
        => Assert.Null(Validation.Password(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcd1234567")]        // 11 chars
    [InlineData("abcdefghijkl")]       // letters only
    [InlineData("123456789012")]       // digits only
    public void Password_RejectsPolicyViolations(string? value)
        => Assert.Equal("密码须为12–128位，且同时包含字母和数字", Validation.Password(value));

    [Fact]
    public void Password_RejectsOverlongValue()
        => Assert.NotNull(Validation.Password(new string('a', 128) + "1"));

    [Theory]
    [InlineData(null, null, 1, 20)]
    [InlineData("", "", 1, 20)]
    [InlineData("abc", "xyz", 1, 20)]
    [InlineData("0", "0", 1, 1)]
    [InlineData("-3", "-9", 1, 1)]
    [InlineData("2", "50", 2, 50)]
    [InlineData("1000001", "5000", 1000000, 1000)]
    [InlineData("99999999999999999999", "99999999999999999999", 1, 20)]
    public void Page_ClampsToSupportedRange(string? page, string? size, int expectedPage, int expectedSize)
    {
        var (actualPage, actualSize) = Validation.Page(page, size);
        Assert.Equal(expectedPage, actualPage);
        Assert.Equal(expectedSize, actualSize);
    }

    [Fact]
    public void Page_ReadsQueryStringValues()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?page=3&pageSize=200");
        var (page, size) = Validation.Page(context.Request);
        Assert.Equal(3, page);
        Assert.Equal(200, size);
    }
}
