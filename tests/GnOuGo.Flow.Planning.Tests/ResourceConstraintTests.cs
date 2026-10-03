using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Planning.Examples;

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
        await Execute(plan, catalog, "area/overridden", true);
        await Execute(plan, catalog, "outside", false);
    }

    [Theory]
    [InlineData("^area/", 6, 30)]
    [InlineData("^zone-", 7, 25)]
    public async Task ArbitraryProducerConstraintsCheckObservedValuesWithoutInventingFiniteDomains(string pattern, int minimum, int maximum)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "renamed-operation", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "unrelated", Method = "prepare",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["destination"] = new JsonObject
                { ["type"] = "string", ["pattern"] = pattern, ["minLength"] = minimum, ["maxLength"] = maximum } }, ["required"] = new JsonArray("destination") }, OutputSchema = new() });
        catalog.Capabilities.Reverse();
        var plan = new TaskPlan { Inputs = [new() { Name = "location", Type = new() { Kind = "string" } }], Root = new() { Tasks =
            [new() { Id = "consumer", Objective = "Use the declared input", Operation = "renamed-operation", Inputs = [new("destination", new() { Kind = "input", Source = "location" })] }] } };
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics); Assert.NotNull(result.Graph);
        await Execute(plan, catalog, pattern[1..] + "valid", true);
        await Execute(plan, catalog, "invalid", false);
        await Execute(plan, catalog, pattern[1..] + new string('x', maximum), false);
        plan.Root.Tasks[0].Inputs[0] = new("destination", new() { Kind = "string", Text = pattern[1..] + "valid" });
        Assert.Empty(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        plan.Root.Tasks[0].Inputs[0].Value.Text = "invalid";
        Assert.Null(new TaskPlanCompiler().Compile(plan, catalog).Graph);
    }
    [Theory]
    [InlineData("action", "payload", "code", "^area/")]
    [InlineData("variant", "arguments", "reference", "^zone-")]
    public async Task LiteralDiscriminatorsSelectNestedChecksBeforeBinding(string selector, string container, string field, string pattern)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        JsonObject Branch(string choice, JsonObject value) => new() { ["type"] = "object", ["properties"] = new JsonObject
            { [selector] = new JsonObject { ["const"] = choice }, [container] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
                { [field] = value, ["untouched"] = new JsonObject { ["type"] = "boolean" } }, ["required"] = new JsonArray(field, "untouched"), ["additionalProperties"] = false } },
            ["required"] = new JsonArray(selector, container), ["additionalProperties"] = false };
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            { [selector] = new JsonObject { ["type"] = "string" }, [container] = new JsonObject { ["type"] = "object" } },
            ["required"] = new JsonArray(selector, container), ["oneOf"] = new JsonArray(
                Branch("selected", new() { ["type"] = "string", ["pattern"] = pattern, ["minLength"] = 6, ["maxLength"] = 20, ["allOf"] = new JsonArray(new JsonObject { ["pattern"] = "[A-Za-z]" }) }),
                Branch("other", new() { ["type"] = "integer" })) };
        catalog.Capabilities.Add(new() { Id = "branch-operation", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "unrelated", Method = "dispatch",
            InputSchema = schema, OutputSchema = new() });
        var plan = new TaskPlan { Inputs = [new() { Name = "location", Type = new() { Kind = "string" } }], Root = new() { Tasks =
            [new() { Id = "consumer", Objective = "Use the selected operation", Operation = "branch-operation", Inputs =
                [new(selector, new() { Kind = "string", Text = "selected" }), new(container, new() { Kind = "object", Members =
                    [new(field, new() { Kind = "input", Source = "location" }), new("untouched", new() { Kind = "boolean", Boolean = true })] })] }] } };
        var original = schema.ToJsonString();
        await Execute(plan, catalog, pattern[1..] + "valid", true);
        await Execute(plan, catalog, "invalid", false);
        Assert.Equal(original, schema.ToJsonString());
        plan.Root.Tasks[0].Inputs[0].Value.Text = "other";
        Assert.Null(new TaskPlanCompiler().Compile(plan, catalog).Graph); // Known incompatible type.
        plan.Root.Tasks[0].Inputs[0] = new(selector, new() { Kind = "input", Source = "location" });
        Assert.Null(new TaskPlanCompiler().Compile(plan, catalog).Graph); // Runtime selector cannot establish a branch.
        plan.Root.Tasks[0].Inputs[0] = new(selector, new() { Kind = "string", Text = "selected" });
        schema["oneOf"]!.AsArray().Add(Branch("selected", new() { ["type"] = "string" }));
        Assert.Null(new TaskPlanCompiler().Compile(plan, catalog).Graph); // Ambiguous selector.
    }

    [Theory]
    [InlineData("{\"type\":\"string\"}", "{\"type\":\"string\",\"pattern\":\"^zone-\"}", true)]
    [InlineData("{\"type\":\"string\"}", "{\"type\":\"string\",\"allOf\":[{\"pattern\":\"^zone-\"}]}", true)]
    [InlineData("{\"type\":\"string\",\"allOf\":[{\"pattern\":\"^outside\"}]}", "{\"type\":\"string\",\"pattern\":\"^zone-\"}", false)]
    [InlineData("{\"type\":\"number\"}", "{\"type\":\"number\",\"minimum\":1}", true)]
    [InlineData("{\"type\":\"string\",\"enum\":[\"outside\"]}", "{\"type\":\"string\",\"pattern\":\"^zone-\"}", false)]
    [InlineData("{\"type\":\"number\",\"minimum\":0}", "{\"type\":\"number\",\"minimum\":1}", false)]
    [InlineData("{\"type\":[\"string\",\"null\"]}", "{\"type\":\"string\",\"pattern\":\"^zone-\"}", false)]
    [InlineData("{\"type\":\"number\"}", "{\"type\":\"string\",\"pattern\":\"^zone-\"}", false)]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", "{\"type\":\"array\",\"maxItems\":3,\"items\":{\"type\":\"string\",\"minLength\":1}}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{}}", "{\"type\":\"object\",\"properties\":{\"key\":{\"type\":\"string\",\"minLength\":1}},\"required\":[\"key\"]}", false)]
    public void RuntimeChecksCannotOverrideKnownIncompatibilities(string actual, string expected, bool allowed)
        => Assert.Equal(allowed, PlanningContractShapes.CanCheckConstraints(JsonNode.Parse(actual)!.AsObject(), JsonNode.Parse(expected)!.AsObject()));

    private static async Task Execute(TaskPlan plan, PlanningCatalog catalog, string location, bool succeeds)
    {
        var called = false; var capability = catalog.Capabilities.Single(c => c.Server is "renamed" or "unrelated");
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer(capability.Server!, new()
        {
            Tools = [new() { Name = capability.Method!, InputSchema = capability.InputSchema, OutputSchema = capability.OutputSchema }],
            ToolHandlers = new() { [capability.Method!] = _ => { called = true; return new() { Content = new JsonObject() }; } }
        });
        // No model is installed: typed constraint checks must be deterministic.
        var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human() };
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        PlanningConfirmationGuards.Apply(compiled.Graph!, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["location"] = location }, PlannerFixture.Ct);
        Assert.True(succeeds == result.Success, result.Error?.Code + ": " + result.Error?.Message); Assert.Equal(succeeds, called);
    }

}
