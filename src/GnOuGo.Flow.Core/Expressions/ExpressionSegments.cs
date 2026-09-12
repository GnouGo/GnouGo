using Acornima;

namespace GnOuGo.Flow.Core.Expressions;

/// <summary>Shared interpolation boundaries, using the JavaScript lexer for strings, comments and nested braces.</summary>
public static class ExpressionSegments
{
    public sealed record Segment(int Start, int Length, string Expression);

    public static IReadOnlyList<Segment> Read(string value)
    {
        var result = new List<Segment>();
        var offset = 0;
        while (value.IndexOf("${", offset, StringComparison.Ordinal) is var start && start >= 0)
        {
            var bodyStart = start + 2;
            var source = value[bodyStart..];
            int end;
            try
            {
                try { end = End(source); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { end = End(BoundarySource(source)); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw new ExpressionParseException("Invalid interpolation: " + ex.Message, start); }
            if (end >= 0) end += bodyStart;
            if (end < 0) throw new ExpressionParseException("Unterminated interpolation expression.", start);
            result.Add(new(start, end - start, value[bodyStart..(end - 1)].Trim()));
            offset = end;
        }
        return result;
    }

    private static int End(string source)
    {
        var tokenizer = new Tokenizer(source);
        var depth = 0;
        while (true)
        {
            var token = tokenizer.GetToken();
            if (token.Kind == TokenKind.EOF) return -1;
            if (token.Kind != TokenKind.Punctuator) continue;
            if (token.Value is "{" or "${") depth++;
            else if (token.Value is "}")
            {
                if (depth == 0) return token.End;
                depth--;
            }
        }
    }

    private static string BoundarySource(string source)
    {
        // The runtime permits literal line breaks in quoted strings. Preserve offsets
        // while lexing; actual evaluation uses the existing escaped normalization.
        var chars = source.ToCharArray();
        char quote = '\0'; var escaped = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (quote == '\0') { if (c is '\'' or '"') quote = c; continue; }
            if (c is '\r' or '\n') chars[i] = ' ';
            if (escaped) { escaped = false; continue; }
            if (c == '\\') escaped = true;
            else if (c == quote) quote = '\0';
        }
        return new string(chars);
    }
}
