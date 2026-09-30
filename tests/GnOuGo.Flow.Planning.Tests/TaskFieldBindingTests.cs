using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskFieldBindingTests
{
    internal static TaskValue Field(TaskValue source, string name) => new() { Kind = "field", Items = [source], Port = name };
    private static TaskValue Input(string name = "record") => new() { Kind = "input", Source = name };
    private static TaskType Record(bool nullable = false, bool optional = false) => new() { Kind = "object", Fields =
        [new() { Name = "message", Required = !optional, Type = new() { Kind = "string", Nullable = nullable } },
         new() { Name = "number", Type = new() { Kind = "integer" } }] };
    private static TaskPlan Plan(TaskType? type = null, TaskValue? selected = null) => new()
    {
        Inputs = [new() { Name = "record", Type = type ?? Record() }],
        Root = new() { Outputs = [new("selected", selected ?? Field(Input(), "message"))] }
    };
    private static async Task<PlanningCatalog> Catalog() => await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
    private static PlanningGraph Compile(TaskPlan plan, PlanningCatalog catalog)
    {
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics); Assert.NotNull(result.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(result.Graph, catalog));
        return result.Graph;
    }
    private static async Task<GnOuGo.Flow.Core.Models.RunResult> Run(TaskPlan plan, PlanningCatalog catalog, JsonObject inputs)
    {
        var yaml = new PlanningGraphCompiler().Compile(Compile(plan, catalog), catalog);
        var engine = new WorkflowEngine();
        Assert.Empty(await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).ValidateAsync(new(yaml, new(), catalog, []), PlannerFixture.Ct));
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        return await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], inputs, PlannerFixture.Ct);
    }

    [Fact]
    public async Task DeclaredFieldCompilesToCheckedProjectionAndPreservesExactValue()
    {
        var catalog = await Catalog(); var plan = Plan();
        var graph = Compile(plan, catalog);
        var projection = Assert.Single(graph.Workflows.SelectMany(w => w.Finally), n => n.Type == "value.project");
        Assert.Equal("message", Assert.Single(Assert.Single(projection.Input.Members.Single(m => m.Name == "paths").Value.Items).Items).Text);
        Assert.DoesNotContain(graph.Workflows.SelectMany(w => w.Steps.Concat(w.Finally)), n => n.Type == "llm.call");
        Assert.Equal(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), JsonSerializer.Serialize(Compile(plan, catalog), PlanningJsonContext.Default.PlanningGraph));
        const string message = "東京 é \"literal\" ${data.other} {{code}}";
        var result = await Run(plan, catalog, new() { ["record"] = new JsonObject { ["message"] = message, ["number"] = 19 } });
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(message, result.Outputs!["selected"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("unknown", "TASK_FIELD_UNKNOWN")]
    [InlineData("opaque", "TASK_FIELD_TYPE")]
    [InlineData("scalar", "TASK_FIELD_TYPE")]
    [InlineData("zero", "TASK_FIELD_INVALID")]
    [InlineData("two", "TASK_FIELD_INVALID")]
    [InlineData("blank", "TASK_FIELD_INVALID")]
    public async Task InvalidSelectionsFailBeforeGraphEmission(string variant, string code)
    {
        var plan = Plan(); var value = plan.Root.Outputs[0].Value;
        switch (variant)
        {
            case "unknown": value.Port = "absent"; break;
            case "opaque": plan.Inputs[0].Type = new() { Kind = "any" }; break;
            case "scalar": plan.Inputs[0].Type = new() { Kind = "string" }; break;
            case "zero": value.Items.Clear(); break;
            case "two": value.Items.Add(Input()); break;
            case "blank": value.Port = " "; break;
        }
        var result = new TaskPlanCompiler().Compile(plan, await Catalog());
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == code && d.Location == "/root/outputs/selected");
    }

    [Theory]
    [InlineData("present", false, true)]
    [InlineData("absent", false, false)]
    [InlineData("null", true, true)]
    [InlineData("null", false, false)]
    [InlineData("wrong", false, false)]
    public async Task OptionalFieldsAreCheckedWithoutDefaultingOrCoercion(string mode, bool nullable, bool success)
    {
        var record = new JsonObject { ["number"] = 1 };
        if (mode == "present") record["message"] = "observed";
        if (mode == "null") record["message"] = null;
        if (mode == "wrong") record["message"] = 42;
        var plan = Plan(Record(nullable, optional: true)); var catalog = await Catalog();
        if (mode == "wrong" || mode == "null" && !nullable)
        {
            var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => Run(plan, catalog, new() { ["record"] = record }));
            Assert.Equal(GnOuGo.Flow.Core.Models.ErrorCodes.InputValidation, error.Code);
            return; // The workflow input contract rejects this before any stage executes.
        }
        var result = await Run(plan, catalog, new() { ["record"] = record });
        Assert.Equal(success, result.Success);
        if (success && mode == "null") Assert.Null(result.Outputs!["selected"]);
        if (success && mode == "present") Assert.Equal("observed", result.Outputs!["selected"]!.ToString());
    }
}
