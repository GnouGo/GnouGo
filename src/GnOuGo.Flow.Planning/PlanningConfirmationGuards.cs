using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Materializes scoped permissions using the existing locked decision and topology contracts.</summary>
internal static class PlanningConfirmationGuards
{
    internal static string Allow(PlanningScopedPolicy policy) => "permit_" + PlanningGraphCompiler.Fingerprint(policy.Id)[..12];
    internal static string Deny(PlanningScopedPolicy policy) => "deny_" + PlanningGraphCompiler.Fingerprint(policy.Id)[..12];
    private static string Prefix(PlanningScopedPolicy policy) => "permission_" + PlanningGraphCompiler.Fingerprint(policy.Id)[..12] + "_";
    internal static bool IsGuard(string key, PlanningPreparation preparation)
        => Required(preparation).Any(p => key.StartsWith(Prefix(p), StringComparison.Ordinal));
    internal static IEnumerable<PlanningScopedPolicy> Required(PlanningPreparation preparation)
        => preparation.ScopedPolicies.Where(p => p.Rule == "require_confirmation" && p.TargetOperationIds.Count > 0);

    internal static void Lock(PlanningPreparation preparation)
    {
        foreach (var policy in Required(preparation))
        {
            var source = preparation.Capabilities.SingleOrDefault(c => c.StepType == "human.input" && c.OperationIds.Contains(policy.PermissionOperationId ?? ""));
            if (source is null || policy.TargetOperationIds.Contains(policy.PermissionOperationId!))
                throw PlanningConfirmationPolicies.Failure("CONFIRMATION_SCOPE_UNRESOLVED", policy.Id, "The protected actions have no distinct locked human permission source.");
            preparation.Decisions.Add(new()
            {
                Group = policy.Id, SourceOperationId = policy.PermissionOperationId!, SourceCapabilityId = source.Id, SourcePointer = "/response",
                ContractSource = PlanningDecisionContract.HumanConfirmation, ResponseSchema = new() { ["type"] = "boolean" },
                AllowedValues = [Allow(policy), Deny(policy)], NoEffectValues = [Deny(policy)], EffectOperationIds = policy.TargetOperationIds.ToList(),
                PermissionOperationIds = [policy.PermissionOperationId!], InputOperationIds = [policy.PermissionOperationId!]
            });
            if (!preparation.Interactions.Any(i => i.OperationId == policy.PermissionOperationId))
                preparation.Interactions.Add(new() { OperationId = policy.PermissionOperationId!, CapabilityId = source.Id });
        }
    }

    internal static void Wrap(PlanningBehaviorPlan plan, PlanningPreparation preparation)
    {
        foreach (var workflow in plan.Workflows) { Visit(workflow.Steps); Visit(workflow.Finally); }
        void Visit(List<PlanningBehaviorNode> nodes)
        {
            foreach (var node in nodes.ToArray())
            {
                Visit(node.Steps); foreach (var outcome in node.Outcomes) Visit(outcome.Steps);
                foreach (var policy in Required(preparation).Where(p => node.OperationIds.Intersect(p.TargetOperationIds, StringComparer.Ordinal).Any()).OrderBy(p => p.Id, StringComparer.Ordinal))
                {
                    var at = nodes.IndexOf(node);
                    if (at < 0) throw PlanningConfirmationPolicies.Failure("CONFIRMATION_POLICY_CONFLICT", node.Key, "An action requires incompatible permission scopes.");
                    nodes[at] = new()
                    {
                        Key = Prefix(policy) + PlanningGraphCompiler.Fingerprint(node.Key)[..12], Kind = "decision", Purpose = "Execute the governed action only after its explicit human permission.",
                        Outcomes = [new(Allow(policy), "Permission accepted", false, [node]), new(Deny(policy), "Permission rejected: no action", false, []),
                            new("default", "Unavailable permission: no action", true, [])]
                    };
                }
            }
        }
    }

