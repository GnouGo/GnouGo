using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class InputDefaultTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("boolean")]
    [InlineData("string")]
    public async Task MissingRuntimeInputDefaultHasNoRuntimeBindingChoicesAndReceivesDeclarationRepair(string type)
    {
        var plan = Plan(new() { Type = type }, new() { Kind = "missing" });
        var runtime = new TestRuntime(plan); var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.IntentPlan = plan; state.Graph = PlanningGraphBuilder.Build(plan, state.Catalog);
        var hole = Assert.Single(PlanningHoleEligibility.Find(state.Graph, state.Catalog));
        Assert.Equal("/workflows/0/inputs/0/default", hole.Path);
        Assert.Empty(PlanningHoleEligibility.Choices(state.Graph, state.Catalog, hole));
        state.IntentPlan = null; state.Graph = null;
        var corrected = Plan(new() { Type = type }, null); runtime.Plans.Enqueue(corrected);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts); Assert.Null(state.IntentPlan!.Inputs[0].Default);
        var request = runtime.Calls[1]; var context = JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
        Assert.Contains(context["diagnostics"]!.AsArray(), d => d!["location"]!.ToString() == "/inputs/0/default");
        Assert.Equal("/inputs/0", context["targets"]![0]!["path"]!.ToString());
        Assert.Equal("input", context["targets"]![0]!["shape"]!.ToString());
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "PLANNING_INVALID");
    }

    [Theory]
    [InlineData(0, 1, PlanningStatus.Stopped)]
    [InlineData(1, 1, PlanningStatus.FinalReview)]
    [InlineData(2, 2, PlanningStatus.FinalReview)]
    public async Task LiteralDomainsKeepZeroOneMultipleRule(int count, int calls, string status)
    {
        var plan = Plan(new() { Type = "string", Enum = Enumerable.Range(0, count).Select(i => "mode" + i).ToList() }, new() { Kind = "missing" });
        var runtime = new TestRuntime(plan); var state = PlannerFixture.Session(); state.Request.MaxRepairAttempts = 0;
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        var graph = PlanningGraphBuilder.Build(plan, state.Catalog); var hole = Assert.Single(PlanningHoleEligibility.Find(graph, state.Catalog));
        var choices = PlanningHoleEligibility.Choices(graph, state.Catalog, hole);
        Assert.Equal(count, choices.Count); Assert.All(choices, c => Assert.Equal("string", c.Value!["kind"]!.ToString()));
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == status, string.Join(";", state.Diagnostics.Select(d => d.Message)));
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(0, state.RepairAttempts);
        if (count > 0) Assert.Equal("mode0", state.IntentPlan!.Inputs[0].Default!.Text);
        else Assert.Contains(state.Diagnostics, d => d.Code == "HOLE_UNRESOLVED" && d.Location == "/workflows/0/inputs/0/default");
    }

    [Fact]
    public async Task NestedDefaultCandidatesAreLiteralAndReflectByMemberName()
    {
        var type = new IntentType { Type = "object", Fields = [new("tag", new() { Type = "string" }), new("modes", new() { Type = "array", Items = new() { Type = "string", Enum = ["only"] } }, true)] };
        var value = new IntentValue { Kind = "object", Members = [new("tag", new() { Kind = "string", Text = "kept" }), new("modes", new() { Kind = "array", Items = [new() { Kind = "missing" }] })] };
        var state = PlannerFixture.Session(); var runtime = new TestRuntime(Plan(type, value));
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.IntentPlan = Plan(type, value); state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        state.Graph.Workflows[0].Inputs[0].Default!.Members.Reverse();
        var hole = Assert.Single(PlanningHoleEligibility.Find(state.Graph, state.Catalog));
        Assert.Equal("/workflows/0/inputs/0/default/members/0/value/items/0", hole.Path);
        var choices = PlanningHoleEligibility.Choices(state.Graph, state.Catalog, hole);
        Assert.Equal("only", Assert.Single(choices).Value!["text"]!.ToString());
        state.Diagnostics = [new("HOLE_UNRESOLVED", hole.Path, "Missing mode.")];
        Assert.Equal("/inputs/0/default/members/1/value/items/0", Assert.Single(PlanningDiagnosticLocations.ForIntent(state)).Location);
        PlanningBusinessChoices.Apply(state, [(hole, choices[0].Value)]);
        Assert.Equal("only", state.IntentPlan.Inputs[0].Default!.Members[1].Value.Items[0].Text);
        state.Diagnostics.Clear(); state.ModelCalls = 1;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Empty(runtime.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortLocationsUseNamesAfterReorderingAndConfirmation(bool subflow)
    {
        var plan = Plan(new() { Type = "string" }, new() { Kind = "missing" });
        plan.Inputs.Add(new("second", new() { Type = "string" }, Default: new() { Kind = "missing" }));
        plan.Outputs.Add(new("second", new() { Kind = "object", Members = [new("a", new() { Kind = "number", Number = 1 }), new("b", new() { Kind = "missing" })] }));
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(1)); var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.Catalog.Capabilities[0].EffectKind = "write";
        if (subflow) plan = new() { Subflows = [new("child", plan.Inputs, plan.Operations, plan.Outputs)] };
        else plan.Operations.Add(new InvokeIntentOperation { Id = "write", Capability = state.Catalog.Capabilities[0].Id });
        state.IntentPlan = plan; state.Graph = PlanningGraphBuilder.Build(plan, state.Catalog);
        if (!subflow) PlanningConfirmationGuards.Apply(state.Graph, state.Catalog);
        var wi = state.Graph.Workflows.FindIndex(w => w.Key == (subflow ? "child" : PlanningConfirmationGuards.Body));
        var workflow = state.Graph.Workflows[wi]; workflow.Inputs.Reverse(); workflow.Outputs.Reverse(); workflow.Outputs[0].Value.Members.Reverse();
        state.Diagnostics = [new("INPUT_DEFAULT_INVALID", $"/workflows/{wi}/inputs/0/default", "Invalid default."), new("HOLE_UNRESOLVED", $"/workflows/{wi}/outputs/0/value/members/0/value", "Missing output.")];
        var mapped = PlanningDiagnosticLocations.ForIntent(state); var root = subflow ? "/subflows/0" : "";
        Assert.Equal(root + "/inputs/1/default", mapped[0].Location);
        Assert.Equal(root + "/outputs/1/value/members/1/value", mapped[1].Location);
        Assert.Contains("Input 'second'", mapped[0].Message);
        Assert.StartsWith("/workflows/", state.Diagnostics[0].Location);
    }

    [Fact]
    public async Task FailedReflectionRollsBackEntireBatchAndStopsWithoutRepair()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime(); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.IntentPlan = Plan(new() { Type = "string", Enum = ["only"] }, new() { Kind = "missing" });
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        state.Graph.Workflows[0].Inputs.Add(new() { Name = "generated", Schema = new() { Type = "string", Enum = ["only"] }, Default = new() { Kind = PlanningValues.Hole } });
        var beforeGraph = PlanningGraphCompiler.Fingerprint(state.Graph); var beforeIntent = PlanningJsonTransport.Intent(state.IntentPlan).ToJsonString();
        var assignments = PlanningHoleEligibility.Find(state.Graph, state.Catalog).Select(h => (h, Assert.Single(PlanningHoleEligibility.Choices(state.Graph, state.Catalog, h)).Value)).ToArray();
        var error = Assert.Throws<WorkflowRuntimeException>(() => PlanningBusinessChoices.Apply(state, assignments));
        Assert.Equal("PLANNING_HOST_CONTRACT", error.Code);
        Assert.Equal(beforeGraph, PlanningGraphCompiler.Fingerprint(state.Graph)); Assert.Equal(beforeIntent, PlanningJsonTransport.Intent(state.IntentPlan).ToJsonString());
        state.Graph.Workflows[0].Inputs.Reverse(); // Make the unreflectable host field the next singleton.
        state.ModelCalls = 1;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(0, state.RepairAttempts); Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task RestartPreservesMissingDefaultDiagnosticsAndCumulativeCounters()
    {
        var plan = Plan(new() { Type = "string" }, new() { Kind = "missing" }); var runtime = new TestRuntime(plan);
        var state = PlannerFixture.Session(); state.ModelCalls = 3; state.RepairAttempts = 1;
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.IntentPlan = plan; state.Graph = PlanningGraphBuilder.Build(plan, state.Catalog);
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "HOLE_UNRESOLVED"); Assert.Empty(runtime.Calls);
        var elapsed = state.ActiveMilliseconds;
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        runtime.Plans.Clear(); runtime.Plans.Enqueue(Plan(new() { Type = "string" }, null));
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(4, state.ModelCalls); Assert.Equal(2, state.RepairAttempts);
        Assert.True(state.ActiveMilliseconds >= elapsed); Assert.Single(runtime.Calls); Assert.Null(state.IntentPlan!.Inputs[0].Default);
    }

    [Fact]
    public async Task AbsentNullAndLiteralDefaultsRemainDistinct()
    {
        foreach (var value in new IntentValue?[] { null, new() { Kind = "null" }, new() { Kind = "string", Text = "fallback" } })
        {
            var runtime = new TestRuntime(Plan(new() { Type = "string", Nullable = true }, value));
            var state = await PlannerFixture.RunAsync(runtime);
            Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.ModelCalls);
            Assert.Equal(value?.Kind, state.Graph!.Workflows[0].Inputs[0].Default?.Kind);
            Assert.Equal(value?.Text, state.IntentPlan!.Inputs[0].Default?.Text);
        }
    }

    [Fact]
    public async Task RuntimeReferenceDefaultIsDiagnosedBeforeFixtureSampling()
    {
        var runtime = new TestRuntime(Plan(new() { Type = "string" }, new() { Kind = "input", Source = "value" }));
        var state = PlannerFixture.Session(); state.Request.MaxRepairAttempts = 0;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "INPUT_DEFAULT_INVALID" && d.Location == "/workflows/0/inputs/0/default");
        Assert.DoesNotContain(state.Diagnostics, d => d.Code is "PLANNING_INVALID" or "SCENARIO_FIXTURE_REQUIRED");
    }

    [Theory]
    [InlineData("{\"type\":\"boolean\",\"const\":false}", "boolean")]
    [InlineData("{\"type\":\"number\",\"default\":7}", "number")]
    [InlineData("{\"type\":\"null\",\"const\":null}", "null")]
    public async Task CatalogConstantAndDefaultCandidatesRemainLiteral(string schema, string kind)
    {
        var runtime = new TestRuntime(); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        var graph = PlanningGraphBuilder.Build(Plan(new() { Type = "string" }, new() { Kind = "missing" }), catalog);
        var hole = Assert.Single(PlanningHoleEligibility.Find(graph, catalog)) with { ExpectedSchema = JsonNode.Parse(schema)!.AsObject() };
        Assert.Equal(kind, Assert.Single(PlanningHoleEligibility.Choices(graph, catalog, hole)).Value!["kind"]!.ToString());
    }

    [Fact]
    public async Task InvalidDefaultChoiceIdLeavesGraphAndIntentUnchanged()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.IntentPlan = Plan(new() { Type = "string", Enum = ["first", "second"] }, new() { Kind = "missing" });
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog); state.ModelCalls = 1;
        var graph = PlanningGraphCompiler.Fingerprint(state.Graph); var intent = PlanningJsonTransport.Intent(state.IntentPlan).ToJsonString();
        runtime.Respond = request => new() { Json = new JsonObject { [request.StructuredOutputSchema!["properties"]!.AsObject().First().Key] = "unissued" } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "INTENT_SCHEMA_INVALID");
        Assert.Equal(graph, PlanningGraphCompiler.Fingerprint(state.Graph!)); Assert.Equal(intent, PlanningJsonTransport.Intent(state.IntentPlan!).ToJsonString());
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.RepairAttempts); Assert.Null(state.Yaml);
    }

    private static WorkflowIntentPlan Plan(IntentType type, IntentValue? value) => new()
    {
        Inputs = [new("value", type, Default: value)],
        Operations = [new CalculateIntentOperation { Id = "echo", Value = new() { Kind = "input", Source = "value" } }],
        Outputs = [new("result", new() { Kind = "result", Source = "echo" })]
    };
}
