using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Lossless sharing of repeated contract objects in model context.</summary>
internal static class PlanningPromptContext
{
    internal const string Instructions = "A context with shared entries uses {$contextRef:id} to reference that exact object in shared. Expand these context references before interpreting schemas; they do not replace or change JSON Schema references or constraints. ";
    internal const string ResultBindings = "An output value's source is an exact node key, never its output alias. " +
        "switch and sequence results are maps keyed by child node keys; select the executed child's result with an explicit compute binding, accounting for absent branches. " +
        "A switch never returns its matched label. When returning a business value, produce that value in each case and default (for example with typed set nodes); empty cases produce no business value and an empty default returns null. " +
        "Loop results are {count,results}, where each results item is a map keyed by child node keys; a workflow.call entry contains its outputs object. " +
        "A direct typed output reference to workflow.call already selects its outputs object: use path:[portName], never path:[outputs,portName]. " +
        "Leave outputSchema null on workflow.call and control-flow nodes; their result contracts are derived from their callees and children. ";

    internal static JsonObject Callees(PlanningSnapshot state, string owner) => new(
        state.Construction.Workflows.Single(w => w.WorkflowKey == owner).Dependencies.Order(StringComparer.Ordinal).Select(key =>
        {
            var workflow = state.Graph!.Workflows.Single(w => w.Key == key);
            JsonObject Schemas(IEnumerable<(string Name, PlanningSchema Schema)> ports) => new(ports.Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Name, PlanningGraphCompiler.ToJsonSchema(p.Schema, state.Preparation!))));
            return new KeyValuePair<string, JsonNode?>(key, new JsonObject
            {
                ["inputs"] = Schemas(workflow.Inputs.Select(p => (p.Name, p.Schema))),
                ["requiredInputs"] = new JsonArray(workflow.Inputs.Where(p => p.Required).Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray()),
                ["outputs"] = Schemas(workflow.Outputs.Select(p => (p.Name, p.Schema)))
            });
        }));

    internal static JsonNode Share(JsonNode context)
    {
        var original = context.ToJsonString(); var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var reserved = false;
        void Count(JsonNode? value)
        {
            if (value is JsonObject obj)
            {
                if (obj.ContainsKey("$contextRef")) reserved = true;
                var json = obj.ToJsonString();
                if (json.Length >= 384) counts[json] = counts.GetValueOrDefault(json) + 1;
                foreach (var child in obj) Count(child.Value);
            }
            else if (value is JsonArray array) foreach (var child in array) Count(child);
        }
        Count(context);
        if (reserved || !counts.Any(p => p.Value > 1)) return context.DeepClone();
        var references = new Dictionary<string, string>(StringComparer.Ordinal); var shared = new JsonObject();
        JsonNode? Render(JsonNode? value, bool inline = false)
        {
            if (value is JsonObject obj)
            {
                var json = obj.ToJsonString();
                if (!inline && counts.GetValueOrDefault(json) > 1)
                {
                    if (!references.TryGetValue(json, out var id))
                    {
                        id = "c" + references.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        references[json] = id; shared[id] = Render(obj, inline: true);
                    }
                    return new JsonObject { ["$contextRef"] = id };
                }
                return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Render(p.Value))));
            }
            return value is JsonArray array ? new JsonArray(array.Select(v => Render(v)).ToArray()) : value?.DeepClone();
        }
        var result = new JsonObject { ["context"] = Render(context), ["shared"] = shared };
        return result.ToJsonString().Length + Instructions.Length < original.Length ? result : context.DeepClone();
    }
}
