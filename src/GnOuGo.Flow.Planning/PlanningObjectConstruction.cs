using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Closed synthesized objects expose each field's computation and dependencies.</summary>
internal static class PlanningObjectConstruction
{
    internal const int Version = 43;

    internal static JsonObject? Contract(PlanningNode node, PlanningPreparation preparation)
    {
        if (node.Type != "set" || node.OutputSchema is null) return null;
        var schema = PlanningGraphCompiler.ToJsonSchema(node.OutputSchema, preparation);
        return schema["type"]?.ToString() == "object" && schema["properties"] is JsonObject &&
            schema["additionalProperties"] is JsonValue flag && flag.TryGetValue<bool>(out var additional) && !additional ? schema : null;
    }

    internal static void Upgrade(JsonObject fields, JsonObject contract)
    {
        if (fields.ContainsKey("values") || fields["input"] is not JsonObject input) return;
        var values = new JsonObject();
        if (input["kind"]?.ToString() == "object" && input["members"] is JsonArray members &&
            members.All(m => m?["name"] is JsonValue) && members.Select(m => m!["name"]!.ToString()).Distinct(StringComparer.Ordinal).Count() == members.Count)
        {
            foreach (var member in members) values[member!["name"]!.ToString()] = member["value"]?.DeepClone();
            var required = (contract["required"] as JsonArray ?? []).Select(p => p!.ToString()).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, _) in contract["properties"]!.AsObject())
                if (!values.ContainsKey(key) && !required.Contains(key)) values[key] = new JsonObject { ["kind"] = "omit" };
            fields.Remove("input");
        }
        // Opaque root computations cannot establish field provenance. Keep the old
        // value visible as a rejected coordinate until the atomic patch removes it;
        // the encrypted revision retains its original implementation permanently.
        fields["values"] = values;
    }
}
