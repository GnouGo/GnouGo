using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>Semantic selection among completely covered matches, before loading binding contracts.</summary>
internal static class CapabilitySelection
{
    internal static async Task ApplyAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var decisions = CapabilityGrounder.Decisions(state);
        if (decisions.All(d => d.Matches.Count <= 1))
        {
            state.Grounding!.Selections = decisions.Select(d => new GroundingSelection(d.ActionId, d.Matches.Select(m => m.CapabilityId).ToList(), "Only viable match after complete catalog coverage.")).ToList();
            return;
        }
        var schema = PlanningSchemas.Object(("selections", PlanningSchemas.Array(PlanningSchemas.Object(
            ("actionId", PlanningSchemas.Enum(decisions.Select(d => d.ActionId).ToArray())),
            ("capabilityIds", PlanningSchemas.Array(PlanningSchemas.Enum(decisions.SelectMany(d => d.Matches).Select(m => m.CapabilityId).Distinct().ToArray()))),
            ("reason", PlanningSchemas.String())))));
        var ids = decisions.SelectMany(d => d.Matches).Select(m => m.CapabilityId).ToHashSet(StringComparer.Ordinal);
        var prompt = """
            Select the smallest sufficient implementation for each business action from its viable matches after complete catalog coverage.
            Choose exact issued capability IDs. Prefer direct declared operations over broad delegated work when they satisfy the action.
            Multiple capabilities are appropriate only when the action requires their combined behavior, never to retain alternative implementations.
            Preserve every required business outcome. Explain the chosen behavior using catalog evidence. Binding checks the authoritative contracts next.
            Native calculations and transformations may select no capabilities. Required external actions must select at least one match.
            """ + "\n" + PlanningJsonTransport.Prompt(new JsonObject { ["request"] = state.Request.Prompt, ["actions"] = new JsonArray(SemanticPlanning.Actions(state.SemanticPlan!).Where(SemanticPlanning.Groundable).Select(a => (JsonNode)SemanticPlanning.ActionContext(a)).ToArray()),
                ["matches"] = new JsonArray(decisions.Select(d => (JsonNode)new JsonObject { ["actionId"] = d.ActionId,
                    ["matches"] = new JsonArray(d.Matches.Select(m => (JsonNode)new JsonObject { ["id"] = m.CapabilityId, ["evidence"] = m.Reason }).ToArray()) }).ToArray()),
                ["capabilities"] = new JsonArray(state.Catalog!.Capabilities.Where(c => ids.Contains(c.Id)).Select(c => (JsonNode)new JsonObject {
                    ["id"] = c.Id, ["description"] = c.Description, ["effect"] = c.EffectKind }).ToArray()) });
        var response = await PlanningModelCalls.CallAsync(state, runtime, "selection", prompt, schema, ct);
        var selections = response["selections"]!.AsArray().Select(s => new GroundingSelection(s!["actionId"]!.GetValue<string>(),
            s["capabilityIds"]!.AsArray().Select(c => c!.GetValue<string>()).ToList(), s["reason"]!.GetValue<string>())).ToList();
        Validate(state, selections);
        state.Grounding!.Selections = selections;
    }
    internal static void Validate(PlanningSession state, List<GroundingSelection> selections)
    {
        var decisions = CapabilityGrounder.Decisions(state);
        if (!decisions.Select(d => d.ActionId).Order().SequenceEqual(selections.Select(s => s.ActionId).Order()) || selections.Any(s =>
            string.IsNullOrWhiteSpace(s.Reason) || s.CapabilityIds.Distinct().Count() != s.CapabilityIds.Count ||
            s.CapabilityIds.Any(id => !decisions.Single(d => d.ActionId == s.ActionId).Matches.Any(m => m.CapabilityId == id)) ||
            SemanticPlanning.External(SemanticPlanning.Actions(state.SemanticPlan!).Single(a => a.Id == s.ActionId)) && s.CapabilityIds.Count == 0))
            throw new PlanningResponseException([new("GROUNDING_SELECTION_INVALID", "/grounding/selections", "Every action needs a consistent selection from its completely covered semantic matches.")]);
    }
}
