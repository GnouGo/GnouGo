using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TaskFieldBindingTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskFieldRepairTests
{
    private const string Constraint = "/tasks/interpret/resultType/fields/records/type/items/fields/domain/type/enum";
    private static TaskValue Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    private static async Task<(TaskPlan Plan, PlanningCatalog Catalog)> Setup()
    {
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "post", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "renamed", Method = "publish",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"domain":{"type":"string","enum":["A","B"]}},"required":["domain"],"additionalProperties":false}""")!.AsObject(), OutputSchema = new() });
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = "interpret", Kind = "transform", Objective = "Interpret supplied data", Inputs = [new("raw", new() { Kind = "string", Text = "sample" })],
                ResultType = new() { Kind = "object", Fields = [new() { Name = "records", Type = new() { Kind = "array", Items = new() { Kind = "object", Fields =
                    [new() { Name = "domain", Type = new() { Kind = "string" } }, new() { Name = "unrelated", Type = new() { Kind = "integer" } }] } } }] } },
            new() { Id = "loop", Kind = "foreach", Objective = "Publish each record", Items = Output("interpret", "records"),
                Body = new() { Tasks = [new() { Id = "nested", Kind = "sequence", Objective = "Keep capture boundaries", Body = new() { Tasks = [new()
                    { Id = "consume", Kind = "operation", Operation = "post", Objective = "Publish the field", Inputs = [new("domain", Field(new() { Kind = "item" }, "domain"))] }] } }] } }
        ] } };
        return (plan, catalog);
    }
    private static TaskType Domain(TaskPlan plan) => plan.Root.Tasks[0].ResultType!.Fields[0].Type.Items!.Fields[0].Type;

    [Fact]
    public async Task RenamingTasksCapabilitiesAndReorderingDistractorsDoesNotChangeBinding()
    {
        var (plan, catalog) = await Setup(); Domain(plan).Enum = ["A", "B"];
        var names = TaskPlanRevisions.Tasks(plan).ToDictionary(t => t.Id, t => "renamed_" + t.Id, StringComparer.Ordinal);
        foreach (var task in TaskPlanRevisions.Tasks(plan))
        {
            task.Id = names[task.Id];
            if (task.Operation == "post") task.Operation = "different_operation";
            foreach (var value in TaskPlanCompiler.Values(task))
                if (value.Kind == "output" && value.Source is { } id) value.Source = names[id];
        }
        var capability = catalog.Capabilities.Single(c => c.Id == "post");
        capability.Id = "different_operation"; capability.Server = "other"; capability.Method = "unrelated_name";
        for (var i = 0; i < 12; i++) catalog.Capabilities.Add(new() { Id = "unused_" + i, Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "other", Method = "unused_" + i });
        catalog.Capabilities.Reverse();
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics); Assert.Empty(PlanningGeneratedGraph.Validate(compiled.Graph!, catalog));
        PlanningConfirmationGuards.Apply(compiled.Graph!, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
    }

    [Fact]
    public async Task LoopAndNestedCaptureKeepTheExactProducerConstraintLocation()
    {
        var (plan, catalog) = await Setup();
        var findings = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics;
        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, d => d.Code == "TASK_INPUT_TYPE" && d.Location == "/tasks/consume/inputs/domain");
        Assert.Contains(findings, d => d.Code == "TASK_TRANSFORM_CONSTRAINT" && d.Location == Constraint);
        var scope = TaskPlanRevisions.Scope(plan, findings);
        Assert.Equal(new[] { "/tasks/consume/inputs/domain", Constraint }, scope);
        var repaired = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        Domain(repaired).Enum = ["A", "B"];
        Assert.Empty(TaskPlanRevisions.Validate(plan, repaired, scope));
        var compiled = new TaskPlanCompiler().Compile(repaired, catalog);
        Assert.Empty(compiled.Diagnostics); Assert.Empty(PlanningGeneratedGraph.Validate(compiled.Graph!, catalog));
        PlanningConfirmationGuards.Apply(compiled.Graph!, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
    }

    [Theory]
    [InlineData("objective")]
    [InlineData("insert")]
    [InlineData("unrelated")]
    [InlineData("ordering")]
    [InlineData("nullable")]
    public async Task ScopedFieldRepairsRejectUnrelatedChangesAtomically(string mutation)
    {
        var (plan, catalog) = await Setup();
        var baseline = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var scope = TaskPlanRevisions.Scope(plan, new TaskPlanCompiler().Compile(plan, catalog).Diagnostics);
        var repaired = JsonSerializer.Deserialize(baseline, PlanningJsonContext.Default.TaskPlan)!;
        Domain(repaired).Enum = ["A", "B"];
        switch (mutation)
        {
            case "objective": repaired.Root.Tasks[0].Objective = "Changed intent"; break;
            case "insert": repaired.Root.Tasks[1].Body!.Tasks.Add(new() { Id = "extra", Kind = "value", Objective = "Unrelated task" }); break;
            case "unrelated": repaired.Root.Tasks[0].ResultType!.Fields[0].Type.Items!.Fields[1].Type.Kind = "string"; break;
            case "ordering": repaired.Root.Tasks.Reverse(); break;
            case "nullable": Domain(repaired).Nullable = true; break;
        }
        Assert.Contains(TaskPlanRevisions.Validate(plan, repaired, scope), d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.Equal(baseline, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Fact]
    public async Task BusinessNamesCannotForgeRepairPaths()
    {
        var (plan, catalog) = await Setup();
        plan.Root.Tasks[0].ResultType!.Fields[0].Type.Items!.Fields[0].Name = "domain/type";
        var consumer = plan.Root.Tasks[1].Body!.Tasks[0].Body!.Tasks[0];
        consumer.Inputs[0].Value.Port = "domain/type";
        var diagnostics = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics;
        Assert.DoesNotContain(diagnostics, d => d.Code == "TASK_TRANSFORM_CONSTRAINT");
        Assert.Equal(new[] { "/tasks/consume/inputs/domain" }, TaskPlanRevisions.Scope(plan, diagnostics));
    }
}
