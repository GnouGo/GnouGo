using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;

internal static class PlannerFixture
{
    internal static WorkflowIntentPlan Greeting(string message = "Hello") => new()
    {
        Summary = "Return a greeting", Workflows = [new()
        {
            Steps = [new() { Key = "greet", Kind = "set", Input = new() { Kind = "object", Members = [new("message", new() { Kind = "string", Text = message })] } }],
            Outputs = [new() { Name = "message", Schema = new(), Value = new() { Kind = "output", Source = "greet", Path = ["message"] } }]
        }]
    };
    internal static PlanningSession Session() => new() { Request = new() { TenantId = "test", Prompt = "Return a greeting", Options = new() { ["generator"] = new JsonObject { ["model"] = "test" } } } };
    internal static async Task<PlanningSession> RunAsync(TestRuntime runtime, PlanningSession? state = null)
    {
        state ??= Session(); var planner = new TypedWorkflowPlanner();
        for (var i = 0; i < 30 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        return state;
    }

}
internal sealed class TestRuntime : IPlanningRuntime
{
    internal readonly List<LLMRequest> Calls = [];
    internal readonly List<PlanningSession> Checkpoints = [];
    internal readonly Queue<WorkflowIntentPlan> Plans = new();
    internal Func<LLMRequest, LLMResponse>? Respond;
    internal IReadOnlyList<PlanningDiagnostic>? Validation { get; set; }
    internal IReadOnlyList<PlanningDiagnostic>? CatalogChanges;
    internal int Discoveries;
    internal readonly WorkflowPlanningRuntime Actual;
    internal TestRuntime(WorkflowIntentPlan? plan = null, IMcpClientFactory? mcp = null)
    {
        Plans.Enqueue(plan ?? PlannerFixture.Greeting());
        Actual = new(new WorkflowEngine { McpClientFactory = mcp }, (_, _) => Task.CompletedTask);
    }
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) { Discoveries++; return Actual.DiscoverAsync(request, ct); }
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        Calls.Add(request);
        if (Respond is not null) return Task.FromResult(Respond(request));
        if (purpose == "choices") return Task.FromResult(new LLMResponse { Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value!["enum"]![0]!.DeepClone()))) });
        var plan = Plans.Count > 1 ? Plans.Dequeue() : Plans.Peek();
        return Task.FromResult(new LLMResponse { Json = PlanningJsonTransport.Intent(plan) });
    }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => Validation is null ? Actual.ValidateAsync(request, ct) : Task.FromResult(Validation);
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct) => Actual.ValidateScenariosAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => CatalogChanges is null ? Actual.ValidateCatalogAsync(catalog, ct) : Task.FromResult(CatalogChanges);
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct)
    {
        Checkpoints.Add(JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!);
        return Task.CompletedTask;
    }
}
