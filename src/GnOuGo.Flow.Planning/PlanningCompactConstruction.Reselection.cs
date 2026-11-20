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
        var feedback = new JsonObject
        {
            ["scope"] = compact.ReplacementScope,
            ["requirementIds"] = requirements.DeepClone(),
            ["reason"] = rejected["reason"]?.DeepClone(),
            ["evidence"] = "rejected:" + (compact.RejectedSteps.Count - 1)
        };
        if (compact.CurrentStep is not { DiscoveryComplete: true, Kind: { } kind } ||
            !NestedControlKinds.Contains(kind, StringComparer.Ordinal) || !NestedControlKinds.Contains(previous["kind"]?.ToString(), StringComparer.Ordinal) ||
            state.Construction!.Candidate is not null || rejected["candidateHash"] is not null ||
            rejected["diagnostics"] is not JsonArray { Count: 1 } diagnostics || diagnostics[0]?["code"]?.ToString() != "CONSTRUCTION_TENTATIVE_SELECTION" ||
            !BusinessFragmentKinds(state).Contains(kind)) return feedback;

        // Selection has already seen the earlier model explanation. Draft against
        // the actual replacement and child forms, with the explanation still available as evidence.
        feedback.Remove("reason");
        feedback["currentControl"] = ControlContract(kind);
        var nested = new JsonObject();
        foreach (var child in NestedControlKinds.Where(child => child != kind)) nested[child] = ControlContract(child);
        feedback["nestedControls"] = nested;
        return feedback;

        JsonObject ControlContract(string control)
        {
            var shape = PlanningSchemas.TaskShapes(state, control).Single()!;
            var integerFields = new JsonObject();
            foreach (var (name, field) in shape["properties"]!.AsObject())
                if (field?["type"]?.ToString() == "integer") integerFields[name] = field.DeepClone();
            return new() { ["kind"] = control, ["required"] = shape["required"]!.DeepClone(), ["integerFields"] = integerFields };
        }
    }
}
