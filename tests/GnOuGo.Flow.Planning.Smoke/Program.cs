using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

// Exercise the native certificate-chain implementation after publish, including
// the .NET Apple crypto archive normalized by the Darwin publish boundary.
using (var key = RSA.Create(2048))
{
    var request = new CertificateRequest("CN=GnOuGo native smoke", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.DisableCertificateDownloads = true;
    if (chain.Build(certificate)) throw new InvalidOperationException("An untrusted certificate was accepted.");
    chain.ChainPolicy.CustomTrustStore.Add(certificate);
    if (!chain.Build(certificate)) throw new InvalidOperationException("Native certificate-chain validation failed.");
}

var collectionExpression = ArtifactCollectionExpression.Build("pages", ["source", "response", "records"]);
if (GeneratedFunctionDocumentation.Validate("function convert(value) { return String(value); }").Single().Code != "FUNCTION_JSDOC_MISSING" ||
    GeneratedFunctionDocumentation.Validate("/** @param {*} value Input\n * @returns {string} Text */ function convert(value) { return String(value); }").Count != 0)
    throw new InvalidOperationException("Published function documentation validation failed.");
if (!ArtifactCollectionExpression.TryRead(collectionExpression, out var collectionLoop, out _) || collectionLoop != "pages")
    throw new InvalidOperationException("Published collection expression parsing failed.");
var collectionValue = new ExpressionEvaluator().Evaluate(collectionExpression,
    JsonNode.Parse("""{"steps":{"pages":{"results":[{"source":{"response":{"records":"[{\"id\":9007199254740993}]"}}},{"source":{"response":{"records":"[2]"}}}]}}}"""));
if (collectionValue?.GetValue<string>() != """[{"id":9007199254740993},2]""")
    throw new InvalidOperationException("Published collection changed original artifact records.");

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
var state = new PlanningSnapshot { Graph = graph, Preparation = preparation, Request = new() { TenantId = "smoke", Prompt = "Compile the typed graph" },
    Dataflow = new() { Bindings = [new("binding", "main", new() { Kind = "output", Source = "value", Path = ["message"] }, new JsonObject { ["type"] = "string" }, "unconditional")] } };
var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
if (restored.Dataflow?.Bindings.Count != 1) throw new InvalidOperationException("Published dataflow persistence failed.");
var compiler = new PlanningGraphCompiler();
var yaml = compiler.Compile(restored.Graph!, preparation);
var imported = PlanningGraphImporter.Import(yaml, preparation);
var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(compiler.Compile(imported, preparation)));
var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), CancellationToken.None);
if (!result.Success || result.Outputs?["message"]?.GetValue<string>() != "ready") throw new InvalidOperationException("Published typed workflow execution failed.");
var computationGraph = JsonSerializer.Deserialize(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
computationGraph.Workflows[0].Steps[0].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Schema = new() { Type = "string" } }] };
computationGraph.Workflows[0].Steps[0].Input.Members[0] = new("message", new() { Kind = "compute", Text = "const result = source.toUpperCase(); return result;",
    Members = [new("source", new() { Kind = "string", Text = "ready" })] });
