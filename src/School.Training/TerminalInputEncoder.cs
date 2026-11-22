using System.Text;

namespace School.Training;

// One encoder per terminal stream so pasted CRLF is normalized even across frame boundaries.
public sealed class TerminalInputEncoder(string newline, string backspace)
{
    private readonly string _newline = newline switch { "CR" or "\r" => "\r", "LF" or "\n" => "\n", _ => "\r\n" };
    private readonly char _backspace = backspace is "BS" or "\b" ? '\b' : '\u007f';
    private bool _previousWasCarriageReturn;

    public string Normalize(string input)
    {
        var output = new StringBuilder(input.Length);
        foreach (var character in input)
        {
            if (character == '\r' || (character == '\n' && !_previousWasCarriageReturn))
                output.Append(_newline);
            else if (character != '\n')
                output.Append(character is '\b' or '\u007f' ? _backspace : character);

            _previousWasCarriageReturn = character == '\r';
        }
        return output.ToString();
    }
}
