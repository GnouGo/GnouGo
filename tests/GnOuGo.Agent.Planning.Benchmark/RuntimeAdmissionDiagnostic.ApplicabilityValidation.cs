using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    private static void CheckApplicabilitySequence(PlanningSnapshot state, PlanningObligation[] operations)
    {
        var calls = state.RequestAccounting.DistinctBy(c => c.Id).Select(c => c.Id).ToList();
        int[] Requests(string prefix) => state.DecisionPages.Where(p => p.RequestId is not null &&
            p.Decisions.Any(d => d.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(p => calls.IndexOf(p.RequestId!)).Distinct().ToArray();
        var qualification = Requests("contribution_");
        var coverage = Requests("coverage_");
        var applicability = Requests("applicability_");
        if (qualification.Any(i => i < 0) || coverage.Any(i => i < 0) || applicability.Any(i => i < 0) ||
            qualification.Length > 0 && coverage.Length > 0 && qualification.Max() >= coverage.Min() ||
            coverage.Length > 0 && applicability.Length > 0 && coverage.Max() >= applicability.Min())
            throw new WorkflowRuntimeException("DIAGNOSTIC_APPLICABILITY_ORDER", "Qualification, support coverage and realized applicability must complete in dependency order.");
        var realized = operations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        void CheckTargets(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["properties"]?["target"]?["enum"] is JsonArray targets &&
                    targets.Any(t => t is null || !realized.Contains(t.ToString())))
                    throw new WorkflowRuntimeException("DIAGNOSTIC_APPLICABILITY_DOMAIN", "Applicability may expose only realized canonical operation IDs.");
                foreach (var child in obj) CheckTargets(child.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) CheckTargets(child);
        }
        foreach (var decision in PlanningOperations.ApplicabilityDecisions(state)) CheckTargets(decision.Schema);
        PlanningOperations.RequireCurrent(state);
    }
}
