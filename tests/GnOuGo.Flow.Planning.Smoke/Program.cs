using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

if (args is ["--decimal-boundary-host"])
{
    var inputSchema = JsonDocument.Parse("""{"type":"object","properties":{"amount":{"type":"number","format":"decimal"},"ordinary":{"type":"number"}},"required":["amount","ordinary"]}""").RootElement.Clone();
    var numericOutputSchema = JsonDocument.Parse("""{"type":"object","properties":{"result":{"type":"number","format":"decimal"},"ordinary":{"type":"number"}},"required":["result","ordinary"]}""").RootElement.Clone();
    var options = new ModelContextProtocol.Server.McpServerOptions { ServerInfo = new() { Name = "numeric-smoke", Version = "1" } };
    options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(new ModelContextProtocol.Protocol.ListToolsResult
    { Tools = [new() { Name = "exchange", InputSchema = inputSchema, OutputSchema = numericOutputSchema }] });
    options.Handlers.CallToolHandler = (request, _) =>
    {
        var amount = request.Params!.Arguments!["amount"].GetDecimal();
        var content = new JsonObject { ["result"] = amount + 0.1m, ["ordinary"] = request.Params.Arguments["ordinary"].GetDouble() }.ToJsonString();
        return ValueTask.FromResult(new ModelContextProtocol.Protocol.CallToolResult
        { StructuredContent = JsonDocument.Parse(content).RootElement.Clone(), Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = content }] });
    };
    await using var transport = new ModelContextProtocol.Server.StreamServerTransport(Console.OpenStandardInput(), Console.OpenStandardOutput(), "numeric-smoke");
    await using var server = ModelContextProtocol.Server.McpServer.Create(transport, options);
    await server.RunAsync();
    return;
}

var historicalNumber = JsonSerializer.Deserialize("""{"kind":"number","number":0.1234567890123456789012345678}""", PlanningJsonContext.Default.TaskValue)!;
if (JsonSerializer.SerializeToNode(historicalNumber, PlanningJsonContext.Default.TaskValue)!["number"]!.ToJsonString() != "0.1234567890123456789012345678")
    throw new InvalidOperationException("Historical numeric tokens changed during Native AOT serialization.");

// Native publication provides a self-contained executable for a real stdio MCP
// exchange. Managed runs exercise the same conversions in the integration suite.
if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
{
    await using var numericFactory = new GnOuGo.Flow.Integrations.ConfiguredMcpClientFactory(new Dictionary<string, GnOuGo.AI.Core.McpServerOptions>
    { ["numeric-smoke"] = new() { Type = "stdio", Command = Environment.ProcessPath!, Args = ["--decimal-boundary-host"] } });
    var numericDocument = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
        version: 1
        workflows:
          main:
            steps:
              - id: exchange
                type: mcp.call
                input: {server: numeric-smoke, method: exchange, request: {amount: 1.25, ordinary: 1e-100}}
            outputs:
              amount: "${data.steps.exchange.response.result}"
              ordinary: "${data.steps.exchange.response.ordinary}"
        """));
    var numericResult = await new WorkflowEngine { McpClientFactory = numericFactory }.ExecuteAsync(numericDocument.Workflows["main"], new JsonObject(), CancellationToken.None);
    if (!numericResult.Success || numericResult.Outputs?["amount"]?.GetValue<double>() != 1.35 || numericResult.Outputs?["ordinary"]?.GetValue<double>() != 1e-100)
        throw new InvalidOperationException("Published decimal MCP boundary failed: " + numericResult.Error?.Message);
    Console.WriteLine("numeric MCP: real native stdio decimal input/output conversion; exact producer-owned calculation");
}

var authoritativePattern = JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string","pattern":"^(?!blocked)[a-z]+$"}},"required":["path"],"additionalProperties":false}""")!.AsObject();
var projectedPattern = PlanningContractValidation.ProjectStructuredOutputSchema(authoritativePattern);
if (projectedPattern["properties"]!["path"]!["pattern"] is not null || authoritativePattern["properties"]!["path"]!["pattern"] is null ||
    PlanningContractValidation.ValidateSchema(projectedPattern, true).Count != 0 ||
    PlanningContractValidation.ValidateInstance(new JsonObject { ["path"] = "blocked" }, authoritativePattern).Count == 0)
    throw new InvalidOperationException("Structured-output projection weakened authoritative pattern validation.");
Console.WriteLine("schema projection: portable wire, unchanged authoritative restrictions");

