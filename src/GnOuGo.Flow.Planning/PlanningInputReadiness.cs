using System.Globalization;
using System.Text;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Review evidence only. A compiled graph does not establish the runtime model,
// settings, cache outcome or complete request envelope. Only dispatch admission
// may compare the actual request against its authoritative effective allowance.
internal static class PlanningInputReadiness
{
    // Source maps are transient: review evidence belongs to the existing result
    // journal, not to another representation of the plan or its runtime inputs.
    internal static IReadOnlyList<PlanningValidationResult> Assess(TaskCompilation compilation, int version, TaskPlan? plan = null)
    {
        if (compilation.Graph is not { } graph) return [];
        if (version != 2) return Assess(graph);
        var tasks = plan is null ? [] : TaskPlanRevisions.Tasks(plan).ToArray();
        var results = new List<PlanningValidationResult>();
        for (var workflow = 0; workflow < graph.Workflows.Count; workflow++)
        {
            var scope = graph.Workflows[workflow];
            var prefix = "/workflows/" + workflow.ToString(CultureInfo.InvariantCulture);
            foreach (var (node, location) in PlanningGraphValidation.Located(scope.Steps, prefix + "/steps")
                         .Concat(PlanningGraphValidation.Located(scope.Finally, prefix + "/finally")))
            {
                var mapping = node.Input.Kind == "dynamic_mapping";
                if (node.Type != "llm.call" && !mapping) continue;
                var authored = compilation.Locate(new("INPUT_READINESS", location + "/input", "")).Location;
                var task = tasks.SingleOrDefault(t => authored == "/tasks/" + t.Id || authored.StartsWith("/tasks/" + t.Id + "/", StringComparison.Ordinal));
                var sources = mapping ? PlanningGraphValidation.Member(node.Input, "sources") : node.Input;
                var label = mapping ? "Source JSON" : "Bound input JSON";
                var measurement = sources is not null && PlanningGraphValidation.IsLiteral(sources)
                    ? label + " contains " + Encoding.UTF8.GetByteCount(PlanningGraphValidation.Literal(sources)?.ToJsonString() ?? "null")
                        .ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes; this is not the complete serialized model request or a token estimate."
                    : label + " is runtime-dependent; its complete size cannot yet be established.";
                IEnumerable<string> inputs = authored.Contains("/inputs/", StringComparison.Ordinal) ? [authored]
                    : task is { Inputs.Count: > 0 } ? task.Inputs.Select(i => "/tasks/" + task.Id + "/inputs/" + i.Name)
                    : mapping && sources is { Kind: "object" } && authored.StartsWith("/tasks/", StringComparison.Ordinal)
                        ? sources.Members.Select(m => authored + "/inputs/" + m.Name) : [authored];
                var boundaries = mapping ? " " + ExtractionBoundary(node, task?.Each is not null) : " Interpretation consumes its complete bound input.";
                results.Add(new("input_readiness:" + location, "unverified",
                    "LLM input readiness at " + authored + ": " + measurement + boundaries + " Authored input locations: " + string.Join(", ", inputs) + "." +
                    " Input fit requires the complete request, runtime model settings and authoritative allowance. Neither output bounds nor iteration ceilings prove input fit; required evidence is never silently truncated.", []));
            }
        }
        return results;
    }

    internal static string ExtractionBoundary(PlanningNode node, bool explicitlyAuthored = false)
    {
        if (PlanningGraphValidation.Member(node.Input, "each") is not null)
            return (explicitlyAuthored ? "Explicit each" : "Resolved each declaration") +
                " selects independent extraction. Bounded examples only teach the mapping; all items retain their order and repetitions and require validation." +
                " The complete collection is not one learning request; shared context and complete examples require separate runtime admission.";
        if (PlanningGraphValidation.Member(node.Input, "infer_each")?.Boolean == true)
            return "Runtime collection detection is enabled; independent processing is not established by this flag." +
                " With no eligible top-level collection and target, learning, when required, receives the whole supplied input; ambiguous collection pairs fail explicitly." +
                " Nested arrays do not establish item independence; the runtime boundary and complete request size remain unverified.";
        return "Whole-input extraction uses all bound sources when learning is required; identity and validated cache paths may avoid inference. A bounded result does not bound that input.";
    }

    internal static IReadOnlyList<PlanningValidationResult> Assess(PlanningGraph graph)
    {
        var results = new List<PlanningValidationResult>();
        for (var workflow = 0; workflow < graph.Workflows.Count; workflow++)
        {
            var scope = graph.Workflows[workflow];
            var prefix = "/workflows/" + workflow.ToString(CultureInfo.InvariantCulture);
            foreach (var (node, location) in PlanningGraphValidation.Located(scope.Steps, prefix + "/steps")
                         .Concat(PlanningGraphValidation.Located(scope.Finally, prefix + "/finally")))
            {
                var mapping = node.Input.Kind == "dynamic_mapping";
                if (node.Type != "llm.call" && !mapping) continue;
                var value = mapping ? PlanningGraphValidation.Member(node.Input, "sources") : node.Input;
                var label = mapping ? "Source JSON" : "Bound input JSON";
                var measurement = value is not null && PlanningGraphValidation.IsLiteral(value)
                    ? label + " contains " + Encoding.UTF8.GetByteCount(PlanningGraphValidation.Literal(value)?.ToJsonString() ?? "null")
                        .ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes; this is not the complete serialized model request or a token estimate."
                    : label + " is runtime-dependent; its complete size cannot yet be established.";
                var independent = mapping && (PlanningGraphValidation.Member(node.Input, "each") is not null ||
                    PlanningGraphValidation.Member(node.Input, "infer_each")?.Boolean == true);
                var collection = independent
                    ? " Independent extraction's complete collection size is not a single request size; shared context and complete examples require separate runtime admission. All collection items remain subject to processing and validation."
                    : "";
                results.Add(new("input_readiness:" + location, "unverified",
                    "LLM input readiness at " + location + ": " + measurement + collection +
                    " Input fit requires the complete request, runtime model settings and authoritative allowance. Neither output bounds nor iteration ceilings prove input fit; required evidence is never silently truncated.", []));
            }
        }
        return results;
    }
}
