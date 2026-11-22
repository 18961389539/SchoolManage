using School.Training;

namespace School.Training.Tests;

public sealed class TerminalInputEncoderTests
{
    [Theory]
    [InlineData("CR", "\r")]
    [InlineData("LF", "\n")]
    [InlineData("CRLF", "\r\n")]
    public void NormalizesLineEndingsWithoutDuplicatingSplitCrLf(string configured, string expected)
    {
        var encoder = new TerminalInputEncoder(configured, "DEL");
        Assert.Equal("one" + expected, encoder.Normalize("one\r"));
        Assert.Equal("two" + expected + "three" + expected, encoder.Normalize("\ntwo\nthree\r\n"));
        Assert.Equal(expected + expected, encoder.Normalize("\r\r"));
    }

    [Theory]
    [InlineData("BS", '\b')]
    [InlineData("DEL", '\u007f')]
    public void NormalizesBackspaceAndDeleteCharactersAndPreservesEscapeSequences(string configured, char expected)
    {
        var encoder = new TerminalInputEncoder("CRLF", configured);
        Assert.Equal($"输入{expected}{expected}\u001b[3~\u001b[A", encoder.Normalize("输入\b\u007f\u001b[3~\u001b[A"));
    }
}
