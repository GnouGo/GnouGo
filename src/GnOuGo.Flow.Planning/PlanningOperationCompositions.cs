using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Operation dependencies terminate at an explicitly owned linear composition.</summary>
internal static class PlanningOperationCompositions
{
    internal static PlanningNode? Owner(PlanningWorkflow workflow, PlanningNode node, PlanningPreparation preparation)
    {
        return PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(parent =>
            parent.Type == "sequence" && parent.CapabilityId is null && parent.If is null && parent.OperationIds.Count > 0 &&
            parent.Steps.Count > 1 && parent.Steps.Contains(node) &&
            parent.Steps.Select(n => n.CapabilityId).Distinct(StringComparer.Ordinal).Count() > 1 &&
            parent.Steps.All(n => n.Type is "mcp.call" or "set" && n.If is null &&
                n.OperationIds.Order(StringComparer.Ordinal).SequenceEqual(parent.OperationIds.Order(StringComparer.Ordinal)) &&
                preparation.Capabilities.FirstOrDefault(c => c.Id == n.CapabilityId) is { } capability &&
                capability.OperationIds.Order(StringComparer.Ordinal).SequenceEqual(parent.OperationIds.Order(StringComparer.Ordinal))));
    }

    internal static IReadOnlyList<string> RequiredInputs(PlanningWorkflow workflow, PlanningNode node, PlanningPreparation preparation)
    {
        var owner = Owner(workflow, node, preparation);
        if (owner is null) return preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId)?.InputOperationIds ?? [];
        if (owner.Steps[^1] != node) return [];
        return owner.Steps.SelectMany(n => preparation.Capabilities.Single(c => c.Id == n.CapabilityId).InputOperationIds).Distinct(StringComparer.Ordinal).ToArray();
    }
}
