namespace GnOuGo.Flow.Core.Expressions;

/// <summary>
/// Exception thrown during expression parsing.
/// </summary>
public sealed class ExpressionParseException : Exception
{
    public int Position { get; }

    public ExpressionParseException(string message, int position)
        : base(message)
    {
        Position = position;
    }
}

