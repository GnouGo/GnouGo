using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Admission proof for interpreted sources. Policy subjects have no operation authority.</summary>
internal static class PlanningSourceGroundingRules
{
    internal static readonly string[] OperationKinds = ["external_read", "external_write", "external_execute", "resource_lifecycle", "cleanup", "human_interaction", "local_processing"];
    internal static readonly string[] PolicyKinds = ["workflow_policy", "implementation_policy", "confirmation_required", "confirmation_forbidden", "rejection_condition", "exact_denial"];
    private static readonly string[] Values = ["explicit_value", "declaration_constraint", "omission_default", "runtime_condition", "runtime_fallback", "business_preference", "information"];
    internal static string[] Kinds(PlanningSourceAuthority authority) => authority switch
    {
        PlanningSourceAuthority.ConstraintsOnly => [.. PolicyKinds, .. Values],
        PlanningSourceAuthority.RequestedBehavior or PlanningSourceAuthority.ExistingBehavior => [.. OperationKinds, .. PolicyKinds, .. Values,
            "declaration_candidate", "business_choice", "iteration", "workflow_boundary"],
        _ => ["information"]
    };

    internal static Dictionary<string, (string Workflow, PlanningNode Node)> BaselineNodes(PlanningSnapshot state)
    {
        var result = new Dictionary<string, (string, PlanningNode)>(StringComparer.Ordinal);
        if (state.Request.Baseline is not { } baseline) return result;
        var fingerprint = PlanningGraphCompiler.Fingerprint(baseline);
        foreach (var workflow in baseline.Workflows)
        foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
        {
            var id = "baseline_" + PlanningGraphCompiler.Fingerprint(state.Request.TenantId + ":" + state.Request.SessionId + ":" + fingerprint + ":" + workflow.Key + ":" + node.Key)[..24];
            if (!result.TryAdd(id, (workflow.Key, node))) throw Failure(state, id, "The baseline has ambiguous node identities.");
        }
        return result;
    }

