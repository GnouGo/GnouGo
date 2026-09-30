using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskArtifactBindingTests
{
    [Fact]
    public async Task WrongFieldIsRejectedAtTheConsumerBeforeLowering()
    {
        var (plan, catalog) = await Fixture();
        plan.Root.Tasks[1].Inputs[0].Value.Port = "display";
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("TASK_ARTIFACT_BINDING", error.Code);
        Assert.Equal("/tasks/use/inputs/location", error.Location);
    }

    [Fact]
    public async Task DeclaredOriginSurvivesCheckedFieldSelectionAndOpaqueExport()
    {
        var (plan, catalog) = await Fixture();
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics); Assert.NotNull(result.Graph);
        Assert.Contains(result.Graph.Workflows.SelectMany(w => w.Steps), n => n.Type == "value.project");
        Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(result.Graph, catalog));
        Assert.True(result.Graph.Workflows[0].Outputs.Single().Schema.Contract!["x-gnougo-opaque"]!.GetValue<bool>());
        Assert.NotEmpty(new PlanningGraphCompiler().Compile(result.Graph, catalog));
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("conditional")]
    [InlineData("parallel")]
    [InlineData("call")]
    public async Task ArtifactExportsAndCapturesRetainTheirOrigin(string kind)
    {
        var (plan, catalog) = await Fixture();
        var binding = plan.Root.Tasks[1].Inputs[0].Value;
        var body = new TaskScope { Outputs = [new("resource", binding)] };
        var container = new PlanTask { Id = "scope", Kind = kind, Objective = "Export the resource", Body = body };
        if (kind == "conditional") { container.Condition = new() { Kind = "boolean", Boolean = true }; container.Otherwise = new() { Outputs = [new("resource", binding)] }; }
        if (kind == "parallel") { container.Body = null; container.Branches = [body, new() { Outputs = [new("other", new() { Kind = "number", Number = 2 })] }]; }
        if (kind == "call")
        {
            container.Body = null; container.Group = "group"; container.Inputs = [new("resource", binding)];
            plan.Groups = [new() { Id = "group", Inputs = [new() { Name = "resource", Type = new() { Kind = "string" } }],
                Body = new() { Outputs = [new("resource", new() { Kind = "input", Source = "resource" })] } }];
        }
        plan.Root.Tasks.Insert(1, container);
        plan.Root.Tasks[2].Inputs = [new("location", Reference("scope", "resource"))];
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics); Assert.NotNull(result.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(result.Graph, catalog));
        if (kind == "conditional")
        {
            container.Otherwise!.Outputs[0] = new("resource", new() { Kind = "string", Text = "same-looking-path" });
            Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_ARTIFACT_BINDING");
        }
    }

    [Theory]
    [InlineData("root")]
    [InlineData("sequence")]
    [InlineData("call")]
    [InlineData("foreach")]
    public async Task OpaqueResultsCrossBoundariesWithoutInventingTypedFields(string boundary)
    {
        var (plan, catalog) = await Fixture();
        var forwarded = Reference("use");
        if (boundary != "root")
        {
            var body = new TaskScope { Outputs = [new("raw", forwarded)] };
            var task = new PlanTask { Id = "forward", Kind = boundary, Objective = "Forward the untyped response", Body = body };
            if (boundary == "foreach") task.Items = new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }, new() { Kind = "number", Number = 2 }] };
            if (boundary == "call")
            {
                task.Body = null; task.Group = "group"; task.Inputs = [new("raw", forwarded)];
                plan.Groups.Add(new() { Id = "group", Inputs = [new() { Name = "raw", Type = new() { Kind = "any" } }],
                    Body = new() { Outputs = [new("raw", new() { Kind = "input", Source = "raw" })] } });
            }
            plan.Root.Tasks.Add(task); plan.Root.Outputs = [new("result", Reference("forward", "raw"))];
        }
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics); Assert.NotNull(compiled.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(compiled.Graph, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph, catalog));
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("renamed", new() { Tools = [
            new() { Name = "allocate", InputSchema = catalog.Capabilities.Single(c => c.Id == "allocate").InputSchema, OutputSchema = catalog.Capabilities.Single(c => c.Id == "allocate").OutputSchema, EffectKind = "read" },
            new() { Name = "inspect", InputSchema = catalog.Capabilities.Single(c => c.Id == "inspect").InputSchema, OutputSchema = new JsonObject(), EffectKind = "read" }
        ], ToolHandlers = new()
        {
            ["allocate"] = _ => new() { Content = new JsonObject { ["handle"] = "observed", ["display"] = "untrusted" } },
            ["inspect"] = request => { Assert.Equal("observed", request!["location"]!.ToString()); return new() { Content = JsonNode.Parse("""{"untyped":[1,null,"é"]}""") }; }
        } });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph, catalog)));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        var expected = JsonNode.Parse("""{"untyped":[1,null,"é"]}""");
        Assert.True(JsonNode.DeepEquals(boundary == "foreach" ? new JsonArray(expected!.DeepClone(), expected.DeepClone()) : expected, result.Outputs!["result"]));
        plan.Root.Outputs = [new("invented", Field(Reference("use"), "untyped"))];
        Assert.Null(new TaskPlanCompiler().Compile(plan, catalog).Graph);
    }

    [Fact]
    public async Task NestedMappingsUseLiteralSegmentsAndPromptUsesOnlyBusinessPorts()
    {
        var (plan, catalog) = await Fixture();
        var producer = catalog.Capabilities.Single(c => c.Id == "allocate");
        producer.OutputSchema = JsonNode.Parse("""{"type":"object","required":["container"],"properties":{"container":{"type":"object","required":["a/b~c"],"properties":{"a/b~c":{"type":"string"}}}}}""")!.AsObject();
        producer.ArtifactContract = new(1, [new("resource", "/container/a~1b~0c", "materialize")], []);
        plan.Root.Tasks[1].Inputs = [new("location", Field(Field(Reference("make"), "container"), "a/b~c"))];
        var result = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(result.Diagnostics);
        Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph!, catalog));
        var described = TaskOperations.ArtifactPorts(producer);
        Assert.Equal("container", described["produces"]![0]!["port"]!.ToString());
        Assert.Equal("a/b~c", described["produces"]![0]!["fields"]![0]!.ToString());
        Assert.DoesNotContain("~1", described.ToJsonString());
    }

    [Fact]
    public async Task BoundedIterationRetainsOriginsThroughItsGeneratedValidation()
    {
        var (plan, catalog) = await Fixture();
        var consume = plan.Root.Tasks[1]; consume.Inputs = [new("location", new() { Kind = "item" })];
        plan.Root.Tasks[1] = new() { Id = "iterate", Kind = "foreach", Objective = "Inspect each declared resource", MaxItems = 2,
            Items = new() { Kind = "array", Items = [Field(Reference("make"), "handle")] }, Body = new() { Tasks = [consume] } };
        plan.Root.Outputs.Clear();
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics); Assert.NotNull(result.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph, catalog));
    }

    [Theory]
    [InlineData("literal")]
    [InlineData("input")]
    [InlineData("transform")]
    public async Task MatchingStringsAndInterpretationCannotCreateArtifacts(string kind)
    {
        var (plan, catalog) = await Fixture();
        TaskValue selected = new() { Kind = "string", Text = "observed" };
        if (kind == "input") { plan.Inputs = [new() { Name = "resource", Type = new() { Kind = "string" } }]; selected = new() { Kind = "input", Source = "resource" }; }
        if (kind == "transform")
        {
            plan.Root.Tasks.Insert(1, new() { Id = "interpret", Kind = "transform", Objective = "Interpret text", Inputs = [new("text", selected)],
                ResultType = new() { Kind = "object", Fields = [new() { Name = "resource", Type = new() { Kind = "string" } }] } });
            selected = Reference("interpret", "resource");
        }
        plan.Root.Tasks.Last().Inputs = [new("location", selected)];
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_ARTIFACT_BINDING");
    }

    [Fact]
    public async Task OptionalNestedArtifactMayBeOmittedButNullDoesNotProveOrigin()
    {
        var (plan, catalog) = await Fixture();
        var consumer = catalog.Capabilities.Single(c => c.Id == "inspect");
        consumer.InputSchema = JsonNode.Parse("""{"type":"object","properties":{"context":{"type":"object","properties":{"resource":{"type":["string","null"]}}}}}""")!.AsObject();
        consumer.ArtifactContract = new(1, [], [new("resource", "/context/resource", false)]);
        var context = new TaskValue { Kind = "object" };
        plan.Root.Tasks[1].Inputs = [new("context", context)];
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics); Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph!, catalog));
        context.Members.Add(new("resource", new() { Kind = "null" }));
        result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_ARTIFACT_BINDING" && d.Location == "/tasks/use/inputs/context");
    }

    internal static TaskValue Reference(string source, string? port = null) => new() { Kind = "output", Source = source, Port = port };
    internal static TaskValue Field(TaskValue source, string field) => new() { Kind = "field", Items = [source], Port = field };
    internal static async Task<(TaskPlan Plan, PlanningCatalog Catalog)> Fixture()
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        var output = JsonNode.Parse("""{"type":"object","required":["handle","display"],"properties":{"handle":{"type":"string"},"display":{"type":"string"}}}""")!.AsObject();
        var input = JsonNode.Parse("""{"type":"object","required":["location"],"properties":{"location":{"type":"string"}}}""")!.AsObject();
        catalog.Capabilities.AddRange([
            new() { Id = "allocate", StepType = "mcp.call", Kind = "tool", Server = "renamed", Method = "allocate", EffectKind = "read",
                InputSchema = new() { ["type"] = "object" }, OutputSchema = output, ArtifactContract = new(1, [new("resource", "/handle", "materialize")], []) },
            new() { Id = "inspect", StepType = "mcp.call", Kind = "tool", Server = "renamed", Method = "inspect", EffectKind = "read",
                InputSchema = input, OutputSchema = new(), ArtifactContract = new(1, [], [new("resource", "/location", true)]) }
        ]);
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = "make", Kind = "operation", Objective = "Allocate resource", Operation = "allocate" },
            new() { Id = "use", Kind = "operation", Objective = "Inspect resource", Operation = "inspect", Inputs = [new("location", Field(Reference("make"), "handle"))] }
        ], Outputs = [new("result", Reference("use"))] } };
        return (plan, catalog);
    }
}
