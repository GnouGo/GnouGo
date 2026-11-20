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
        if (compact.CurrentStep is not { DiscoveryComplete: true, Kind: { } kind } selected ||
            !NestedControlKinds.Contains(kind, StringComparer.Ordinal) || state.Construction!.Candidate is not null ||
            compact.RejectedSteps.Count < 2 || !compact.RejectedSteps.TakeLast(2).All(SameTentativeControl) ||
            !BusinessFragmentKinds(state).Contains(kind)) return feedback;

        // The selection already considered both model diagnoses. Keep them at
        // their evidence addresses and present the actual fields for drafting.
        var shape = PlanningSchemas.TaskShapes(state, kind).Single()!;
        var integerFields = new JsonObject();
        foreach (var (name, field) in shape["properties"]!.AsObject())
            if (field?["type"]?.ToString() == "integer") integerFields[name] = field.DeepClone();
        feedback.Remove("reason");
        feedback["currentControl"] = new JsonObject { ["kind"] = kind, ["required"] = shape["required"]!.DeepClone(),
            ["integerFields"] = integerFields };
        return feedback;

        bool SameTentativeControl(JsonObject entry) =>
            entry["candidateHash"] is null && entry["diagnostics"] is JsonArray { Count: 1 } diagnostics &&
            diagnostics[0]?["code"]?.ToString() == "CONSTRUCTION_TENTATIVE_SELECTION" &&
            entry["step"] is JsonObject prior && prior["kind"]?.ToString() == kind &&
            prior["baseHash"]?.ToString() == selected.BaseHash && prior["scope"]?.ToString() == selected.Scope &&
            prior["blockId"]?.ToString() == selected.BlockId && prior["groupId"]?.ToString() == selected.GroupId &&
            prior["requirementIds"] is JsonArray ids && selected.RequirementIds.ToHashSet(StringComparer.Ordinal).SetEquals(ids.Select(id => id!.ToString())) &&
            prior["operationIds"] is JsonArray operations && selected.OperationIds.ToHashSet(StringComparer.Ordinal).SetEquals(operations.Select(id => id!.ToString()));
    }
}
