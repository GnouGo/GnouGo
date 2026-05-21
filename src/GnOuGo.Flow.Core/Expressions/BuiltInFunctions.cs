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
        ["length"] = Len, // alias
        ["lower"] = Lower,
        ["upper"] = Upper,
        ["trim"] = Trim,
        ["contains"] = Contains,
        ["startsWith"] = StartsWith,
        ["endsWith"] = EndsWith,
        ["replace"] = Replace,
        ["substring"] = Substring,
        ["toNumber"] = ToNumber,
        ["json"] = Json,
        ["pick"] = Pick,
        ["omit"] = Omit,
        ["fromJson"] = FromJson,
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

    /// <summary>
    /// substring(s, start) — returns characters from <paramref name="args"/>[1] to end.
    /// substring(s, start, length) — returns <paramref name="args"/>[2] characters starting at <paramref name="args"/>[1].
    /// </summary>
    private static JsonNode? Substring(JsonNode?[] args)
    {
        if (args.Length < 2) return args.Length > 0 ? args[0] : null;
        var s = ExpressionEvaluator.GetString(args[0]);
        var start = (int)ExpressionEvaluator.GetNumber(args[1]);
        if (start < 0) start = 0;
        if (start >= s.Length) return JsonValue.Create("");
        if (args.Length >= 3 && args[2] != null)
        {
            var length = (int)ExpressionEvaluator.GetNumber(args[2]);
            if (length <= 0) return JsonValue.Create("");
            if (start + length > s.Length) length = s.Length - start;
            return JsonValue.Create(s.Substring(start, length));
        }
        return JsonValue.Create(s[start..]);
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

    /// <summary>
    /// pick(obj, ...keys) — returns a new object containing only the requested keys.
    /// Keys can be passed as separate arguments or as arrays, e.g. pick(obj, "a", "b") or pick(obj, ["a", "b"]).
    /// </summary>
    private static JsonNode? Pick(JsonNode?[] args)
    {
        if (args.Length < 1 || args[0] is not JsonObject source) return new JsonObject();

        var result = new JsonObject();
        foreach (var key in EnumerateKeys(args.Skip(1)))
        {
            if (source.TryGetPropertyValue(key, out var value))
                result[key] = value?.DeepClone();
        }
        return result;
    }

    /// <summary>
    /// omit(obj, ...keys) — returns a new object with the requested keys removed.
    /// Keys can be passed as separate arguments or as arrays, e.g. omit(obj, "secret") or omit(obj, ["secret", "token"]).
    /// </summary>
    private static JsonNode? Omit(JsonNode?[] args)
    {
        if (args.Length < 1 || args[0] is not JsonObject source) return new JsonObject();

        var keysToOmit = EnumerateKeys(args.Skip(1)).ToHashSet(StringComparer.Ordinal);
        var result = new JsonObject();
        foreach (var kv in source)
        {
            if (!keysToOmit.Contains(kv.Key))
                result[kv.Key] = kv.Value?.DeepClone();
        }
        return result;
    }

    private static IEnumerable<string> EnumerateKeys(IEnumerable<JsonNode?> args)
    {
        foreach (var arg in args)
        {
            if (arg is null) continue;
            if (arg is JsonArray arr)
            {
                foreach (var nestedKey in EnumerateKeys(arr))
                    yield return nestedKey;
                continue;
            }
            if (arg is JsonObject) continue;

            var scalarKey = ExpressionEvaluator.GetString(arg);
            if (!string.IsNullOrEmpty(scalarKey))
                yield return scalarKey;
        }
    }

    /// <summary>
    /// fromJson(s) — parses a JSON string into a JsonNode (object, array, or value).
    /// </summary>
    private static JsonNode? FromJson(JsonNode?[] args)
    {
        if (args.Length < 1 || args[0] == null) return null;
        var s = ExpressionEvaluator.GetString(args[0]);
        if (string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            return JsonNode.Parse(s);
        }
        catch
        {
            return null;
        }
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



