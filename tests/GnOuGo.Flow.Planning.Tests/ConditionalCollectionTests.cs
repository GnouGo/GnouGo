using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConditionalCollectionTests
{
    [Theory]
    [InlineData("missing", "TASK_FIELD_UNKNOWN")]
    [InlineData("opaque", "TASK_FIELD_TYPE")]
    [InlineData("scalar", "TASK_FIELD_TYPE")]
    public async Task FieldSelectionCannotInventContractsInAnAlternative(string variant, string code)
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        var other = variant switch
        {
            "opaque" => new TaskValue { Kind = "input", Source = "unknown" },
            "scalar" => new TaskValue { Kind = "number", Number = 1 },
            _ => new TaskValue { Kind = "object", Members = [new("different", new() { Kind = "array" })] }
        };
        var plan = new TaskPlan { Inputs = [new() { Name = "unknown", Type = new() { Kind = "any" } }], Root = new()
        {
            Tasks = [new() { Id = "choose", Kind = "conditional", Objective = "Select the supplied record", Condition = new() { Kind = "boolean", Boolean = true },
                Body = new() { Outputs = [new("record", new() { Kind = "object", Members = [new("entries", new() { Kind = "array" })] })] },
                Otherwise = new() { Outputs = [new("record", other)] } }],
            Outputs = [new("rows", TaskFieldBindingTests.Field(new() { Kind = "output", Source = "choose", Port = "record" }, "entries"))]
        } };
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("string")]
    [InlineData("array")]
    public async Task AlternativesCannotInventArrayOrElementCompatibility(string kind)
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = PlanningCorpus.Tasks("collections", catalog);
        var loop = plan.Root.Tasks.Single();
        var other = kind == "array" ? new TaskValue { Kind = "array", Items = [new() { Kind = "string", Text = "not numeric" }] }
            : new TaskValue { Kind = kind, Text = kind == "string" ? "not an array" : null };
        plan.Root.Tasks.Insert(0, new() { Id = "choose", Kind = "conditional", Objective = "Select rows",
            Condition = new() { Kind = "boolean", Boolean = true },
            Body = new() { Outputs = [new("rows", new() { Kind = "input", Source = "values" })] },
            Otherwise = new() { Outputs = [new("rows", other)] } });
        loop.Items = new() { Kind = "output", Source = "choose", Port = "rows" };
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(compilation.Graph);
        Assert.Contains(compilation.Diagnostics, d => d.Code == (kind == "array" ? "TASK_GROUP_INPUT_TYPE" : "TASK_ITEMS_INVALID"));
    }

    [Fact]
    public void UnknownArrayContentsRemainOpaqueAlongsideTypedAlternatives()
    {
        var schema = JsonNode.Parse("""{"anyOf":[{"type":"array","items":{"type":"string"}},{"type":"array"}]}""")!.AsObject();
        var before = schema.ToJsonString();
        var items = PlanningContractShapes.IterationItems(schema)!;
        Assert.Contains(items["anyOf"]!.AsArray(), item => item!["x-gnougo-opaque"]?.GetValue<bool>() == true);
        Assert.Equal(before, schema.ToJsonString());
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, false, null)]
    [InlineData(true, true, null)]
    [InlineData(false, false, "entries")]
    [InlineData(false, true, "values")]
    [InlineData(true, false, "values")]
    [InlineData(true, true, "entries")]
    public async Task TypedAndEmptyBranchesPreserveNestedExportsAndLoopExecution(bool enabled, bool parallel, string? field)
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = PlanningCorpus.Tasks("collections", catalog);
        var loop = plan.Root.Tasks.Single(); loop.Parallel = parallel; loop.MaxItems = 3;
        plan.Inputs.Add(new() { Name = "enabled", Type = new() { Kind = "boolean" } });
        var choose = new PlanTask { Id = "choose_rows", Kind = "conditional", Objective = "Use the supplied rows when enabled",
            Condition = new() { Kind = "input", Source = "enabled" },
            Body = new() { Outputs = [new("rows", new() { Kind = "input", Source = "values" })] },
            Otherwise = new() { Outputs = [new("rows", new() { Kind = "array" })] } };
        plan.Root.Tasks.Insert(0, new() { Id = "container", Kind = "sequence", Objective = "Export selected rows",
            Body = new() { Tasks = [choose], Outputs = [new("exported", new() { Kind = "output", Source = choose.Id, Port = "rows" })] } });
        loop.Items = new() { Kind = "output", Source = "container", Port = "exported" };
        if (field is not null)
        {
            foreach (var branch in new[] { choose.Body!, choose.Otherwise! })
                branch.Outputs[0] = new("rows", new() { Kind = "object", Members = [new(field, branch.Outputs[0].Value)] });
            loop.Items = TaskFieldBindingTests.Field(loop.Items, field);
        }
        var before = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics); Assert.NotNull(compiled.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(compiled.Graph, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph, catalog);
        var flow = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(flow.Workflows["main"], new JsonObject
            { ["enabled"] = enabled, ["values"] = new JsonArray(2, 5) }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(JsonNode.DeepEquals(enabled ? new JsonArray(4, 10) : new JsonArray(), result.Outputs!["result"]));
        Assert.Equal(before, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
    }
}
