using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private static JsonObject? ReselectionContext(PlanningSession state)
    {
        var compact = State(state);
        if (compact.ReplacementScope is null || compact.ReplacementRequirements is null ||
            compact.RejectedSteps.LastOrDefault() is not JsonObject rejected || string.IsNullOrWhiteSpace(rejected["reason"]?.ToString()) ||
            rejected["step"] is not JsonObject previous ||
            previous["baseHash"]?.ToString() != PlanningConstruction.Hash(state.Plan) ||
            previous["scope"]?.ToString() != compact.ReplacementScope ||
            previous["blockId"]?.ToString() != compact.CurrentBlockId ||
            previous["requirementIds"] is not JsonArray requirements ||
            !requirements.Select(id => id!.ToString()).ToHashSet(StringComparer.Ordinal).SetEquals(compact.ReplacementRequirements)) return null;
        if (compact.CurrentStep is { } current && (current.BaseHash != previous["baseHash"]!.ToString() ||
            current.Scope != compact.ReplacementScope || current.BlockId != compact.CurrentBlockId ||
            !current.RequirementIds.ToHashSet(StringComparer.Ordinal).SetEquals(compact.ReplacementRequirements))) return null;
        return new()
        {
            ["scope"] = compact.ReplacementScope,
            ["requirementIds"] = requirements.DeepClone(),
            ["reason"] = rejected["reason"]?.DeepClone(),
            ["evidence"] = "rejected:" + (compact.RejectedSteps.Count - 1)
        };
    }
}
