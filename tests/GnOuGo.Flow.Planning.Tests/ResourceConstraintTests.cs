using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ResourceConstraintTests
{
    [Fact]
    public async Task SharedLiteralRetainsItsConstraintThroughValuesCapturesAndSerialization()
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "consume", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "renamed", Method = "unrelated",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"key":{"type":"string","pattern":"^area/","minLength":6}},"required":["key"]}""")!.AsObject(), OutputSchema = new() });
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = "declared", Kind = "value", Objective = "Declare shared value", Outputs = [new("location", new() { Kind = "string", Text = "area/example" })] },
            new() { Id = "nested", Kind = "sequence", Objective = "Consume captured value", Body = new() { Tasks =
                [new() { Id = "use", Operation = "consume", Objective = "Use declared value", Inputs = [new("key", new() { Kind = "output", Source = "declared", Port = "location" })] }] } }
        ] } };
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics); Assert.NotNull(result.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph, catalog));
        PlanningConfirmationGuards.Apply(result.Graph, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(result.Graph, catalog));
        var copy = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        Assert.Empty(new TaskPlanCompiler().Compile(copy, catalog).Diagnostics);
        copy.Root.Tasks[0].Outputs[0].Value.Text = "outside";
        Assert.Null(new TaskPlanCompiler().Compile(copy, catalog).Graph);
        // A workflow default can be overridden and must never be promoted to a constant.
        plan.Inputs.Add(new() { Name = "location", Type = new() { Kind = "string" }, Default = new() { Kind = "string", Text = "area/default" } });
        plan.Root.Tasks[0].Outputs[0] = new("location", new() { Kind = "input", Source = "location" });
        Assert.Null(new TaskPlanCompiler().Compile(plan, catalog).Graph);
    }

    [Theory]
    [InlineData("^area/", 6, 30)]
    [InlineData("^zone-", 7, 25)]
    public async Task ArbitraryProducerConstraintsDiagnoseConsumerWithoutWideningRepair(string pattern, int minimum, int maximum)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "renamed-operation", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "unrelated", Method = "prepare",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["destination"] = new JsonObject
                { ["type"] = "string", ["pattern"] = pattern, ["minLength"] = minimum, ["maxLength"] = maximum } }, ["required"] = new JsonArray("destination") }, OutputSchema = new() });
        catalog.Capabilities.Reverse();
        var plan = new TaskPlan { Inputs = [new() { Name = "location", Type = new() { Kind = "string" } }], Root = new() { Tasks =
            [new() { Id = "consumer", Objective = "Use the declared input", Operation = "renamed-operation", Inputs = [new("destination", new() { Kind = "input", Source = "location" })] }] } };
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph);
        var error = Assert.Single(result.Diagnostics);
        Assert.Contains("pattern " + JsonValue.Create(pattern)!.ToJsonString(), error.Message);
        Assert.Contains("minLength " + minimum, error.Message); Assert.Contains("maxLength " + maximum, error.Message);
        Assert.Equal("/tasks/consumer/inputs/destination", error.Location);
        Assert.Equal(new[] { error.Location }, TaskPlanRevisions.Scope(plan, result.Diagnostics));
        plan.Root.Tasks[0].Inputs[0] = new("destination", new() { Kind = "string", Text = pattern[1..] + "valid" });
        Assert.Empty(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        plan.Root.Tasks[0].Inputs[0].Value.Text = "invalid";
        Assert.Null(new TaskPlanCompiler().Compile(plan, catalog).Graph);
    }
}
