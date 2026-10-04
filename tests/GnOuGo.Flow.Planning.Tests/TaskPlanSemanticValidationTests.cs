using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskPlanSemanticValidationTests
{
    [Theory]
    [InlineData("sequence", false)]
    [InlineData("foreach", false)]
    [InlineData("foreach", true)]
    [InlineData("conditional", false)]
    [InlineData("conditional", true)]
    [InlineData("parallel", false)]
    [InlineData("call", false)]
    public void ConsumingAScopeWithoutExportsDoesNotManufactureAnEmptyResult(string kind, bool alternate)
    {
        TaskScope Body(string id) => new() { Tasks = [new() { Id = id, Kind = "value", Objective = "Produce a business value",
            Outputs = [new("payload", PlanningCorpus.Number(42))] }] };
        var body = Body("source_alpha");
        var task = new PlanTask { Id = "bounded_work", Kind = kind, Objective = "Perform scoped work" };
        var plan = new TaskPlan { Root = new() { Tasks = [task] } };
        if (kind == "call") { task.Group = "group_alpha"; plan.Groups.Add(new() { Id = task.Group, Body = body }); }
        else if (kind == "parallel") task.Branches = [body, Body("source_beta")];
        else task.Body = body;
        if (kind == "conditional") { task.Condition = new() { Kind = "boolean", Boolean = alternate }; task.Otherwise = Body("source_gamma"); }
        if (kind == "foreach") { task.Items = new() { Kind = "array", Items = [PlanningCorpus.Number(1)] }; task.Parallel = alternate; }
        var consumer = new PlanTask { Id = "consumer_delta", Kind = "value", Objective = "Consume scoped data",
            Outputs = [new("result", PlanningCorpus.Business("output", task.Id))] };
        plan.Root.Tasks.Add(consumer);
        var result = new TaskPlanCompiler().Compile(plan, new());
        Assert.Null(result.Graph); Assert.Empty(result.Sources);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_EXPORT_REQUIRED" && d.Location == "/tasks/consumer_delta/outputs/result");

        // A scope used only for ordering/effects remains valid, including presence checks.
        consumer.Outputs = [new("result", new() { Kind = "present", Source = task.Id })]; consumer.DependsOn = [task.Id];
        Assert.Empty(new TaskPlanCompiler().Compile(plan, new()).Diagnostics);

        // Selecting exports is explicit business intent, never inferred from the last task.
        body.Outputs = [new("alpha", PlanningCorpus.Business("output", "source_alpha", "payload"))];
        if (task.Otherwise is { } other) other.Outputs = [new("alpha", PlanningCorpus.Business("output", "source_gamma", "payload"))];
        if (kind == "parallel") task.Branches[1].Outputs = [new("beta", PlanningCorpus.Business("output", "source_beta", "payload"))];
        consumer.Outputs = [new("result", PlanningCorpus.Business("output", task.Id))];
        Assert.Empty(new TaskPlanCompiler().Compile(plan, new()).Diagnostics);
    }

    [Fact]
    public void DuplicateChoiceIdentitiesFailClosedWithoutThrowing()
    {
        var plan = PlanningCorpus.Decision();
        plan.Choices[0].Selected = plan.Choices[0].Recommended;
        plan.Choices.Add(plan.Choices[0]);
        var result = new TaskPlanCompiler().Compile(plan, new());
        Assert.Null(result.Graph); Assert.Empty(result.Sources);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_IDENTITY_INVALID");
    }

    [Fact]
    public void IndependentNestedReferencesAndGroupInputsAreAllReportedBeforeLowering()
    {
        var plan = PlanningCorpus.Greeting();
        plan.Inputs.Add(new() { Name = "optional", Required = false });
        plan.Groups.Add(new() { Id = "group", Inputs = [new() { Name = "optional", Required = false }],
            Body = new() { Outputs = [new("missing", PlanningCorpus.Business("output", "missing_group", "value"))] } });
        plan.Root.Outputs.Add(new("object", new() { Kind = "object", Members = [new("first", PlanningCorpus.Business("output", "missing_a", "value")), new("second", PlanningCorpus.Business("output", "missing_b", "value"))] }));
        plan.Root.Outputs.Add(new("array", new() { Kind = "array", Items = [PlanningCorpus.Business("output", "missing_c", "value"), PlanningCorpus.Business("output", "missing_d", "value")] }));
        var result = new TaskPlanCompiler().Compile(plan, new());
        Assert.Null(result.Graph); Assert.Empty(result.Sources);
        Assert.Equal(5, result.Diagnostics.Count(d => d.Code == "TASK_REFERENCE_UNKNOWN"));
        Assert.Equal(new[] { "/groups/group/inputs/optional", "/inputs/optional" }, result.Diagnostics.Where(d => d.Code == "TASK_DEFAULT_REQUIRED").Select(d => d.Location));
    }

    [Fact]
    public void InvalidProducerSuppressesDependentTypeErrorsButNotIndependentReferences()
    {
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "producer", Objective = "Unavailable operation", Operation = "unknown" },
            new() { Id = "consumer", Kind = "value", Objective = "Use producer", Outputs = [new("value", PlanningCorpus.Business("output", "producer", "missing"))] }],
            Outputs = [new("dependent", PlanningCorpus.Business("output", "consumer", "value")), new("unrelated", PlanningCorpus.Business("output", "absent", "value"))] } };
        var result = new TaskPlanCompiler().Compile(plan, new());
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_OPERATION_UNKNOWN");
        Assert.Contains(result.Diagnostics, d => d.Location == "/root/outputs/unrelated");
    }

    [Fact]
    public void DuplicateIdentitiesAndCyclesFailAtSemanticLocations()
    {
        var plan = PlanningCorpus.Greeting();
        plan.Root.Tasks.Add(new() { Id = "greet", Kind = "value", Objective = "Duplicate" });
        plan.Root.Tasks.Add(new() { Id = "cycle", Kind = "value", Objective = "Cycle", DependsOn = ["cycle"] });
        var result = new TaskPlanCompiler().Compile(plan, new());
        Assert.Null(result.Graph); Assert.Empty(result.Sources);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_IDENTITY_INVALID" && d.Location == "/tasks/greet/id");
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_DEPENDENCY_CYCLE" && d.Location == "/tasks/cycle/dependsOn");
    }

    [Fact]
    public void ParallelSiblingReferencesDoNotGrantExportPermissions()
    {
        var left = PlanningCorpus.Greeting().Root;
        var right = new TaskScope { Tasks = [new() { Id = "other", Kind = "value", Objective = "Invalid sibling read", Outputs = [new("value", PlanningCorpus.Business("output", "greet", "message"))] }] };
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "parallel", Kind = "parallel", Objective = "Concurrent work", Branches = [left, right] }] } };
        var result = new TaskPlanCompiler().Compile(plan, new());
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_REFERENCE_UNKNOWN");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "TASK_EXPORT_REQUIRED");
        Assert.Empty(result.Sources);
    }
}
