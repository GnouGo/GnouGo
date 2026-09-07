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
for (var attempt = 0; attempt < 20 && session.Status != PlanningStatus.Approved; attempt++)
{
    var kind = session.Status == PlanningStatus.BehaviorReview ? "accept_behavior" : session.Status == PlanningStatus.FinalReview ? "approve" : "advance";
    session = await planner.AdvanceAsync(session, new() { Kind = kind, ExpectedRevision = session.Revision, ArtifactHash = session.ArtifactHash }, runtime, CancellationToken.None);
    session = JsonSerializer.Deserialize(JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
    if (session.Status is PlanningStatus.Failed or PlanningStatus.Unsupported) throw new InvalidOperationException("Published planner failed: " + session.Diagnostics.FirstOrDefault()?.Message);
}
if (session.Status != PlanningStatus.Approved || session.ApprovedHash != PlanningGraphCompiler.Fingerprint(session.Yaml!)) throw new InvalidOperationException("Published planner did not reach exact revision approval.");
var recovery = new PlanningSnapshot { Request = new() { TenantId = "smoke", Prompt = "Recover an invalid assessment" } };
runtime.InvalidIntent = true;
recovery = await planner.AdvanceAsync(recovery, new() { ExpectedRevision = recovery.Revision }, runtime, CancellationToken.None);
if (recovery.Status != PlanningStatus.Recovery || recovery.Outcome is not null || recovery.IntentChecked) throw new InvalidOperationException("Published intent recovery failed.");
recovery = JsonSerializer.Deserialize(JsonSerializer.Serialize(recovery, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
recovery = await planner.AdvanceAsync(recovery, new() { Kind = "edit_intent", Text = "Return the ready message", ExpectedRevision = recovery.Revision }, runtime, CancellationToken.None);
runtime.InvalidIntent = false;
recovery = await planner.AdvanceAsync(recovery, new() { ExpectedRevision = recovery.Revision }, runtime, CancellationToken.None);
if (!recovery.IntentChecked || recovery.IntentHistory.Count != 1 || recovery.Diagnostics.Count != 0) throw new InvalidOperationException("Published edited intent did not resume.");
var behaviorFailure = new PlanningSnapshot { Request = new() { TenantId = "smoke", Prompt = "Return the ready message" },
    Status = PlanningStatus.Failed, CurrentPhase = PlanningPhase.Behavior, IntentChecked = true, Graph = graph, Preparation = preparation };
behaviorFailure = await planner.AdvanceAsync(behaviorFailure, new() { Kind = "retry", ExpectedRevision = behaviorFailure.Revision }, runtime, CancellationToken.None);
behaviorFailure = JsonSerializer.Deserialize(JsonSerializer.Serialize(behaviorFailure, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
behaviorFailure = await planner.AdvanceAsync(behaviorFailure, new() { ExpectedRevision = behaviorFailure.Revision }, runtime, CancellationToken.None);
if (behaviorFailure.Status != PlanningStatus.BehaviorReview || behaviorFailure.ReviewedGraph is not null || behaviorFailure.ApprovedHash is not null)
    throw new InvalidOperationException("Published recovery bypassed behavior review.");
var metadata = new PlanningSnapshot { Preparation = new() { Capabilities = [new()
{
    CatalogId = "declared", Resolution = "mcp", Activation = new("all_on_value", "group", "decision", "APPLY")
    { AllowedValues = ["APPLY", "NO_EFFECT"], NoEffectValues = ["NO_EFFECT"], DecisionOutputPath = "/json/outcome" },
    ArtifactContract = new(1, [new("opaque.resource", "/value", "materialize")], [])
}] }, Diagnostics = [new("CONTRACT", "$", "Contract finding", ValidationStage: PlanningValidationStage.ConditionalActivation)] };
metadata = JsonSerializer.Deserialize(JsonSerializer.Serialize(metadata, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
if (metadata.Preparation?.Capabilities[0].Resolution != "mcp" || metadata.Preparation.Capabilities[0].Activation?.DecisionOutputPath != "/json/outcome" ||
    metadata.Preparation.Capabilities[0].ArtifactContract?.Produces[0].Pointer != "/value" ||
    metadata.Diagnostics[0].ValidationStage != PlanningValidationStage.ConditionalActivation)
    throw new InvalidOperationException("Published activation and artifact metadata did not survive serialization.");
Console.WriteLine("Typed planning AOT smoke passed.");

sealed class SmokeRuntime(PlanningGraph graph, PlanningPreparation preparation) : IPlanningRuntime
{
    public bool InvalidIntent { get; set; }
    public Task<PlanningPreparation> PrepareAsync(PlanningRequest request, CancellationToken ct) => Task.FromResult(preparation);
    public Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct)
    {
        var json = InvalidIntent ? new JsonObject() : phase switch
        {
            "intent" => JsonNode.Parse("""{"outcome":"ready","evidence":[],"reason":"Clear request","questions":[]}"""),
            "behavior" => JsonSerializer.SerializeToNode(new PlanningBehaviorPlan { Summary = graph.Summary, Entrypoint = graph.Entrypoint,
                Workflows = graph.Workflows.Select(w => new PlanningBehaviorWorkflow { Key = w.Key, Purpose = "Return the ready message",
                    Steps = w.Steps.Select(n => new PlanningBehaviorNode { Key = n.Key, Purpose = "Return the ready message" }).ToList(),
                    Outputs = w.Outputs.Select(o => new PlanningBehaviorPort(o.Name, "The ready message", true)).ToList() }).ToList() }, PlanningJsonContext.Default.PlanningBehaviorPlan),
            "fragment" => PlanningFragments.Values(graph.Workflows[0]),
            "fragment_inputs" or "fragment_contracts" or "fragment_implementation" or "fragment_outputs" => UnitResponse(request, phase),
            "semantic_review" => JsonNode.Parse("""{"findings":[]}"""),
            _ => throw new InvalidOperationException("Unexpected model phase: " + phase)
        };
        return Task.FromResult(new LLMResponse { Json = json, Text = json!.ToJsonString() });
    }
    private JsonObject UnitResponse(LLMRequest request, string phase)
    {
        var workflow = graph.Workflows[0];
        workflow.Steps[0].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Schema = new() { Type = "string" } }] };
        return PlanningConstruction.Values(workflow, new() { Kind = phase[9..], NodeKeys = (request.StructuredOutputSchema?["properties"]?["nodes"]?["properties"] as JsonObject ?? []).Select(p => p.Key).ToList() });
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
