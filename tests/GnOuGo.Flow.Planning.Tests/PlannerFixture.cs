using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Flow.Planning.Tests;
internal static class PlannerFixture
{
    internal static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static PlanningSession Session() => new() { Request = new() { TenantId = "test", Prompt = "Return a greeting" } };
    internal static PlanningGraph Greeting(string message = "Hello") => new()
    {
        Summary = "Return a greeting", Workflows = [new()
        {
            Steps = [new() { Key = "greet", Type = "set", Input = PlanningCorpus.Obj(("message", PlanningCorpus.Text(message))) }],
            Outputs = [new() { Name = "message", Value = PlanningCorpus.Ref("output", "greet", "message") }]
        }]
    };
    internal static PlanningRequirements Requirements() => new() { Summary = "Return a greeting", Outcomes = [new("message", "Return a greeting")] };
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
    internal PlanningProposal Proposal = new() { Requirements = PlannerFixture.Requirements(), Graph = PlannerFixture.Greeting() };
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
        return Task.FromResult(Respond?.Invoke(request, purpose) ?? Response(request, Proposal));
    }
    internal static LLMResponse Response(LLMRequest request, PlanningProposal proposal) => new()
    { Json = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal), request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => Validation is null ? Actual.ValidateAsync(request, ct) : Task.FromResult(Validation);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => CatalogChanges is null ? Actual.ValidateCatalogAsync(catalog, ct) : Task.FromResult(CatalogChanges);
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct) { Checkpoints.Add(PlannerFixture.Clone(state)); return Task.CompletedTask; }
}
