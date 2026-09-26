using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning;

internal static class PlanningJsonTransport
{
    // This is a view of the existing DTO, not a second plan. Drop only irrelevant
    // representation defaults; round-tripping must preserve every semantic slot.
    internal static JsonNode? TaskPlanPrompt(TaskPlan? plan)
    {
        if (plan is null) return null;
        var node = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.TaskPlan)!;
        Trim(node);
        return node;
        static void Trim(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var child in array) Trim(child); return; }
            if (node is not JsonObject obj) return;
            var kind = obj["kind"]?.ToString();
            string[]? fields = null; JsonObject? defaults = null;
            if (obj.ContainsKey("id") && kind is not null)
            {
                fields = kind switch
                {
                    "operation" => ["operation", "inputs"], "value" => ["outputs"], "transform" => ["inputs", "resultType"],
                    "sequence" => ["body"], "conditional" => ["condition", "body", "otherwise"],
                    "parallel" => ["branches", "maxConcurrency"], "foreach" => ["items", "body", "parallel", "maxItems", "maxConcurrency"],
                    "call" => ["group", "inputs"], _ => null
                };
                if (fields is not null) fields = ["id", "kind", "objective", "dependsOn", ..fields];
                defaults = JsonSerializer.SerializeToNode(new PlanTask(), PlanningJsonContext.Default.PlanTask)!.AsObject();
            }
            else if (obj.ContainsKey("members") && kind is not null)
            {
                fields = kind switch
                {
                    "null" or "item" or "index" => [], "string" => ["text"], "number" => ["number"], "boolean" => ["boolean"],
                    "object" => ["members"], "array" or "json" => ["items"], "input" or "choice" or "present" => ["source"],
                    "output" => ["source", "port"], "predicate" => ["predicate", "items"], _ => null
                };
                if (fields is not null) fields = ["kind", ..fields];
                defaults = JsonSerializer.SerializeToNode(new TaskValue(), PlanningJsonContext.Default.TaskValue)!.AsObject();
            }
            else if (obj.ContainsKey("fields") && kind is not null)
            {
                fields = kind switch { "array" => ["kind", "nullable", "items"], "object" => ["kind", "nullable", "fields"],
                    "string" => ["kind", "nullable", "enum"], "number" or "integer" or "boolean" or "any" => ["kind", "nullable"], _ => null };
                defaults = JsonSerializer.SerializeToNode(new TaskType(), PlanningJsonContext.Default.TaskType)!.AsObject();
            }
            if (fields is not null && defaults is not null)
                foreach (var (key, value) in obj.ToArray())
                    if (!fields.Contains(key, StringComparer.Ordinal) && defaults.ContainsKey(key) && JsonNode.DeepEquals(value, defaults[key])) obj.Remove(key);
            foreach (var child in obj.Select(p => p.Value)) Trim(child);
        }
    }

    // Prompt JSON is model input, never HTML. Literal Unicode avoids expanding business text into escape sequences.
    internal static string Prompt(JsonNode value) => value.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    internal static JsonArray Diagnostics(IEnumerable<PlanningDiagnostic> diagnostics) => new(diagnostics
        .GroupBy(d => (d.Code, d.Message, d.Required, d.ValidationStage, d.Rule,
            Prerequisite: d.Prerequisite is null ? null : JsonSerializer.Serialize(d.Prerequisite, PlanningJsonContext.Default.PlanningPrerequisiteContext))).Select(group =>
        {
            var item = new JsonObject { ["code"] = group.Key.Code, ["message"] = group.Key.Message,
                ["locations"] = new JsonArray(group.Select(d => d.Location).Distinct(StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["required"] = group.Key.Required };
            if (group.Key.ValidationStage is not null) item["validationStage"] = group.Key.ValidationStage;
            if (group.Key.Rule is not null) item["rule"] = group.Key.Rule;
            if (group.Key.Prerequisite is not null) item["prerequisite"] = JsonNode.Parse(group.Key.Prerequisite);
            return (JsonNode)item;
        }).ToArray());
    internal static PlanningValue Literal(JsonNode? json) => json switch
    {
        null => new(),
        JsonObject obj => new() { Kind = "object", Members = obj.Select(p => new PlanningMember(p.Key, Literal(p.Value))).ToList() },
        JsonArray array => new() { Kind = "array", Items = array.Select(Literal).ToList() },
        JsonValue value when value.TryGetValue<string>(out var text) => new() { Kind = "string", Text = text },
        JsonValue value when value.TryGetValue<bool>(out var boolean) => new() { Kind = "boolean", Boolean = boolean },
        JsonValue value => new() { Kind = "number", Number = decimal.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) },
        _ => throw new InvalidOperationException("Unsupported literal.")
    };

    public static int EstimateInputTokens(string prompt, JsonObject schema) => checked((Encoding.UTF8.GetByteCount(prompt) + Encoding.UTF8.GetByteCount(schema.ToJsonString()) + 2) / 3 + 256);
}
