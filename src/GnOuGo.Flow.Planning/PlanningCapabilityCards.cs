using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class PlanningCapabilityCards
{
    internal static JsonObject Card(PlanningCapability capability) => new()
    {
        ["id"] = capability.Id, ["name"] = capability.Method ?? capability.Id,
        ["description"] = capability.Description[..Math.Min(capability.Description.Length, 300)],
        ["arguments"] = Signature(Editable(capability)), ["result"] = Signature(capability.OutputSchema), ["effect"] = capability.EffectKind
    };
    private static JsonObject Editable(PlanningCapability capability)
    {
        var schema = (JsonObject)capability.InputSchema.DeepClone();
        foreach (var binding in capability.RequestBindings)
        {
            var segments = binding.Path.Split('/').Skip(1).Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            var parent = schema;
            foreach (var part in segments.SkipLast(1)) parent = parent["properties"]?[part] as JsonObject ?? new();
            if (segments.Length > 0 && parent["properties"] is JsonObject properties) properties.Remove(segments[^1]);
        }
        return schema;
    }
    internal static string Signature(JsonNode? schema, int depth = 0)
    {
        if (schema is not JsonObject obj || obj.Count == 0) return "unknown";
        var type = obj["type"]?.ToString() ?? "object";
        if (depth >= 3) return type;
        if (obj["properties"] is JsonObject fields)
        {
            var required = (obj["required"] as JsonArray ?? []).Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            return "{" + string.Join(",", fields.Select(f => f.Key + (required.Contains(f.Key) ? "!:" : "?:") + Signature(f.Value, depth + 1))) + "}";
        }
        if (obj["items"] is { } item) return "array<" + Signature(item, depth + 1) + ">";
        if (obj["enum"] is JsonArray options) return type + ":" + string.Join('|', options.Select(o => o?.ToString()));
        return type;
    }
}
