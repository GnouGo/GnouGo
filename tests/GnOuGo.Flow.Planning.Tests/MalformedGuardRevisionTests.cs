using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class MalformedGuardRevisionTests
{
    [Fact]
    public async Task TwoMalformedGuardsRequireBothExplicitSlotsWithoutBroadeningTheRevision()
    {
        var state = await Fixture("read_left", "use_left", "available", "available},{");
        var tasks = state.Plan!.Root.Tasks;
        var producer = Copy(tasks[0]); producer.Id = "read_right";
        var consumer = Copy(tasks[1]); consumer.Id = "use_right";
        foreach (var field in consumer.Requires!.Items) field.Items.Single().Source = producer.Id;
        consumer.Outputs.Single().Value.Items.Single().Source = producer.Id;
        tasks.AddRange([producer, consumer]);
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        var paths = new[] { "/tasks/use_left/requires", "/tasks/use_right/requires" };
        Assert.Equal(paths, TaskPlanRevisions.UnrepairableRequirements(state).Select(diagnostic => diagnostic.Location));
        Authorize(state, paths);
        var baseline = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)!;
        var corrected = OutputBindingRoutingTests.Apply(state,
            RepairPatchTests.Edit(state, paths[0], "replace", Guard(state.Plan.Root.Tasks[1], "available")),
            RepairPatchTests.Edit(state, paths[1], "replace", Guard(state.Plan.Root.Tasks[3], "available")));
        Assert.Empty(new TaskPlanCompiler().Compile(corrected, state.Catalog!).Diagnostics);
        foreach (var index in new[] { 1, 3 }) baseline["root"]!["tasks"]![index]!["requires"]!["items"]![1]!["port"] = "available";
        Assert.True(JsonNode.DeepEquals(baseline, JsonSerializer.SerializeToNode(corrected, PlanningJsonContext.Default.TaskPlan)));
        Assert.Equal(paths, RepairPatchTests.Slots(state).Select(slot => slot.Location));
        Assert.Equal("available},{", state.Plan.Root.Tasks[1].Requires!.Items[1].Port);
    }

    [Theory]
    [InlineData("inspect", "adapt", "ready")]
    [InlineData("acquire", "consume", "disponible")]
    public async Task MalformedFieldRequiresExplicitNarrowRevisionAndPreservesBothAssertions(string producer, string consumer, string field)
    {
        var state = await Fixture(producer, consumer, field, field + "},{");
        var path = "/tasks/" + consumer + "/requires";
        var blocker = Assert.Single(TaskPlanRevisions.UnrepairableRequirements(state));
        Assert.Equal(path, blocker.Location);
        Assert.Contains(field + "},{", blocker.Message, StringComparison.Ordinal);
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        Authorize(state, [path]);
        var request = RepairPatchTests.Request(state);
        var corrected = OutputBindingRoutingTests.Apply(state, RepairPatchTests.Edit(state, path, "replace", Guard(state.Plan!.Root.Tasks[1], field)));
        Assert.Empty(new TaskPlanCompiler().Compile(corrected, state.Catalog!).Diagnostics);
        var assertion = corrected.Root.Tasks[1].Requires!;
        Assert.Equal("and", assertion.Predicate); Assert.Equal(new[] { "success", field }, assertion.Items.Select(value => value.Port));
        var expected = JsonNode.Parse(original)!; expected["root"]!["tasks"]![1]!["requires"]!["items"]![1]!["port"] = field;
        Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(corrected, PlanningJsonContext.Default.TaskPlan)));
        Assert.Equal(path, Assert.Single(PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray())!["location"]!.ToString());
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        var outside = Copy(corrected.Root.Tasks[0]); outside.Objective += " rewritten";
        corrected.Root.Tasks[0] = outside;
        Assert.Contains(TaskPlanRevisions.Validate(state.Plan, corrected, state.RevisionScope, state.Catalog, state.EditablePaths), diagnostic => diagnostic.Code == "REVISION_SCOPE_CHANGED");
    }

    [Theory]
    [InlineData("ok},{")]
    [InlineData("prêt")]
    [InlineData("length")]
    public async Task LiteralDeclaredPropertyNamesAreNeverSanitized(string field)
    {
        var state = await Fixture("read", "use", field, field);
        var before = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        Assert.Empty(new TaskPlanCompiler().Compile(state.Plan!, state.Catalog!).Diagnostics);
        Assert.Equal(field, state.Plan!.Root.Tasks[1].Requires!.Items[1].Port);
        Assert.Equal(before, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(0, state.ModelCalls);
    }

    private static void Authorize(PlanningSession state, IReadOnlyList<string> paths)
    {
        TaskPlanRevisions.ValidateEditablePaths(state, paths);
        state.EditablePaths = paths.ToList(); state.RevisionScope = paths.ToList();
    }
    private static JsonNode Guard(PlanTask task, string field)
    {
        var guard = JsonSerializer.SerializeToNode(task.Requires, PlanningJsonContext.Default.TaskValue)!;
        guard["items"]![1]!["port"] = field; return guard;
    }
    private static PlanTask Copy(PlanTask task) => JsonSerializer.Deserialize(JsonSerializer.Serialize(task, PlanningJsonContext.Default.PlanTask), PlanningJsonContext.Default.PlanTask)!;
    private static async Task<PlanningSession> Fixture(string producer, string consumer, string declared, string referenced)
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("observations", new() { Tools = [new() { Name = "read_flags", EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
                { ["success"] = new JsonObject { ["type"] = "boolean" }, [declared] = new JsonObject { ["type"] = "boolean" } },
                ["required"] = new JsonArray("success", declared), ["additionalProperties"] = false } }] });
        var catalog = await TaskPlanCompilerTests.Catalog(new(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask));
        TaskValue Field(string name) => new() { Kind = "field", Port = name, Items = [new() { Kind = "output", Source = producer }] };
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.Requirements = PlannerFixture.Requirements();
        state.Plan = new() { Root = new() { Tasks = [
            new() { Id = producer, Objective = "Acquire declared flags", Operation = catalog.Capabilities.Single(capability => capability.Method == "read_flags").Id },
            new() { Id = consumer, Kind = "value", Objective = "Return the observed flag after both assertions",
                Requires = new() { Kind = "predicate", Predicate = "and", Items = [Field("success"), Field(referenced)] },
                Outputs = [new("message", Field(declared))] }],
            Outputs = [new("message", new() { Kind = "output", Source = consumer, Port = "message" })] } };
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        return state;
    }
}
