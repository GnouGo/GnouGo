using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning;

/// <summary>Lossless sharing of repeated contract objects in model context.</summary>
internal static class PlanningPromptContext
{
    internal const string Instructions = "A context with shared entries uses {$contextRef:id} to reference that exact object in shared. Expand these context references before interpreting schemas; they do not replace or change JSON Schema references or constraints. ";

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
