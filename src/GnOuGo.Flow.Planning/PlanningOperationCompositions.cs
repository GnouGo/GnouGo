using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Operation dependencies terminate at an explicitly owned linear composition.</summary>
internal static class PlanningOperationCompositions
{
    internal static PlanningNode? Owner(PlanningWorkflow workflow, PlanningNode node, PlanningPreparation preparation)
    {
        foreach (var parent in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
        {
            if (parent.Type != "sequence" || parent.If is not null || parent.OperationIds.Count == 0 ||
                parent.Steps.Count < 2 || parent.Steps[^1].Type is not ("mcp.call" or "set")) continue;
            if (!UnboundOrLocal(parent, parent.OperationIds, preparation)) continue;
            var members = PlanningGraphCompiler.Enumerate(parent.Steps).ToArray();
            if (!members.Contains(node)) continue;
            // sequence already returns every child result. Independent reads form
            // a collection, not a pipeline ending at the last tool. Its argument
            // contract need not accept the other observations. Keep each read's
            // own locked dependencies; unknown or mutating contracts stay strict.
            if (members.All(member => member.Type == "mcp.call" &&
                preparation.Capabilities.FirstOrDefault(c => c.Id == member.CapabilityId)?.EffectKind == "read")) continue;
            var operations = parent.OperationIds.Order(StringComparer.Ordinal).ToArray();
            bool Owned(PlanningNode member)
            {
                if (member.If is not null || !member.OperationIds.Order(StringComparer.Ordinal).SequenceEqual(operations)) return false;
                if (member.Type is "loop.sequential" or "loop.parallel" or "sequence")
                    return UnboundOrLocal(member, operations, preparation) &&
                        member.Steps.Count > 0 && member.Cases.Count == 0 && member.Default.Count == 0 && member.Branches.Count == 0;
                return member.Type is "mcp.call" or "set" &&
                    preparation.Capabilities.FirstOrDefault(c => c.Id == member.CapabilityId) is { } capability &&
                    capability.OperationIds.Order(StringComparer.Ordinal).SequenceEqual(operations);
            }
            if (members.All(Owned) && members.Where(n => n.CapabilityId is not null).Select(n => n.CapabilityId).Distinct(StringComparer.Ordinal).Count() > 1)
                return parent;
        }
        return null;
    }

    private static bool UnboundOrLocal(PlanningNode node, IEnumerable<string> operations, PlanningPreparation preparation) =>
        node.CapabilityId is null || preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId) is { Resolution: "local" } local &&
        PlanningCapabilityBindings.Supports(local, node.Type) && local.OperationIds.Order(StringComparer.Ordinal).SequenceEqual(operations.Order(StringComparer.Ordinal));

    internal static IReadOnlyList<string> RequiredInputs(PlanningWorkflow workflow, PlanningNode node, PlanningPreparation preparation)
    {
        var owner = Owner(workflow, node, preparation);
        if (owner is null) return node.Type == "workflow.call"
            ? preparation.Capabilities.Where(c => c.OperationIds.Intersect(node.OperationIds, StringComparer.Ordinal).Any()).SelectMany(c => c.InputOperationIds).Distinct(StringComparer.Ordinal).ToArray()
            : preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId)?.InputOperationIds ?? [];
        if (owner.Steps[^1] != node) return [];
        return PlanningGraphCompiler.Enumerate([owner]).Where(n => n.CapabilityId is not null)
            .SelectMany(n => preparation.Capabilities.Single(c => c.Id == n.CapabilityId).InputOperationIds).Distinct(StringComparer.Ordinal).ToArray();
    }
}
