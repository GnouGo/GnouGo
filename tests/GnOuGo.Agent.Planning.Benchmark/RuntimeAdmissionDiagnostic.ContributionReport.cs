using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    private static void AddContributionReport(JsonObject report, PlanningSnapshot state)
    {
        PlanningExecutionContributionProof[] proofs;
        try { proofs = PlanningOperations.ReadContributions(state); }
        catch (WorkflowRuntimeException error)
        {
            report["contributionProofsAvailable"] = false;
            report["contributionProofUnavailableCode"] = error.Code;
            report["qualifiedContributions"] = null;
            return;
        }
        report["contributionProofsAvailable"] = true;
        report["qualifiedContributions"] = new JsonArray(proofs.SelectMany(p => p.Contributions.Select(c =>
        {
            var r = state.References.Single(r => r.Id == c.EvidenceReference);
            return (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["parentRuntimeEvidence"] = p.RuntimeEvidenceId,
                ["preliminaryRuntimeKind"] = state.RuntimeEvidence.Single(e => e.Id == p.RuntimeEvidenceId).Kind,
                ["ownedReference"] = c.EvidenceReference, ["sourceId"] = r.SourceId,
                ["sourceStart"] = r.Start, ["sourceLength"] = r.Length,
                ["qualifiedRole"] = c.Role, ["basis"] = c.Basis,
                ["supportBasis"] = c.Role == "supports" ? c.Basis : null,
                ["effectId"] = c.EffectId, ["ownerReference"] = c.OwnerReference,
                ["boundaryReference"] = c.BoundaryReference, ["origin"] = c.Origin.ToString(),
                ["decisionId"] = p.DecisionId, ["version"] = p.Version,
                ["domainFingerprint"] = p.DomainFingerprint, ["proofFingerprint"] = p.ProofFingerprint
            };
        })).ToArray());
        report["contributionCounts"] = new JsonObject(proofs.SelectMany(p => p.Contributions)
            .GroupBy(c => c.Role + ":" + c.Origin).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, JsonValue.Create(g.Count()))));
        try
        {
            var applicability = PlanningOperations.ReadApplicability(state);
            report["governingApplicability"] = System.Text.Json.JsonSerializer.SerializeToNode(
                applicability.ToList(), PlanningJsonContext.Default.ListPlanningGoverningApplicabilityProof);
            report["governingProperties"] = new JsonArray(applicability.Select(p => (JsonNode)new JsonObject
            {
                ["contributionId"] = p.ContributionId, ["ownedReference"] = p.EvidenceReference,
                ["preliminaryRuntimeKind"] = state.RuntimeEvidence.Single(e => e.Id == p.RuntimeEvidenceId).Kind,
                ["targets"] = new JsonArray(p.Targets.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                ["outcome"] = p.Outcome, ["origin"] = p.Origin.ToString(), ["decisionId"] = p.DecisionId,
                ["realizedSetFingerprint"] = p.RealizedSetFingerprint, ["proofFingerprint"] = p.ProofFingerprint
            }).ToArray());
        }
        catch (WorkflowRuntimeException error)
        { report["governingApplicability"] = null; report["applicabilityUnavailableCode"] = error.Code; }
        report["preliminaryEvidenceRolePopulated"] = state.RuntimeEvidence.Count(e => e.EvidenceRole is not null);
    }
}
