using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Compact typed model transport.</summary>
internal static class PlanningModelValues
{

    internal static JsonNode? Compact(JsonNode? node)
    {
        if (node is JsonArray list) return new JsonArray(list.Select(Compact).ToArray());
        if (node is not JsonObject obj) return node?.DeepClone();
        var names = obj.Select(p => p.Key).ToArray();
        if (obj["kind"] is JsonValue kind)
        {
            names = kind.GetValue<string>() switch
            {
                "null" => ["kind"],
                "string" or "expression" => ["kind", "text"],
                "number" => ["kind", "number"],
                "boolean" => ["kind", "boolean"],
                "object" => ["kind", "members"],
                "array" => ["kind", "items"],
                "input" => ["kind", "source", "path"],
                "workflow" => ["kind", "source"],
                "loop_item" or "loop_index" or "loop_previous" or "artifact_collection" => ["kind", "source", "path"],
                "decision_binding" => ["kind", "items"],
                "confirmation" => ["kind", "source", "text", "items"],
                "output" => ["kind", "source", "path", "resultChannel"],
                "template" or "compute" => ["kind", "text", "members"],
                _ => names
            };
        }
        else if (obj.ContainsKey("schemaPointer") && obj.ContainsKey("nullable"))
        {
            if (obj["capabilityId"] is not null) return new JsonObject { ["kind"] = "reference", ["capabilityId"] = obj["capabilityId"]!.DeepClone(), ["schemaPointer"] = obj["schemaPointer"]?.DeepClone() ?? JsonValue.Create("/output") };
            var inline = new JsonObject { ["kind"] = "inline" };
            foreach (var (name, value) in obj.Where(p => p.Key is not ("capabilityId" or "schemaPointer"))) inline[name] = Compact(value);
            return inline;
        }
        return new JsonObject(names.Select(name => new KeyValuePair<string, JsonNode?>(name, name == "resultChannel" ? obj[name]?.DeepClone() ?? JsonValue.Create("default") : Compact(obj[name]))));
    }
}
