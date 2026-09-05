using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

var graph = new PlanningGraph
{
    Summary = "Published typed compiler smoke",
    Workflows = [new()
    {
        Key = "main",
        Steps = [new() { Key = "value", Type = "set", Input = new() { Kind = "object", Members = [new("message", new() { Kind = "string", Text = "ready" })] } }],
        Outputs = [new() { Name = "message", Schema = new() { Type = "string" }, Value = new() { Kind = "output", Source = "value", Path = ["message"] } }]
    }]
};
var preparation = new PlanningPreparation { AllowedStepTypes = ["set"] };
var state = new PlanningSnapshot { Graph = graph, Preparation = preparation, Request = new() { TenantId = "smoke", Prompt = "Compile the typed graph" } };
var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
var compiler = new PlanningGraphCompiler();
var yaml = compiler.Compile(restored.Graph!, preparation);
var imported = PlanningGraphImporter.Import(yaml, preparation);
var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(compiler.Compile(imported, preparation)));
var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), CancellationToken.None);
if (!result.Success || result.Outputs?["message"]?.GetValue<string>() != "ready") throw new InvalidOperationException("Published typed workflow execution failed.");
var planner = new TypedWorkflowPlanner();
var session = new PlanningSnapshot { Request = new() { TenantId = "smoke", Prompt = "Return the ready message" } };
var runtime = new SmokeRuntime(graph, preparation);
for (var attempt = 0; attempt < 12 && session.Status != PlanningStatus.Approved; attempt++)
{
    var kind = session.Status == PlanningStatus.BehaviorReview ? "accept_behavior" : session.Status == PlanningStatus.FinalReview ? "approve" : "advance";
    session = await planner.AdvanceAsync(session, new() { Kind = kind, ExpectedRevision = session.Revision, ArtifactHash = session.ArtifactHash }, runtime, CancellationToken.None);
    session = JsonSerializer.Deserialize(JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
    if (session.Status is PlanningStatus.Failed or PlanningStatus.Unsupported) throw new InvalidOperationException("Published planner failed: " + session.Diagnostics.FirstOrDefault()?.Message);
}
if (session.Status != PlanningStatus.Approved || session.ApprovedHash != PlanningGraphCompiler.Fingerprint(session.Yaml!)) throw new InvalidOperationException("Published planner did not reach exact revision approval.");
Console.WriteLine("Typed planning AOT smoke passed.");

sealed class SmokeRuntime(PlanningGraph graph, PlanningPreparation preparation) : IPlanningRuntime
{
    public Task<PlanningPreparation> PrepareAsync(PlanningRequest request, CancellationToken ct) => Task.FromResult(preparation);
    public Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct)
    {
        var json = phase switch
        {
            "intent" => JsonNode.Parse("""{"outcome":"ready","evidence":"","reason":"","questions":[]}"""),
            "behavior" => JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningGraph),
            "fragment" => JsonSerializer.SerializeToNode(graph.Workflows[0], PlanningJsonContext.Default.PlanningWorkflow),
            "semantic_review" => JsonNode.Parse("""{"findings":[]}"""),
            _ => throw new InvalidOperationException("Unexpected model phase: " + phase)
        };
        return Task.FromResult(new LLMResponse { Json = json, Text = json!.ToJsonString() });
    }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(string yaml, PlanningRequest request, PlanningPreparation prepared, CancellationToken ct)
    {
        new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        return Task.FromResult<IReadOnlyList<PlanningDiagnostic>>([]);
    }
    public async Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation prepared, CancellationToken ct)
    {
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), ct);
        return [new("nominal", result.Success && result.Outputs?["message"]?.GetValue<string>() == "ready" ? "passed" : "failed", "Execute the published greeting workflow", [])];
    }
}
