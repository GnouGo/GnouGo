using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RepairRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonNode Context(string prompt) => JsonNode.Parse(prompt[prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;

    [Fact]
    public async Task OperationIdentifierRecoversAnOmittedCapabilityDespiteInvalidArguments()
    {
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(40, """{"volume":{"type":"number"}}""", ["volume"]));
        var state = PlannerFixture.Session(); state.ModelCalls = 1;
        state.Request.Prompt = "Collect requested data with volume";
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        var wanted = state.Catalog.Capabilities.Last();
        foreach (var capability in state.Catalog.Capabilities) capability.Description = state.Request.Prompt;
        wanted.Description = "Temperature sensor reading";
        Assert.DoesNotContain(wanted, PlanningCapabilityCards.Shortlist(state.Catalog, state.Request.Prompt, 12000));
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation {
            Id = "temperature_sensor_reading", Purpose = state.Request.Prompt,
            Arguments = [new("volume", new() { Kind = "string", Text = "invalid" })] }] };
        Rebuild(state);
        var hole = Assert.Single(PlanningHoleEligibility.Find(state.Graph!, state.Catalog));
        Assert.Empty(PlanningHoleEligibility.Choices(state.Graph!, state.Catalog, hole));
        var targets = PlanningCorrections.Batch(state);
        var advice = Assert.Single(Context(PlanningCorrections.Prompt(state, targets))["alternatives"]!.AsArray())!["capabilities"]!.AsArray();
        Assert.Equal(4, advice.Count);
        Assert.Equal(wanted.Id, advice[0]!["id"]!.ToString());
        Assert.Null(((InvokeIntentOperation)state.IntentPlan.Operations[0]).Capability);
        runtime.Respond = request => Correction(request, new WorkflowIntentPlan { Operations = [new InvokeIntentOperation {
            Id = "temperature_sensor_reading", Capability = wanted.Id, Arguments = [new("volume", new() { Kind = "number", Number = 3 })] }] });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Message)));
        Assert.Single(runtime.Calls); Assert.Equal(1, state.RepairAttempts); Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task UntypedBranchResultOffersAnAtomicConversionTargetAndTheAbsentContract()
    {
        var (state, _) = await Untyped();
        var targets = PlanningCorrections.Batch(state);
        var target = Assert.Single(targets);
        Assert.Equal("/operations/0/branches/0/body", target.Path);
        Assert.Equal("block", target.Shape);
        var context = Context(PlanningCorrections.Prompt(state, targets));
        var binding = Assert.Single(context["bindings"]!["values"]!.AsArray(), b => b!["value"]!["source"]?.ToString() == "read");
        Assert.Equal("/operations/0/branches/0/body/operations/0", binding!["sourcePath"]!.ToString());
        Assert.Equal("absent", binding["contractStatus"]!.ToString());
        Assert.Empty(binding["contract"]!["schema"]!.AsObject());
        Assert.Contains("SCHEMA_REFERENCE_INVALID", context["diagnostics"]!.ToJsonString());
        Assert.NotEmpty(state.Diagnostics);
        Assert.Null(state.Yaml);
    }

    [Fact]
    public async Task MockedLocalConversionReachesReviewWithOneRepair()
    {
        var (state, runtime) = await Untyped();
        var corrected = Converted(state.IntentPlan!);
        runtime.Respond = request => Correction(request, corrected);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Single(runtime.Calls); Assert.Equal(1, state.RepairAttempts);
        Assert.Empty(state.Catalog!.Capabilities.Single().OutputSchema);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task ConversionEditsOnlyTheExistingBlockAndDoesNotResendLargeSiblings()
    {
        var (state, _) = await Untyped();
        var parallel = (ParallelIntentOperation)state.IntentPlan!.Operations[0];
        parallel.Branches.Add(new("independent", new([new CalculateIntentOperation {
            Id = "unrelated", Purpose = new string('z', 36_000), Value = new() { Kind = "number", Number = 1 }
        }], new() { Kind = "result", Source = "unrelated" })));
        Rebuild(state);
        var targets = PlanningCorrections.Batch(state); var target = Assert.Single(targets);
        Assert.Equal("/operations/0/branches/0/body", target.Path);
        Assert.Equal("block", target.Shape);
        var prompt = PlanningCorrections.Prompt(state, targets);
        Assert.DoesNotContain(new string('z', 100), prompt);
        Assert.True(PlanningJsonTransport.EstimateInputTokens(prompt, PlanningCorrections.Schema(targets)) <= 12_000);
    }

    [Theory]
    [InlineData("{\"count\":4}", false, true)]
    [InlineData("{\"count\":4}", true, true)]
    [InlineData("broken json", true, false)]
    [InlineData("{}", false, false)]
    [InlineData("{\"count\":\"4\"}", false, false)]
    [InlineData("{\"count\":null}", false, false)]
    [InlineData("null", false, false)]
    [InlineData("[]", false, false)]
    public async Task CheckedConversionRejectsInvalidPayloadBeforeDownstreamEffect(string payload, bool asText, bool success)
    {
        var (state, runtime) = await Untyped();
        var corrected = Converted(state.IntentPlan!);
        runtime.Respond = request => Correction(request, corrected);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var document = WorkflowParser.Parse(state.Yaml!);
        var main = document.Workflows[document.Entrypoint!];
        var downstream = 0; var reads = 0;
        main.Steps.Add(new() { Id = "downstream", Type = "mcp.call", Input = new JsonObject {
            ["server"] = "laboratory", ["method"] = "consume", ["request"] = new JsonObject() } });
        var factory = new InMemoryMcpClientFactory(); var config = new MockMcpServerConfig();
        foreach (var name in new[] { "read", "consume" }) config.Tools.Add(new() { Name = name, EffectKind = "read", InputSchema = new JsonObject { ["type"] = "object" } });
        config.ToolHandlers["read"] = _ => { reads++; return new McpCallResult { Content = asText ? JsonValue.Create(payload) : JsonNode.Parse(payload) }; };
        config.ToolHandlers["consume"] = _ => { downstream++; return new McpCallResult { Content = new JsonObject() }; };
        factory.RegisterServer("laboratory", config);
        var compiled = new WorkflowCompiler().Compile(document);
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(success == result.Success, result.Error?.Code + ": " + result.Error?.Message); Assert.Equal(success ? 1 : 0, downstream);
        Assert.Equal(1, reads);
        if (!success) Assert.DoesNotContain("MCP_", result.Error!.Code);
        if (success) Assert.Equal("4", result.Outputs!["samples"]!["sample"]!["count"]!.ToJsonString());
    }

    [Theory]
    [InlineData("branch", "/operations/0/then/operations/0/branches/0/body")]
    [InlineData("loop", "/operations/0/body/operations/0/branches/0/body")]
    [InlineData("cleanup", "/operations/0/operations/0/branches/0/body")]
    [InlineData("subflow", "/subflows/0/operations/0/branches/0/body")]
    public async Task ConversionTargetStaysInItsSmallestExistingScope(string container, string expected)
    {
        var (state, _) = await Untyped(); var inner = state.IntentPlan!.Operations[0];
        var body = new IntentBlock([inner], new() { Kind = "string", Text = "finished" });
        state.IntentPlan = container switch {
            "branch" => new() { Operations = [new ChooseIntentOperation { Id = "route", Condition = new() { Kind = "boolean", Boolean = true }, Then = body,
                Otherwise = new([], new() { Kind = "string", Text = "finished" }) }] },
            "loop" => new() { Operations = [new EachIntentOperation { Id = "iterate", Items = new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }] }, Body = body }] },
            "cleanup" => new() { Operations = [new CleanupIntentOperation { Id = "finish", Operations = [inner] }] },
            _ => new() { Operations = [new CallIntentOperation { Id = "run", Flow = "worker" }], Subflows = [new("worker", [], [inner], [new("done", new() { Kind = "string", Text = "finished" })])] }
        };
        Rebuild(state);
        var target = Assert.Single(PlanningCorrections.Batch(state));
        Assert.True(expected == target.Path, target.Path + "\n" + string.Join("\n", PlanningDiagnosticLocations.ForIntent(state).Select(d => d.Location + " " + d.Code + " " + d.Message)));
        Assert.Equal("block", target.Shape);
        Assert.DoesNotContain(PlanningDiagnosticLocations.ForIntent(state), d => d.Code == "PLANNING_HOST_CONTRACT");
    }

    [Fact]
    public async Task DeclaredContractWithAnInvalidFieldKeepsThePreciseValueTarget()
    {
        var (state, _) = await Untyped();
        state.Catalog!.Capabilities.Single().OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"count":{"type":"number"}},"required":["count"]}""")!.AsObject();
        var block = ((ParallelIntentOperation)state.IntentPlan!.Operations[0]).Branches[0].Body;
        block.Result.Path = ["invented"];
        Rebuild(state);
        // Isolate the incorrect reference from downstream boundary consequences.
        state.Diagnostics = [new("OUTPUT_REFERENCE_INVALID", "/operations/0/branches/0/body/result", "Undeclared field.", ValidationStage: "intent")];
        var target = Assert.Single(PlanningCorrections.Batch(state));
        Assert.Equal("/operations/0/branches/0/body/result", target.Path);
        var context = Context(PlanningCorrections.Prompt(state, [target]));
        var binding = Assert.Single(context["bindings"]!["values"]!.AsArray(), b => b!["value"]!["source"]?.ToString() == "read");
        Assert.Equal("declared", binding!["contractStatus"]!.ToString());
        Assert.Equal("number", binding["contract"]!["schema"]!["properties"]!["count"]!["type"]!.ToString());
        Assert.Contains(PlanningExecutableValidation.Validate(state.Graph!, state.Catalog), d => d.Code == "OUTPUT_REFERENCE_INVALID");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task UnchangedAndUnissuedCorrectionsStopWithinTwoAttempts(bool invalid, int attempts)
    {
        var (state, runtime) = await Untyped();
        var before = PlanningJsonTransport.Intent(state.IntentPlan!).ToJsonString();
        runtime.Respond = request => invalid ? new() { Json = new JsonObject { ["changes"] = new JsonArray(new JsonObject {
            ["target"] = "unissued", ["replacement"] = new JsonObject { ["kind"] = "null" } }) } } : Correction(request, state.IntentPlan!);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(attempts, state.RepairAttempts);
        Assert.Equal(before, PlanningJsonTransport.Intent(state.IntentPlan!).ToJsonString()); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task ReservedTopologyCorrectionReplaysAfterRestartWithoutAnotherCharge()
    {
        var (state, runtime) = await Untyped(); var corrected = Converted(state.IntentPlan!);
        var targets = PlanningCorrections.Batch(state);
        var request = new LLMRequest { ClientRequestId = "original", Prompt = PlanningCorrections.Prompt(state, targets),
            StructuredOutputSchema = PlanningCorrections.Schema(targets), MaxTokens = 8192, Model = "test" };
        state.PendingCall = new() { Id = "original", Purpose = "repair", Request = request }; state.ModelCalls = 2; state.RepairAttempts = 1;
        state.Usage = new() { Calls = 2, InputTokens = 1500, OutputTokens = 400, TotalTokens = 1900 };
        state.ApprovedHash = "stale";
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        runtime.Respond = replay => { Assert.Equal(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), JsonSerializer.Serialize(replay, PlanningJsonContext.Default.LLMRequest)); return Correction(replay, corrected); };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts);
        Assert.Equal(1900, state.Usage!.TotalTokens); Assert.Null(state.ApprovedHash);
    }

    private static LLMResponse Correction(LLMRequest request, WorkflowIntentPlan corrected) => new() { Json = new JsonObject {
        ["changes"] = new JsonArray(Context(request.Prompt)["targets"]!.AsArray().Select(t => (JsonNode)new JsonObject {
            ["target"] = t!["id"]!.DeepClone(), ["replacement"] = PlanningFieldPaths.Read(PlanningJsonTransport.Intent(corrected), t["path"]!.ToString())!.DeepClone()
        }).ToArray()) } };

    private static WorkflowIntentPlan Converted(WorkflowIntentPlan original)
    {
        var invoke = (InvokeIntentOperation)((ParallelIntentOperation)original.Operations[0]).Branches[0].Body.Operations[0];
        return new() { Operations = [new ParallelIntentOperation { Id = "collect", Branches = [new("sample", new([
            invoke, new CalculateIntentOperation { Id = "validated", ResultType = new() { Type = "object", Fields = [new("count", new() { Type = "number" })] },
                Value = new() { Kind = "compute", Text = """
                    const data = typeof payload === 'string' ? JSON.parse(payload) : payload;
                    if (data === null || typeof data !== 'object' || Array.isArray(data) || typeof data.count !== 'number' || !Number.isFinite(data.count)) throw new Error('Invalid sample count');
                    return { count: data.count };
                    """, Members = [new("payload", new() { Kind = "result", Source = "read" })] } }
        ], new() { Kind = "result", Source = "validated" }))] }], Outputs = [new("samples", new() { Kind = "result", Source = "collect" })] };
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Untyped()
    {
        var factory = new InMemoryMcpClientFactory();
        var config = new MockMcpServerConfig();
        config.Tools.Add(new() { Name = "read", EffectKind = "read", InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            ExampleResponse = JsonNode.Parse("""{"count":4}""") });
        factory.RegisterServer("laboratory", config);
        var runtime = new TestRuntime(mcp: factory); var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.ModelCalls = 1;
        Assert.Empty(state.Catalog.Capabilities.Single().OutputSchema);
        state.IntentPlan = new() { Operations = [new ParallelIntentOperation { Id = "collect", Branches = [new("sample", new([
            new InvokeIntentOperation { Id = "read", Capability = state.Catalog.Capabilities.Single().Id }
        ], new() { Kind = "result", Source = "read" }))] }], Outputs = [new("samples", new() { Kind = "result", Source = "collect" })] };
        Rebuild(state); return (state, runtime);
    }

    private static void Rebuild(PlanningSession state)
    {
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan!, state.Catalog!);
        state.Diagnostics = PlanningExecutableValidation.Validate(state.Graph, state.Catalog!).ToList();
    }
}
