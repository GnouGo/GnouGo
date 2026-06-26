using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GnOuGo.Flow.Core.Parsing;

/// <summary>
/// Helper extensions for working with YamlDotNet RepresentationModel (AOT-safe).
/// </summary>
public static class YamlHelpers
{
    public static string? GetScalar(this YamlMappingNode map, string key)
    {
        if (map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar)
            return scalar.Value;
        return null;
    }

    public static int? GetInt(this YamlMappingNode map, string key)
    {
        if (!TryGetScalarNode(map, key, out var scalar))
            return null;

        EnsurePlainScalar(scalar, key, "integer");
        var s = scalar.Value?.Trim();
        if (string.IsNullOrWhiteSpace(s))
            throw new WorkflowParseException($"Field '{key}' must be an unquoted integer scalar.");

        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i;

        throw new WorkflowParseException($"Field '{key}' must be an unquoted integer scalar.");
    }

    public static double? GetDouble(this YamlMappingNode map, string key)
    {
        if (!TryGetScalarNode(map, key, out var scalar))
            return null;

        EnsurePlainScalar(scalar, key, "number");
        var s = scalar.Value?.Trim();
        if (string.IsNullOrWhiteSpace(s))
            throw new WorkflowParseException($"Field '{key}' must be an unquoted number scalar.");

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;

        throw new WorkflowParseException($"Field '{key}' must be an unquoted number scalar.");
    }

    public static bool? GetBool(this YamlMappingNode map, string key)
    {
        if (!TryGetScalarNode(map, key, out var scalar))
            return null;

        EnsurePlainScalar(scalar, key, "boolean");
        var s = scalar.Value?.Trim();
        if (string.IsNullOrWhiteSpace(s))
            throw new WorkflowParseException($"Field '{key}' must be an unquoted boolean scalar.");

        return s.ToLowerInvariant() switch
        {
            "true" or "yes" => true,
            "false" or "no" => false,
            _ => throw new WorkflowParseException($"Field '{key}' must be an unquoted boolean scalar.")
        };
    }

    public static YamlMappingNode? GetMapping(this YamlMappingNode map, string key)
    {
        if (map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlMappingNode m)
            return m;
        return null;
    }

    public static YamlSequenceNode? GetSequence(this YamlMappingNode map, string key)
    {
        if (map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlSequenceNode s)
            return s;
        return null;
    }

    public static bool HasKey(this YamlMappingNode map, string key) =>
        map.Children.ContainsKey(new YamlScalarNode(key));

    public static List<string> GetStringList(this YamlMappingNode map, string key)
    {
        var seq = map.GetSequence(key);
        if (seq == null) return new List<string>();
        return seq.Children.OfType<YamlScalarNode>().Select(n => n.Value ?? "").ToList();
    }

    public static Dictionary<string, string> GetStringMap(this YamlMappingNode map, string key)
    {
        var m = map.GetMapping(key);
        if (m == null) return new();
        var dict = new Dictionary<string, string>();
        foreach (var child in m.Children)
        {
            if (child.Key is YamlScalarNode k && child.Value is YamlScalarNode v)
                dict[k.Value ?? ""] = v.Value ?? "";
        }
        return dict;
    }

    private static bool TryGetScalarNode(YamlMappingNode map, string key, out YamlScalarNode scalar)
    {
        scalar = null!;
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node))
            return false;

        if (node is YamlScalarNode scalarNode)
        {
            scalar = scalarNode;
            return true;
        }

        throw new WorkflowParseException($"Field '{key}' must be a scalar.");
    }

    private static void EnsurePlainScalar(YamlScalarNode scalar, string key, string typeName)
    {
        if (scalar.Style is ScalarStyle.Any or ScalarStyle.Plain)
            return;

        throw new WorkflowParseException($"Field '{key}' must be an unquoted {typeName} scalar.");
    }
}
