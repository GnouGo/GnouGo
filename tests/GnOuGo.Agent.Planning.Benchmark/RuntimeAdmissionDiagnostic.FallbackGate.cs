using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    internal static void RequireFallbackOwnership(PlanningSnapshot state, PlanningObligation operation, string source, int start, int length)
    {
        var admission = operation.OperationAdmission!;
        var contributions = admission.ExecutionContributions.SelectMany(p => p.Contributions).DistinctBy(c => c.Id).ToArray();
        PlanningReference Reference(string id) => state.References.Single(r => r.Id == id);
        var fallbacks = contributions.Where(c => c.Role == "governing_property" && c.GoverningKind == "runtime_fallback" &&
            c.EffectId is null && admission.Assignments.Any(a => a.ContributionId == c.Id && a.Disposition == "attach") &&
            admission.GoverningApplicability.Any(a => a.Version == 2 && a.ContributionId == c.Id && a.EvidenceReference == c.EvidenceReference &&
                a.Outcome == "active" && a.Targets.SequenceEqual([operation.Id]) && !string.IsNullOrEmpty(a.ProofFingerprint))).ToArray();
        if (start < 0 || length <= 0 || Enumerable.Range(start, length).Any(i => !char.IsWhiteSpace(state.Request.Prompt[i]) &&
            !fallbacks.Any(c => Reference(c.EvidenceReference) is var r && r.SourceId == source && r.Start <= i && i < r.Start + r.Length)))
            throw new WorkflowRuntimeException("DIAGNOSTIC_FALLBACK_OWNERSHIP", "Runtime fallback requires exact governing qualification and current applicability to the classifier.");
        foreach (var support in contributions.Where(c => c.Role == "supports"))
        {
            var owned = Reference(support.EvidenceReference);
            if (!support.RuntimeEvidenceIds.Any(id => state.RuntimeEvidence.Single(e => e.Id == id).ActionReference is { } action &&
                Reference(action) is var parent && parent.SourceId == owned.SourceId && parent.Start <= owned.Start && parent.Start + parent.Length >= owned.Start + owned.Length))
                throw new WorkflowRuntimeException("DIAGNOSTIC_SUPPORT_WIDENING", "Support must remain within independently owned runtime execution evidence.");
            if (owned.SourceId != source || owned.Start >= start + length || start >= owned.Start + owned.Length) continue;
            // A complete request may cite a nested qualifier, but only its validated
            // composition can explain the overlap. It grants no support to the child.
            var proof = admission.ExecutionContributions.Single(p => p.Contributions.Any(c => c.Id == support.Id));
            if (!fallbacks.Any(c => proof.Units.Any(u => u.Id == c.UnitId && u.ParentRequestUnitId == support.UnitId)) ||
                owned.Start >= start && owned.Start + owned.Length <= start + length)
                throw new WorkflowRuntimeException("DIAGNOSTIC_FALLBACK_SUPPORT", "Fallback cannot acquire independent executable support or be absorbed without qualified composition.");
        }
    }

    private static void AddFallbackReport(JsonObject report, PlanningSnapshot state)
    {
        var operations = state.Obligations.Where(o => o.OperationAdmission is not null).ToArray();
        report["fallbackOwnershipGatePassed"] = report["case"]?.ToString() == "local" && report["status"]?.ToString() == "passed";
        report["qualifiedFallbacks"] = new JsonArray(operations.SelectMany(o => o.OperationAdmission!.ExecutionContributions.SelectMany(p => p.Contributions)
            .Where(c => c.GoverningKind == "runtime_fallback").DistinctBy(c => c.Id).Select(c =>
            {
                var r = state.References.Single(r => r.Id == c.EvidenceReference);
                return (JsonNode)new JsonObject
                {
                    ["contributionId"] = c.Id, ["reference"] = r.Id, ["sourceId"] = r.SourceId, ["start"] = r.Start, ["length"] = r.Length,
                    ["role"] = c.Role, ["governingKind"] = c.GoverningKind,
                    ["sourceBindings"] = JsonSerializer.SerializeToNode(c.SourceBindings, PlanningJsonContext.Default.ListPlanningContributionSourceBinding),
                    ["applicability"] = JsonSerializer.SerializeToNode(o.OperationAdmission.GoverningApplicability.Where(a => a.ContributionId == c.Id).ToList(),
                        PlanningJsonContext.Default.ListPlanningGoverningApplicabilityProof)
                };
            })).ToArray());
    }
}
