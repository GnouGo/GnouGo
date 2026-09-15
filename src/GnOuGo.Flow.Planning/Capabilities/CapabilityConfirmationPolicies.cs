using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityConfirmationPolicies
{
    internal static CapabilityInventory Apply(CapabilityInventory inventory)
    {
        if (inventory.PolicyScopeVersion != 2)
            throw PlanningConfirmationPolicies.Failure("CONFIRMATION_SCOPE_UNRESOLVED", "inventory", "The inventory must be reassessed with scoped policy evidence.");
        var policies = JsonSerializer.Deserialize(JsonSerializer.Serialize(inventory.ScopedPolicies.ToList(), PlanningJsonContext.Default.ListPlanningScopedPolicy), PlanningJsonContext.Default.ListPlanningScopedPolicy)!;
        var ungovernedWrites = inventory.Operations.Where(o => o.ExternalEffectKind == "write" &&
            PlanningConfirmationPolicies.Effective(policies, o.Id) is null).Select(o => o.Id).Order(StringComparer.Ordinal).ToList();
        if (ungovernedWrites.Count > 0) policies.Add(new()
        {
            Id = "platform_write_permission", Rule = "require_confirmation", ScopeKind = "effect", Target = "write",
            Applicability = "unless_explicit", Origin = "platform_default", Status = "resolved", TargetOperationIds = ungovernedWrites,
            EvidenceFingerprint = PlanningGraphCompiler.Fingerprint("platform_write_permission:v1:" + string.Join('|', ungovernedWrites))
        });
        var governed = inventory.Operations.Select(o => (Operation: o, Policy: PlanningConfirmationPolicies.Effective(policies, o.Id)))
            .Where(p => p.Policy?.Rule == "require_confirmation").ToArray();
        var operations = inventory.Operations.ToList();
        foreach (var group in governed.GroupBy(g => g.Policy!.Id, StringComparer.Ordinal))
        {
            var policy = policies.Single(p => p.Id == group.Key);
            var targets = group.Select(g => g.Operation).OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
            var existing = targets.Select(o => o.DecisionSourceOperationId).Distinct(StringComparer.Ordinal).ToArray();
            // Reuse only the permission already declared for every governed action.
            var permission = existing.Length == 1 && inventory.Operations.Any(o => o.Id == existing[0] && o.ExecutionKind == "human_interaction")
                ? existing[0] : "platform_confirm_action_" + PlanningGraphCompiler.Fingerprint(policy.Id + ":" + string.Join('|', targets.Select(o => o.Id)))[..16];
            policy.PermissionOperationId = permission;
            policy.TargetOperationIds = targets.Select(o => o.Id).ToList();
            if (!operations.Any(o => o.Id == permission))
            {
                var common = targets.Select(o => o.InputOperationIds.Where(id => !policy.TargetOperationIds.Contains(id)).ToHashSet(StringComparer.Ordinal))
                    .Aggregate((left, right) => { left.IntersectWith(right); return left; });
                operations.Add(new(permission, ScopedConfirmationOperationDescription, true, "human_interaction", "none")
                { PermissionPolicyId = policy.Id, InputOperationIds = common.Order(StringComparer.Ordinal).ToArray() });
            }
            foreach (var target in targets)
            {
                var index = operations.FindIndex(o => o.Id == target.Id);
                operations[index] = target with { InputOperationIds = target.InputOperationIds.Append(permission).Distinct(StringComparer.Ordinal).ToArray() };
            }
        }
        foreach (var inactive in policies.Where(p => p.Rule == "require_confirmation" && p.PermissionOperationId is null))
            inactive.TargetOperationIds.Clear();
        return inventory with { Operations = operations, ScopedPolicies = policies };
    }
}
