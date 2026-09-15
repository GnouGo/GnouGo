using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Encodings.Web;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Lossless sharing of repeated contract objects in model context.</summary>
internal static class PlanningPromptContext
{
    // Model text is not HTML. Escaping Unicode and apostrophes adds tokens without changing the contract.
    private static readonly JsonSerializerOptions TextOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = PlanningJsonContext.Default };
    internal static string Json(JsonNode value) => value.ToJsonString(TextOptions);
    internal const string Instructions = "A context with shared entries uses {$contextRef:id} to reference that exact value in shared. Expand these context references before interpreting schemas; they do not replace or change JSON Schema references or constraints. ";
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
        var original = Json(context); var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var reserved = false;
        void Count(JsonNode? value)
        {
            if (value is JsonObject obj)
            {
                if (obj.ContainsKey("$contextRef")) reserved = true;
                var json = Json(obj);
                if (json.Length >= 128) counts[json] = counts.GetValueOrDefault(json) + 1;
                foreach (var child in obj) Count(child.Value);
            }
            else if (value is JsonArray array) foreach (var child in array) Count(child);
            else if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length >= 32)
            { var json = Json(scalar); counts[json] = counts.GetValueOrDefault(json) + 1; }
        }
        Count(context);
        foreach (var pair in counts.Where(p => (p.Value - 1L) * p.Key.Length <= p.Value * 24L + 16).ToArray()) counts.Remove(pair.Key);
        if (reserved || !counts.Any(p => p.Value > 1)) return context.DeepClone();
        var references = new Dictionary<string, string>(StringComparer.Ordinal); var shared = new JsonObject();
        JsonNode? Render(JsonNode? value, bool inline = false)
        {
            if (value is JsonObject obj)
            {
                var json = Json(obj);
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
            if (value is JsonValue scalar && counts.GetValueOrDefault(Json(scalar)) > 1)
            {
                var json = Json(scalar);
                if (!references.TryGetValue(json, out var id))
                { id = "c" + references.Count.ToString(System.Globalization.CultureInfo.InvariantCulture); references[json] = id; shared[id] = scalar.DeepClone(); }
                return new JsonObject { ["$contextRef"] = id };
            }
            return value is JsonArray array ? new JsonArray(array.Select(v => Render(v)).ToArray()) : value?.DeepClone();
        }
        var result = new JsonObject { ["context"] = Render(context), ["shared"] = shared };
        return Json(result).Length + Instructions.Length < original.Length ? result : context.DeepClone();
    }
}
