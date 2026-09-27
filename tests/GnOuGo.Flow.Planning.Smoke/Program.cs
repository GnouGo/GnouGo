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
var proposal = new PlanningProposal { DiscoveryRequests = [new("browser"), new("document")] };
if (JsonSerializer.Deserialize(JsonSerializer.Serialize(proposal, PlanningJsonContext.Default.PlanningProposal), PlanningJsonContext.Default.PlanningProposal)!.DiscoveryRequests!.Count != 2) throw new InvalidOperationException("Discovery batch serialization failed");
Console.WriteLine("typed transforms: passed; mocked inference; ordered products and cleanup verified");

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
Console.WriteLine("typed field bindings: passed; checked MCP selectors; ordered records; no inference");
