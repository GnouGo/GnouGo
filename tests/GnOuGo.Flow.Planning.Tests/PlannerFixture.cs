using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Flow.Planning.Tests;
internal static class PlannerFixture
{
    internal static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static PlanningSession Session() => new() { IntentVersion = 2, Request = new() { TenantId = "test", Prompt = "Return a greeting" } };
    internal static PlanningGraph Greeting(string message = "Hello") => new()
    {
        Summary = "Return a greeting", Workflows = [new()
        {
            Steps = [new() { Key = "greet", Type = "set", Input = PlanningCorpus.Obj(("message", PlanningCorpus.Text(message))) }],
            Outputs = [new() { Name = "message", Value = PlanningCorpus.Ref("output", "greet", "message") }]
        }]
    };
    internal static PlanningRequirements Requirements() => new() { Summary = "Return a greeting", Inputs = [], Outcomes = [new("message", "Return a greeting")] };
    internal static PlanningSession Clone(PlanningSession state) => JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
    internal static async Task<PlanningSession> RunAsync(TestRuntime runtime, PlanningSession? state = null)
    {
        state ??= Session(); var planner = new HybridWorkflowPlanner();
        for (var i = 0; i < 30 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        return state;
    }
}
internal sealed class TestRuntime : IPlanningRuntime
{
    internal readonly List<LLMRequest> Calls = [];
    internal readonly List<PlanningSession> Checkpoints = [];
    internal readonly WorkflowPlanningRuntime Actual;
    internal PlanningProposal Proposal = new() { Requirements = PlannerFixture.Requirements(), Plan = PlanningCorpus.Greeting() };
    internal Func<LLMRequest, string, LLMResponse>? Respond;
    internal IReadOnlyList<PlanningDiagnostic>? CatalogChanges;
    internal IReadOnlyList<PlanningDiagnostic>? Validation { get; set; }
    internal int Discoveries;
    public ICapabilityCatalog Capabilities { get; set; }
    internal TestRuntime(WorkflowEngine? engine = null)
    { Actual = new(engine ?? new(), (_, _) => Task.CompletedTask); Capabilities = Actual.Capabilities; }
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) { Discoveries++; return Actual.DiscoverAsync(request, ct); }
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        Calls.Add(request);
        var response = Respond?.Invoke(request, purpose) ?? Response(request, Proposal);
        if (PlanningRepairPatch.Issued(request.StructuredOutputSchema?.AsObject()) && response.Json?["plan"] is JsonObject plan)
            response.Json = PatchResponse(request, Checkpoints[^1], plan.Deserialize(PlanningJsonContext.Default.TaskPlan)!);
        return Task.FromResult(response);
    }
    internal static LLMResponse Response(LLMRequest request, PlanningProposal proposal) => new()
    { Json = PlanningRepairPatch.Issued(request.StructuredOutputSchema?.AsObject())
        ? proposal.Plan is not null
            ? new JsonObject { ["discoveryRequests"] = null, ["plan"] = JsonSerializer.SerializeToNode(proposal.Plan, PlanningJsonContext.Default.TaskPlan) }
            : PlanningCorpus.Transport(new JsonObject { ["discoveryRequests"] = JsonSerializer.SerializeToNode(proposal.DiscoveryRequests, PlanningJsonContext.Default.ListPlanningDiscoveryRequest), ["clarifications"] = JsonSerializer.SerializeToNode(proposal.Clarifications, PlanningJsonContext.Default.ListPlanningQuestion), ["patch"] = null }, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())
        : PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal), request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };

    // Existing scripted tests state the intended candidate. Convert only authorized
    // differences into explicit edits; never discard an attempted unrelated change.
    internal static JsonNode PatchResponse(LLMRequest request, PlanningSession state, TaskPlan candidate)
    {
        var violations = TaskPlanRevisions.Validate(state.Plan, candidate, state.RevisionScope, state.Catalog, state.EditablePaths).ToList();
        if (violations.Count > 0) return new JsonObject { ["plan"] = JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.TaskPlan) };
        var before = PlanningRepairPatch.Index(JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)!);
        var after = PlanningRepairPatch.Index(JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.TaskPlan)!);
        var edits = new JsonArray();
        foreach (var slot in RepairPatchTests.Slots(state))
        {
            var old = before.GetValueOrDefault(slot.Location)?.Node;
            var value = after.GetValueOrDefault(slot.Location)?.Node;
            var action = "replace";
            if (slot.Kind == "exports")
            {
                var existing = old!.AsArray().Select(o => o!["name"]!.ToString()).ToHashSet(StringComparer.Ordinal);
                value = new JsonArray(value!.AsArray().Where(o => !existing.Contains(o!["name"]!.ToString())).Select(o => o!.DeepClone()).ToArray());
                if (value.AsArray().Count == 0) continue;
                action = "add";
            }
            else
            {
                if (JsonNode.DeepEquals(old, value)) continue;
                if (slot.Kind is "value" or "reference" or "binding")
                {
                    if (value is null && slot.Actions.Contains("remove")) action = "remove";
                    else if (slot.Actions.Contains("remove_owned")) action = "remove_owned";
                    else if (slot.Kind == "binding") action = "add";
                    if (value is JsonObject binding && binding.ContainsKey("name")) value = binding["value"];
                }
            }
            var edit = new JsonObject { ["slot"] = slot.Id, ["action"] = action };
            if (action is not ("remove" or "remove_owned")) edit["value"] = value?.DeepClone();
            edits.Add((JsonNode)edit);
        }
        var result = new JsonObject { ["discoveryRequests"] = null, ["patch"] = new JsonObject { ["edits"] = edits } };
        return PlanningCorpus.Transport(result, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())!;
    }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => Validation is null ? Actual.ValidateAsync(request, ct) : Task.FromResult(Validation);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => CatalogChanges is null ? Actual.ValidateCatalogAsync(catalog, ct) : Task.FromResult(CatalogChanges);
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct) { Checkpoints.Add(PlannerFixture.Clone(state)); return Task.CompletedTask; }
}
