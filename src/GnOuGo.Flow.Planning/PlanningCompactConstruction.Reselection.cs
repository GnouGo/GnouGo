using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private static JsonObject? MatchingRejectedStep(PlanningSession state)
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
        return rejected;
    }

    private static bool WithdrewTentativeRequirementClosure(PlanningSession state)
    {
        if (State(state).CurrentStep is not { Kind: "close", CloseRequirementIds.Count: 0, CloseScopes.Count: 0 } ||
            MatchingRejectedStep(state) is not { } rejected) return false;
        return rejected["candidateHash"] is null && rejected["extension"] is null &&
            rejected["reviews"] is JsonArray { Count: 0 } &&
            rejected["step"]?["kind"]?.ToString() == "close" &&
            rejected["step"]?["closeRequirementIds"] is JsonArray { Count: > 0 } &&
            rejected["step"]?["closeScopes"] is JsonArray { Count: 0 } &&
            rejected["diagnostics"] is JsonArray { Count: 1 } diagnostics &&
            diagnostics[0]?["code"]?.ToString() == "CONSTRUCTION_TENTATIVE_SELECTION";
    }

    private static JsonObject? ReselectionContext(PlanningSession state)
    {
        if (MatchingRejectedStep(state) is not { } rejected) return null;
        var compact = State(state);
        var previous = rejected["step"]!.AsObject();
        var feedback = new JsonObject
        {
            ["scope"] = compact.ReplacementScope,
            ["requirementIds"] = previous["requirementIds"]!.DeepClone(),
            ["reason"] = rejected["reason"]?.DeepClone(),
            ["evidence"] = "rejected:" + (compact.RejectedSteps.Count - 1)
        };
        var targets = ReplacementRollbackTargets(state, compact, rejected, previous);
        if (targets.Length > 0)
        {
            feedback["rollbackTaskIds"] = List(targets);
            feedback["rollbackEffect"] = "When an appended step would execute too late, select rollback with one cited task and the ordering reason. Its dependent region reopens for reconstruction. Preserve original objectives, effects, literals, cleanup and relative order; ordinary validation and business review follow.";
        }
        if (compact.CurrentStep is not { DiscoveryComplete: true, Kind: { } kind } ||
            !NestedControlKinds.Contains(kind, StringComparer.Ordinal) || !NestedControlKinds.Contains(previous["kind"]?.ToString(), StringComparer.Ordinal) ||
            state.Construction!.Candidate is not null || rejected["candidateHash"] is not null ||
            rejected["diagnostics"] is not JsonArray { Count: 1 } diagnostics || diagnostics[0]?["code"]?.ToString() != "CONSTRUCTION_TENTATIVE_SELECTION" ||
            !BusinessFragmentKinds(state).Contains(kind)) return feedback;

        // Selection has already seen the earlier model explanation. Draft against
        // the actual replacement and child forms, with the explanation still available as evidence.
        feedback.Remove("reason");
        feedback["evidenceRole"] = "Previous tentative selection. Use the current and nested control contracts below for this draft.";
        feedback["currentControl"] = ControlContract(kind);
        var nested = new JsonObject();
        foreach (var child in NestedControlKinds) nested[child] = ControlContract(child);
        feedback["nestedControls"] = nested;
        return feedback;

        JsonObject ControlContract(string control)
        {
            var shape = PlanningSchemas.TaskShapes(state, control, compactDraft: true).Single()!;
            var integerFields = new JsonObject();
            foreach (var (name, field) in shape["properties"]!.AsObject())
                if (field?["type"]?.ToString() == "integer") integerFields[name] = field.DeepClone();
            return new() { ["kind"] = control, ["required"] = shape["required"]!.DeepClone(), ["integerFields"] = integerFields };
        }
    }

    internal static void RollbackReplacement(PlanningSession state, JsonNode rollback)
    {
        var task = rollback["taskId"]!.ToString();
        if (ReselectionContext(state)?["rollbackTaskIds"] is not JsonArray ids || !ids.Any(id => id!.ToString() == task))
            PlanningConstruction.Fail("CONSTRUCTION_ROLLBACK_SCOPE", "/rollback/taskId", "The cited task must still belong to the current rejected claim.");
        PlanningConstruction.Rollback(state, task, rollback["reason"]!.ToString());
    }

    private static string[] ReplacementRollbackTargets(PlanningSession state, PlanningCompactConstructionState compact,
        JsonObject rejected, JsonObject previous)
    {
        if (compact.CurrentStep is not null || state.Construction!.Candidate is not null || state.EditablePaths is not null ||
            state.ApprovedHash is not null || compact.SuspendedSteps?.Any(Active) == true ||
            rejected["disposition"]?.ToString() != "abandon" || state.Request.Options["automatic_correction"]?.ToString() == "false" ||
            !PlanningModelCalls.HasRepairCapacity(state) || !PlanningModelCalls.HasDraftRepairCapacity(state, state.Construction.DraftCorrections))
            return [];

        var review = (rejected["reviews"] as JsonArray)?.OfType<JsonObject>().LastOrDefault(row =>
            row["purpose"]?.ToString() == PlanningConstruction.LocalPurpose);
        var baseline = previous["baseHash"]!.ToString();
        if (review?["outcome"]?.ToString() != "needs_change" || review["candidateHash"]?.ToString() != baseline ||
            review["baselineHash"]?.ToString() != baseline || review["pendingRepairPaths"] is not JsonArray paths ||
            review["defectWitness"] is not JsonObject || !HybridWorkflowPlanner.BusinessSourcesAndContractsValid(state, review))
            return [];

        var symbols = new TaskPlanSymbols(state.Plan!);
        if (state.Construction.RebuildTasks.Any(task => !symbols.Tasks.ContainsKey(task.Id))) return [];
        return paths.Select(path => path?.ToString().Split('/')).OfType<string[]>()
            .Where(parts => parts.Length >= 4 && parts[0] == "" && parts[1] == "tasks" &&
                symbols.Tasks.TryGetValue(parts[2], out var task) && task.Scope.Path == compact.ReplacementScope)
            .Select(parts => parts[2]).Distinct(StringComparer.Ordinal).ToArray();
    }
}
