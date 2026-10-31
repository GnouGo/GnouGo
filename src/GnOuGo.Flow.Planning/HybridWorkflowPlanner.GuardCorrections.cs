using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{

    private static bool SameValue(TaskValue? left, TaskValue? right) => JsonNode.DeepEquals(
        System.Text.Json.JsonSerializer.SerializeToNode(left, PlanningJsonContext.Default.TaskValue),
        System.Text.Json.JsonSerializer.SerializeToNode(right, PlanningJsonContext.Default.TaskValue));

    private static Dictionary<string, List<TaskPlanCompiler.RepairSource>> GuardSources(TaskPlan baseline, PlanningCatalog catalog, TaskPlan? candidate = null, PlanningRequest? request = null)
    {
        var references = new Dictionary<string, List<TaskValue>>(StringComparer.Ordinal);
        foreach (var plan in candidate is null ? new[] { baseline } : new[] { baseline, candidate })
            foreach (var task in TaskPlanRevisions.Tasks(plan).Where(t => t.Requires is not null))
            {
                var path = "/tasks/" + task.Id + "/requires";
                if (!references.TryGetValue(path, out var values)) references[path] = values = [];
                values.AddRange(TaskPlanCompiler.Values(task.Requires!).Where(v => GuardProducer(v) is not null));
            }
        // The compiler resolves visibility, fields, references and nullability. This
        // optional inspection does not change repair presentation or old requests.
        return TaskPlanCompiler.RepairSources(baseline, catalog, references, request);
    }

    private static string? GuardProducer(TaskValue value) => value.Kind switch
    {
        "output" when value.Source is not null => value.Source,
        "field" when value.Items.Count == 1 => GuardProducer(value.Items[0]),
        _ => null
    };

    private static bool NullableGuardReference(TaskValue value, List<TaskPlanCompiler.RepairSource> sources)
    {
        if (GuardProducer(value) is null) return false;
        var schema = sources.FirstOrDefault(s => SameValue(s.Value, value))?.Schema;
        if (schema is null || PlanningContractShapes.IsOpaque(schema) ||
            new[] { "$ref", "anyOf", "oneOf", "allOf", "const", "enum", "not", "if" }.Any(schema.ContainsKey)) return false;
        var types = PlanningContractCompatibility.Types(schema);
        return types.Length == 2 && types.Contains("null", StringComparer.Ordinal) &&
            types.Any(t => t is "string" or "object" or "array" or "number" or "integer");
    }

    private static bool NullableGuardReplacement(TaskValue original, TaskValue replacement, List<TaskPlanCompiler.RepairSource> sources)
    {
        var checks = 0; var flags = new List<TaskValue>();
        return Visit(replacement) && checks == 1;
        bool Visit(TaskValue value)
        {
            if (value.Kind == "predicate" && value.Predicate == "and" && value.Items.Count == 2)
                return Visit(value.Items[0]) && Visit(value.Items[1]);
            if (value.Kind == "predicate" && value.Predicate == "not_equal" && value.Items.Count == 2 &&
                SameValue(value.Items[0], original) && SameValue(value.Items[1], new() { Kind = "null" }))
            { checks++; return true; }
            if (GuardProducer(value) != GuardProducer(original) || flags.Any(f => SameValue(f, value))) return false;
            var schema = sources.FirstOrDefault(s => SameValue(s.Value, value))?.Schema;
            if (schema?["type"]?.ToString() != "boolean" ||
                new[] { "$ref", "anyOf", "oneOf", "allOf", "const", "enum" }.Any(schema.ContainsKey)) return false;
            flags.Add(value); return true;
        }
    }

    private static bool CorrectableGuard(TaskValue value, List<TaskPlanCompiler.RepairSource> sources)
    {
        if (NullableGuardReference(value, sources)) return true;
        if (value.Kind == "predicate" && value.Predicate is "and" or "or" && value.Items.Count == 2)
            return value.Items.All(item => CorrectableGuard(item, sources));
        if (value.Kind == "predicate" && value.Predicate == "not" && value.Items.Count == 1)
            return CorrectableGuard(value.Items[0], sources);
        // Already valid predicates remain intact. A reference with an unknown type
        // cannot be guessed into a business condition.
        if (value.Kind == "predicate") return true;
        return sources.Any(s => SameValue(s.Value, value) && s.Schema["type"]?.ToString() == "boolean") || value.Kind == "boolean";
    }

    private static bool PreservesNullableGuard(TaskValue original, TaskValue replacement, List<TaskPlanCompiler.RepairSource> sources)
    {
        if (SameValue(original, replacement)) return true;
        if (NullableGuardReference(original, sources)) return NullableGuardReplacement(original, replacement, sources);
        if (original.Kind != "predicate" || original.Predicate is not ("and" or "or" or "not") ||
            original.Kind != replacement.Kind || original.Predicate != replacement.Predicate || original.Items.Count != replacement.Items.Count) return false;
        var old = System.Text.Json.JsonSerializer.SerializeToNode(original, PlanningJsonContext.Default.TaskValue)!.AsObject();
        var next = System.Text.Json.JsonSerializer.SerializeToNode(replacement, PlanningJsonContext.Default.TaskValue)!.AsObject();
        old.Remove("items"); next.Remove("items");
        return JsonNode.DeepEquals(old, next) && original.Items.Select((value, index) => PreservesNullableGuard(value, replacement.Items[index], sources)).All(valid => valid);
    }

    private static PlanningDiagnostic UnsafeGuard(string path) => new("AUTOMATIC_GUARD_CORRECTION_UNSAFE", path,
        "The diagnosed assertion cannot be preserved by a known nullable reference != null and declared nonnullable Boolean fields from the same producer. " +
        "Keep valid operands and their order; review this guard explicitly. No structural regeneration was dispatched for this unsupported change.");
}
