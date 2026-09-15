using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Resolves policy subjects before computing permission requirements. No executor-wide policy inference.</summary>
internal static class PlanningConfirmationPolicies
{
    internal static void RequireCurrent(PlanningSnapshot state)
    {
        if (state.Preparation is { PolicyScopeVersion: not 2 })
            throw Failure("CONFIRMATION_SCOPE_UNRESOLVED", "preparation", "The accepted preparation predates scoped permission proof. Revise the intent to reassess its governing policies before execution or approval.");
        if (state.Preparation is not null) PlanningSourceGroundingRules.ValidateAll(state);
    }
    internal static async Task ResolveAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        PlanningSourceGroundingRules.ValidateAll(state);
        var candidates = state.Obligations.Where(o => o.Kind is "confirmation_required" or "confirmation_forbidden" or "rejection_condition" or "workflow_policy" or "exact_denial")
            .OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        var groups = candidates.GroupBy(o => o.Grounding!.ClauseReference).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        var decisions = groups.Select(g => PlanningPolicyClauseDecisions.Build(state, g.Key, g.ToArray(), candidates, operations)).ToArray();
        // Reassemble from durable pages. Pending scope state is never permission proof.
        state.ScopedPolicies = candidates.Select(o => new PlanningScopedPolicy { Id = "policy_" + o.Id, ObligationId = o.Id,
            ClauseReference = o.Grounding!.ClauseReference, EvidenceFingerprint = PlanningPolicyClauseDecisions.Fingerprint(state, o.Grounding.ClauseReference, candidates, operations) }).ToList();
        var answers = await PlanningDecisionPages.ResolveAsync(state, runtime, "confirmation_scope", "$plan", decisions, ct);
        var assignments = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var decision in decisions)
            foreach (var field in answers[decision.Id]!.AsObject()) assignments.Add(field.Key, field.Value!);
        var policies = new List<PlanningScopedPolicy>();
        var permissions = new Dictionary<string, PlanningScopedPolicy>(StringComparer.Ordinal);
        foreach (var obligation in candidates)
        {
            var answer = assignments[obligation.Id]; var rule = answer["rule"]!.ToString();
            if (rule == "unknown") throw Failure("CONFIRMATION_SCOPE_UNRESOLVED", obligation.Id, "The complete clause did not establish its policy semantics.");
            if (rule is "not_confirmation_policy" or "rejection_condition") continue;
            var policy = new PlanningScopedPolicy
            {
                Id = "policy_" + obligation.Id, ObligationId = obligation.Id, ClauseReference = obligation.Grounding!.ClauseReference,
                EvidenceFingerprint = PlanningPolicyClauseDecisions.Fingerprint(state, obligation.Grounding.ClauseReference, candidates, operations),
                Origin = PlanningChoiceEvidence.Origin(state, obligation.Grounding.ClauseReference), Rule = rule,
                ScopeKind = answer["scope"]!["kind"]!.ToString(), Target = answer["scope"]!["target"]!.ToString(),
                Applicability = answer["applicability"]!.ToString(), Status = "resolved",
                GoverningObligationIds = [obligation.Id], GoverningReferences = obligation.EvidenceReferences.Append(obligation.Grounding.ClauseReference).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
            };
            policy.TargetOperationIds = policy.ScopeKind == "effect" ? operations.Where(o => Effect(o) == policy.Target).Select(o => o.Id).ToList()
                : policy.ScopeKind == "operation" ? [policy.Target] : answer["interactionTargets"]?.AsArray().Select(v => v!.ToString()).ToList() ?? [];
            Validate(state, policy);
            // Only the same complete clause, scope, applicability and semantics can coalesce.
            var equivalent = policies.SingleOrDefault(p => p.ClauseReference == policy.ClauseReference && p.Rule == policy.Rule && p.ScopeKind == policy.ScopeKind &&
                p.Target == policy.Target && p.Applicability == policy.Applicability && p.TargetOperationIds.Order(StringComparer.Ordinal).SequenceEqual(policy.TargetOperationIds.Order(StringComparer.Ordinal)));
            if (equivalent is null) { policies.Add(policy); equivalent = policy; }
            else
            {
                equivalent.GoverningObligationIds.Add(obligation.Id);
                equivalent.GoverningReferences = equivalent.GoverningReferences.Concat(policy.GoverningReferences).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            }
            permissions.Add(obligation.Id, equivalent);
        }
        foreach (var obligation in candidates.Where(o => assignments[o.Id]["rule"]!.ToString() == "rejection_condition"))
        {
            var owner = assignments[obligation.Id]["permissionRule"]!.ToString();
            if (owner == obligation.Id || !permissions.TryGetValue(owner, out var permission) || permission.Rule != "require_confirmation")
                throw Failure("CONFIRMATION_SCOPE_UNRESOLVED", obligation.Id, "The rejection condition requires a supported permission rule, not a retired or unrelated interaction.");
            permission.GoverningObligationIds.Add(obligation.Id);
            permission.GoverningReferences = permission.GoverningReferences.Concat(obligation.EvidenceReferences).Append(obligation.Grounding!.ClauseReference).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            permissions.Add(obligation.Id, permission);
        }
        foreach (var policy in policies) Validate(state, policy);
        // Do not change conflict detection: only admitted actions and adjudicated rules reach it.
        foreach (var operation in operations) _ = Effective(policies, operation.Id);
        state.ScopedPolicies = policies;
        foreach (var obligation in candidates)
        {
            var fingerprint = PlanningPolicyClauseDecisions.Fingerprint(state, obligation.Grounding!.ClauseReference, candidates, operations);
            var disposition = assignments[obligation.Id]["rule"]!.ToString();
            if (obligation.AdjudicationFingerprint != fingerprint)
            {
                var kind = disposition is "not_confirmation_policy" or "rejection_condition" ? "policy_classification_retired" : "policy_normalized";
                state.Events.Add(new(kind, "confirmation_scope", DateTimeOffset.UtcNow, 1));
                System.Diagnostics.Activity.Current?.AddEvent(new("planning." + kind, tags: new()
                { ["obligation_id"] = obligation.Id, ["authority"] = obligation.Grounding.Authority.ToString(), ["disposition"] = disposition }));
            }
            obligation.AdjudicationFingerprint = fingerprint; obligation.Disposition = disposition;
            obligation.PolicyIds = permissions.TryGetValue(obligation.Id, out var permission) ? [permission.Id] : [];
        }
        await runtime.CheckpointAsync(state, ct);
    }

    internal static string Effect(PlanningObligation operation) => operation.Kind switch
    { "external_read" => "read", "external_write" => "write", "external_execute" => "execute", "resource_lifecycle" or "cleanup" => "lifecycle", _ => "none" };

    internal static void Validate(PlanningSnapshot state, PlanningScopedPolicy policy)
    {
        if (policy.Status != "resolved" || policy.Applicability is not ("always" or "unless_explicit") ||
            policy.Rule is not ("require_confirmation" or "forbid_confirmation" or "forbid_interaction") ||
            policy.ScopeKind is not ("operation" or "effect" or "interaction"))
            throw Failure("CONFIRMATION_SCOPE_UNRESOLVED", policy.Id, "The governed action or policy applicability is not established.");
        if (!PlanningChoiceEvidence.Current(state, policy.ClauseReference) || policy.Origin != PlanningChoiceEvidence.Origin(state, policy.ClauseReference) ||
            !state.Obligations.Any(o => o.Id == policy.ObligationId && o.EvidenceReferences.Any(r => PlanningChoiceEvidence.Parent(state, r).Id == policy.ClauseReference)))
            throw Failure("CONFIRMATION_SCOPE_STALE", policy.Id, "The scoped policy has no current owned governing evidence.");
        if (!policy.GoverningObligationIds.Contains(policy.ObligationId, StringComparer.Ordinal) ||
            policy.GoverningObligationIds.Distinct(StringComparer.Ordinal).Count() != policy.GoverningObligationIds.Count)
            throw Failure("CONFIRMATION_SCOPE_STALE", policy.Id, "The adjudication must retain its distinct governing obligations.");
        var governing = policy.GoverningObligationIds.Select(id => state.Obligations.SingleOrDefault(o => o.Id == id)).ToArray();
        if (governing.Any(o => o is null)) throw Failure("CONFIRMATION_SCOPE_STALE", policy.Id, "The adjudication has a foreign governing obligation.");
        foreach (var obligation in governing) PlanningSourceGroundingRules.Validate(state, obligation!);
        var expectedReferences = governing.SelectMany(o => o!.EvidenceReferences.Append(o.Grounding!.ClauseReference)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        if (!expectedReferences.SequenceEqual(policy.GoverningReferences.Order(StringComparer.Ordinal)))
            throw Failure("CONFIRMATION_SCOPE_STALE", policy.Id, "The adjudication lost or changed its governing references.");
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray();
        if (policy.TargetOperationIds.Distinct(StringComparer.Ordinal).Count() != policy.TargetOperationIds.Count ||
            policy.TargetOperationIds.Any(id => !operations.Any(o => o.Id == id)))
            throw Failure("CONFIRMATION_SCOPE_UNRESOLVED", policy.Id, "The policy refers to an unknown or duplicate semantic action.");
        var expected = policy.ScopeKind switch
        {
            "effect" when policy.Target is "read" or "write" or "execute" or "lifecycle" => operations.Where(o => Effect(o) == policy.Target).Select(o => o.Id),
            "operation" when operations.Any(o => o.Id == policy.Target) => [policy.Target],
            "interaction" when policy.Target == policy.ClauseReference => policy.TargetOperationIds.Where(id => operations.Any(o => o.Id == id && o.Kind == "human_interaction")),
            _ => null
        };
        if (expected is null || !expected.Order(StringComparer.Ordinal).SequenceEqual(policy.TargetOperationIds.Order(StringComparer.Ordinal)) ||
            policy.Rule == "forbid_interaction" && policy.ScopeKind != "interaction")
            throw Failure("CONFIRMATION_SCOPE_UNRESOLVED", policy.Id, "The target is incompatible with the declared scope or interaction rule.");
    }

    internal static PlanningScopedPolicy? Effective(IEnumerable<PlanningScopedPolicy> policies, string operation)
    {
        var relevant = policies.Where(p => p.TargetOperationIds.Contains(operation)).ToArray();
        if (relevant.Any(p => p.Status != "resolved" || p.Applicability is not ("always" or "unless_explicit") ||
            p.Rule is not ("require_confirmation" or "forbid_confirmation" or "forbid_interaction")))
            throw Failure("CONFIRMATION_SCOPE_UNRESOLVED", operation, "A permission policy lacks current scope proof.");
        var explicitRules = relevant.Where(p => p.Origin is "intent" or "answer" && p.Applicability == "always").ToArray();
        var effective = relevant.Where(p => p.Applicability == "always" || explicitRules.Length == 0).ToArray();
        var permissions = effective.Where(p => p.Rule is "require_confirmation" or "forbid_confirmation").ToArray();
        if (permissions.Select(p => p.Rule).Distinct(StringComparer.Ordinal).Count() > 1)
            throw Failure("CONFIRMATION_POLICY_CONFLICT", operation, "Required and forbidden confirmation govern the same action. Policies: " +
                string.Join(", ", permissions.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.Id + " [" + p.ClauseReference + "]")));
        if (effective.Any(p => p.Rule == "forbid_interaction"))
            throw Failure("INTERACTION_POLICY_DENIED", operation, "The requested human interaction is prohibited by its scoped governing policy.");
        return permissions.OrderBy(p => p.Applicability == "always" ? 0 : 1).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    internal static string Fingerprint(IEnumerable<PlanningScopedPolicy> policies)
        => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(policies.OrderBy(p => p.Id, StringComparer.Ordinal).ToList(), PlanningJsonContext.Default.ListPlanningScopedPolicy));

    internal static WorkflowRuntimeException Failure(string code, string target, string message)
        => new(code, message, details: new JsonObject { ["policy_target"] = target,
            ["location"] = "/preparation/policies/@" + target.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal) });
}