foreach (var complete in new[] { true, false })
{
    var finalizerPlan = new TaskPlan
    {
        Root = new()
        {
            Tasks = [new() { Id = "observed", Kind = "value", Objective = "Require the observed business condition",
                Requires = new() { Kind = "boolean", Boolean = complete }, Outputs = [new("record", new() { Kind = "string", Text = "observed" })] }],
            Always = [new() { Id = "finalize", Kind = "sequence", Objective = "Preserve available evidence", Body = new()
            {
                Tasks = [new() { Id = "collect", Kind = "conditional", Objective = "Read only available observations",
                    Condition = new() { Kind = "present", Source = "observed" },
                    Body = new() { Outputs = [new("record", new() { Kind = "output", Source = "observed", Port = "record" })] },
                    Otherwise = new() { Outputs = [new("record", new() { Kind = "null" })] } }],
                Outputs = [new("record", new() { Kind = "output", Source = "collect", Port = "record" })],
                Always = [new() { Id = "release", Kind = "value", Objective = "Finalize after evidence", Outputs = [new("released", new() { Kind = "boolean", Boolean = true })] }]
            } }]
        }
    };
    finalizerPlan = JsonSerializer.Deserialize(JsonSerializer.Serialize(finalizerPlan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
    if (finalizerPlan.Root.Tasks[0].Requires?.Boolean != complete) throw new InvalidOperationException("Required condition serialization failed.");
    var finalizerEngine = new WorkflowEngine();
    var finalizerCatalog = await new WorkflowPlanningRuntime(finalizerEngine, (_, _) => Task.CompletedTask).DiscoverAsync(new(), CancellationToken.None);
    var finalizerCompilation = new TaskPlanCompiler().Compile(finalizerPlan, finalizerCatalog);
    if (finalizerCompilation.Graph is null || finalizerCompilation.Diagnostics.Count != 0 || PlanningExecutableValidation.Validate(finalizerCompilation.Graph, finalizerCatalog).Count != 0)
        throw new InvalidOperationException("Required condition/finalizer capture compilation failed.");
    var finalizerDocument = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(finalizerCompilation.Graph, finalizerCatalog)));
    var finalizerResult = await finalizerEngine.ExecuteAsync(finalizerDocument.Workflows[finalizerDocument.Entrypoint!], new JsonObject(), CancellationToken.None);
    if (finalizerResult.Success != complete || !complete && finalizerResult.Error?.Code != "INPUT_VALIDATION" ||
        !finalizerResult.StepResults.Any(r => r.Output?["outputs"] is JsonObject obj && obj.ContainsKey("record") && obj["record"]?.ToString() == (complete ? "observed" : null)))
        throw new InvalidOperationException("Required condition/finalizer capture execution failed: " + finalizerResult.Error?.Code + ": " + finalizerResult.Error?.Message + "; " + string.Join("; ", finalizerResult.StepResults.Select(r => r.Output?.ToJsonString())));
}
Console.WriteLine("required conditions: true/false and guarded ancestor payloads survive Native AOT serialization and execution");

var mapped = new GnOuGo.Flow.Core.Scripting.JintSandbox().ExecuteMapping(
    "({name:m.decode(m.text(source.html,'<h1>([^<]+)</h1>')),amount:source.amount})",
    JsonNode.Parse("{\"html\":\"<h1>A &amp; B</h1>\",\"amount\":7922816251426433759354395033.5}"), CancellationToken.None);
if (mapped?["name"]?.ToString() != "A & B" || mapped["amount"]!.ToJsonString() != "7922816251426433759354395033.5")
    throw new InvalidOperationException("Restricted mapping extraction/decimal preservation failed in Native AOT.");
var mappingArtifact = new MappingArtifact("smoke", "source", null, GnOuGo.Flow.Core.Scripting.JintSandbox.MappingProfileVersion);
if (JsonSerializer.Deserialize(JsonSerializer.Serialize(mappingArtifact, MappingArtifactJsonContext.Default.MappingArtifact), MappingArtifactJsonContext.Default.MappingArtifact) != mappingArtifact)
    throw new InvalidOperationException("Mapping artifact serialization failed.");
