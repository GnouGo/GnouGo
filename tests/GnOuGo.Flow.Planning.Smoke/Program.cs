using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

// Additive host failure contracts must survive source-generated Native AOT serialization.
var taskFailure = new AgentTaskResult("failed", null, [], [], new(0, 0, 0))
{ Failure = new() { Code = "AGENT_ISOLATION_REQUIRED", Message = "Mandatory host isolation is not configured." } };
var restoredFailure = JsonSerializer.Deserialize(JsonSerializer.Serialize(taskFailure, AgentTaskJsonContext.Default.AgentTaskResult), AgentTaskJsonContext.Default.AgentTaskResult)!;
if (restoredFailure.Failure?.Code != taskFailure.Failure.Code) throw new InvalidOperationException("Agent failure serialization failed");
Console.WriteLine("agent failure: structured host diagnostic survives source-generated serialization");

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
    state = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, CancellationToken.None);
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
var noPlan = new JsonObject { ["requirements"] = new JsonObject { ["summary"] = "Return data", ["outcomes"] =
    new JsonArray(new JsonObject { ["id"] = "data", ["description"] = "Return declared data" }) }, ["discoveryRequests"] = null, ["plan"] = null };
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
    Requirements = new() { Summary = "Use text", Outcomes = [new("text", "Use business text")] }, Plan = ownedPlan, Catalog = ownedCatalog,
    Diagnostics = ownedRejection.Diagnostics.ToList(), RevisionScope = TaskPlanRevisions.Scope(ownedPlan, ownedRejection.Diagnostics).ToList() };
var repairSchema = PlanningSchemas.Proposal(repairState);
var repairRequest = new LLMRequest { Prompt = HybridWorkflowPlanner.Prompt(repairState), StructuredOutputSchema = repairSchema };
var removeOwned = JsonNode.Parse("""{"discoveryRequests":null,"patch":{"edits":[{"slot":"s0","action":"remove"}]}}""")!;
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

// Version-two replacement must preserve business arguments under Native AOT too.
repairState.Plan = repairedPlan;
repairState.Plan.Root.Tasks[0].Operation = "unresolved_operation";
repairState.Diagnostics = new TaskPlanCompiler().Compile(repairState.Plan, ownedCatalog).Diagnostics.ToList();
repairState.RevisionScope = TaskPlanRevisions.Scope(repairState.Plan, repairState.Diagnostics).ToList();
var structuralRequest = new PlanningPrompt(repairState).Request();
var structuralSlot = PlanningRepairPatch.Slots(repairState, structuralRequest.StructuredOutputSchema!["$defs"]!.DeepClone().AsObject()).Single(s => s.Kind == "task");
var structuralPatch = new RepairPatch { Edits = [new() { Slot = structuralSlot.Id, Action = "replace_task", Value = JsonNode.Parse("""
{"id":"work","kind":"operation","objective":"Use the business text","dependsOn":[],"operation":"owned_operation","inputs":[{"name":"text","value":{"kind":"string","text":"business"}}]}
""") }] };
structuralPatch = JsonSerializer.Deserialize(JsonSerializer.Serialize(structuralPatch, RepairJsonContext.Default.RepairPatch), RepairJsonContext.Default.RepairPatch)!;
var structurallyRepaired = PlanningRepairPatch.Apply(repairState, structuralPatch, structuralRequest);
if (structurallyRepaired.Root.Tasks[0].Operation != "owned_operation" || structurallyRepaired.Root.Tasks[0].Inputs.Single().Value.Text != "business" ||
    repairState.Plan.Root.Tasks[0].Operation != "unresolved_operation") throw new InvalidOperationException("Structural repair lost authority or business intent");
Console.WriteLine("structural repair: passed; version-two authority, preserved arguments and atomic AOT round trip; no inference");
