using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class InputConstraintRepairTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NullableIterationDiagnosesOnlyItsInputConstraint(bool grouped, bool nested)
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        var input = new TaskInput { Name = "entries", Type = new() { Kind = "array", Nullable = true, Items = new() { Kind = "string" } } };
        var body = new TaskScope { Tasks = [new() { Id = "iterate", Kind = "foreach", Objective = "Preserve every supplied entry", MaxItems = 4,
            Items = new() { Kind = "input", Source = "entries" }, Body = new() { Outputs = [new("entry", new() { Kind = "item" })] } }],
            Outputs = [new("result", new() { Kind = "output", Source = "iterate", Port = "entry" })] };
        if (nested)
        {
            input.Type = new() { Kind = "object", Fields = [new() { Name = "values", Type = input.Type }, new() { Name = "untouched", Type = new() { Kind = "boolean" } }] };
            body.Tasks[0].Items = new() { Kind = "field", Port = "values", Items = [body.Tasks[0].Items!] };
        }
        state.Plan = grouped ? new() { Groups = [new() { Id = "reuse", Inputs = [input], Body = body }] } : new() { Inputs = [input], Root = body };
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        var location = (grouped ? "/groups/reuse" : "") + "/inputs/entries/type" + (nested ? "/fields/values/type" : "") + "/nullable";
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_INPUT_CONSTRAINT" && d.Location == location);
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        Assert.Equal(new[] { location, "/tasks/iterate/items" }, state.RevisionScope);
        var slots = RepairPatchTests.Slots(state);
        var slot = Assert.Single(slots, s => s.Location == location);
        Assert.DoesNotContain(slots, s => s.Location.EndsWith("/required", StringComparison.Ordinal) || s.Location.EndsWith("/default", StringComparison.Ordinal));
        var request = new PlanningPrompt(state).Request();
        var repaired = PlanningRepairPatch.Apply(state, new() { Edits = [new() { Slot = slot.Id, Action = "replace", Value = JsonValue.Create(false) }] }, request);
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog).Diagnostics);
        Assert.True((nested ? input.Type.Fields[0].Type : input.Type).Nullable); Assert.True(input.Required); Assert.Null(input.Default);
        var before = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)!;
        var invalid = new RepairPatch { Edits = [new() { Slot = slot.Id, Action = "replace", Value = new JsonObject { ["required"] = false } }] };
        Assert.Throws<PlanningResponseException>(() => PlanningRepairPatch.Apply(state, invalid, request));
        Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)));
    }
}
