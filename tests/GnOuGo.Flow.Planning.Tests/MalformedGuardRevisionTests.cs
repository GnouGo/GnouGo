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
        var (runtime, state) = await Fixture("read_left", "use_left", "available", "available},{");
        var tasks = runtime.Proposal.Plan!.Root.Tasks;
        PlanTask Clone(PlanTask task) => JsonSerializer.Deserialize(JsonSerializer.Serialize(task, PlanningJsonContext.Default.PlanTask), PlanningJsonContext.Default.PlanTask)!;
        var producer = Clone(tasks[0]); producer.Id = "read_right";
        var consumer = Clone(tasks[1]); consumer.Id = "use_right";
        foreach (var field in consumer.Requires!.Items) field.Items.Single().Source = producer.Id;
        consumer.Outputs.Single().Value.Items.Single().Source = producer.Id;
        tasks.AddRange([producer, consumer]);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        var paths = new[] { "/tasks/use_left/requires", "/tasks/use_right/requires" };
        Assert.Equal(paths, state.Diagnostics.Where(d => d.Code == "REVISION_REQUIRED").Select(d => d.Location));
        Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        var baseline = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)!;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", PreserveRequirements = true,
            EditablePaths = paths.ToList(), ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            Text = "Correct only the two malformed property references. Preserve both ordered assertions in each guard." }, runtime, PlannerFixture.Ct);
        foreach (var task in tasks.Where(t => t.Requires is not null)) task.Requires!.Items[1].Port = "available";
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Null(state.ApprovedHash);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(0, state.ReplanAttempts);
        foreach (var index in new[] { 1, 3 }) baseline["root"]!["tasks"]![index]!["requires"]!["items"]![1]!["port"] = "available";
        Assert.True(JsonNode.DeepEquals(baseline, JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)));
        Assert.Equal(paths, PlanningRepairPatch.RequestContext(runtime.Calls[1])["repair"]!["slots"]!.AsArray().Select(s => s!["location"]!.ToString()));
    }

    [Theory]
    [InlineData("inspect", "adapt", "ready")]
    [InlineData("acquire", "consume", "disponible")]
    public async Task MalformedFieldRequiresExplicitNarrowRevisionAndPreservesBothAssertions(string producer, string consumer, string field)
    {
        var (runtime, state) = await Fixture(producer, consumer, field, field + "},{");
        var planner = new HybridWorkflowPlanner();
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        var path = "/tasks/" + consumer + "/requires";
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_REQUIRED" && d.Location == path);
        Assert.Contains(state.Diagnostics, d => d.Message.Contains("'" + field + "},{'", StringComparison.Ordinal));
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Single(runtime.Calls); Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));

        state = await planner.AdvanceAsync(state, new() { Kind = "revise", PreserveRequirements = true,
            EditablePaths = [path], ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            Text = "Correct only the malformed field reference, preserving both ordered assertions." }, runtime, PlannerFixture.Ct);
        Assert.Single(runtime.Calls); Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        runtime.Proposal.Plan!.Root.Tasks[1].Requires!.Items[1].Port = field;
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        var assertion = state.Plan!.Root.Tasks[1].Requires!;
        Assert.Equal("and", assertion.Predicate); Assert.Equal(new[] { "success", field }, assertion.Items.Select(v => v.Port));
        var expected = JsonNode.Parse(original)!;
        expected["root"]!["tasks"]![1]!["requires"]!["items"]![1]!["port"] = field;
        Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)));
        var slots = PlanningRepairPatch.RequestContext(runtime.Calls[1])["repair"]!["slots"]!.AsArray();
        Assert.Equal(path, Assert.Single(slots)!["location"]!.ToString());
        var calls = runtime.Calls.Count;
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(calls, runtime.Calls.Count); Assert.Equal(PlanningStatus.FinalReview, state.Status);
    }

    [Theory]
    [InlineData("ok},{")]
    [InlineData("prêt")]
    [InlineData("length")]
    public async Task LiteralDeclaredPropertyNamesAreNeverSanitized(string field)
    {
        var (runtime, state) = await Fixture("read", "use", field, field);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(field, state.Plan!.Root.Tasks[1].Requires!.Items[1].Port);
        Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
    }

    private static async Task<(TestRuntime, PlanningSession)> Fixture(string producer, string consumer, string declared, string referenced)
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("observations", new() { Tools = [new() { Name = "read_flags", EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
                { ["success"] = new JsonObject { ["type"] = "boolean" }, [declared] = new JsonObject { ["type"] = "boolean" } },
                ["required"] = new JsonArray("success", declared), ["additionalProperties"] = false } }] });
        var runtime = new TestRuntime(new() { McpClientFactory = factory });
        var catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
        TaskValue Field(string name) => new() { Kind = "field", Port = name, Items = [new() { Kind = "output", Source = producer }] };
        runtime.Proposal.Plan = new() { Root = new() { Tasks = [
            new() { Id = producer, Objective = "Acquire declared flags", Operation = catalog.Capabilities.Single(c => c.Method == "read_flags").Id },
            new() { Id = consumer, Kind = "value", Objective = "Return the observed flag after both assertions",
                Requires = new() { Kind = "predicate", Predicate = "and", Items = [Field("success"), Field(referenced)] },
                Outputs = [new("message", Field(declared))] }],
            Outputs = [new("message", new() { Kind = "output", Source = consumer, Port = "message" })] } };
        var state = PlannerFixture.Session(); state.Catalog = catalog;
        return (runtime, state);
    }
}