var computation = new WorkflowCompiler().Compile(WorkflowParser.Parse(compiler.Compile(computationGraph, preparation)));
var computationResult = await new WorkflowEngine().ExecuteAsync(computation.Workflows[computation.Entrypoint!], new JsonObject(), CancellationToken.None);
if (!computationResult.Success || computationResult.Outputs?["message"]?.ToString() != "READY") throw new InvalidOperationException("Published named computation failed.");
var planner = new TypedWorkflowPlanner();
var session = new PlanningSnapshot { Request = new() { TenantId = "smoke", Prompt = "Return the ready message" } };
var runtime = new SmokeRuntime(graph, preparation) { ForceContractOutputLimit = true };
for (var attempt = 0; attempt < 30 && session.Status != PlanningStatus.Approved; attempt++)
{
    var kind = session.Status == PlanningStatus.Recovery ? "retry" : session.Status == PlanningStatus.BehaviorReview ? "accept_behavior" : session.Status == PlanningStatus.FinalReview ? "approve" : "advance";
    session = await planner.AdvanceAsync(session, new() { Kind = kind, ExpectedRevision = session.Revision, ArtifactHash = session.ArtifactHash }, runtime, CancellationToken.None);
    session = JsonSerializer.Deserialize(JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
    if (session.Status is PlanningStatus.Failed or PlanningStatus.Unsupported) throw new InvalidOperationException("Published planner failed: " + session.Diagnostics.FirstOrDefault()?.Message);
}
if (session.Status != PlanningStatus.Approved || session.ApprovedHash != PlanningGraphCompiler.Fingerprint(session.Yaml!)) throw new InvalidOperationException("Published planner did not reach exact revision approval.");
if (!runtime.FlatSchemaReceived || !session.ConstructionUnits.Any(u => u.FlatSchemaGeneration && u.SchemaDeclarations is not null && u.Status == "validated"))
    throw new InvalidOperationException("Published flat schema recovery did not retain and validate declarations.");
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
    ArtifactContract = new(1, [new("opaque.resource", "/value", "materialize", "json_array")], [])
}] }, Diagnostics = [new("CONTRACT", "$", "Contract finding", ValidationStage: PlanningValidationStage.ConditionalActivation)] };
metadata = JsonSerializer.Deserialize(JsonSerializer.Serialize(metadata, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
if (metadata.Preparation?.Capabilities[0].Resolution != "mcp" || metadata.Preparation.Capabilities[0].Activation?.DecisionOutputPath != "/json/outcome" ||
    metadata.Preparation.Capabilities[0].ArtifactContract?.Produces[0].Pointer != "/value" ||
    metadata.Preparation.Capabilities[0].ArtifactContract?.Produces[0].Encoding != "json_array" ||
    metadata.Diagnostics[0].ValidationStage != PlanningValidationStage.ConditionalActivation)
    throw new InvalidOperationException("Published activation and artifact metadata did not survive serialization.");
var continuationGraph = new PlanningGraph { Workflows = [new() { Key = "main", Steps = [new()
{
    Key = "repeat", Type = "loop.sequential", Input = new() { Kind = "object", Members = [new("while", new()
    { Kind = "compute", Text = "previous == null || previous.more === true", Members = [new("previous", new() { Kind = "loop_previous", Source = "repeat", Path = ["observe"] })] })] },
    Steps = [new() { Key = "observe", Input = new() { Kind = "object", Members = [new("more", new() { Kind = "boolean", Boolean = false })] } }]
}], Outputs = [new() { Name = "count", Schema = new() { Type = "integer" }, Value = new() { Kind = "output", Source = "repeat", Path = ["count"] } }] }] };
continuationGraph = JsonSerializer.Deserialize(JsonSerializer.Serialize(continuationGraph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
preparation.AllowedStepTypes.Add("loop.sequential");
var continuation = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(continuationGraph, preparation)));
var continuationResult = await new WorkflowEngine().ExecuteAsync(continuation.Workflows[continuation.Entrypoint!], new JsonObject(), CancellationToken.None);
if (!continuationResult.Success || continuationResult.Outputs?["count"]?.GetValue<int>() != 1)
    throw new InvalidOperationException("Published typed sequential continuation failed: " + continuationResult.Error?.Message);
var guardedFallback = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
    version: 1
    workflows:
      main:
        steps:
          - id: source
            type: llm.call
            input:
              model: fake
              prompt: No external client is configured in this smoke test.
              structured_output:
                schema_inline:
                  type: object
                  properties:
                    count: {type: integer}
                  required: [count]
                  additionalProperties: false
                strict: true
            on_error:
              cases:
                - action: continue
                  set_output: ${data.inputs.fallback}
          - id: after
            type: set
            input: {executed: true}
        finally:
          - id: cleanup
            type: set
            input: {cleaned: true}
    """));
foreach (var valid in new[] { true, false })
{
    var guardedResult = await new WorkflowEngine().ExecuteAsync(guardedFallback.Workflows[guardedFallback.Entrypoint!],
        new JsonObject { ["fallback"] = new JsonObject { ["json"] = new JsonObject { ["count"] = valid ? JsonValue.Create(2) : JsonValue.Create("wrong") } } }, CancellationToken.None);
    if (guardedResult.Success != valid || guardedResult.StepResults.Any(s => s.StepId == "after") != valid ||
        !guardedResult.StepResults.Any(s => s.StepId == "cleanup")) throw new InvalidOperationException("Published structured continuation guard failed.");
}
Console.WriteLine("Typed planning AOT smoke passed.");

sealed class SmokeRuntime(PlanningGraph graph, PlanningPreparation preparation) : IPlanningRuntime
{
    public bool InvalidIntent { get; set; }
    public bool ForceContractOutputLimit { get; set; }
    public bool FlatSchemaReceived { get; private set; }
    public Task<PlanningPreparation> PrepareAsync(PlanningRequest request, CancellationToken ct) => Task.FromResult(preparation);
    public Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct)
    {
        if (phase == "fragment_contracts" && ForceContractOutputLimit)
        {
            ForceContractOutputLimit = false;
            return Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" });
        }
        if (request.StructuredOutputSchema?["$defs"]?["flat_schema"] is not null)
        {
            FlatSchemaReceived = true;
            return Task.FromResult(new LLMResponse { Json = JsonNode.Parse("""
                {"nodes":{"value":{"outputSchema":{"fields":[
                  {"path":"","required":true,"schema":{"kind":"inline","type":"object","nullable":false,"description":null}},
                  {"path":"/properties/message","required":true,"schema":{"kind":"inline","type":"string","nullable":false,"description":null,"enum":[]}}
                ]}}}}
                """) });
        }
        var json = InvalidIntent ? new JsonObject() : phase switch
        {
            "intent" => JsonNode.Parse("""{"outcome":"ready","evidence":[],"reason":"Clear request","questions":[]}"""),
            "behavior" => JsonSerializer.SerializeToNode(new PlanningBehaviorPlan { Summary = graph.Summary, Entrypoint = graph.Entrypoint,
                Workflows = graph.Workflows.Select(w => new PlanningBehaviorWorkflow { Key = w.Key, Purpose = "Return the ready message",
                    Steps = w.Steps.Select(n => new PlanningBehaviorNode { Key = n.Key, Purpose = "Return the ready message", InputDependencies = [] }).ToList(),
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
        var response = PlanningConstruction.Values(workflow, new() { Kind = phase[9..], NodeKeys = (request.StructuredOutputSchema?["properties"]?["nodes"]?["properties"] as JsonObject ?? []).Select(p => p.Key).ToList() });
        foreach (var (key, shape) in request.StructuredOutputSchema?["properties"]?["nodes"]?["properties"] as JsonObject ?? [])
            if (shape?["properties"]?["values"] is not null && response["nodes"]?[key] is JsonObject fields)
            {
                fields["values"] = new JsonObject(fields["input"]!["members"]!.AsArray().Select(m => new KeyValuePair<string, JsonNode?>(m!["name"]!.ToString(), m["value"]?.DeepClone())));
                fields.Remove("input");
            }
        return response;
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
