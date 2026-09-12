using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Exact JSON coordinates. Names and stable graph identities canonicalize diagnostics.</summary>
internal static class PlanningFieldPaths
{
    internal static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static string Token(string value) => value.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    internal static JsonObject Json(PlanningGraph graph) => JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningGraph)!.AsObject();
    internal static JsonNode? Read(JsonNode root, string path)
    {
        JsonNode? current = root;
        foreach (var token in Parts(path)) current = current switch
        {
            JsonObject obj => obj[token],
            JsonArray array when Index(token, array.Count) is { } index => array[index],
            _ => throw new InvalidOperationException("The exact field does not exist: " + path)
        };
        return current;
    }
    internal static JsonNode? ReadOptional(JsonNode root, string path)
    { try { return Read(root, path); } catch (InvalidOperationException) { return null; } }
    internal static void Replace(JsonNode root, string path, JsonNode? value)
    {
        var split = path.LastIndexOf('/');
        if (split < 0 || path.Length == 0) throw new InvalidOperationException("Root replacement is forbidden.");
        var parent = Read(root, path[..split]); var token = Token(path[(split + 1)..]);
        if (parent is JsonObject obj && obj.ContainsKey(token)) obj[token] = value?.DeepClone();
        else if (parent is JsonArray array && Index(token, array.Count) is { } index) array[index] = value?.DeepClone();
        else throw new InvalidOperationException("The exact replacement field does not exist: " + path);
    }
    internal static string Canonical(JsonNode root, string path)
    {
        if (!path.StartsWith('/')) return path;
        var canonical = ""; JsonNode? current = root;
        foreach (var token in Parts(path))
        {
            var name = token;
            if (current is JsonArray array && Index(token, array.Count) is { } index)
            {
                current = array[index];
                if (current is JsonObject item && (item["key"] ?? item["name"]) is JsonValue identity && identity.TryGetValue<string>(out var id)) name = "@" + id;
                else if (current is JsonObject outcome && outcome["value"] is JsonValue label && label.TryGetValue<string>(out var outcomeId)) name = "@" + outcomeId;
            }
            else current = current is JsonObject obj ? obj[token] : null;
            canonical += "/" + Escape(name);
        }
        return canonical;
    }
    internal static string DiagnosticId(PlanningDiagnostic diagnostic, JsonNode? graph = null) => diagnostic.Code + "|" +
        (graph is null ? diagnostic.Location : Canonical(graph, diagnostic.Location)) + "|" + diagnostic.Rule;
    private static IEnumerable<string> Parts(string path)
    {
        if (path.Length == 0) yield break;
        if (!path.StartsWith('/')) throw new InvalidOperationException("An absolute field pointer is required.");
        foreach (var token in path.Split('/').Skip(1))
        {
            for (var i = 0; i < token.Length; i++) if (token[i] == '~' && (++i == token.Length || token[i] is not ('0' or '1'))) throw new InvalidOperationException("Invalid pointer escape.");
            yield return Token(token);
        }
    }
    private static int? Index(string token, int count) => int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
        token == index.ToString(CultureInfo.InvariantCulture) && index >= 0 && index < count ? index : null;
}
