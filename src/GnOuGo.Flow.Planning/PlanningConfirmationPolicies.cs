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
        if (state.Preparation is { PolicyScopeVersion: not 1 })
            throw Failure("CONFIRMATION_SCOPE_UNRESOLVED", "preparation", "The accepted preparation predates scoped permission proof. Revise the intent to reassess its governing policies before execution or approval.");
    }
    internal static async Task ResolveAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var obligations = state.Obligations.Where(o => o.Kind is "confirmation_required" or "confirmation_forbidden" or "workflow_policy" or "exact_denial").OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        var decisions = new List<PlanningDecisionPages.Decision>();
        var pending = new List<PlanningScopedPolicy>();
        foreach (var obligation in obligations)
        {
            var clause = PlanningChoiceEvidence.Parent(state, obligation.EvidenceReferences[0]);
            var fingerprint = PlanningGraphCompiler.Fingerprint(clause.SourceFingerprint + ":" + clause.Id + ":" +
                string.Join('|', operations.Select(o => o.Id + ":" + string.Join(',', o.EvidenceReferences))));
            var retained = state.ScopedPolicies.SingleOrDefault(p => p.ObligationId == obligation.Id && p.EvidenceFingerprint == fingerprint);
            if (retained is { Status: "resolved" }) { Validate(state, retained); pending.Add(retained); continue; }
            var policy = new PlanningScopedPolicy { Id = "policy_" + obligation.Id, ObligationId = obligation.Id,
                ClauseReference = clause.Id, EvidenceFingerprint = fingerprint, Origin = PlanningChoiceEvidence.Origin(state, clause.Id) };
            pending.Add(policy);
            var alternatives = new JsonArray(
                PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("effect")),
                    ("target", PlanningHoleRequests.Enum("read", "write", "execute", "lifecycle"))),
                PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("unknown")),
                    ("target", PlanningHoleRequests.Enum("unknown"))));
            if (operations.Length > 0) alternatives.Add((JsonNode)PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("operation")),
                ("target", PlanningHoleRequests.Enum(operations.Select(o => o.Id).ToArray()))));
            var humans = operations.Where(o => o.Kind == "human_interaction").ToArray();
            var targets = new JsonObject { ["type"] = "array", ["maxItems"] = humans.Length,
                ["items"] = humans.Length == 0 ? PlanningHoleRequests.Type("string") : PlanningHoleRequests.Enum(humans.Select(o => o.Id).ToArray()) };
            var permissionSchema = PlanningHoleRequests.Object(
                ("rule", PlanningHoleRequests.Enum(obligation.Kind == "confirmation_required" ? ["require_confirmation"] :
                    obligation.Kind == "confirmation_forbidden" ? ["forbid_confirmation"] : ["require_confirmation", "forbid_confirmation"])),
                ("scope", new JsonObject { ["anyOf"] = alternatives }),
                ("interactionTargets", new JsonObject { ["type"] = "array", ["maxItems"] = 0, ["items"] = PlanningHoleRequests.Type("string") }),
                ("applicability", PlanningHoleRequests.Enum("always", "unless_explicit", "unknown")));
            var responseSchema = obligation.Kind == "confirmation_required" ? permissionSchema : new JsonObject { ["anyOf"] = new JsonArray(permissionSchema,
                PlanningHoleRequests.Object(("rule", PlanningHoleRequests.Enum("forbid_interaction")),
                    ("scope", PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("interaction")), ("target", PlanningHoleRequests.Enum(clause.Id)))),
                    ("interactionTargets", targets), ("applicability", PlanningHoleRequests.Enum("always", "unless_explicit", "unknown")))) };
            if (obligation.Kind is "workflow_policy" or "exact_denial") responseSchema["anyOf"]!.AsArray().Add((JsonNode)PlanningHoleRequests.Object(
                ("rule", PlanningHoleRequests.Enum("not_confirmation_policy")),
                ("scope", PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum("none")), ("target", PlanningHoleRequests.Enum(clause.Id)))),
                ("interactionTargets", new JsonObject { ["type"] = "array", ["maxItems"] = 0, ["items"] = PlanningHoleRequests.Type("string") }),
                ("applicability", PlanningHoleRequests.Enum("always"))));
            decisions.Add(new(policy.Id, responseSchema, new JsonObject
            {
                ["clauseReference"] = clause.Id, ["clause"] = PlanningChoiceEvidence.Text(state, clause.Id),
                ["operations"] = new JsonObject(operations.Select(o => new KeyValuePair<string, JsonNode?>(o.Id,
                    new JsonObject { ["kind"] = o.Kind, ["clause"] = PlanningChoiceEvidence.Text(state, PlanningChoiceEvidence.Parent(state, o.EvidenceReferences[0]).Id) }))),
                ["task"] = "Identify the exact action whose permission this clause governs. Requiring/forbidding confirmation for an action differs from prohibiting a particular human interaction. Use an effect class only for an explicit class-wide rule; otherwise select its operation. An interaction prohibition selects this clause as its subject and only matching human operation IDs; no matching operation means an empty target list, never a new operation or an executor-wide ban. unless_explicit requires a declared exception for an explicit user instruction. Other workflow policies and implementation prohibitions select not_confirmation_policy when offered. Unestablished permission subjects or conditions are unknown."
            }, fingerprint));
        }
        state.ScopedPolicies = pending;
        var answers = await PlanningDecisionPages.ResolveAsync(state, runtime, "confirmation_scope", "$plan", decisions, ct);
        foreach (var policy in pending.Where(p => p.Status != "resolved"))
        {
            var answer = answers[policy.Id]!;
            policy.Rule = answer["rule"]!.ToString(); policy.ScopeKind = answer["scope"]!["kind"]!.ToString();
            policy.Target = answer["scope"]!["target"]!.ToString(); policy.Applicability = answer["applicability"]!.ToString();
            if (policy.Rule == "not_confirmation_policy") { policy.Status = "not_applicable"; continue; }
            policy.TargetOperationIds = policy.ScopeKind == "effect" ? operations.Where(o => Effect(o) == policy.Target).Select(o => o.Id).ToList()
                : policy.ScopeKind == "operation" ? [policy.Target] : answer["interactionTargets"]!.AsArray().Select(v => v!.ToString()).ToList();
            if (policy.ScopeKind != "interaction" && answer["interactionTargets"]!.AsArray().Count != 0)
                throw Failure("CONFIRMATION_SCOPE_UNRESOLVED", policy.Id, "Interaction targets cannot extend an operation or effect scope.");
            policy.Status = "resolved";
            Validate(state, policy);
            state.Events.Add(new("confirmation_policy_resolved", "confirmation_scope", DateTimeOffset.UtcNow, policy.TargetOperationIds.Count));
            System.Diagnostics.Activity.Current?.AddEvent(new("planning.confirmation_policy", tags: new()
            { ["policy_id"] = policy.Id, ["scope"] = policy.ScopeKind, ["rule"] = policy.Rule, ["target_count"] = policy.TargetOperationIds.Count }));
        }
        state.ScopedPolicies = pending.Where(p => p.Status != "not_applicable").ToList();
        foreach (var operation in operations) _ = Effective(state.ScopedPolicies, operation.Id);
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
