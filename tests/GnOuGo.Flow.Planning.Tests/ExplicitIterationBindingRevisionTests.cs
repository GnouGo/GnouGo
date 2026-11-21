using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ExplicitIterationBindingRevisionTests
{
    [Fact]
    public async Task ExplicitItemsPatchPreservesIterationLimitsAndExecutesTheExactReplacement()
    {
        var state = await State();
        var baseline = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        const string path = "/tasks/iterate/items";
        TaskPlanRevisions.ValidateEditablePaths(state, [path]);
        state.EditablePaths = state.RevisionScope = [path];
        var request = RepairPatchTests.Request(state);
        var restored = PlannerFixture.Clone(state);
        Assert.True(PlanningRepairPatch.Verify(restored, request));
        var slot = Assert.Single(RepairPatchTests.Slots(restored));
        Assert.Equal(path, slot.Location);
        var replacement = new JsonObject { ["kind"] = "input", ["source"] = "replacement" };
        var repaired = RepairPatchTests.Apply(restored, RepairPatchTests.Edit(restored, path, "replace", replacement));
        var loop = repaired.Root.Tasks[0];
        Assert.Equal("foreach", loop.Kind);
        Assert.Equal(4, loop.MaxItems);
        Assert.False(loop.Parallel);
        Assert.Equal(1, loop.MaxConcurrency);
        Assert.Equal(JsonSerializer.Serialize(state.Plan!.Root.Tasks[1], PlanningJsonContext.Default.PlanTask),
            JsonSerializer.Serialize(repaired.Root.Tasks[1], PlanningJsonContext.Default.PlanTask));
        Assert.Equal(7, restored.ModelCalls);
        Assert.Equal(2, restored.ReplanAttempts);
        Assert.Null(restored.ApprovedHash);
        var compiled = new TaskPlanCompiler().Compile(repaired, restored.Catalog!);
        Assert.Empty(compiled.Diagnostics);
        PlanningConfirmationGuards.Apply(compiled.Graph!, restored.Catalog!);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, restored.Catalog!);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine { HumanInputProvider = new PlanningCorpus.Human() }.ExecuteAsync(
            document.Workflows[document.Entrypoint!], new JsonObject
            {
                ["source"] = new JsonArray("before"),
                ["replacement"] = new JsonArray("first", "first", null)
            }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        var outputs = Assert.IsType<JsonObject>(result.Outputs);
        Assert.True(JsonNode.DeepEquals(new JsonArray("first", "first", null), outputs["rows"]));
        Assert.Equal(42, outputs["unchanged"]!.GetValue<int>());
        state.EditablePaths = null;
        state.RevisionScope.Clear();
        Assert.Equal(baseline, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var outside = PlanningConstruction.Clone(repaired);
        outside.Root.Tasks[0].MaxItems = 5;
        Assert.Contains(TaskPlanRevisions.Validate(state.Plan, outside, [path], state.Catalog, [path]),
            diagnostic => diagnostic.Code == "REVISION_SCOPE_CHANGED");
    }

    [Theory]
    [InlineData("/tasks/iterate/maxItems")]
    [InlineData("/tasks/iterate/maxConcurrency")]
    [InlineData("/tasks/iterate/parallel")]
    [InlineData("/tasks/iterate/kind")]
    [InlineData("/tasks/iterate/body")]
    [InlineData("/tasks/unrelated/items")]
    [InlineData("/tasks/missing/items")]
    public async Task ItemsAuthorityDoesNotGrantStructuralOrUnrelatedEdits(string path)
    {
        var state = await State();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Throws<ArgumentException>(() => TaskPlanRevisions.ValidateEditablePaths(state, [path]));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    private static async Task<PlanningSession> State()
    {
        var state = PlannerFixture.Session();
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        TaskType Vector() => new() { Kind = "array", Items = new() { Kind = "string", Nullable = true }, MaxItems = 4 };
        state.Plan = new()
        {
            Inputs = [new() { Name = "source", Type = Vector(), Required = true }, new() { Name = "replacement", Type = Vector(), Required = true }],
            Root = new()
            {
                Tasks = [
                    new() { Id = "iterate", Kind = "foreach", Objective = "Retain the supplied vector in order", Items = new() { Kind = "input", Source = "source" },
                        MaxItems = 4, Parallel = false, MaxConcurrency = 1,
                        Body = new() { Tasks = [new() { Id = "retain", Kind = "value", Objective = "Keep the current cell", Outputs = [new("cell", new() { Kind = "item" })] }],
                            Outputs = [new("cell", new() { Kind = "output", Source = "retain", Port = "cell" })] } },
                    new() { Id = "unrelated", Kind = "value", Objective = "Preserve the independent constant", Outputs = [new("number", new() { Kind = "number", Number = 42 })] }
                ],
                Outputs = [new("rows", new() { Kind = "output", Source = "iterate", Port = "cell" }),
                    new("unchanged", new() { Kind = "output", Source = "unrelated", Port = "number" })]
            }
        };
        state.Requirements = new() { Inputs = state.Plan.Inputs, Outputs = [new() { Name = "rows", Type = Vector() }, new() { Name = "unchanged", Type = new() { Kind = "number" } }] };
        state.ModelCalls = 7;
        state.ReplanAttempts = 2;
        Assert.Empty(new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics);
        return state;
    }
}