    internal static IEnumerable<PlanningDiagnostic> BehaviorFindings(PlanningBehaviorPlan plan, PlanningPreparation preparation)
    {
        var findings = new List<PlanningDiagnostic>();
        foreach (var workflow in plan.Workflows)
        { Visit(workflow.Steps, [], "/workflows/@" + workflow.Key + "/steps"); Visit(workflow.Finally, [], "/workflows/@" + workflow.Key + "/finally"); }
        return findings;
        void Visit(IEnumerable<PlanningBehaviorNode> nodes, HashSet<string> permissions, string path)
        {
            foreach (var node in nodes)
            {
                var location = path + "/@" + node.Key;
                Check(node.OperationIds.Concat(preparation.Capabilities.Where(c => c.Id == node.CapabilityId).SelectMany(c => c.OperationIds)), permissions, location, preparation, findings);
                Visit(node.Steps, permissions, location + "/steps");
                foreach (var outcome in node.Outcomes)
                {
                    var allowed = new HashSet<string>(permissions, StringComparer.Ordinal);
                    foreach (var policy in Required(preparation).Where(p => node.Kind == "decision" && node.Key.StartsWith(Prefix(p), StringComparison.Ordinal) && outcome.Key == Allow(p) && !outcome.IsDefault)) allowed.Add(policy.Id);
                    Visit(outcome.Steps, allowed, location + "/outcomes/@" + outcome.Key);
                }
            }
        }
    }

    internal static IEnumerable<PlanningDiagnostic> GraphFindings(PlanningGraph graph, PlanningPreparation preparation)
    {
        var findings = new List<PlanningDiagnostic>();
        foreach (var workflow in graph.Workflows)
        { Visit(workflow, workflow.Steps, [], "/workflows/@" + workflow.Key + "/steps"); Visit(workflow, workflow.Finally, [], "/workflows/@" + workflow.Key + "/finally"); }
        return findings;
        void Visit(PlanningWorkflow workflow, IEnumerable<PlanningNode> nodes, HashSet<string> permissions, string path)
        {
            foreach (var node in nodes)
            {
                var location = path + "/@" + node.Key;
                Check(node.OperationIds.Concat(preparation.Capabilities.Where(c => c.Id == node.CapabilityId).SelectMany(c => c.OperationIds)), permissions, location, preparation, findings);
                Visit(workflow, node.Steps, permissions, location + "/steps");
                Visit(workflow, node.Default, permissions, location + "/default");
                for (var i = 0; i < node.Branches.Count; i++) Visit(workflow, node.Branches[i].Steps, permissions, location + "/branches/" + i);
                foreach (var branch in node.Cases)
                {
                    var allowed = new HashSet<string>(permissions, StringComparer.Ordinal);
                    foreach (var policy in Required(preparation).Where(p => node.Type == "switch" && node.Key.StartsWith(Prefix(p), StringComparison.Ordinal) && branch.Value == Allow(p) && branch.When is null))
                    {
                        try
                        {
                            var expected = PlanningDecisionRouting.Resolve(workflow, node, preparation, graph);
                            if (node.Expr is not null && JsonSerializer.Serialize(expected, PlanningJsonContext.Default.PlanningValue) == JsonSerializer.Serialize(node.Expr, PlanningJsonContext.Default.PlanningValue)) allowed.Add(policy.Id);
                            else findings.Add(new("CONFIRMATION_GUARD_INVALID", location + "/expr", "The guard must consume the exact locked permission response."));
                        }
                        catch (InvalidOperationException) { findings.Add(new("CONFIRMATION_GUARD_INVALID", location + "/expr", "The locked permission source is unavailable at the protected action.")); }
                    }
                    Visit(workflow, branch.Steps, allowed, location + "/cases/" + (branch.Value ?? "default"));
                }
            }
        }
    }

    private static void Check(IEnumerable<string> operations, HashSet<string> permissions, string location, PlanningPreparation preparation, List<PlanningDiagnostic> findings)
    {
        foreach (var policy in preparation.ScopedPolicies.Where(p => p.TargetOperationIds.Intersect(operations, StringComparer.Ordinal).Any()))
        {
            if (policy.Status != "resolved") findings.Add(new("CONFIRMATION_SCOPE_UNRESOLVED", location, "The operation policy has no current scope proof."));
            else if (policy.Rule == "require_confirmation" && !permissions.Contains(policy.Id)) findings.Add(new("CONFIRMATION_GUARD_REQUIRED", location, "The action must remain in the accepted branch of its scoped permission: " + policy.Id));
            else if (policy.Rule == "forbid_interaction") findings.Add(new("INTERACTION_POLICY_DENIED", location, "The exact human interaction is forbidden: " + policy.Id));
        }
    }
}
