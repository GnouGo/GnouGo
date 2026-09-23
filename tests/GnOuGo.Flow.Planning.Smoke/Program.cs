using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

foreach (var name in PlanningCorpus.Names)
{
    var environment = new PlanningBenchmarkCases.Environment(name); var engine = new WorkflowEngine { McpClientFactory = environment.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
    var runtime = new PlanningCorpus.Runtime(name, engine); var planner = new TypedWorkflowPlanner();
    var state = new PlanningSession { Request = new() { TenantId = "smoke", Prompt = PlanningCorpus.Prompt(name) } };
    for (var i = 0; i < 20 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
    {
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
    }
    if (state.Status != PlanningStatus.FinalReview || state.ModelCalls > state.Request.MaxModelCalls || environment.Effects.Count != 0) throw new InvalidOperationException(JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
    state = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, CancellationToken.None);
    if (state.Status != PlanningStatus.Approved) throw new InvalidOperationException("Approval failed.");
    var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
    var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], PlanningBenchmarkCases.Inputs(name, "nominal"), CancellationToken.None);
    if (!environment.Verify(result)) throw new InvalidOperationException("Independent result assertion failed: " + result.Error?.Message);
    Console.WriteLine(name + ": passed, calls=" + state.ModelCalls + ", replans=" + state.ReplanAttempts + ", scenarios=" + state.Scenarios.Count);
}
foreach (var mode in new[] { PlanningMode.Auto, PlanningMode.Interactive })
{
    var planner = new TypedWorkflowPlanner(); var runtime = new DecisionRuntime();
    var state = new PlanningSession { Request = new() { TenantId = "smoke", Prompt = "Return 42", Mode = mode } };
    state = await planner.AdvanceAsync(state, new(), runtime, CancellationToken.None);
    state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
    if (mode == PlanningMode.Interactive)
    {
        if (state.Status != PlanningStatus.WaitingForDecision) throw new InvalidOperationException("Interactive decision did not pause.");
        state = await planner.AdvanceAsync(state, new() { Kind = "answer_decision", ExpectedRevision = state.Revision, DecisionAnswer = new(state.PendingDecision!.Id, "brief") }, runtime, CancellationToken.None);
    }
    if (state.Decisions.Count != 1 || state.Decisions[0].Answer.OptionId != "brief") throw new InvalidOperationException("Decision receipt was not retained.");
    state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
    state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
    if (state.SemanticPlan is null || state.DecisionContinuation is not null || runtime.Calls != 1) throw new InvalidOperationException("Decision continuation repeated or lost work.");
    Console.WriteLine("planner decision " + mode + ": passed");
}
Console.WriteLine("Planner Native AOT smoke passed.");

sealed class DecisionRuntime : IPlanningRuntime
{
    private readonly PlanningCorpus.Runtime _runtime = new("local", new WorkflowEngine());
    public int Calls;
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        Calls++;
        var plan = PlanningCorpus.Semantic(PlanningCorpus.Intent("local", new()));
        JsonObject Option(string id, bool preferred)
        {
            var candidate = SemanticPlanning.Json(plan); candidate["summary"] = id; candidate["actions"]![0]!["purpose"] = "Return 42 with " + id + " presentation";
            return new() { ["id"] = id, ["label"] = id, ["reason"] = "Business preference", ["preferred"] = preferred, ["result"] = candidate };
        }
        return Task.FromResult(new LLMResponse { Json = new JsonObject { ["result"] = null, ["decision"] = new JsonObject
        { ["question"] = "Which presentation?", ["context"] = "Choose the report presentation.", ["evidence"] = "Return 42", ["allowCustomAnswer"] = true,
            ["options"] = new JsonArray(Option("brief", true), Option("detailed", false)) } } });
    }
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => _runtime.DiscoverAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => _runtime.ValidateAsync(request, ct);
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct) => _runtime.ValidateScenariosAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => _runtime.ValidateCatalogAsync(catalog, ct);
    public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
}
