namespace GnOuGo.Flow.Core.Parsing;

/// <summary>
/// Exception thrown during YAML parsing with line/column info.
/// </summary>
public sealed class WorkflowParseException : Exception
{
    public int Line { get; }
    public int Column { get; }

    public WorkflowParseException(string message, int line = 0, int column = 0, Exception? inner = null)
        : base($"[{line}:{column}] {message}", inner)
    {
        Line = line;
        Column = column;
    }
}

