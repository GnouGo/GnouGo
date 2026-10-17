using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TaskPlanCompiler
{
    private Bound SelectField(TaskValue value, Scope scope, bool consume)
    {
        if (value.Items.Count != 1 || string.IsNullOrWhiteSpace(value.Port) || value.Source is not null ||
            value.Text is not null || value.Number is not null || value.Boolean is not null || value.Predicate is not null || value.Members.Count != 0)
            Fail("TASK_FIELD_INVALID", "Field selection requires one business object and a nonblank literal field name.");
        var source = Value(value.Items[0], scope, consume: false);
        var schema = FieldContract(source.Schema, 0);
        // Only an unambiguous semantic location can grant producer-type repairs.
        var location = source.TypeLocation is { } declared && !value.Port!.Contains('/')
            ? declared + "/fields/" + value.Port + "/type" : null;
        // Compose explicit nested selections against the authoritative container.
        // Emit one check at consumption, including inside branches and finalizers.
        var selected = new Bound(new() { Kind = "output" }, schema,
            SelectionSource: source.SelectionSource ?? source, SelectionPath: [.. source.SelectionPath ?? [], value.Port!], TypeLocation: location);
        return consume ? Consume(selected, scope) : selected;

        JsonObject FieldContract(JsonObject contract, int depth)
        {
            if (depth > 32) Fail("TASK_FIELD_TYPE", "Field selection exceeds supported contract nesting.");
            if ((contract["anyOf"] ?? contract["oneOf"]) is JsonArray alternatives)
            {
                if (alternatives.Count == 0 || alternatives.Any(a => a is not JsonObject)) Fail("TASK_FIELD_TYPE", "Field selection requires declared object alternatives.");
                return new() { ["anyOf"] = new JsonArray(alternatives.Select(a => (JsonNode)FieldContract(a!.AsObject(), depth + 1).DeepClone()).ToArray()) };
            }
            // A nullable object still declares its fields. The checked projection fails
            // on a null container; it never supplies a default or establishes success.
            var type = contract["type"];
            var isObject = type?.ToString() == "object" || type is JsonArray types &&
                types.Any(t => t?.ToString() == "object") && types.All(t => t?.ToString() is "object" or "null");
            if (!isObject || PlanningContractShapes.IsOpaque(contract))
                Fail("TASK_FIELD_TYPE", "Field '" + value.Port + "' requires a declared object; received " +
                    (type?.ToJsonString() ?? "opaque") + " from " + Origin() +
                    ". Arrays have no field/index/length ports; use typed iteration, flatten or lookup, or declare a scalar result. Opaque values cannot supply fields.");
            if (contract["properties"] is not JsonObject fields || fields[value.Port!] is not JsonObject)
                Fail("TASK_FIELD_UNKNOWN", Origin() + " does not declare field '" + value.Port + "'. Available fields: " +
                    string.Join(", ", (contract["properties"] as JsonObject ?? []).Select(p => p.Key)) + ". Field names are literal, not paths.");
            return contract["properties"]![value.Port!]!.AsObject();
        }

        string Origin() => string.Join(", ", Values(value.Items[0]).Where(v => v.Kind is "output" or "input" or "item")
            .Select(v => v.Kind + ":" + v.Source + (v.Port is null ? "" : "." + v.Port)).DefaultIfEmpty("the explicit binding"));
    }
}
