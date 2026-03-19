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
        var s = map.GetScalar(key);
        return s != null && int.TryParse(s, out var i) ? i : null;
    }

    public static double? GetDouble(this YamlMappingNode map, string key)
    {
        var s = map.GetScalar(key);
        return s != null && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static bool? GetBool(this YamlMappingNode map, string key)
    {
        var s = map.GetScalar(key);
        return s?.ToLowerInvariant() switch
        {
            "true" or "yes" => true,
            "false" or "no" => false,
            _ => null
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
}

