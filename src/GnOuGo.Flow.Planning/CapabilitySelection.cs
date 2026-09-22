using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;
namespace GnOuGo.Flow.Planning;

/// <summary>Semantic selection among completely covered matches, before loading binding contracts.</summary>
internal static class CapabilitySelection
{
    internal static async Task ApplyAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct, string purpose = "selection")
    {
        var decisions = CapabilityGrounder.Decisions(state);
        var ambiguous = decisions.Where(d => d.Matches.Count > 1).ToList();
        var established = decisions.Where(d => d.Matches.Count <= 1).Select(d => new GroundingSelection(d.ActionId,
            d.Matches.Select(m => m.CapabilityId).ToList(), d.Matches.Count == 0 ? "Native implementation after complete catalog coverage." : "Only viable match after complete catalog coverage.")).ToList();
        if (ambiguous.Count == 0)
        {
            state.Grounding!.Selections = established;
            return;
        }
        var schema = Schema(ambiguous);
        var ids = decisions.SelectMany(d => d.Matches).Select(m => m.CapabilityId).ToHashSet(StringComparer.Ordinal);
        var prompt = """
            Select the smallest sufficient implementation for each business action from its viable matches after complete catalog coverage.
            For each action, mark its issued capabilities true when selected and false otherwise. Prefer direct declared operations over broad delegated work when they satisfy the action.
            Each action's capability map in the response schema is its complete viable match set. Give one concise sentence explaining each selection.
            Multiple capabilities are appropriate only when the action requires their combined behavior, never to retain alternative implementations.
            Select a compatible implementation across the complete workflow. Required artifact inputs need original authoritative producers among the selected actions.
            Neither a model transformation nor an opaque observation establishes artifact identity. Prefer a viable implementation whose prerequisites can actually be supplied.
            Preserve every required business outcome. Explain the chosen behavior using catalog evidence. Binding checks the authoritative contracts next.
            Native calculations and transformations may select no capabilities. Required external actions must select at least one match.
            """ + "\n" + PlanningJsonTransport.Prompt(new JsonObject { ["request"] = state.Request.Prompt, ["actions"] = new JsonArray(SemanticPlanning.Actions(state.SemanticPlan!).Where(a => ambiguous.Any(d => d.ActionId == a.Id)).Select(a => (JsonNode)SemanticPlanning.ActionContext(a)).ToArray()),
                ["establishedSelections"] = new JsonArray(established.Select(s => (JsonNode)new JsonObject { ["actionId"] = s.ActionId,
                    ["capabilities"] = new JsonArray(s.CapabilityIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) }).ToArray()),
                ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics),
                ["capabilities"] = new JsonArray(state.Catalog!.Capabilities.Where(c => ids.Contains(c.Id)).Select(c => (JsonNode)new JsonObject {
                    ["id"] = c.Id, ["description"] = c.Description, ["effect"] = c.EffectKind,
                    ["artifacts"] = JsonSerializer.SerializeToNode(c.ArtifactContract, PlanningJsonContext.Default.McpArtifactContract) }).ToArray()) });
        var rejected = state.RejectedProposalHash;
        var response = await PlanningModelCalls.CallAsync(state, runtime, purpose, prompt, schema, ct);
        var selections = response["selections"]!.AsObject().Select(s => new GroundingSelection(s.Key,
            s.Value!["capabilities"]!.AsObject().Where(c => c.Value!.GetValue<bool>()).Select(c => c.Key).ToList(), s.Value["reason"]!.GetValue<string>())).Concat(established).ToList();
        try { Validate(state, selections); }
        catch (PlanningResponseException)
        {
            state.RejectedProposalHash = PlanningGraphCompiler.Fingerprint(new JsonArray(selections.OrderBy(s => s.ActionId, StringComparer.Ordinal)
                .Select(s => (JsonNode)new JsonArray(JsonValue.Create(s.ActionId), new JsonArray(s.CapabilityIds.Order(StringComparer.Ordinal)
                    .Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()))).ToArray()).ToJsonString());
            if (state.RejectedProposalHash == rejected) throw new WorkflowRuntimeException("REPLAN_NO_PROGRESS", "The model repeated the same invalid capability selection.");
            throw;
        }
        state.Grounding!.Selections = selections;
    }
    internal static void Validate(PlanningSession state, List<GroundingSelection> selections)
    {
        var decisions = CapabilityGrounder.Decisions(state);
        var errors = new List<PlanningDiagnostic>();
        foreach (var action in decisions)
        {
            var matches = selections.Where(s => s.ActionId == action.ActionId).ToArray();
            var location = "/actions/" + action.ActionId;
            if (matches.Length != 1) { errors.Add(new("GROUNDING_SELECTION_INVALID", location, "Exactly one selection is required for this action.")); continue; }
            var selection = matches[0];
            if (string.IsNullOrWhiteSpace(selection.Reason)) errors.Add(new("GROUNDING_SELECTION_INVALID", location, "Explain the selected implementation using its catalog evidence."));
            if (selection.CapabilityIds.Distinct().Count() != selection.CapabilityIds.Count)
                errors.Add(new("GROUNDING_SELECTION_INVALID", location, "Select each capability at most once."));
            foreach (var id in selection.CapabilityIds.Where(id => !action.Matches.Any(m => m.CapabilityId == id)))
                errors.Add(new("GROUNDING_SELECTION_INVALID", location, "Capability " + id + " was not issued as a semantic match for this action. Select only from this action's matches."));
            if (SemanticPlanning.External(SemanticPlanning.Actions(state.SemanticPlan!).Single(a => a.Id == action.ActionId)) && selection.CapabilityIds.Count == 0)
                errors.Add(new("GROUNDING_SELECTION_INVALID", location, "This external business action requires at least one of its semantically matched capabilities."));
        }
        foreach (var selection in selections.Where(s => !decisions.Any(d => d.ActionId == s.ActionId)))
            errors.Add(new("GROUNDING_SELECTION_INVALID", "/actions/" + selection.ActionId, "This action was not issued for selection."));
        if (errors.Count > 0) throw new PlanningResponseException(errors);
    }
    internal static JsonObject Schema(IReadOnlyList<GroundingDecision> decisions) => PlanningSchemas.Object(("selections", PlanningSchemas.Object(
        decisions.Select(d => (d.ActionId, PlanningSchemas.Object(
            ("capabilities", PlanningSchemas.Object(d.Matches.Select(m => (m.CapabilityId, PlanningSchemas.Type("boolean"))).ToArray())),
            ("reason", PlanningSchemas.String())))).ToArray())));
}
