using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;

internal static class PlannerFixture
{
    internal static PlanningGraph Build(GroundedPlan plan, PlanningCatalog catalog) => PlanningGraphBuilder.Build(GroundedPlanValidator.RequireValid(plan, catalog));
    internal static InMemoryMcpClientFactory Factory(int count, string fields = "{}", string[]? required = null)
    {
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
        for (var i = 0; i < count; i++) server.Tools.Add(new() { Name = "read_" + i, Description = "Read a value", EffectKind = "read", InputSchema = new JsonObject { ["type"] = "object", ["properties"] = JsonNode.Parse(fields), ["required"] = new JsonArray((required ?? []).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()), ["additionalProperties"] = false }, OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"number"}},"required":["value"]}""") });
        factory.RegisterServer("fixture", server); return factory;
    }
    internal static GroundedPlan Greeting(string message = "Hello") => new()
    {
        Summary = "Return a greeting",
        Operations = [new CalculateGroundedOperation { Id = "greet", Value = new() { Kind = "string", Text = message } }],
        Outputs = [new("message", new() { Kind = "result", Source = "greet" })]
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
    internal readonly Queue<GroundedPlan> Plans = new();
    internal Func<LLMRequest, LLMResponse>? Respond;
    internal IReadOnlyList<PlanningDiagnostic>? Validation { get; set; }
    internal Exception? ValidationFailure;
    internal IReadOnlyList<PlanningDiagnostic>? CatalogChanges { get; set; }
    internal int Discoveries;
    internal readonly WorkflowPlanningRuntime Actual;
    internal TestRuntime(GroundedPlan? plan = null, IMcpClientFactory? mcp = null)
    {
        Plans.Enqueue(plan ?? PlannerFixture.Greeting());
        Actual = new(new WorkflowEngine { McpClientFactory = mcp }, (_, _) => Task.CompletedTask);
    }
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) { Discoveries++; return Actual.DiscoverAsync(request, ct); }
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        Calls.Add(request);
        if (Respond is not null) return Task.FromResult(Respond(request));
        var plan = purpose == "replan" && Plans.Count > 1 ? Plans.Dequeue() : Plans.Peek();
        return Task.FromResult(GnOuGo.Planning.Examples.PlanningCorpus.FixtureResponse(request, purpose, plan));
    }

    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => ValidationFailure is { } failure
        ? Task.FromException<IReadOnlyList<PlanningDiagnostic>>(failure)
        : Validation is null ? Actual.ValidateAsync(request, ct) : Task.FromResult(Validation);
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct) => Actual.ValidateScenariosAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => CatalogChanges is null ? Actual.ValidateCatalogAsync(catalog, ct) : Task.FromResult(CatalogChanges);
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct)
    {
        Checkpoints.Add(JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!);
        return Task.CompletedTask;
    }
}
