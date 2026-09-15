using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Read-only executable context with absent typed options omitted.</summary>
internal static class PlanningSemanticContext
{
    internal static JsonNode Graph(PlanningGraph graph) => Prune(PlanningModelValues.Compact(
        JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningGraph)))!;
    internal static JsonNode Executable(PlanningGraph graph)
    {
        var json = Prune(PlanningModelValues.Compact(JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningGraph)), omitDescriptions: true)!;
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = json["workflows"]![wi]!.AsObject(); var nodes = new JsonObject();
            JsonArray Index(JsonArray? sequence, string path)
            {
                var references = new JsonArray();
                if (sequence is null) return references;
                for (var index = 0; index < sequence.Count; index++)
                {
                    var node = sequence[index]!.DeepClone().AsObject(); var key = node["key"]!.ToString();
                    var location = path + "/" + index;
                    node.Remove("key"); node["location"] = location;
                    foreach (var field in new[] { "steps", "default" })
                        if (node[field] is JsonArray children) node[field] = Index(children, location + "/" + field);
                    foreach (var field in new[] { "cases", "branches" })
                        if (node[field] is JsonArray branches)
                            for (var branch = 0; branch < branches.Count; branch++)
                                if (branches[branch]?["steps"] is JsonArray children) branches[branch]!["steps"] = Index(children, location + "/" + field + "/" + branch + "/steps");
                    nodes[key] = node; references.Add((JsonNode)JsonValue.Create(key)!);
                }
                return references;
            }
            workflow["steps"] = Index(workflow["steps"] as JsonArray, "/workflows/" + wi + "/steps");
            workflow["finally"] = Index(workflow["finally"] as JsonArray, "/workflows/" + wi + "/finally");
            workflow["nodes"] = nodes;
        }
        return json;
    }

    private static JsonNode? Prune(JsonNode? node, bool omitDescriptions = false)
    {
        if (node is JsonArray array) return new JsonArray(array.Select(value => Prune(value, omitDescriptions)).ToArray());
        if (node is not JsonObject obj) return node?.DeepClone();
        var result = new JsonObject();
        foreach (var (name, value) in obj)
        {
            if (omitDescriptions && name is "purpose" or "description") continue;
            // Case values and enum members are literal data, including nulls and empty objects.
            if (name == "value" && !(value is JsonObject literal && literal["kind"] is not null) || name == "enum" && value is JsonArray { Count: > 0 })
            { result[name] = value?.DeepClone(); continue; }
            if (value is null || value is JsonArray { Count: 0 }) continue;
            result[name] = Prune(value, omitDescriptions);
        }
        return result;
    }
}
