using System.Globalization;
using System.Text;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Review evidence only. A compiled graph does not establish the runtime model,
// settings, cache outcome or complete request envelope. Only dispatch admission
// may compare the actual request against its authoritative effective allowance.
internal static class PlanningInputReadiness
{
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
