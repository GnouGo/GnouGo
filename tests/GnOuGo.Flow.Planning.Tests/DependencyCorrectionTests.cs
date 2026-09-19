using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DependencyCorrectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CalculateIntentOperation Number(string id, params string[] after) => new() { Id = id, After = [.. after], Value = new() { Kind = "number", Number = 7 } };
    private static JsonNode Context(PlanningSession state)
    {
        var prompt = PlanningCorrections.Prompt(state, PlanningCorrections.Targets(state));
        return JsonNode.Parse(prompt[prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
    }
    private static async Task<PlanningSession> State(WorkflowIntentPlan intent, bool confirmation = false)
    {
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(1)); var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.IntentPlan = intent;
        if (confirmation)
        {
            state.Catalog.Capabilities[0].EffectKind = "write";
            intent.Operations.Insert(0, new InvokeIntentOperation { Id = "acquire", Capability = state.Catalog.Capabilities[0].Id });
        }
        state.Graph = PlanningGraphBuilder.Build(intent, state.Catalog); PlanningConfirmationGuards.Apply(state.Graph, state.Catalog);
        state.Diagnostics = PlanningExecutableValidation.Validate(state.Graph, state.Catalog).ToList();
        return state;
    }

    [Fact]
    public async Task CleanupRepairReceivesScopesAndCycleFreeBusinessPredecessorsAfterConfirmation()
    {
        var state = await State(new() { Operations = [Number("result", "release_all"), Number("later", "result"),
            new CleanupIntentOperation { Id = "release_all", Operations = [Number("release")] }] }, confirmation: true);
        Assert.Contains(state.Diagnostics, d => d.Code == "DEPENDENCY_UNKNOWN");
        var context = Context(state);
        var target = Assert.Single(context["dependencyTargets"]!.AsArray());
        Assert.Equal("result", target!["operation"]!.ToString());
        Assert.Equal(["acquire"], target["eligibleAfter"]!.AsArray().Select(v => v!.ToString()));
        var operations = context["bindings"]!["operations"]!.AsArray();
        var release = operations.Single(o => o!["id"]!.ToString() == "release")!;
        Assert.Equal("/", release["scope"]!.ToString()); Assert.Equal("finalizer", release["placement"]!.ToString());
        Assert.Equal("cleanup_group", operations.Single(o => o!["id"]!.ToString() == "release_all")!["placement"]!.ToString());
        Assert.Contains("not an executable predecessor", target["explanation"]!.ToString());
        Assert.DoesNotContain("__planning_", context["bindings"]!.ToJsonString());
        Assert.DoesNotContain("__planning_", context["dependencyTargets"]!.ToJsonString());
    }

    [Fact]
    public async Task RepeatedIdsInBranchesExposeOnlyTheirOwnScope()
    {
        var state = await State(new() { Operations = [Number("outer"), new ChooseIntentOperation { Id = "route", Condition = new() { Kind = "boolean", Boolean = true },
            Then = new([Number("same"), Number("consumer", "outer")], new() { Kind = "result", Source = "consumer" }),
            Otherwise = new([Number("same"), Number("other")], new() { Kind = "result", Source = "other" }) }] });
        var target = Assert.Single(Context(state)["dependencyTargets"]!.AsArray());
        Assert.Equal("/operations/1/then/operations/1", target!["path"]!.ToString());
        Assert.Equal(["same"], target["eligibleAfter"]!.AsArray().Select(v => v!.ToString()));
        Assert.Equal("/operations/1/then", target["scope"]!.ToString());
    }

    [Fact]
    public async Task FinalizerChoicesPermitMainStepOrderingButDoNotMutateTheGraph()
    {
        var state = await State(new() { Operations = [Number("resource"), new CleanupIntentOperation { Id = "cleanup", Operations = [Number("release", "absent")] }] });
        var before = PlanningGraphCompiler.Fingerprint(state.Graph!);
        var target = Assert.Single(Context(state)["dependencyTargets"]!.AsArray());
        Assert.Equal(["resource"], target!["eligibleAfter"]!.AsArray().Select(v => v!.ToString()));
        Assert.Contains("ordering only", target["explanation"]!.ToString());
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(state.Graph!));
    }

    [Fact]
    public async Task CalculationAndCleanupExamplesAreSharedByInterpretationAndRepair()
    {
        var state = await State(new() { Operations = [Number("result", "missing")] });
        foreach (var prompt in new[] { PlanningModelCalls.IntentPrompt(state), PlanningCorrections.Prompt(state, PlanningCorrections.Targets(state)) })
        {
            Assert.Contains("amount * rate", prompt);
            Assert.Contains("undeclared", prompt);
            Assert.Contains("ordering only, not successful completion", prompt);
            Assert.Contains("cleanup", prompt);
        }
    }

    [Fact]
    public async Task UndeclaredAssignmentIsRejectedThenCorrectedWithBoundParameters()
    {
        var invalid = new WorkflowIntentPlan { Operations = [new CalculateIntentOperation { Id = "subtotal", Value = new() { Kind = "compute", Text = "label = units * price",
            Members = [new("units", new() { Kind = "number", Number = 3 }), new("price", new() { Kind = "number", Number = 7 })] } }],
            Outputs = [new("result", new() { Kind = "result", Source = "subtotal" })] };
        var corrected = new WorkflowIntentPlan { Operations = [new CalculateIntentOperation { Id = "subtotal", Value = new() { Kind = "compute", Text = "units * price",
            Members = [new("units", new() { Kind = "number", Number = 3 }), new("price", new() { Kind = "number", Number = 7 })] } }], Outputs = invalid.Outputs };
        var runtime = new TestRuntime(invalid); runtime.Plans.Enqueue(corrected);
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts);
        Assert.Contains("COMPUTATION_BINDING_INVALID", runtime.Calls[1].Prompt); Assert.Contains("label", runtime.Calls[1].Prompt);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), Ct);
        Assert.True(result.Success); Assert.Equal("21", result.Outputs!["result"]!.ToString());
    }

    [Theory]
    [InlineData("release", "DEPENDENCY_SCOPE")]
    [InlineData("later", "DEPENDENCY_CYCLE")]
    public async Task IssuedOperationReplacementStillRejectsInvalidDependencies(string dependency, string expected)
    {
        var state = await State(new() { Operations = [Number("result", "release_all"), Number("later", "result"),
            new CleanupIntentOperation { Id = "release_all", Operations = [Number("release")] }] });
        state.ModelCalls = 1; state.Request.MaxRepairAttempts = 1;
        var runtime = new TestRuntime { Respond = request =>
        {
            var context = JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
            var target = Assert.Single(context["targets"]!.AsArray())!; var replacement = target["fragment"]!.DeepClone();
            replacement["after"] = new JsonArray(dependency);
            return new() { Json = new JsonObject { ["changes"] = new JsonArray(new JsonObject { ["target"] = target["id"]!.DeepClone(), ["replacement"] = replacement }) } };
        } };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(1, state.RepairAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == expected); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("parallel")]
    [InlineData("each")]
    [InlineData("choose")]
    public async Task CapturedResultsInsideControlFlowCannotCreateDependencyCycles(string kind)
    {
        var body = new IntentBlock([], new() { Kind = "result", Source = "consumer" });
        IntentOperation container = kind switch
        {
            "parallel" => new ParallelIntentOperation { Id = "container", Branches = [new("branch", body)] },
            "each" => new EachIntentOperation { Id = "container", Items = new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }] }, Body = body },
            _ => new ChooseIntentOperation { Id = "container", Condition = new() { Kind = "boolean", Boolean = true }, Then = body, Otherwise = new([], new() { Kind = "number", Number = 1 }) }
        };
        var state = await State(new() { Operations = [Number("consumer", "missing"), container] });
        var target = Assert.Single(Context(state)["dependencyTargets"]!.AsArray());
        Assert.Empty(target!["eligibleAfter"]!.AsArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedDependencyCorrectionRetainsIndependentCleanupAndPermissionChecks(bool workInCleanup)
    {
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig(); var effects = new List<string>();
        var variant = "nominal"; CancellationTokenSource? cancellation = null;
        foreach (var method in new[] { "perform", "release" })
        {
            server.Tools.Add(new() { Name = method, EffectKind = method == "perform" ? "write" : "lifecycle",
                InputSchema = JsonNode.Parse("""{"type":"object","properties":{}}"""), OutputSchema = JsonNode.Parse("""{"type":"object","properties":{}}"""), ExampleResponse = new JsonObject() });
            server.ToolHandlers[method] = _ =>
            {
                effects.Add(method);
                if (method == "perform" && variant == "failure") throw new InvalidOperationException("Injected failure");
                if (method == "perform" && variant == "cancelled") cancellation!.Cancel();
                return new() { Content = new JsonObject() };
            };
        }
        factory.RegisterServer("fixture", server);
        var runtime = new TestRuntime(mcp: factory); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        var perform = new InvokeIntentOperation { Id = "perform", Capability = catalog.Capabilities.Single(c => c.Method == "perform").Id };
        var cleanup = new CleanupIntentOperation { Id = "release_all", Operations = [new InvokeIntentOperation { Id = "release", Capability = catalog.Capabilities.Single(c => c.Method == "release").Id }] };
        var original = new WorkflowIntentPlan { Operations = workInCleanup ? [cleanup, Number("result", "release_all")] : [perform, Number("result", "release_all"), cleanup],
            Outputs = [new("result", new() { Kind = "result", Source = "result" })] };
        if (workInCleanup) { cleanup.Operations[0].After = ["perform"]; cleanup.Operations.Insert(0, perform); }
        var calls = 0;
        runtime.Respond = request =>
        {
            if (++calls == 1) return new() { Json = PlanningJsonTransport.Intent(original) };
            var context = JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
            var target = Assert.Single(context["targets"]!.AsArray())!;
            Assert.Equal("/operations/1", target["path"]!.ToString());
            Assert.Equal(workInCleanup ? [] : new[] { "perform" }, context["dependencyTargets"]![0]!["eligibleAfter"]!.AsArray().Select(v => v!.ToString()));
            var replacement = target["fragment"]!.DeepClone(); replacement["after"] = new JsonArray();
            return new() { Json = new JsonObject { ["changes"] = new JsonArray(new JsonObject { ["target"] = target["id"]!.DeepClone(), ["replacement"] = replacement }) } };
        };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts); Assert.Equal(["release_all"], original.Operations[1].After);
        Assert.Empty(effects);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        foreach (var scenario in new[] { "nominal", "failure", "cancelled", "denied", "unavailable" })
        {
            variant = scenario; effects.Clear(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct); cancellation = stop;
            var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = scenario == "unavailable" ? null : new PlanningCorpus.Human(scenario != "denied") };
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), stop.Token);
            var meetsBusinessRequest = result.Success == (scenario == "nominal") &&
                effects.SequenceEqual(scenario is "denied" or "unavailable" ? [] : new[] { "perform", "release" });
            // Structural validity cannot fix primary work placed in finalization: independent
            // failure and cancellation checks must still reject that executable proposal.
            Assert.Equal(!workInCleanup || scenario is not ("failure" or "cancelled"), meetsBusinessRequest);
            if (scenario == "nominal") Assert.Equal("7", result.Outputs!["result"]!.ToString());
        }
    }
}