    internal static PlanningSourceGrounding Create(PlanningSnapshot state, PlanningObligation obligation, string? baselineReference = null)
    {
        if (obligation.EvidenceReferences.Count == 0) throw Failure(state, obligation.Id, "An obligation requires owned source evidence.");
        var reference = state.References.SingleOrDefault(r => r.Id == obligation.EvidenceReferences[0]);
        if (obligation.EvidenceReferences.Any(id => !state.References.Any(r => r.Id == id) || !PlanningChoiceEvidence.Current(state, id)))
            throw Failure(state, obligation.Id, "The source evidence is foreign or no longer current.");
        var source = reference is null ? null : PlanningIntentAssessment.IntentSources(state).SingleOrDefault(s => s.Id == reference.SourceId);
        if (source is null || source.Authority == PlanningSourceAuthority.Unknown || !Kinds(source.Authority).Contains(obligation.Kind, StringComparer.Ordinal))
            throw Failure(state, obligation.Id, "This source cannot introduce the selected semantic obligation.");
        if (source.Baseline is { Field: not null } annotation && !PlanningBaselineProjection.AnnotationKinds(annotation).Contains(obligation.Kind, StringComparer.Ordinal))
            throw Failure(state, obligation.Id, "An annotation cannot override its structural owner or create declarations or operations.");
        var clause = PlanningChoiceEvidence.Parent(state, reference!.Id);
        var declared = PlanningDeclaredPolicyProjection.Owns(state, source);
        if (declared && (obligation.EvidenceReferences.Count != 1 ||
            !PlanningDeclaredPolicyProjection.Clauses(state).Any(c => c.Reference.Id == reference.Id &&
                c.Clause.Meanings.Any(m => m.Kind == obligation.Kind && m.Required == obligation.Required))))
            throw Failure(state, obligation.Id, "The policy obligation differs from its producer-declared meaning or owned clause.");
        if (obligation.EvidenceReferences.Any(id => PlanningChoiceEvidence.Parent(state, id).Id != clause.Id))
            throw Failure(state, obligation.Id, "An obligation must belong to one complete owned clause.");
        var operation = OperationKinds.Contains(obligation.Kind, StringComparer.Ordinal);
        if (source.Structural && (!operation || source.Baseline!.OwnerKind != "node"))
            throw Failure(state, obligation.Id, "Typed contracts and containers do not create interpreted obligations.");
        if (source.Authority == PlanningSourceAuthority.ExistingBehavior && operation)
        {
            if (source.Baseline is not { OwnerKind: "node", Field: null } owner || baselineReference is null ||
                PlanningBaselineProjection.NodeReference(state, owner) != baselineReference)
                throw Failure(state, obligation.Id, "An existing operation must reference an issued baseline node.");
        }
        else if (baselineReference is not null) throw Failure(state, obligation.Id, "This semantic role cannot claim baseline operation authority.");
        var role = operation ? source.Authority == PlanningSourceAuthority.RequestedBehavior ? PlanningSourceSemanticRole.RequestedAction : PlanningSourceSemanticRole.ExistingAction
            : obligation.Kind is "runtime_condition" or "runtime_fallback" or "rejection_condition" ? PlanningSourceSemanticRole.RuntimeCondition
            : source.Authority == PlanningSourceAuthority.ConstraintsOnly || PolicyKinds.Contains(obligation.Kind, StringComparer.Ordinal) ? PlanningSourceSemanticRole.PolicyConstraint
            : PlanningSourceSemanticRole.Declaration;
        var fingerprint = PlanningGraphCompiler.Fingerprint("source-v6:" + source.Authority + ":" + role + ":" + clause.Id + ":" + clause.SourceFingerprint + ":" +
            obligation.Kind + ":" + obligation.Required + ":" + string.Join('|', obligation.EvidenceReferences) + ":" + baselineReference + ":" + PlanningDeclaredPolicyProjection.Fingerprint(state));
        return new(source.Authority, role, clause.Id, baselineReference, fingerprint)
        { DeclaredPolicyFingerprint = declared ? PlanningDeclaredPolicyProjection.Fingerprint(state) : null };
    }

    internal static void Validate(PlanningSnapshot state, PlanningObligation obligation)
    {
        if (obligation.Grounding is not { } grounding || grounding != Create(state, obligation, obligation.Grounding.BaselineReference))
            throw Failure(state, obligation.Id, "Current source authority and semantic grounding must be established before admission.");
        if (obligation.OperationAdmission is not null) PlanningOperations.Validate(state, obligation);
        else if (OperationKinds.Contains(obligation.Kind, StringComparer.Ordinal) && obligation.Disposition == "admitted")
            throw new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException("INTENT_OPERATION_PROOF_MISSING", "Preliminary operation hints cannot grant canonical authority.",
                details: new JsonObject { ["location"] = "/operations/@" + obligation.Id });
    }
    internal static string Fingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint(string.Join('|',
        state.Obligations.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => o.Id + ":" + o.Grounding?.Fingerprint + ":" + o.Disposition + ":" + o.AdjudicationFingerprint + ":" + o.OperationAdmission?.ProofFingerprint)));

    internal static void ValidateAll(PlanningSnapshot state)
    {
        foreach (var obligation in state.Obligations) Validate(state, obligation);
    }
    private static GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException Failure(PlanningSnapshot state, string id, string message)
    {
        state.Events.Add(new("source_authority_rejected", state.CurrentPhase ?? PlanningPhase.Intent, DateTimeOffset.UtcNow, 1));
        System.Diagnostics.Activity.Current?.AddEvent(new("planning.source_authority_rejected", tags: new() { ["obligation_id"] = id }));
        return new("INTENT_SOURCE_AUTHORITY_UNPROVEN", message, details: new JsonObject { ["source_obligation"] = id,
            ["location"] = "/obligations/@" + id.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal) });
    }
}
