using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Scripting;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LookupCompilationTests
{
    internal static TaskValue Lookup(TaskValue records, TaskValue identities, string field = "key")
        => new() { Kind = "lookup", Port = field, Items = [records, identities] };
    internal static TaskValue Input(string name) => new() { Kind = "input", Source = name };
    internal static TaskType ArrayOf(TaskType item) => new() { Kind = "array", Items = item };
    internal static TaskPlan Plan(string identityType = "string") => new()
    {
        Inputs = [new() { Name = "records", Type = ArrayOf(ProductTransformationPlan.Obj(("key", new() { Kind = identityType }), ("payload", new() { Nullable = true }))) },
            new() { Name = "selected", Type = ArrayOf(new() { Kind = identityType }) }],
        Root = new() { Tasks = [new() { Id = "resolve", Kind = "value", Objective = "Reconnect selected identities to their original records",
            Outputs = [new("rows", Lookup(Input("records"), Input("selected")))] }], Outputs = [new("rows", ProductTransformationPlan.Ref("resolve", "rows"))] }
    };

    internal static async Task<string> Compile(TaskPlan plan, WorkflowEngine engine)
    {
        var catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).DiscoverAsync(
            new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        var result = new TaskPlanCompiler().Compile(plan, catalog, true, true);
        Assert.Empty(result.Diagnostics); Assert.Empty(PlanningExecutableValidation.Validate(result.Graph!, catalog));
        var before = JsonSerializer.Serialize(result.Graph, PlanningJsonContext.Default.PlanningGraph);
        Assert.DoesNotContain("m.lookup", before);
        var yaml = new PlanningGraphCompiler().Compile(result.Graph!, catalog);
        Assert.Equal(before, JsonSerializer.Serialize(result.Graph, PlanningJsonContext.Default.PlanningGraph));
        return yaml;
    }

    internal static async Task<GnOuGo.Flow.Core.Models.RunResult> Execute(string yaml, JsonObject values, WorkflowEngine engine)
    {
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        return await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], values, PlannerFixture.Ct);
    }

    [Theory]
    [InlineData("[\"b\",\"a\",\"b\"]", "[{\"key\":\"b\",\"payload\":null},{\"key\":\"a\",\"payload\":\"exact\"},{\"key\":\"b\",\"payload\":null}]")]
    [InlineData("[]", "[]")]
    public async Task RepeatedSelectionsPreserveOrderAndOriginalValues(string selected, string expected)
    {
        var engine = new WorkflowEngine(); var yaml = await Compile(Plan(), engine);
        Assert.DoesNotContain("llm.call", yaml); Assert.DoesNotContain("mapping.dynamic", yaml);
        var values = JsonNode.Parse("{\"records\":[{\"key\":\"a\",\"payload\":\"exact\"},{\"key\":\"b\",\"payload\":null}]}")!.AsObject();
        values["selected"] = JsonNode.Parse(selected); var original = values.ToJsonString();
        var result = await Execute(yaml, values, engine);
        Assert.True(result.Success, result.Error?.Message); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), result.Outputs!["rows"]));
        Assert.Equal(original, values.ToJsonString());
    }

    [Theory]
    [InlineData("[{\"key\":\"a\",\"payload\":\"x\"},{\"key\":\"a\",\"payload\":\"y\"}]", "[\"a\"]")]
    [InlineData("[{\"key\":\"a\",\"payload\":\"x\"}]", "[\"a\",\"unknown\"]")]
    [InlineData("[{\"key\":\" \",\"payload\":\"x\"}]", "[]")]
    public async Task InvalidIdentityBatchFailsAtomicallyBeforeAction(string records, string selected)
    {
        var calls = 0; var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("independent", new() { Tools = [new() { Name = "consume", EffectKind = "write",
            InputSchema = JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"rows\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"key\":{\"type\":\"string\"},\"payload\":{\"type\":[\"string\",\"null\"]}},\"required\":[\"key\",\"payload\"]}}},\"required\":[\"rows\"]}"),
            OutputSchema = JsonNode.Parse("{\"type\":\"object\",\"properties\":{}}") }], ToolHandlers = new() {
                ["consume"] = _ => { calls++; return new() { Content = new JsonObject() }; } } });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var catalog = await TaskPlanCompilerTests.Catalog(new(engine, (_, _) => Task.CompletedTask)); catalog.Policy.RequireExternalConfirmation = false;
        var plan = Plan(); plan.Root.Tasks.Add(new() { Id = "act", Kind = "operation", Objective = "Consume only a fully resolved selection",
            Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "consume")).Id,
            Inputs = [new("rows", ProductTransformationPlan.Ref("resolve", "rows"))] });
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, true, true); Assert.Empty(compiled.Diagnostics);
        var result = await Execute(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog), new() {
            ["records"] = JsonNode.Parse(records), ["selected"] = JsonNode.Parse(selected) }, engine);
        Assert.False(result.Success); Assert.Equal("CONTRACT_UNSATISFIED", result.Error!.Code); Assert.Null(result.Outputs); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("arity")]
    [InlineData("missing_key")]
    [InlineData("wrong_type")]
    [InlineData("opaque")]
    [InlineData("extra_field")]
    public async Task InvalidBindingsFailBeforeLowering(string variant)
    {
        var plan = Plan(); var value = plan.Root.Tasks[0].Outputs[0].Value;
        switch (variant)
        {
            case "arity": value.Items.RemoveAt(1); break;
            case "missing_key": value.Port = "invented"; break;
            case "wrong_type": plan.Inputs[1].Type.Items!.Kind = "integer"; break;
            case "opaque": plan.Inputs[0].Type.Items = new() { Kind = "any" }; break;
            case "extra_field": value.Text = "fallback"; break;
        }
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_LOOKUP_INVALID");
    }

    [Theory]
    [InlineData("[{\"key\":0,\"payload\":\"a\"},{\"key\":2,\"payload\":\"a\"}]", "[2,0,2]", true)]
    [InlineData("[{\"key\":9007199254740992,\"payload\":null}]", "[9007199254740992]", false)]
    public async Task IntegerIdentitiesRemainExactAndBounded(string records, string selected, bool success)
    {
        var engine = new WorkflowEngine(); var yaml = await Compile(Plan("integer"), engine);
        var result = await Execute(yaml, new() { ["records"] = JsonNode.Parse(records), ["selected"] = JsonNode.Parse(selected) }, engine);
        Assert.Equal(success, result.Success);
        if (success) Assert.Equal(new[] { 2, 0, 2 }, result.Outputs!["rows"]!.AsArray().Select(r => r!["key"]!.GetValue<int>()));
        else Assert.Equal("CONTRACT_UNSATISFIED", result.Error!.Code);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(2.5, false)]
    public async Task NumericLiteralSelectionsAreCheckedAsSafeIntegers(double identity, bool valid)
    {
        var plan = Plan("integer");
        plan.Root.Tasks[0].Outputs[0].Value.Items[1] = new() { Kind = "array", Items = [new() { Kind = "number", Number = identity }] };
        var engine = new WorkflowEngine(); var yaml = await Compile(plan, engine);
        var result = await Execute(yaml, new() { ["records"] = JsonNode.Parse("[{\"key\":2,\"payload\":null}]"), ["selected"] = new JsonArray() }, engine);
        Assert.Equal(valid, result.Success);
        if (!valid) Assert.Equal("CONTRACT_UNSATISFIED", result.Error!.Code);
    }

    [Fact]
    public async Task LiteralIdentityUnionPreservesOrderAndRepetitions()
    {
        var plan = Plan();
        plan.Root.Tasks[0].Outputs[0].Value.Items[1] = new() { Kind = "array", Items = [
            new() { Kind = "string", Text = "b" }, new() { Kind = "string", Text = "a" }, new() { Kind = "string", Text = "b" }] };
        var engine = new WorkflowEngine(); var yaml = await Compile(plan, engine);
        var result = await Execute(yaml, JsonNode.Parse("""{"records":[{"key":"a","payload":"one"},{"key":"b","payload":null}],"selected":[]}""")!.AsObject(), engine);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new[] { "b", "a", "b" }, result.Outputs!["rows"]!.AsArray().Select(r => r!["key"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("9007199254740991.1")]
    [InlineData("1e-100")]
    public void NumericIdentityValidationNeverRoundsFractionsIntoIntegers(string number)
    {
        var source = JsonNode.Parse("{\"rows\":[{\"key\":" + number + "}],\"ids\":[]}");
        Assert.Equal("CONTRACT_UNSATISFIED", Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox()
            .ExecuteMapping("m.lookup(source.rows,source.ids,'key')", source, PlannerFixture.Ct)).Code);
    }

    [Fact]
    public void HelperCannotBeUsedByLearnedMappingsAndNeverCoercesKeys()
    {
        const string script = "m.lookup(source.rows,source.ids,'key')";
        Assert.Throws<WorkflowRuntimeException>(() => JintSandbox.ValidateMapping(script));
        JintSandbox.ValidateMapping(script, learned: false);
        foreach (var source in new[] { "{\"rows\":[{\"key\":1}],\"ids\":[\"1\"]}", "{\"rows\":[{}],\"ids\":[]}",
            "{\"rows\":[{\"key\":null}],\"ids\":[]}", "{\"rows\":[{\"key\":1.5}],\"ids\":[]}" })
            Assert.Equal("CONTRACT_UNSATISFIED", Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping(script, JsonNode.Parse(source), PlannerFixture.Ct)).Code);
    }

    [Fact]
    public async Task ExplicitEmptySelectionNeedsNoInventedElementType()
    {
        var plan = Plan(); plan.Root.Tasks[0].Outputs[0].Value.Items[1] = new() { Kind = "array" };
        var engine = new WorkflowEngine(); var yaml = await Compile(plan, engine);
        var result = await Execute(yaml, new() { ["records"] = new JsonArray(), ["selected"] = new JsonArray() }, engine);
        Assert.True(result.Success, result.Error?.Message); Assert.Empty(result.Outputs!["rows"]!.AsArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothBranchesCaptureTheirOriginalSelection(bool selected)
    {
        var plan = Plan(); var original = plan.Root.Tasks.Single();
        plan.Inputs.Add(new() { Name = "branch", Type = new() { Kind = "boolean" } });
        plan.Root.Tasks = [new() { Id = "resolve", Kind = "conditional", Objective = "Preserve the selected branch interface",
            Condition = Input("branch"), Body = new() { Outputs = original.Outputs }, Otherwise = new() { Outputs = original.Outputs } }];
        var engine = new WorkflowEngine(); var yaml = await Compile(plan, engine);
        var values = JsonNode.Parse("{\"records\":[{\"key\":\"é\",\"payload\":null}],\"selected\":[\"é\",\"é\"]}")!.AsObject(); values["branch"] = selected;
        var result = await Execute(yaml, values, engine); Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.Outputs!["rows"]!.AsArray().Count);
    }

    [Fact]
    public void LookupObservesCancellationAndSharesItsHelperAllowance()
    {
        var source = JsonNode.Parse("{\"rows\":[{\"key\":\"a\"}],\"ids\":[\"a\",\"a\",\"a\"]}");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new JintSandbox().ExecuteMapping("m.lookup(source.rows,source.ids,'key')", source, cancelled.Token));
        var bounded = new JintSandbox(maxStatements: 3);
        Assert.Throws<WorkflowRuntimeException>(() => bounded.ExecuteMapping("m.lookup(source.rows,source.ids,'key')", source, PlannerFixture.Ct));
    }

    [Fact]
    public async Task FreshProposalSupportsLookupWithoutChangingIssuedRepairSchemas()
    {
        var plan = Plan(); var runtime = new TestRuntime { Proposal = new() { Plan = plan, Requirements = new() {
            Summary = "Reconnect selected identities", Inputs = plan.Inputs, Outputs = [new() { Name = "rows", Type = plan.Inputs[0].Type }], Outcomes = [new("resolve", "Return the selected original records")] } } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains("lookup", runtime.Calls[0].StructuredOutputSchema!.ToJsonString());
        var legacy = PlanningSchemas.FullProposal(state, compact: false, flatten: false, lookup: false);
        Assert.DoesNotContain("\"lookup\"", legacy.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionPreservesProducerOwnershipButCannotCreateIt(bool forged)
    {
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        var use = plan.Root.Tasks[1]; plan.Root.Tasks.RemoveAt(1); plan.Root.Outputs.Clear();
        var origin = forged ? new TaskValue { Kind = "string", Text = "plausible" } : TaskArtifactBindingTests.Reference("make", "handle");
        var rows = new TaskValue { Kind = "array", Items = [new() { Kind = "object", Members = [
            new("key", new() { Kind = "string", Text = "entry" }), new("resource", origin)] }] };
        plan.Root.Tasks.Add(new() { Id = "selected", Kind = "value", Objective = "Reconnect observed resource",
            Outputs = [new("rows", Lookup(rows, new() { Kind = "array", Items = [new() { Kind = "string", Text = "entry" }] }))] });
        use.Inputs = [new("location", TaskArtifactBindingTests.Field(new() { Kind = "item" }, "resource"))];
        plan.Root.Tasks.Add(new() { Id = "consume", Kind = "foreach", Objective = "Use original resources", Items = ProductTransformationPlan.Ref("selected", "rows"), Body = new() { Tasks = [use] } });
        var result = new TaskPlanCompiler().Compile(plan, catalog, true, true);
        if (forged) Assert.Contains(result.Diagnostics, d => d.Code == "TASK_ARTIFACT_BINDING");
        else { Assert.Empty(result.Diagnostics); Assert.Empty(PlanningExecutableValidation.Validate(result.Graph!, catalog)); }
    }
}
