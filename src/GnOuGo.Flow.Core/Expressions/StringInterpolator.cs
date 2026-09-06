using System.Text;
using System.Text.Json.Nodes;
namespace GnOuGo.Flow.Core.Expressions;
/// <summary>
/// Detects ${...} expressions in strings, evaluates them via Jint, and returns the result.
/// If the entire string is a single ${...}, returns the typed result.
/// If there are multiple or embedded, returns a string concatenation.
/// </summary>
public sealed class StringInterpolator
{
    private readonly ExpressionEvaluator _evaluator;
    public StringInterpolator(ExpressionEvaluator evaluator)
    {
        _evaluator = evaluator;
    }
    /// <summary>
    /// Check whether a string contains ${...} expressions.
    /// </summary>
    public static bool HasExpressions(string? value) =>
        value != null && value.Contains("${");
    /// <summary>
    /// Evaluates all ${...} in the given string.
    /// If the string is exactly one expression "${expr}", returns the typed JsonNode result.
    /// Otherwise returns a string with all expressions replaced.
    /// </summary>
    public JsonNode? Interpolate(string value, JsonNode? context)
    {
        var segments = ExpressionSegments.Read(value);
        if (segments.Count == 1 && string.IsNullOrWhiteSpace(value[..segments[0].Start]) && string.IsNullOrWhiteSpace(value[(segments[0].Start + segments[0].Length)..]))
            return _evaluator.Evaluate(NormalizeStringLiteralLineBreaks(segments[0].Expression), context);
        var result = new StringBuilder();
        var offset = 0;
        foreach (var segment in segments)
        {
            result.Append(value, offset, segment.Start - offset);
            var expr = NormalizeStringLiteralLineBreaks(segment.Expression);
            var val = _evaluator.Evaluate(expr, context);
            result.Append(val == null ? "" : ExpressionEvaluator.GetString(val));
            offset = segment.Start + segment.Length;
        }
        result.Append(value, offset, value.Length - offset);
        return JsonValue.Create(result.ToString());
    }

    internal static string NormalizeStringLiteralLineBreaks(string expression)
    {
        if (!expression.Contains('\n') && !expression.Contains('\r')) return expression;
        // Preserve valid JavaScript verbatim, including quotes inside comments/regex.
        // Normalization exists only for the legacy extension allowing raw quoted newlines.
        try { new Acornima.Parser().ParseExpression(expression); return expression; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        StringBuilder? builder = null;
        char quote = '\0';
        var escaped = false;

        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (quote != '\0' && (c == '\r' || c == '\n'))
            {
                builder ??= new StringBuilder(expression.Length + 8).Append(expression, 0, i);
                builder.Append(c == '\r' ? "\\r" : "\\n");
                escaped = false;
                continue;
            }

            builder?.Append(c);

            if (quote == '\0')
            {
                if (c is '\'' or '"')
                {
                    quote = c;
                    escaped = false;
                }

                continue;
            }

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == quote)
                quote = '\0';
        }

        return builder?.ToString() ?? expression;
    }

    /// <summary>
    /// Recursively resolve expressions in a JsonNode tree.
    /// </summary>
    public JsonNode? ResolveDeep(JsonNode? node, JsonNode? context)
    {
        if (node == null) return null;
        if (node is JsonValue val && val.TryGetValue(out string? s) && HasExpressions(s))
        {
            return Interpolate(s, context)?.DeepClone();
        }
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var kv in obj)
            {
                result[kv.Key] = ResolveDeep(kv.Value, context);
            }
            return result;
        }
        if (node is JsonArray arr)
        {
            var result = new JsonArray();
            foreach (var item in arr)
            {
                result.Add(ResolveDeep(item, context));
            }
            return result;
        }
        return node.DeepClone();
    }
}
