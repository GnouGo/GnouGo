using System.Text;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Expressions;

/// <summary>
/// Built-in functions available in expressions.
/// All are allow-listed, no IO access.
/// </summary>
public static class BuiltInFunctions
{
    public static readonly Dictionary<string, Func<JsonNode?[], JsonNode?>> All = new()
    {
        ["exists"] = Exists,
        ["coalesce"] = Coalesce,
        ["len"] = Len,
        ["lower"] = Lower,
        ["upper"] = Upper,
        ["trim"] = Trim,
        ["contains"] = Contains,
        ["startsWith"] = StartsWith,
        ["endsWith"] = EndsWith,
        ["replace"] = Replace,
        ["toNumber"] = ToNumber,
        ["json"] = Json,
        ["now"] = Now,
        ["formatDate"] = FormatDate,
        ["base64"] = Base64,
    };

    private static JsonNode? Exists(JsonNode?[] args)
    {
        if (args.Length < 1) return JsonValue.Create(false);
        return JsonValue.Create(args[0] != null);
    }

    private static JsonNode? Coalesce(JsonNode?[] args)
    {
        foreach (var arg in args)
        {
            if (arg != null) return arg.DeepClone();
        }
        return null;
    }

    private static JsonNode? Len(JsonNode?[] args)
    {
        if (args.Length < 1 || args[0] == null) return JsonValue.Create(0);
        var val = args[0];
        if (val is JsonArray arr) return JsonValue.Create(arr.Count);
        if (val is JsonValue jv && jv.TryGetValue(out string? s))
            return JsonValue.Create(s?.Length ?? 0);
        return JsonValue.Create(0);
    }

    private static JsonNode? Lower(JsonNode?[] args)
    {
        if (args.Length < 1) return null;
        return JsonValue.Create(ExpressionEvaluator.GetString(args[0]).ToLowerInvariant());
    }

    private static JsonNode? Upper(JsonNode?[] args)
    {
        if (args.Length < 1) return null;
        return JsonValue.Create(ExpressionEvaluator.GetString(args[0]).ToUpperInvariant());
    }

    private static JsonNode? Trim(JsonNode?[] args)
    {
        if (args.Length < 1) return null;
        return JsonValue.Create(ExpressionEvaluator.GetString(args[0]).Trim());
    }

    private static JsonNode? Contains(JsonNode?[] args)
    {
        if (args.Length < 2) return JsonValue.Create(false);
        var s = ExpressionEvaluator.GetString(args[0]);
        var sub = ExpressionEvaluator.GetString(args[1]);
        return JsonValue.Create(s.Contains(sub, StringComparison.Ordinal));
    }

    private static JsonNode? StartsWith(JsonNode?[] args)
    {
        if (args.Length < 2) return JsonValue.Create(false);
        var s = ExpressionEvaluator.GetString(args[0]);
        var prefix = ExpressionEvaluator.GetString(args[1]);
        return JsonValue.Create(s.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static JsonNode? EndsWith(JsonNode?[] args)
    {
        if (args.Length < 2) return JsonValue.Create(false);
        var s = ExpressionEvaluator.GetString(args[0]);
        var suffix = ExpressionEvaluator.GetString(args[1]);
        return JsonValue.Create(s.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static JsonNode? Replace(JsonNode?[] args)
    {
        if (args.Length < 3) return args.Length > 0 ? args[0] : null;
        var s = ExpressionEvaluator.GetString(args[0]);
        var old = ExpressionEvaluator.GetString(args[1]);
        var @new = ExpressionEvaluator.GetString(args[2]);
        return JsonValue.Create(s.Replace(old, @new, StringComparison.Ordinal));
    }

    private static JsonNode? ToNumber(JsonNode?[] args)
    {
        if (args.Length < 1 || args[0] == null) return JsonValue.Create(0.0);
        return JsonValue.Create(ExpressionEvaluator.GetNumber(args[0]));
    }

    private static JsonNode? Json(JsonNode?[] args)
    {
        if (args.Length < 1 || args[0] == null) return JsonValue.Create("null");
        return JsonValue.Create(args[0]!.ToJsonString());
    }

    private static JsonNode? Now(JsonNode?[] args)
    {
        return JsonValue.Create(DateTimeOffset.Now.ToString("O"));
    }

    private static JsonNode? FormatDate(JsonNode?[] args)
    {
        if (args.Length < 1 || args[0] == null) return null;
        var s = ExpressionEvaluator.GetString(args[0]!);
        var fmt = args.Length > 1 && args[1] != null ? ExpressionEvaluator.GetString(args[1]!) : "yyyy-MM-dd";
        if (DateTimeOffset.TryParse(s, out var dto))
            return JsonValue.Create(dto.ToString(fmt));
        if (double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var ts))
            return JsonValue.Create(DateTimeOffset.FromUnixTimeMilliseconds((long)ts).ToString(fmt));
        return JsonValue.Create(s);
    }

    private static JsonNode? Base64(JsonNode?[] args)
    {
        if (args.Length < 1 || args[0] == null) return null;
        var s = ExpressionEvaluator.GetString(args[0]);
        return JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes(s)));
    }
}



