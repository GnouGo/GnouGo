using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityInventoryDecisions
{
    internal static async Task<CapabilityInventory> BuildAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        await PlanningSourceDecisions.RelateAsync(state, runtime, ct);
        var sources = PlanningSourceDecisions.Sources(state);
        var obligations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToDictionary(o => o.Id, StringComparer.Ordinal);
        var ordered = new List<PlanningObligation>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(PlanningObligation obligation)
        {
            if (!visited.Add(obligation.Id)) return;
            foreach (var relation in state.ObligationRelations.Where(r => r.Consumer == obligation.Id).OrderBy(r => r.Producer, StringComparer.Ordinal))
                if (obligations.TryGetValue(relation.Producer, out var producer)) Visit(producer);
            ordered.Add(obligation);
        }
        foreach (var obligation in obligations.Values.OrderBy(o => o.Id, StringComparer.Ordinal)) Visit(obligation);
        CapabilityEvidenceAnchor Anchor(PlanningObligation obligation)
        {
            var reference = state.References.Single(r => r.Id == obligation.EvidenceReferences[0]);
            return new(reference.Id, reference.SourceId, reference.Start, reference.Length, PlanningReferences.Resolve(state, reference.Id, sources));
        }
        var operations = ordered.Select(obligation =>
        {
            var relations = state.ObligationRelations.Where(r => r.Consumer == obligation.Id).ToArray();
            var decision = relations.SingleOrDefault(r => r.Role is "decision" or "decision_no_effect");
            var failure = relations.SingleOrDefault(r => r.Role == "failure");
            var local = obligation.Kind == "local_processing";
            var evidence = Anchor(obligation);
            var implementationPolicies = relations.Where(r => r.Role == "policy").Select(r => state.Obligations.Single(o => o.Id == r.Producer)).ToArray();
            var intrinsic = new[] { evidence }.Concat(implementationPolicies.Select(Anchor)).ToArray();
            return new CapabilityInventoryOperation(obligation.Id, PlanningSourceDecisions.Text(state, obligation), obligation.Required,
                local ? "local_processing" : obligation.Kind == "human_interaction" ? "human_interaction" : "external_effect",
                obligation.Kind switch { "external_read" => "read", "external_write" => "write", "external_execute" => "execute", "resource_lifecycle" or "cleanup" => "lifecycle", _ => "none" },
                decision?.Producer ?? "", failure is null ? "requested_effect" : "derived_failure_handling", failure?.Producer ?? "",
                decision?.Role == "decision_no_effect", obligation.Required ? "" : evidence.Excerpt)
            {
                InputOperationIds = relations.Where(r => r.Role != "failure" && obligations.ContainsKey(r.Producer)).Select(r => r.Producer).Distinct(StringComparer.Ordinal).ToArray(),
                CoverageRequirementEvidence = local ? [] : intrinsic, CoverageRequirements = local ? [] : intrinsic.Select(e => e.Excerpt).ToArray(),
                OptionalityEvidenceAnchor = obligation.Required ? null : evidence,
                NoEffectOutcomeEvidenceAnchor = decision?.Role == "decision_no_effect" ? evidence : null,
                WorkflowStructureCoverageRequirementIds = decision?.Role == "decision_no_effect" ? new([evidence.Id], StringComparer.Ordinal) : new(StringComparer.Ordinal)
            };
        }).ToArray();
        var constraints = state.Obligations.Where(o => o.Kind is "workflow_policy" or "exact_denial" or "confirmation_required" or "confirmation_forbidden")
            .Select(o => new CapabilityInventoryConstraint(o.Id, PlanningSourceDecisions.Text(state, o), o.Required, o.Kind == "exact_denial" ? "exact_denial" : "workflow_policy")).ToArray();
        if (operations.Length == 0)
            throw new WorkflowRuntimeException("INTENT_OPERATION_UNRESOLVED", "No evidenced runtime operation was established. This is not proof of an unavailable capability.");
        var confirmation = state.Obligations.Where(o => o.Kind is "confirmation_required" or "confirmation_forbidden").ToArray();
        if (confirmation.Select(o => o.Kind).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new WorkflowRuntimeException("CONFIRMATION_POLICY_CONFLICT", "The governing sources disagree about confirmation; no implementation was authorized.");
        var policy = confirmation.FirstOrDefault();
        return new(true, operations, constraints, [], policy is null ? "unspecified" : policy.Kind == "confirmation_required" ? "required" : "forbidden",
            policy is null ? "" : PlanningSourceDecisions.Text(state, policy))
        { ExternalWriteConfirmationEvidenceAnchor = policy is null ? null : Anchor(policy) };
    }
}
