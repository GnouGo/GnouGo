using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConditionalRepairDiagnosticsTests
{
    private static async Task<PlanningSession> Fixture()
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        state.Catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Catalog.Capabilities.Add(new() { Id = "dispatch", Kind = "tool", StepType = "mcp.call", Server = "renamed", Method = "invoke", EffectKind = "read",
            InputSchema = JsonNode.Parse("""
            {"type":"object","properties":{"mode":{"type":"string","enum":["read","write"]},"parameters":{"type":"object","additionalProperties":{"type":"string"}}},"required":["mode","parameters"],"additionalProperties":false,
             "oneOf":[{"properties":{"mode":{"const":"read"},"parameters":{"type":"object","properties":{"path":{"type":"string","pattern":"^item/"}},"required":["path"],"additionalProperties":false}},"required":["mode","parameters"]},
                      {"properties":{"mode":{"const":"write"},"parameters":{"type":"object","properties":{"destination":{"type":"string"}},"required":["destination"],"additionalProperties":false}},"required":["mode","parameters"]}]}
            """)!.AsObject(), OutputSchema = new() { ["type"] = "object" } });
        state.Plan = new() { Inputs = [new() { Name = "values", Type = new() { Kind = "array", Items = new() { Kind = "string", Enum = ["item/a", "item/b"] } } }], Root = new() { Tasks = [
            new() { Id = "check", Kind = "transform", Objective = "Retain every input value unchanged", Inputs = [new("values", new() { Kind = "input", Source = "values" })],
                ResultType = new() { Kind = "object", Fields = [new() { Name = "values", Type = new() { Kind = "array", Items = new() { Kind = "string" } } }] } },
            new() { Id = "loop", Kind = "foreach", Objective = "Consume each value", MaxItems = 3, Items = new() { Kind = "output", Source = "check", Port = "values" }, Body = new() { Tasks = [
                new() { Id = "use", Objective = "Read the selected item", Operation = "dispatch", Inputs = [new("mode", new() { Kind = "string", Text = "read" }),
                    new("parameters", new() { Kind = "object", Members = [new("path", new() { Kind = "item" })] })] } ] } }
        ] } };
        return state;
    }
    private static void Diagnose(PlanningSession state)
    {
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan!, state.Catalog!).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan!, state.Diagnostics).ToList();
    }
    [Fact]
    public async Task ProvenConditionalBranchDiagnosesOnlyBindingAndNestedProducerDomain()
    {
        var state = await Fixture(); Diagnose(state);
        const string domain = "/tasks/check/resultType/fields/values/type/items/enum";
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_INPUT_TYPE" && d.Location == "/tasks/use/inputs/parameters");
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_TRANSFORM_CONSTRAINT" && d.Location == domain);
        Assert.Equal(new[] { domain, "/tasks/use/inputs/parameters" }, state.RevisionScope);
        var request = new PlanningPrompt(state).Request();
        var slot = Assert.Single(RepairPatchTests.Slots(state), s => s.Location == domain);
        var repaired = PlanningRepairPatch.Apply(state, new() { Edits = [new() { Slot = slot.Id, Action = "replace", Value = new JsonArray("item/a", "item/b") }] }, request);
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog!).Diagnostics);
        Assert.Null(state.Plan!.Root.Tasks[0].ResultType!.Fields[0].Type.Items!.Enum);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task AmbiguousOrUnprovenSelectorsNeverGrantBranchSpecificPermissions(bool ambiguous)
    {
        var state = await Fixture();
        if (ambiguous)
        {
            var branches = state.Catalog!.Capabilities.Single(c => c.Id == "dispatch").InputSchema["oneOf"]!.AsArray();
            branches.Add(branches[0]!.DeepClone());
        }
        else
        {
            state.Plan!.Inputs.Add(new() { Name = "mode", Type = new() { Kind = "string", Enum = ["read", "write"] } });
            state.Plan.Root.Tasks[1].Body!.Tasks[0].Inputs[0] = new("mode", new() { Kind = "input", Source = "mode" });
        }
        Diagnose(state);
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_INPUT_TYPE" && d.Location == "/tasks/use/inputs");
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "TASK_TRANSFORM_CONSTRAINT");
        Assert.Empty(state.RevisionScope);
    }
    [Theory]
    [InlineData(1, false, false)][InlineData(3, true, false)]
    [InlineData(1, false, true)][InlineData(3, true, true)]
    public async Task IterationCeilingCountsAllItemsIndependentlyOfConcurrency(int ceiling, bool succeeds, bool parallel)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = new TaskPlan { Inputs = [new() { Name = "values", Type = new() { Kind = "array", Items = new() { Kind = "string" } } }], Root = new() { Tasks = [
            new() { Id = "loop", Kind = "foreach", Objective = "Preserve every item", MaxItems = ceiling, MaxConcurrency = 1, Parallel = parallel,
                Items = new() { Kind = "input", Source = "values" }, Body = new() { Outputs = [new("values", new() { Kind = "item" })] } }
        ], Outputs = [new("result", new() { Kind = "output", Source = "loop", Port = "values" })] } };
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        var values = new JsonArray("b", "a", "b");
        var run = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["values"] = values }, PlannerFixture.Ct);
        Assert.Equal(succeeds, run.Success);
        if (succeeds) Assert.True(JsonNode.DeepEquals(values, run.Outputs!["result"]));
        else Assert.Equal("INPUT_VALIDATION", run.Error!.Code);
    }
}
