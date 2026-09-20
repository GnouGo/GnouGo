using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DiagnosticRepairBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("input", "/inputs/0")]
    [InlineData("result", "/operations/0/value")]
    [InlineData("item", "/operations/1/items")]
    public async Task GeneratedCaptureSchemaErrorsPointToBusinessSources(string kind, string expected)
    {
        var state = await CaptureSession(kind);
        var body = state.Graph!.Workflows[1];
        Assert.Equal(PlanningValues.Hole, body.Inputs[0].Schema.Type);
        state.Diagnostics = PlanningExecutableValidation.Validate(state.Graph, state.Catalog!).Where(d => d.Location == "/workflows/1/inputs/0/schema").ToList();
        Assert.NotEmpty(state.Diagnostics);
        var original = JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic);
        foreach (var finding in PlanningDiagnosticLocations.ForIntent(state))
        {
            Assert.NotEqual("PLANNING_HOST_CONTRACT", finding.Code);
            Assert.Equal(expected, finding.Location);
        }
        Assert.Equal(original, JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        Assert.Contains(PlanningCorrections.Targets(state), t => t.Path == expected);
    }

    [Theory]
    [InlineData(false, "/workflows/0/finally/0/input/members/0/value/members/0/value/items/0/members/0/value")]
    [InlineData(true, "/workflows/1/finally/0/input/members/0/value/members/0/value/items/0/members/0/value")]
    public async Task NestedCleanupTemplateRetainsItsLocationThroughMcpAndConfirmation(bool confirmation, string graphPath)
    {
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(1, """{"entries":{"type":"array","items":{"type":"object","properties":{"label":{"type":"string"}},"required":["label"]}}}""", ["entries"]));
        var state = PlannerFixture.Session(); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        if (confirmation) state.Catalog.Capabilities[0].EffectKind = "write";
        state.IntentPlan = new() { Operations = [new CleanupIntentOperation { Id = "cleanup", Operations = [new InvokeIntentOperation
        {
            Id = "release", Capability = state.Catalog.Capabilities[0].Id,
            Arguments = [new("entries", new() { Kind = "array", Items = [new() { Kind = "object", Members = [new("label", BadTemplate())] }] })]
        }] }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        if (confirmation) PlanningConfirmationGuards.Apply(state.Graph, state.Catalog);
        var failure = Assert.Single(PlanningGraphCompiler.ValidateValues(state.Graph, state.Catalog));
        Assert.Equal(graphPath, failure.Location);
        state.Diagnostics = [failure];
        var mapped = Assert.Single(PlanningDiagnosticLocations.ForIntent(state));
        Assert.Equal("VALUE_LOWERING_INVALID", mapped.Code);
        Assert.Equal("/operations/0/operations/0/arguments/0/value/items/0/members/0/value", mapped.Location);
        Assert.Equal("value", Assert.Single(PlanningCorrections.Targets(state)).Shape);
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(state.Graph, state.Catalog));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CapturesAcrossNestedBranchesAndSubflowsKeepBusinessInputOwnership(bool subflow, bool confirmation)
    {
        var state = await CaptureSession("input");
        var each = (EachIntentOperation)state.IntentPlan!.Operations[1];
        var choose = new ChooseIntentOperation { Id = "route", Condition = new() { Kind = "boolean", Boolean = true },
            Then = new([each], new() { Kind = "result", Source = "iterate" }), Otherwise = new([], new() { Kind = "number", Number = 0 }) };
        var parallel = new ParallelIntentOperation { Id = "branches", Branches = [new("first", new([choose], new() { Kind = "result", Source = "route" }))] };
        state.IntentPlan.Operations = [parallel];
        if (subflow)
        {
            state.IntentPlan.Subflows = [new("job", state.IntentPlan.Inputs, state.IntentPlan.Operations, [])];
            state.IntentPlan.Operations = [new CallIntentOperation { Id = "call", Flow = "job", Arguments = [new("value", new() { Kind = "input", Source = "value" })] }];
        }
        if (confirmation)
        {
            state.Catalog = await new TestRuntime(mcp: BusinessCorrectionTests.Factory(1)).DiscoverAsync(state.Request, Ct);
            state.Catalog.Capabilities[0].EffectKind = "write";
            state.IntentPlan.Operations.Add(new InvokeIntentOperation { Id = "persist", Capability = state.Catalog.Capabilities[0].Id });
        }
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog!);
        PlanningConfirmationGuards.Apply(state.Graph, state.Catalog!);
        var inputs = state.Graph.Workflows.SelectMany((w, wi) => w.Inputs.Select((p, pi) => (Workflow: w, Port: p, Path: $"/workflows/{wi}/inputs/{pi}/schema")))
            .Where(p => p.Port.Name.StartsWith("capture_", StringComparison.Ordinal) && p.Port.Schema.Type == PlanningValues.Hole).ToArray();
        Assert.True(inputs.Length >= 3);
        state.Diagnostics = inputs.Select(p => new PlanningDiagnostic("SCHEMA_INVALID", p.Path, "Unresolved captured contract.")).ToList();
        var mapped = PlanningDiagnosticLocations.ForIntent(state);
        Assert.NotEmpty(mapped);
        Assert.All(mapped, d => { Assert.Equal("SCHEMA_INVALID", d.Code); Assert.Equal(subflow ? "/subflows/0/inputs/0" : "/inputs/0", d.Location); });
    }

    [Fact]
    public async Task ValidSourceWithBrokenGeneratedCaptureRemainsAHostFailure()
    {
        var state = await CaptureSession("input");
        state.IntentPlan!.Inputs[0] = new("value", new() { Type = "string" });
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog!);
        state.Graph.Workflows[1].Inputs[0].Schema = new() { Type = PlanningValues.Hole };
        state.ModelCalls = 1;
        state.Diagnostics = [new("SCHEMA_INVALID", "/workflows/1/inputs/0/schema", "Broken generated port.")];
        var runtime = new TestRuntime();
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "PLANNING_HOST_CONTRACT");
        Assert.Empty(runtime.Calls); Assert.Equal(0, state.RepairAttempts);
    }

    [Fact]
    public async Task NestedCatalogBindingCannotBecomeAModelRepairTarget()
    {
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(1, """{"settings":{"type":"object","properties":{"label":{"type":"string"}},"required":["label"]}}""", ["settings"]));
        var state = PlannerFixture.Session(); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.Catalog.Capabilities[0].RequestBindings.Add(new("/settings/label", JsonValue.Create("${hostInvalid}")));
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation { Id = "operation", Capability = state.Catalog.Capabilities[0].Id,
            Arguments = [new("settings", new() { Kind = "object", Members = [new("label", new() { Kind = "string", Text = "business value" })] })] }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog); state.ModelCalls = 1;
        state.Diagnostics = PlanningGraphCompiler.ValidateValues(state.Graph, state.Catalog).ToList();
        Assert.Single(state.Diagnostics);
        Assert.Equal("PLANNING_HOST_CONTRACT", Assert.Single(PlanningDiagnosticLocations.ForIntent(state)).Code);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Empty(runtime.Calls); Assert.Equal(0, state.RepairAttempts);
    }

    [Theory]
    [InlineData("correct", PlanningStatus.FinalReview, 1)]
    [InlineData("unchanged", PlanningStatus.Stopped, 1)]
    [InlineData("invalid", PlanningStatus.Stopped, 2)]
    public async Task CombinedFailureUsesBoundedTypedRepairAfterRestart(string response, string expectedStatus, int repairs)
    {
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(1, """{"text":{"type":"string"}}""", ["text"]));
        var state = await CaptureSession("item"); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.IntentPlan!.Operations.Add(new CleanupIntentOperation { Id = "cleanup", Operations = [new InvokeIntentOperation { Id = "release",
            Capability = state.Catalog.Capabilities[0].Id, Arguments = [new("text", BadTemplate())] }] });
        var invalid = state.IntentPlan;
        var valid = JsonSerializer.Deserialize(PlanningJsonTransport.Intent(invalid), PlanningJsonContext.Default.WorkflowIntentPlan)!;
        ((CalculateIntentOperation)valid.Operations[0]).Value = new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }, new() { Kind = "number", Number = 2 }] };
        ((InvokeIntentOperation)((CleanupIntentOperation)valid.Operations[2]).Operations[0]).Arguments = [new("text", new() { Kind = "string", Text = "released" })];
        state.IntentPlan = null; state.Graph = null; state.ModelCalls = 3;
        runtime.Plans.Clear(); runtime.Plans.Enqueue(invalid); runtime.Plans.Enqueue(response == "correct" ? valid : invalid);
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(4, state.ModelCalls); Assert.Equal(0, state.RepairAttempts); Assert.NotEmpty(state.Diagnostics);
        Assert.DoesNotContain(PlanningDiagnosticLocations.ForIntent(state), d => d.Code == "PLANNING_HOST_CONTRACT");
        var targets = PlanningCorrections.Targets(state);
        Assert.Contains(targets, t => t.Path == "/operations/0" || t.Path == "/operations/0/value");
        Assert.Contains(targets, t => t.Path.StartsWith("/operations/2/operations/0", StringComparison.Ordinal));
        var originalRequestSchema = runtime.Calls[0].StructuredOutputSchema!.DeepClone();
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        if (response == "invalid") runtime.Respond = _ => new() { Json = JsonNode.Parse("""{"changes":[{"target":"unissued","replacement":{"kind":"string","text":"wrong"}}]}""") };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == expectedStatus, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(repairs, state.RepairAttempts); Assert.Equal(4 + repairs, state.ModelCalls);
        Assert.True(JsonNode.DeepEquals(originalRequestSchema, runtime.Calls[0].StructuredOutputSchema));
        Assert.Null(state.ApprovedHash);
        if (response == "correct") { Assert.NotNull(state.Yaml); Assert.NotEmpty(state.Scenarios); }
        else Assert.Null(state.Yaml);
    }

    private static IntentValue BadTemplate() => new() { Kind = "template", Text = "release {label}", Members = [new("label", new() { Kind = "string", Text = "sample" })] };

    private static async Task<PlanningSession> CaptureSession(string kind)
    {
        var state = PlannerFixture.Session(); state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, Ct);
        var value = new IntentValue { Kind = kind, Source = kind == "input" ? "value" : kind == "result" ? "source" : "iterate" };
        state.IntentPlan = new()
        {
            Inputs = kind == "input" ? [new("value")] : [],
            Operations = [new CalculateIntentOperation { Id = "source", Value = new() { Kind = "compute", Text = "undefinedValue" } },
                new EachIntentOperation { Id = "iterate", Items = kind == "item" ? new() { Kind = "result", Source = "source" } : new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }] },
                    Body = new([new CalculateIntentOperation { Id = "use", Value = value }], new() { Kind = "result", Source = "use" }) }]
        };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        return state;
    }
}
