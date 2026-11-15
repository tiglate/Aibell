namespace Aibell.Mpd.Client;

/// <summary>MPD answered a command with <c>ACK [error@index] {command} message</c>.</summary>
public sealed class MpdProtocolException : Exception
{
    /// <summary>The ACK_ERROR value for a missing object ("No such song", "No such playlist").</summary>
    public const int NoExist = 50;

    /// <summary>The ACK_ERROR value for a wrong or missing password and for a missing permission.</summary>
    public const int Permission = 4;

    public MpdProtocolException() : base("MPD reported an error.")
    {
    }

    public MpdProtocolException(string message) : base(message)
    {
    }

    public MpdProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public MpdProtocolException(int code, int listIndex, string command, string message) : base(message)
    {
        Code = code;
        ListIndex = listIndex;
        Command = command;
    }

    public int Code { get; }

    public int ListIndex { get; }

    public string Command { get; } = string.Empty;

    /// <summary>Reads an <c>ACK</c> line. A line that does not follow the format still becomes an exception that carries the whole text.</summary>
    public static MpdProtocolException Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string rest = line.StartsWith("ACK", StringComparison.Ordinal) ? line[3..].TrimStart() : line;
        int close = rest.IndexOf(']', StringComparison.Ordinal);
        if (rest.StartsWith('[') && close > 0)
        {
            string[] numbers = rest[1..close].Split('@');
            string tail = rest[(close + 1)..].TrimStart();
            string command = string.Empty;
            if (tail.StartsWith('{'))
            {
                int end = tail.IndexOf('}', StringComparison.Ordinal);
                if (end > 0)
                {
                    command = tail[1..end];
                    tail = tail[(end + 1)..].TrimStart();
                }
            }
            if (numbers.Length == 2 && int.TryParse(numbers[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int code)
                && int.TryParse(numbers[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index))
                return new MpdProtocolException(code, index, command, tail);
        }
        return new MpdProtocolException(0, 0, string.Empty, rest);
    }
}
