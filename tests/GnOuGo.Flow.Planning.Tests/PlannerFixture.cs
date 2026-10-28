using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Flow.Planning.Tests;
internal static class PlannerFixture
{
    internal static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static PlanningSession Session() => Capture(new() { Request = new() { TenantId = "test", Prompt = "Return a greeting",
        MaxModelCalls = 64, MaxReplanAttempts = 4, Generation = new("medium", 12_000, 8_192) } });
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
    internal static PlanningSession Capture(PlanningSession state)
    {
        PlanningSessionCreation.Capture(state);
        return Clone(state);
    }
    internal static async Task<PlanningSession> RunAsync(TestRuntime runtime, PlanningSession? state = null)
    {
        state ??= Session(); var planner = new HybridWorkflowPlanner();
        for (var i = 0; i < 30 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        return state;
    }
}
internal sealed class TestRuntime : IPlanningRuntime, IPlanningConstructionAdmission
{
    internal readonly List<LLMRequest> Calls = [];
    internal readonly List<PlanningSession> Checkpoints = [];
    internal Action<PlanningSession>? Checkpointed;
    internal readonly WorkflowPlanningRuntime Actual;
    internal TaskPlanFixture Proposal = new() { Requirements = PlannerFixture.Requirements(), Plan = PlanningCorpus.Greeting() };
    internal Func<LLMRequest, string, LLMResponse>? Respond;
    internal Action<LLMRequest, int, IReadOnlyList<int>>? AdmitConstruction { get; set; }
    internal IReadOnlyList<PlanningDiagnostic>? CatalogChanges { get; set; }
    internal IReadOnlyList<PlanningDiagnostic>? Validation { get; set; }
    internal int Discoveries;
    public ICapabilityCatalog Capabilities { get; set; }
    internal TestRuntime(WorkflowEngine? engine = null)
    { Actual = new(engine ?? new(), (_, _) => Task.CompletedTask); Capabilities = Actual.Capabilities; }
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) { Discoveries++; return Actual.DiscoverAsync(request, ct); }
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        Calls.Add(request);
        LLMResponse response;
        try { response = Respond?.Invoke(request, purpose) ?? throw new InvalidOperationException("Configure explicit responses for the current incremental planning protocol."); }
        catch (Exception error)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(error.ToString());
            throw;
        }
        return Task.FromResult(response);
    }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => Validation is null ? Actual.ValidateAsync(request, ct) : Task.FromResult(Validation);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => CatalogChanges is null ? Actual.ValidateCatalogAsync(catalog, ct) : Task.FromResult(CatalogChanges);
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct)
    { Checkpoints.Add(PlannerFixture.Clone(state)); Checkpointed?.Invoke(state); return Task.CompletedTask; }
    public Task AdmitConstructionAsync(LLMRequest request, int inputTokenCeiling, IReadOnlyList<int> outputTokenCeilings, CancellationToken ct)
    { AdmitConstruction?.Invoke(request, inputTokenCeiling, outputTokenCeilings); return Task.CompletedTask; }
}
