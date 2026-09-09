using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>Loop state exists during the condition/body, not while resolving initial inputs.</summary>
internal static class PlanningLoopBindings
{
    internal static string ConditionScope(string node) => PlanningConstruction.ValueScope(node) + "condition_";

    internal static void Constrain(PlanningNode node, JsonObject fields, JsonObject definitions, IEnumerable<PlanningBinding> bindings)
    {
        if (node.Type is not ("loop.sequential" or "loop.parallel") || fields["input"] is null) return;
        var prefix = PlanningConstruction.ValueScope(node.Key); var condition = ConditionScope(node.Key);
        foreach (var name in new[] { "value", "member" })
        {
            var copy = definitions[prefix + name]!.DeepClone();
            Rewrite(copy); definitions[condition + name] = copy;
        }
        var initial = bindings.Where(b => b.Value.Source != node.Key || b.Value.Kind is not ("loop_previous" or "loop_index" or "loop_item")).Select(b => b.Id).ToArray();
        var variants = definitions[prefix + "value"]!["anyOf"]!.AsArray();
        foreach (var variant in variants.OfType<JsonObject>().Where(v => v["properties"]?["kind"]?["enum"]?[0]?.ToString() == "binding").ToArray()) variants.Remove(variant);
        if (initial.Length > 0) variants.Add((JsonNode)PlanningDataflow.BindingSchema(initial));
        var members = new JsonArray();
        foreach (var (name, _) in BuiltInStepContracts.Get(node.Type)!.InputSchema["properties"]!.AsObject())
            members.Add((JsonNode)Object(new()
            {
                ["name"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(name) },
                ["value"] = new JsonObject { ["$ref"] = "#/$defs/" + (name == "while" ? condition : prefix) + "value" }
            }));
        fields["input"] = Object(new()
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("object") },
            ["members"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["anyOf"] = members } }
        });

        void Rewrite(JsonNode? value)
        {
            if (value is JsonObject obj)
            {
                if (obj["$ref"]?.ToString() is { } reference && reference.StartsWith("#/$defs/" + prefix, StringComparison.Ordinal))
                    obj["$ref"] = "#/$defs/" + condition + reference[("#/$defs/" + prefix).Length..];
                foreach (var child in obj.Select(p => p.Value)) Rewrite(child);
            }
            else if (value is JsonArray array) foreach (var child in array) Rewrite(child);
        }
    }

    private static JsonObject Object(JsonObject properties) => new() { ["type"] = "object", ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()), ["additionalProperties"] = false };
}
