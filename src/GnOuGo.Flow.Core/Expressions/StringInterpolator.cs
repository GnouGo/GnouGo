using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace GnOuGo.Flow.Core.Expressions;
/// <summary>
/// Detects ${...} expressions in strings, evaluates them via Jint, and returns the result.
/// If the entire string is a single ${...}, returns the typed result.
/// If there are multiple or embedded, returns a string concatenation.
/// </summary>
public sealed class StringInterpolator
{
    private static readonly Regex ExprRegex = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);
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
        var trimmed = value.Trim();
        // If the entire string is a single expression, return typed result
        if (trimmed.StartsWith("${") && trimmed.EndsWith("}"))
        {
            // Verify it's a single expression (no text before/after)
            var inner = trimmed[2..^1].Trim();
            if (!inner.Contains("${"))
            {
                return _evaluator.Evaluate(NormalizeStringLiteralLineBreaks(inner), context);
            }
        }
        // Otherwise interpolate as string
        var result = ExprRegex.Replace(value, match =>
        {
            var expr = NormalizeStringLiteralLineBreaks(match.Groups[1].Value.Trim());
            var val = _evaluator.Evaluate(expr, context);
            return val == null ? "" : ExpressionEvaluator.GetString(val);
        });
        return JsonValue.Create(result);
    }

    private static string NormalizeStringLiteralLineBreaks(string expression)
    {
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
            return Interpolate(s, context);
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