Console.WriteLine("restricted mappings: observed HTML extraction, exact decimals, source-generated artifact serialization");
var eachDeclaration = new TaskPlan { Root = new() { Tasks = [new() { Id = "extract", Kind = "transform", Mode = "extract", Each = new("pages", "rows") }] } };
if (JsonSerializer.Deserialize(JsonSerializer.Serialize(eachDeclaration, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!.Root.Tasks[0].Each != new TaskExtractionEach("pages", "rows"))
    throw new InvalidOperationException("Independent extraction declaration did not survive Native AOT serialization.");
var eachValues = new GnOuGo.Flow.Core.Scripting.JintSandbox().ExecuteMappingItems("m.text(source.pages,'<h1>([^<]+)</h1>')",
    JsonNode.Parse("{\"pages\":[\"<h1>first</h1>\",\"<h1>second</h1>\"]}")!.AsObject(), "pages", new JsonObject { ["type"] = "string" });
if (eachValues.ToJsonString() != "[\"first\",\"second\"]") throw new InvalidOperationException("Independent extraction failed in Native AOT.");
var repeatedPages = new JsonObject { ["pages"] = new JsonArray(Enumerable.Range(0, 80)
    .Select(i => (JsonNode?)JsonValue.Create("<h1>row-" + i + "</h1><pre>" + new string('x', 14000) + "</pre>")).ToArray()) };
var repeatedValues = new GnOuGo.Flow.Core.Scripting.JintSandbox().ExecuteMappingItems("m.text(source.pages,'<h1>([^<]+)</h1>')",
    repeatedPages, "pages", new JsonObject { ["type"] = "string" });
if (!repeatedValues.Select(v => v!.GetValue<string>()).SequenceEqual(Enumerable.Range(0, 80).Select(i => "row-" + i)))
    throw new InvalidOperationException("Repeated extraction exceeded the unchanged sandbox allowance or lost values in Native AOT.");
Console.WriteLine("collection mappings: independent extraction, ordered complete results, optional declaration serialization");

// Additive host failure contracts must survive source-generated Native AOT serialization.
var taskFailure = new AgentTaskResult("failed", null, [], [], new(0, 0, 0))
{ Failure = new() { Code = "AGENT_ISOLATION_REQUIRED", Message = "Mandatory host isolation is not configured." } };
var restoredFailure = JsonSerializer.Deserialize(JsonSerializer.Serialize(taskFailure, AgentTaskJsonContext.Default.AgentTaskResult), AgentTaskJsonContext.Default.AgentTaskResult)!;
if (restoredFailure.Failure?.Code != taskFailure.Failure.Code) throw new InvalidOperationException("Agent failure serialization failed");
Console.WriteLine("agent failure: structured host diagnostic survives source-generated serialization");

// Clarification is a response in the existing planning loop, not a separate model phase.
var clarificationRuntime = new ClarificationRuntime(); var clarificationPlanner = new HybridWorkflowPlanner();
var clarificationState = new PlanningSession { Request = new() { TenantId = "smoke", Mode = "auto", Prompt = "Return a value with the intended interface" } };
clarificationState = await clarificationPlanner.AdvanceAsync(clarificationState, new(), clarificationRuntime, CancellationToken.None);
clarificationState = JsonSerializer.Deserialize(JsonSerializer.Serialize(clarificationState, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
if (clarificationState.Status != PlanningStatus.Clarification || clarificationState.Plan is not null || clarificationState.PendingQuestions?.Count != 1)
    throw new InvalidOperationException("Early clarification did not survive Native AOT recovery.");
clarificationState = await clarificationPlanner.AdvanceAsync(clarificationState, new() { Kind = "answer", ExpectedRevision = clarificationState.Revision,
    Answers = [new("interface", Text: "Use no caller inputs")] }, clarificationRuntime, CancellationToken.None);
if (clarificationRuntime.Calls != 1 || clarificationState.AnswerHistory?.Single().Answers.Single().Text != "Use no caller inputs")
    throw new InvalidOperationException("Answer was not retained before dispatch.");
clarificationState = JsonSerializer.Deserialize(JsonSerializer.Serialize(clarificationState, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
clarificationState = await clarificationPlanner.AdvanceAsync(clarificationState, new() { ExpectedRevision = clarificationState.Revision }, clarificationRuntime, CancellationToken.None);
PlanningArtifactApproval.Verify(clarificationState);
if (clarificationState.Status != PlanningStatus.FinalReview || clarificationState.ApprovedHash is not null || clarificationRuntime.Calls != 2)
    throw new InvalidOperationException("Clarification bypassed review or lost accounting.");
if (clarificationState.IntentVersion != 2 || clarificationState.OutcomeVersion is not null || clarificationState.OutcomeBindings is not null)
    throw new InvalidOperationException("Business planning profile did not survive Native AOT recovery.");
var intentHash = clarificationState.ComputeArtifactHash();
clarificationState.Requirements!.Summary += " revised";
if (intentHash == clarificationState.ComputeArtifactHash()) throw new InvalidOperationException("Requirements were not bound to approval.");
Console.WriteLine("business intent: persisted requirements and approval identity survive Native AOT serialization");
Console.WriteLine("clarification: auto pause, custom answer, restart, unchanged budget and separate approval passed with deterministic inference");

// A shared location is lowered to an approved literal without an agent dispatch.
var workspacePlan = JsonSerializer.Deserialize("""
{"root":{"tasks":[
 {"id":"location","kind":"value","objective":"Declare location","outputs":[{"name":"path","value":{"kind":"string","text":"workflows/smoke/project"}}]},
 {"id":"work","kind":"operation","operation":"bounded","objective":"Check project","inputs":[
  {"name":"workspace","value":{"kind":"output","source":"location","port":"path"}},
  {"name":"objective","value":{"kind":"string","text":"Check project"}},
  {"name":"capabilities","value":{"kind":"array","items":[{"kind":"string","text":"project.read"}]}},
  {"name":"budget","value":{"kind":"object","members":[{"name":"max_model_calls","value":{"kind":"number","number":1}},{"name":"max_total_tokens","value":{"kind":"number","number":1000}},{"name":"max_elapsed_milliseconds","value":{"kind":"number","number":1000}}]}},
  {"name":"output_schema","value":{"kind":"object","members":[{"name":"type","value":{"kind":"string","text":"object"}}]}},
  {"name":"verification","value":{"kind":"array","items":[{"kind":"object","members":[{"name":"id","value":{"kind":"string","text":"check"}},{"name":"kind","value":{"kind":"string","text":"file.content"}},{"name":"subject","value":{"kind":"string","text":"result.txt"}},{"name":"facts_schema","value":{"kind":"object","members":[{"name":"type","value":{"kind":"string","text":"object"}}]}}]}]}}
 ]}]}}
""", PlanningJsonContext.Default.TaskPlan)!;
var workspaceCatalog = new PlanningCatalog { AllowedStepTypes = ["set", "agent.run"], Capabilities = [new() { Id = "bounded", StepType = "agent.run", Kind = "agent", FixedInput = new() { ["runner"] = "fixture" }, InputSchema = AgentTaskContracts.InputSchema, OutputSchema = new() { ["type"] = "object" } }] };
var workspaceCompiled = new TaskPlanCompiler().Compile(workspacePlan, workspaceCatalog);
if (workspaceCompiled.Graph is null || workspaceCompiled.Diagnostics.Count != 0) throw new InvalidOperationException("Constant workspace compilation failed");
var workspaceGraph = JsonSerializer.Deserialize(JsonSerializer.Serialize(workspaceCompiled.Graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
if (workspaceGraph.Workflows[0].Steps.Single(s => s.Type == "agent.run").Input.Members.Single(m => m.Name == "workspace").Value.Text != "workflows/smoke/project" ||
    PlanningGeneratedGraph.Validate(workspaceGraph, workspaceCatalog).Any()) throw new InvalidOperationException("Workspace scope was not preserved");
Console.WriteLine("constant workspace: approved literal survives compilation and source-generated serialization; no agent execution");

// Use the real executor contract: an agent payload can contain open objects while
// evidence, usage and other envelope fields retain their authoritative shapes.
workspaceCatalog.Capabilities[0].OutputSchema = new GnOuGo.Flow.Core.Runtime.Executors.AgentRunExecutor().Contract.OutputSchema;
workspacePlan.Root.Outputs = [new("observed", new() { Kind = "output", Source = "work" })];
var outputCompiled = new TaskPlanCompiler().Compile(workspacePlan, workspaceCatalog);
var outputGraph = JsonSerializer.Deserialize(JsonSerializer.Serialize(outputCompiled.Graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
if (outputCompiled.Diagnostics.Count != 0 || PlanningGraphValidation.Validate(outputGraph, workspaceCatalog).Count != 0)
    throw new InvalidOperationException("Partial operation output contract did not survive serialization");
var outputSchema = outputGraph.Workflows[0].Outputs[0].Schema.Contract!;
if (outputSchema["properties"]?["output"]?["type"]?.ToString() != "object" ||
    outputSchema["properties"]?["output"]?["additionalProperties"]?["x-gnougo-opaque"]?.ToString() != "true")
    throw new InvalidOperationException("Output schema specialization or nested opacity was lost");
workspaceCatalog.AllowedStepTypes.AddRange(["human.input", "set", "workflow.call"]);
PlanningConfirmationGuards.Apply(outputGraph, workspaceCatalog);
_ = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(outputGraph, workspaceCatalog)));
Console.WriteLine("operation outputs: approved payload and partial envelope survive source-generated serialization and YAML compilation");


foreach (var name in PlanningCorpus.Names)
{
    var environment = new PlanningBenchmarkCases.Environment(name); var engine = new WorkflowEngine { McpClientFactory = environment.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
    var runtime = new PlanningCorpus.Runtime(name, engine); var planner = new HybridWorkflowPlanner();
    // The frozen stress corpus uses the same existing request limits as its live campaign.
    var state = new PlanningSession { Request = new() { TenantId = "smoke", Prompt = PlanningCorpus.Prompt(name),
        Generation = new() { MaxInputTokensPerRequest = 96000, MaxOutputTokens = 32768 } } };
    for (var i = 0; i < 20 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
    {
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
    }
    if (state.Status != PlanningStatus.FinalReview || state.ModelCalls > state.Request.MaxModelCalls || environment.Effects.Count != 0) throw new InvalidOperationException(JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
    state = await planner.AdvanceAsync(state, new() { Kind = "approve", ReviewedRequirementIds = state.Requirements!.Outcomes.Select(r => r.Id).ToList(), ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, CancellationToken.None);
    if (state.Status != PlanningStatus.Approved) throw new InvalidOperationException("Approval failed.");
    var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
    var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], PlanningBenchmarkCases.Inputs(name, "nominal"), CancellationToken.None);
    if (!environment.Verify(result)) throw new InvalidOperationException("Independent result assertion failed: " + result.Error?.Message);
    Console.WriteLine(name + ": passed, calls=" + state.ModelCalls + ", replans=" + state.ReplanAttempts + ", validations=" + state.ValidationResults.Count);
}

// Typed semantic transformations must survive source-generated serialization and AOT.
var products = new ProductTransformationFixture();
var productEngine = new WorkflowEngine { McpClientFactory = products.Factory(), LLMClient = products, LlmDefaults = new() { Model = "mock" }, HumanInputProvider = new PlanningCorpus.Human() };
var productRuntime = new WorkflowPlanningRuntime(productEngine, (_, _) => Task.CompletedTask);
var productCatalog = await productRuntime.DiscoverAsync(new(), CancellationToken.None);
foreach (var source in await productRuntime.Capabilities.ListSourcesAsync(CancellationToken.None))
{
    var page = await productRuntime.Capabilities.ListAsync(source.Id, null, CancellationToken.None);
    foreach (var summary in page.Capabilities) productCatalog.Capabilities.Add(await productRuntime.Capabilities.ResolveAsync(summary, CancellationToken.None));
}
var productPlan = ProductTransformationPlan.Create(productCatalog, products);
productPlan = JsonSerializer.Deserialize(JsonSerializer.Serialize(productPlan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
var productCompilation = new TaskPlanCompiler().Compile(productPlan, productCatalog);
if (productCompilation.Graph is null || productCompilation.Diagnostics.Count != 0) throw new InvalidOperationException("Transform compilation failed");
PlanningConfirmationGuards.Apply(productCompilation.Graph, productCatalog);
var productDocument = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(productCompilation.Graph, productCatalog)));
var productResult = await productEngine.ExecuteAsync(productDocument.Workflows[productDocument.Entrypoint!], new JsonObject { ["search"] = ProductTransformationFixture.SearchUrl }, CancellationToken.None);
if (!productResult.Success || !products.VerifyText() || products.Effects.Last() != "close") throw new InvalidOperationException("Transform oracle failed: " + productResult.Error?.Message);
var proposal = new PlanningProposal { DiscoveryRequests = [new("browser", Query: "inspect pages"), new("document")] };
if (JsonSerializer.Deserialize(JsonSerializer.Serialize(proposal, PlanningJsonContext.Default.PlanningProposal), PlanningJsonContext.Default.PlanningProposal)!.DiscoveryRequests!.Count != 2) throw new InvalidOperationException("Discovery batch serialization failed");
if (JsonSerializer.Deserialize(JsonSerializer.Serialize(proposal, PlanningJsonContext.Default.PlanningProposal), PlanningJsonContext.Default.PlanningProposal)!.DiscoveryRequests![0].Query != "inspect pages") throw new InvalidOperationException("Discovery query serialization failed");
var discoveryReceipt = new CapabilityPage("source", null, [], "rank:snapshot:8", Query: "inspect pages");
var artifactReceipt = new CapabilityPage("source", null,
    [new("producer", "source", "declared", "", "mcp.call", "read", "v1", ArtifactContract: new(1, [new("evidence", "/value", "materialize")], []))],
    "filtered-next", Query: "request", ProducedArtifactKind: "evidence");
var artifactRoundtrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(artifactReceipt, PlanningJsonContext.Default.CapabilityPage), PlanningJsonContext.Default.CapabilityPage)!;
if (artifactRoundtrip.ProducedArtifactKind != "evidence" || artifactRoundtrip.Capabilities[0].ArtifactContract?.Produces[0].Kind != "evidence")
    throw new InvalidOperationException("Artifact-filter discovery serialization failed");
var filterRequest = new PlanningDiscoveryRequest("source", ProducedArtifactKind: "evidence");
if (JsonSerializer.Deserialize(JsonSerializer.Serialize(filterRequest, PlanningJsonContext.Default.PlanningDiscoveryRequest), PlanningJsonContext.Default.PlanningDiscoveryRequest)!.ProducedArtifactKind != "evidence")
    throw new InvalidOperationException("Artifact-filter request serialization failed");
var focusedDiscovery = new CapabilityDiscoveryState { PresentationQuery = "inspect pages", Pages = [discoveryReceipt],
    Inspections = [new("source", OperationIds: ["selected_operation"])] };
if (JsonSerializer.Deserialize(JsonSerializer.Serialize(focusedDiscovery, PlanningJsonContext.Default.CapabilityDiscoveryState), PlanningJsonContext.Default.CapabilityDiscoveryState)!.PresentationQuery != "inspect pages") throw new InvalidOperationException("Discovery focus serialization failed");
if (JsonSerializer.Deserialize(JsonSerializer.Serialize(discoveryReceipt, PlanningJsonContext.Default.CapabilityPage), PlanningJsonContext.Default.CapabilityPage)!.Query != "inspect pages") throw new InvalidOperationException("Discovery receipt serialization failed");
if (JsonSerializer.Deserialize(JsonSerializer.Serialize(focusedDiscovery, PlanningJsonContext.Default.CapabilityDiscoveryState), PlanningJsonContext.Default.CapabilityDiscoveryState)!.Inspections![0].OperationIds![0] != "selected_operation")
    throw new InvalidOperationException("Contract inspection selection serialization failed");
Console.WriteLine("typed transforms: passed; mocked inference; ordered products and cleanup verified");

// New plan-only requests and saved request schemas retain their distinct meanings in AOT.
var bounded = new PlanningSession { Request = new() { TenantId = "smoke", Prompt = "Return declared data" }, ModelCalls = 6 };
bounded.Discovery.Sources.Add(new("declared", "Declared source"));
var boundedSchema = PlanningSchemas.Proposal(bounded);
var noPlan = new JsonObject { ["requirements"] = new JsonObject { ["summary"] = "Return data", ["outputs"] = new JsonArray(), ["outcomes"] =
    new JsonArray(new JsonObject { ["id"] = "data", ["description"] = "Return declared data" }), ["inputs"] = null }, ["discoveryRequests"] = null, ["plan"] = null, ["clarifications"] = null };
if (!PlanningSchemas.AllowsNoPlan(boundedSchema) || PlanningContractValidation.ValidateInstance(noPlan, boundedSchema).Count != 0)
    throw new InvalidOperationException("Closed discovery must permit a safe no-plan response");
bounded.PendingCall = new() { Id = "retained", Purpose = "tasks", Request = new() { StructuredOutputSchema = boundedSchema } };
bounded = JsonSerializer.Deserialize(JsonSerializer.Serialize(bounded, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
if (!PlanningSchemas.AllowsNoPlan(bounded.PendingCall!.Request.StructuredOutputSchema) || bounded.ModelCalls != 6)
    throw new InvalidOperationException("Saved discovery restriction or accounting changed");
Console.WriteLine("bounded discovery: passed; issued no-plan schema and counters survive serialization");

// Explicit finite domains and deterministic encoding use the existing runtime,
// including source-generated serialization. No model or MCP client is supplied.
var encodingPlan = new TaskPlan { Inputs = [new() { Name = "decision", Type = new() { Kind = "string", Enum = ["allow", "deny"] } }],
    Root = new() { Outputs = [new("encoded", new() { Kind = "json", Items = [new() { Kind = "object", Members =
        [new("decision", new() { Kind = "input", Source = "decision" })] }] })] } };
encodingPlan = JsonSerializer.Deserialize(JsonSerializer.Serialize(encodingPlan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
if (!encodingPlan.Inputs[0].Type.Enum!.SequenceEqual(new[] { "allow", "deny" })) throw new InvalidOperationException("Enum serialization failed");
var encodingEngine = new WorkflowEngine();
var encodingRuntime = new WorkflowPlanningRuntime(encodingEngine, (_, _) => Task.CompletedTask);
var encodingCatalog = await encodingRuntime.DiscoverAsync(new(), CancellationToken.None);
var encodingGraph = new TaskPlanCompiler().Compile(encodingPlan, encodingCatalog);
if (encodingGraph.Graph is null || encodingGraph.Diagnostics.Count != 0) throw new InvalidOperationException("Encoding compilation failed");
var encodingYaml = new PlanningGraphCompiler().Compile(encodingGraph.Graph, encodingCatalog);
if ((await encodingRuntime.ValidateAsync(new(encodingYaml, new(), encodingCatalog, []), CancellationToken.None)).Count != 0) throw new InvalidOperationException("Encoding validation failed");
var encodingDocument = new WorkflowCompiler().Compile(WorkflowParser.Parse(encodingYaml));
foreach (var selected in new[] { "allow", "deny" })
{
    var result = await encodingEngine.ExecuteAsync(encodingDocument.Workflows["main"], new JsonObject { ["decision"] = selected }, CancellationToken.None);
    if (!result.Success || JsonNode.Parse(result.Outputs!["encoded"]!.GetValue<string>())!["decision"]!.GetValue<string>() != selected)
        throw new InvalidOperationException("Encoding changed the business value");
}
Console.WriteLine("enum contracts and JSON encoding: passed; no inference");

// Typed record fields remain semantic values through serialization and AOT;
// the runtime performs checked projection into an MCP selector without inference.
var selectedStates = new List<string>();
var fieldFactory = new InMemoryMcpClientFactory();
var selectorSchema = JsonNode.Parse("""{"type":"object","required":["event"],"properties":{"event":{"type":"string","enum":["allow","deny"]}}}""")!.AsObject();
fieldFactory.RegisterServer("renamed", new() { Tools = [new() { Name = "consume", InputSchema = selectorSchema }], ToolHandlers = new()
    { ["consume"] = input => { selectedStates.Add(input!["event"]!.GetValue<string>()); return new() { Content = new JsonObject() }; } } });
encodingEngine.McpClientFactory = fieldFactory;
encodingCatalog.Capabilities.Add(new() { Id = "selector", Version = "1", Kind = "tool", StepType = "mcp.call", Server = "renamed", Method = "consume",
    InputSchema = selectorSchema, OutputSchema = new() { ["type"] = "object" }, EffectKind = "none" });
var fieldPlan = new TaskPlan { Inputs = [new() { Name = "records", Type = new() { Kind = "array", Items = new() { Kind = "object", Fields =
    [new() { Name = "state", Type = new() { Kind = "string", Enum = ["allow", "deny"] } }] } } }],
    Root = new() { Tasks = [new() { Id = "records", Kind = "foreach", Objective = "Read states in order", Items = new() { Kind = "input", Source = "records" },
        Body = new() { Tasks = [new() { Id = "consume", Kind = "operation", Objective = "Consume the declared state", Operation = "selector",
            Inputs = [new("event", new() { Kind = "field", Port = "state", Items = [new() { Kind = "item" }] })] }], Outputs = [new("states", new() { Kind = "field", Port = "state", Items = [new() { Kind = "item" }] })] } }],
        Outputs = [new("states", new() { Kind = "output", Source = "records", Port = "states" })] } };
// Composite scope exports must retain the selected fields after successful finalization.
fieldPlan.Root.Tasks[0].Body!.Outputs.Add(new("details", new() { Kind = "object", Members =
    [new("state", new() { Kind = "field", Port = "state", Items = [new() { Kind = "item" }] })] }));
fieldPlan.Root.Outputs.Add(new("details", new() { Kind = "output", Source = "records", Port = "details" }));
fieldPlan = JsonSerializer.Deserialize(JsonSerializer.Serialize(fieldPlan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
var fieldGraph = new TaskPlanCompiler().Compile(fieldPlan, encodingCatalog);
if (fieldGraph.Graph is null || fieldGraph.Diagnostics.Count != 0) throw new InvalidOperationException("Field compilation failed");
var fieldYaml = new PlanningGraphCompiler().Compile(fieldGraph.Graph, encodingCatalog);
var fieldDiagnostics = await encodingRuntime.ValidateAsync(new(fieldYaml, new(), encodingCatalog, PlanningGraphCompiler.CapabilityBindings(fieldGraph.Graph)), CancellationToken.None);
if (fieldDiagnostics.Count != 0) throw new InvalidOperationException("Field validation failed: " + string.Join("; ", fieldDiagnostics.Select(d => d.Code + ": " + d.Message)));
var fieldDocument = new WorkflowCompiler().Compile(WorkflowParser.Parse(fieldYaml));
var fieldRun = await encodingEngine.ExecuteAsync(fieldDocument.Workflows["main"], new JsonObject { ["records"] = new JsonArray(new JsonObject { ["state"] = "deny" }, new JsonObject { ["state"] = "allow" }) }, CancellationToken.None);
if (!selectedStates.SequenceEqual(new[] { "deny", "allow" }) || !fieldRun.Success || !fieldRun.Outputs!["states"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { "deny", "allow" }))
    throw new InvalidOperationException("Field binding changed order or value");
if (!JsonNode.DeepEquals(JsonNode.Parse("""[{"state":"deny"},{"state":"allow"}]"""), fieldRun.Outputs!["details"]))
    throw new InvalidOperationException("Composite scope exports changed their values or order");
Console.WriteLine("typed field bindings: passed; checked MCP selectors; ordered records and composed exports; no inference");

// A typed collection and a literal empty branch retain their element contract.
var conditionalRows = JsonSerializer.Deserialize(JsonSerializer.Serialize(fieldPlan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
conditionalRows.Inputs.Add(new() { Name = "selected", Type = new() { Kind = "boolean" } });
conditionalRows.Root.Tasks[0].Items = new() { Kind = "output", Source = "choose_rows", Port = "rows" };
conditionalRows.Root.Tasks.Insert(0, new() { Id = "choose_rows", Kind = "conditional", Objective = "Select rows",
    Condition = new() { Kind = "input", Source = "selected" },
    Body = new() { Outputs = [new("rows", new() { Kind = "input", Source = "records" })] },
    Otherwise = new() { Outputs = [new("rows", new() { Kind = "array" })] } });
var conditionalRowsGraph = new TaskPlanCompiler().Compile(conditionalRows, encodingCatalog);
if (conditionalRowsGraph.Graph is null || conditionalRowsGraph.Diagnostics.Count != 0) throw new InvalidOperationException("Conditional array compilation failed");
var conditionalRowsYaml = new PlanningGraphCompiler().Compile(conditionalRowsGraph.Graph, encodingCatalog);
if ((await encodingRuntime.ValidateAsync(new(conditionalRowsYaml, new(), encodingCatalog, PlanningGraphCompiler.CapabilityBindings(conditionalRowsGraph.Graph)), CancellationToken.None)).Count != 0)
    throw new InvalidOperationException("Conditional array validation failed");
var conditionalRowsDocument = new WorkflowCompiler().Compile(WorkflowParser.Parse(conditionalRowsYaml));
foreach (var selected in new[] { false, true })
{
    selectedStates.Clear();
    var result = await encodingEngine.ExecuteAsync(conditionalRowsDocument.Workflows["main"], new JsonObject
        { ["selected"] = selected, ["records"] = new JsonArray(new JsonObject { ["state"] = "deny" }, new JsonObject { ["state"] = "allow" }) }, CancellationToken.None);
    var expected = selected ? new JsonArray("deny", "allow") : new JsonArray();
    if (!result.Success || !JsonNode.DeepEquals(expected, result.Outputs!["states"]) || selectedStates.Count != expected.Count)
        throw new InvalidOperationException("Conditional array execution changed values or ran an empty body");
}
Console.WriteLine("conditional arrays: passed; typed and empty branches execute with preserved contracts; no inference");

// Literal null exports use authoritative schemas across conditional boundaries.
var nullPlan = new TaskPlan { Inputs = [new() { Name = "selected", Type = new() { Kind = "boolean" } }],
    Root = new() { Tasks = [new() { Id = "choose", Kind = "conditional", Objective = "Choose a continuation", Condition = new() { Kind = "input", Source = "selected" },
        Body = new() { Outputs = [new("cursor", new() { Kind = "string", Text = "page-two" })] },
        Otherwise = new() { Outputs = [new("cursor", new())] } }],
        Outputs = [new("cursor", new() { Kind = "output", Source = "choose", Port = "cursor" })] } };
nullPlan = JsonSerializer.Deserialize(JsonSerializer.Serialize(nullPlan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
var nullCompilation = new TaskPlanCompiler().Compile(nullPlan, encodingCatalog);
if (nullCompilation.Graph is null || nullCompilation.Diagnostics.Count != 0) throw new InvalidOperationException("Null-only compilation failed");
var nullYaml = new PlanningGraphCompiler().Compile(nullCompilation.Graph, encodingCatalog);
if ((await encodingRuntime.ValidateAsync(new(nullYaml, new(), encodingCatalog, []), CancellationToken.None)).Count != 0) throw new InvalidOperationException("Null-only validation failed");
var nullDocument = new WorkflowCompiler().Compile(WorkflowParser.Parse(nullYaml));
foreach (var selected in new[] { false, true })
{
    var result = await encodingEngine.ExecuteAsync(nullDocument.Workflows["main"], new JsonObject { ["selected"] = selected }, CancellationToken.None);
    if (!result.Success || !result.Outputs!.AsObject().ContainsKey("cursor") ||
        !JsonNode.DeepEquals(selected ? JsonValue.Create("page-two") : null, result.Outputs["cursor"]))
        throw new InvalidOperationException("Conditional null output changed its value");
}
Console.WriteLine("null-only contracts: passed; both conditional branches; no inference");

var cleanupPlan = JsonSerializer.Deserialize("""
{"inputs":[{"name":"selected","type":{"kind":"boolean"}}],"root":{
 "tasks":[{"id":"prepare","kind":"value","objective":"Retain condition","outputs":[{"name":"flag","value":{"kind":"input","source":"selected"}}]}],
 "always":[{"id":"finish","kind":"conditional","objective":"Export explicit cleanup result",
 "condition":{"kind":"field","port":"flag","items":[{"kind":"output","source":"prepare"}]},
 "body":{"outputs":[{"name":"result","value":{"kind":"string","text":"performed"}}]},
 "otherwise":{"outputs":[{"name":"result","value":{"kind":"string","text":"skipped"}}]}}],
 "outputs":[{"name":"cleanup","value":{"kind":"output","source":"finish","port":"result"}}]}}
""", PlanningJsonContext.Default.TaskPlan)!;
var cleanupGraph = new TaskPlanCompiler().Compile(cleanupPlan, encodingCatalog);
if (cleanupGraph.Graph is null || cleanupGraph.Diagnostics.Count != 0) throw new InvalidOperationException("Cleanup compilation failed");
var cleanupYaml = new PlanningGraphCompiler().Compile(cleanupGraph.Graph, encodingCatalog);
if ((await encodingRuntime.ValidateAsync(new(cleanupYaml, new(), encodingCatalog, []), CancellationToken.None)).Count != 0) throw new InvalidOperationException("Cleanup validation failed");
var cleanupDocument = new WorkflowCompiler().Compile(WorkflowParser.Parse(cleanupYaml));
foreach (var selected in new[] { true, false })
{
    var run = await encodingEngine.ExecuteAsync(cleanupDocument.Workflows["main"], new JsonObject { ["selected"] = selected }, CancellationToken.None);
    if (!run.Success || run.Outputs!["cleanup"]!.GetValue<string>() != (selected ? "performed" : "skipped")) throw new InvalidOperationException("Conditional cleanup changed its selected output");
}
Console.WriteLine("conditional cleanup: passed; guarded field selection and explicit branch exports; no inference");

// Catalog-owned fields stay out of semantic intent through serialization and AOT.
var ownedCatalog = await productRuntime.DiscoverAsync(new(), CancellationToken.None);
ownedCatalog.Capabilities.Add(new() { Id = "owned_operation", Version = "v1", Kind = "registered", StepType = "set", EffectKind = "none",
    InputSchema = JsonNode.Parse("""{"type":"object","properties":{"selector":{"type":"string"},"text":{"type":"string"}},"required":["selector","text"],"additionalProperties":false}""")!.AsObject(),
    OutputSchema = new() { ["type"] = "object" }, FixedInput = new() { ["selector"] = "host" } });
var ownedPlan = new TaskPlan { Root = new() { Tasks = [new() { Id = "work", Kind = "operation", Objective = "Use the business text", Operation = "owned_operation",
    Inputs = [new("text", new() { Kind = "string", Text = "business" })] }] } };
ownedPlan = JsonSerializer.Deserialize(JsonSerializer.Serialize(ownedPlan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
var ownedCompilation = new TaskPlanCompiler().Compile(ownedPlan, ownedCatalog);
if (ownedCompilation.Graph is null || ownedCompilation.Diagnostics.Count != 0) throw new InvalidOperationException("Owned input compilation failed");
var ownedYaml = new PlanningGraphCompiler().Compile(ownedCompilation.Graph, ownedCatalog);
var ownedWorkflow = WorkflowParser.Parse(ownedYaml);
if (ownedWorkflow.Workflows.Values.SelectMany(w => w.Steps).Single().Input!["selector"]!.ToString() != "host")
    throw new InvalidOperationException("Catalog input was not injected");
ownedPlan.Root.Tasks[0].Inputs.Add(new("selector", new() { Kind = "string", Text = "host" }));
var ownedRejection = new TaskPlanCompiler().Compile(ownedPlan, ownedCatalog);
if (ownedRejection.Graph is not null || !ownedRejection.Diagnostics.Any(d => d.Code == "TASK_INPUT_HOST_OWNED"))
    throw new InvalidOperationException("Catalog override was accepted");
Console.WriteLine("catalog-owned bindings: passed; host injection and explicit override rejection verified");

// Patch-only repair is a private wire contract, round-tripped through source generation.
var repairState = new PlanningSession { Request = new() { TenantId = "smoke", Prompt = "Use the declared business text" },
    IntentVersion = 2, Requirements = new() { Summary = "Use text", Inputs = [], Outcomes = [new("text", "Use business text")] }, Plan = ownedPlan, Catalog = ownedCatalog,
    Diagnostics = ownedRejection.Diagnostics.ToList(), RevisionScope = TaskPlanRevisions.Scope(ownedPlan, ownedRejection.Diagnostics).ToList() };
var repairSchema = PlanningSchemas.Proposal(repairState);
var repairRequest = new LLMRequest { Prompt = HybridWorkflowPlanner.Prompt(repairState), StructuredOutputSchema = repairSchema };
var removeOwned = JsonNode.Parse("""{"discoveryRequests":null,"clarifications":null,"patch":{"edits":[{"slot":"s0","action":"remove"}]}}""")!;
if (PlanningContractValidation.ValidateSchema(repairSchema, strict: true).Count != 0 || PlanningContractValidation.ValidateInstance(removeOwned, repairSchema).Count != 0)
    throw new InvalidOperationException("Typed repair schema failed");
var patchResponse = removeOwned.Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!;
patchResponse = JsonSerializer.Deserialize(JsonSerializer.Serialize(patchResponse, RepairJsonContext.Default.PlanningRepairResponse), RepairJsonContext.Default.PlanningRepairResponse)!;
repairState = JsonSerializer.Deserialize(JsonSerializer.Serialize(repairState, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
var repairedPlan = PlanningRepairPatch.Apply(repairState, patchResponse.Patch!, repairRequest);
if (repairedPlan.Root.Tasks[0].Inputs.Any(i => i.Name == "selector") || repairState.Plan!.Root.Tasks[0].Inputs.All(i => i.Name != "selector") ||
    new TaskPlanCompiler().Compile(repairedPlan, ownedCatalog).Diagnostics.Count != 0)
    throw new InvalidOperationException("Atomic source-generated repair failed");
Console.WriteLine("typed repair patches: passed; source-generated recovery; owned removal; immutable baseline; no inference");

// Structural replacement must preserve business arguments under Native AOT too.
repairState.Plan = repairedPlan;
repairState.Plan.Root.Tasks[0].Operation = "unresolved_operation";
repairState.Diagnostics = new TaskPlanCompiler().Compile(repairState.Plan, ownedCatalog).Diagnostics.ToList();
repairState.RevisionScope = TaskPlanRevisions.Scope(repairState.Plan, repairState.Diagnostics).ToList();
var structuralRequest = new PlanningPrompt(repairState).Request();
var structuralSlot = PlanningRepairPatch.Slots(repairState, PlanningSchemas.FullProposal(repairState, compact: false)["$defs"]!.AsObject()).Single(s => s.Kind == "task");
var structuralPatch = new RepairPatch { Edits = [new() { Slot = structuralSlot.Id, Action = "replace_task", Value = JsonNode.Parse("""
{"id":"work","kind":"operation","objective":"Use the business text","dependsOn":[],"operation":"owned_operation","inputs":[{"name":"text","value":{"kind":"string","text":"business"}}]}
""") }] };
structuralPatch = JsonSerializer.Deserialize(JsonSerializer.Serialize(structuralPatch, RepairJsonContext.Default.RepairPatch), RepairJsonContext.Default.RepairPatch)!;
var structurallyRepaired = PlanningRepairPatch.Apply(repairState, structuralPatch, structuralRequest);
if (structurallyRepaired.Root.Tasks[0].Operation != "owned_operation" || structurallyRepaired.Root.Tasks[0].Inputs.Single().Value.Text != "business" ||
    repairState.Plan.Root.Tasks[0].Operation != "unresolved_operation") throw new InvalidOperationException("Structural repair lost authority or business intent");
Console.WriteLine("structural repair: passed; version-eight authority, preserved arguments and atomic AOT round trip; no inference");

repairState.Plan = structurallyRepaired;
repairState.Diagnostics.Clear();
repairState.EditablePaths = ["/tasks/work/inputs/text"];
repairState.RevisionScope = [.. repairState.EditablePaths];
TaskPlanRevisions.ValidateEditablePaths(repairState, repairState.EditablePaths);
repairState = JsonSerializer.Deserialize(JsonSerializer.Serialize(repairState, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
var targetedRequest = new PlanningPrompt(repairState).Request();
var targetedPatch = JsonNode.Parse("""{"edits":[{"slot":"s0","action":"replace","value":{"kind":"string","text":"explicit revision"}}]}""")!
    .Deserialize(RepairJsonContext.Default.RepairPatch)!;
var targetedPlan = PlanningRepairPatch.Apply(repairState, targetedPatch, targetedRequest);
if (targetedPlan.Root.Tasks[0].Inputs.Single().Value.Text != "explicit revision" ||
    repairState.Plan!.Root.Tasks[0].Inputs.Single().Value.Text != "business" || repairState.EditablePaths?.Single() != "/tasks/work/inputs/text")
    throw new InvalidOperationException("Targeted revision lost its serialized authority or changed the retained baseline");
Console.WriteLine("targeted revision: passed; optional authority round trip, typed patches and unchanged baseline; no inference");

sealed class ClarificationRuntime : IPlanningRuntime
{
    private readonly WorkflowPlanningRuntime _inner = new(new(), (_, _) => Task.CompletedTask);
    internal int Calls;
    public ICapabilityCatalog Capabilities => _inner.Capabilities;
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => _inner.DiscoverAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => _inner.ValidateAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => _inner.ValidateCatalogAsync(catalog, ct);
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        var proposal = ++Calls == 1 ? new PlanningProposal { Clarifications = [new("interface", "Which caller interface?", [new("none", "No caller inputs"), new("value", "A caller-supplied value")], "none")] }
            : new PlanningProposal { Requirements = PlanningCorpus.Requirements("local"), Plan = PlanningCorpus.LiteralResult() };
        return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal),
            request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) });
    }
}
