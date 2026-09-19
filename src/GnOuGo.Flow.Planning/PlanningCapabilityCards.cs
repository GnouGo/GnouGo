using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCapabilityCards
{
    internal static IReadOnlyList<PlanningCapability> Shortlist(PlanningCatalog catalog, string query, int maxInputTokens)
    {
        var result = new List<PlanningCapability>(); var bytes = 0; var allowance = Math.Max(0, maxInputTokens / 2) * 3;
        foreach (var capability in Rank(catalog.Capabilities, query).Take(24))
        {
            var size = Encoding.UTF8.GetByteCount(Card(capability).ToJsonString()) + 1;
            if (bytes + size > allowance) break;
            result.Add(capability); bytes += size;
        }
        return result;
    }
    internal static IEnumerable<PlanningCapability> Rank(IEnumerable<PlanningCapability> capabilities, string query)
    {
        var terms = Words(query); var documents = capabilities.Select(c => (Capability: c, Words: Words(c.Method + " " + c.Description + " " + c.Metadata?.ToJsonString()))).ToArray();
        var frequencies = terms.ToDictionary(t => t, t => documents.Count(d => d.Words.Contains(t)), StringComparer.Ordinal);
        return documents.OrderByDescending(d => terms.Where(d.Words.Contains).Sum(t => Math.Log(1 + (documents.Length + 1.0) / (frequencies[t] + 1))))
            .ThenBy(d => d.Capability.Id, StringComparer.Ordinal).Select(d => d.Capability);
    }
    private static HashSet<string> Words(string text) => Tokens().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)] private static partial Regex Tokens();
    internal static JsonObject Card(PlanningCapability capability)
    {
        var input = capability.InputSchema.DeepClone().AsObject();
        foreach (var binding in capability.RequestBindings)
        {
            var parts = binding.Path.Split('/').Skip(1).Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            JsonNode? parent = input;
            for (var i = 0; i < parts.Length - 1; i++) parent = parent?["properties"]?[parts[i]];
            if (parts.Length > 0 && parent?["properties"] is JsonObject fields) fields.Remove(parts[^1]);
        }
        return new() { ["id"] = capability.Id, ["name"] = capability.Method, ["description"] = capability.Description.Length > 300 ? capability.Description[..300] : capability.Description,
            ["arguments"] = Signature(input), ["result"] = Signature(capability.OutputSchema), ["effect"] = capability.EffectKind };
    }
    private static string Signature(JsonObject schema, int depth = 0)
    {
        var type = schema["type"]?.ToString() ?? "unknown";
        if (depth > 3) return type;
        if (schema["properties"] is JsonObject properties)
        {
            var required = (schema["required"] as JsonArray ?? []).Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            return "{" + string.Join(",", properties.Select(p => p.Key + (required.Contains(p.Key) ? "!:" : "?:") + (p.Value is JsonObject child ? Signature(child, depth + 1) : "unknown"))) + "}";
        }
        if (schema["items"] is JsonObject items) return "[" + Signature(items, depth + 1) + "]";
        return schema["enum"] is JsonArray values ? type + "=" + values.ToJsonString() : type;
    }
}
