using GnOuGo.Flow.Core.Planning;
using System.Text.Json;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class AssertionContradictionTests
{
    [Fact]
    public async Task ExplicitlyEmptyRepetitionDoesNotInventAnAssertionFailure()
    {
        TaskValue Input() => new() { Kind = "input", Source = "flag" };
        var plan = new TaskPlan { Inputs = [new() { Name = "flag", Type = new() { Kind = "boolean" } }], Root = new() { Tasks = [new()
        {
            Id = "each", Kind = "foreach", Objective = "Handle the explicitly supplied occurrences", Items = new() { Kind = "array", Items = [] },
            Body = new() { Tasks = [new() { Id = "yes", Kind = "value", Objective = "Check the selected occurrence", Requires = Input() },
                new() { Id = "no", Kind = "value", Objective = "Check the selected occurrence", Requires = new() { Kind = "predicate", Predicate = "not", Items = [Input()] } }] }
        }] } };
        var request = PlannerFixture.Session().Request; request.Options["planning_validation_profile"] = "explicit-assertions-v1";
        Assert.Empty(new TaskPlanCompiler().Compile(plan, await new TestRuntime().DiscoverAsync(new(), PlannerFixture.Ct), request).Diagnostics);
        plan.Root.Tasks[0].Items!.Items.Add(new() { Kind = "boolean", Boolean = true });
        Assert.Contains(new TaskPlanCompiler().Compile(plan, await new TestRuntime().DiscoverAsync(new(), PlannerFixture.Ct), request).Diagnostics,
            d => d.Code == "TASK_GUARD_CONTRADICTION");
    }

    [Theory]
    [InlineData("0", "-0", false, false)]
    [InlineData("1", "1.0", false, false)]
    [InlineData("9007199254740992", "9007199254740993", true, false)]
    [InlineData("9007199254740992", "9007199254740993", false, true)]
    public async Task NumericFactsUseExactTokensAndNumericEquality(string first, string second, bool negate, bool conflict)
    {
        TaskValue Guard(string number, bool unequal) => new() { Kind = "predicate", Predicate = unequal ? "not_equal" : "equal",
            Items = [new() { Kind = "input", Source = "value" }, JsonSerializer.Deserialize("{\"kind\":\"number\",\"number\":" + number + "}", PlanningJsonContext.Default.TaskValue)!] };
        var plan = new TaskPlan { Inputs = [new() { Name = "value", Type = new() { Kind = "number" } }], Root = new() { Tasks = [
            new() { Id = "first", Kind = "value", Objective = "Check the exact number", Requires = Guard(first, false) },
            new() { Id = "second", Kind = "value", Objective = "Check the exact number", Requires = Guard(second, negate) }] } };
        var request = PlannerFixture.Session().Request; request.Options["planning_validation_profile"] = "explicit-assertions-v1";
        var findings = new TaskPlanCompiler().Compile(plan, await new TestRuntime().DiscoverAsync(new(), PlannerFixture.Ct), request).Diagnostics;
        Assert.Equal(conflict, findings.Any(d => d.Code == "TASK_GUARD_CONTRADICTION"));
        Assert.All(findings, d => Assert.Equal("TASK_GUARD_CONTRADICTION", d.Code));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitlyUnreachableAlternativesDoNotInventAnAssertionFailure(bool literal)
    {
        TaskValue Input() => new() { Kind = "input", Source = "flag" };
        var plan = new TaskPlan { Inputs = [new() { Name = "flag", Type = new() { Kind = "boolean" } }], Root = new() { Tasks = [new()
        {
            Id = "route", Kind = "conditional", Objective = "Retain an explicit unused alternative",
            Condition = literal ? new() { Kind = "boolean", Boolean = false } : new() { Kind = "predicate", Predicate = "and", Items = [Input(), new() { Kind = "predicate", Predicate = "not", Items = [Input()] }] },
            Body = new() { Tasks = [new() { Id = "yes", Kind = "value", Objective = "Check the selected alternative", Requires = Input() },
                new() { Id = "no", Kind = "value", Objective = "Check the selected alternative", Requires = new() { Kind = "predicate", Predicate = "not", Items = [Input()] } }] }, Otherwise = new()
        }] } };
        var request = PlannerFixture.Session().Request; request.Options["planning_validation_profile"] = "explicit-assertions-v1";
        Assert.Empty(new TaskPlanCompiler().Compile(plan, await new TestRuntime().DiscoverAsync(new(), PlannerFixture.Ct), request).Diagnostics);
    }
    [Theory]
    [InlineData("boolean")]
    [InlineData("literal")]
    [InlineData("null")]
    [InlineData("branch")]
    public async Task FreshValidationDiagnosesExplicitContradictionsAndHistoricalCompilationRemainsAvailable(string variant)
    {
        var catalog = await new TestRuntime().DiscoverAsync(new(), PlannerFixture.Ct);
        TaskValue Input() => new() { Kind = "input", Source = "value" };
        TaskValue Equal(TaskValue value) => new() { Kind = "predicate", Predicate = "equal", Items = [Input(), value] };
        var first = variant is "boolean" or "branch" ? Input() : Equal(new() { Kind = "string", Text = "a" });
        var second = variant is "boolean" or "branch" ? new TaskValue { Kind = "predicate", Predicate = "not", Items = [Input()] }
            : Equal(variant == "null" ? new() { Kind = "null" } : new() { Kind = "string", Text = "b" });
        PlanTask AssertAt(string id, TaskValue value) => new() { Id = id, Kind = "value", Objective = "Validate the supplied value", Requires = value,
            Outputs = [new("result", new() { Kind = "boolean", Boolean = true })] };
        var plan = new TaskPlan { Inputs = [new() { Name = "value", Type = new() { Kind = variant is "boolean" or "branch" ? "boolean" : "string", Nullable = variant == "null" } }],
            Root = new() { Tasks = [AssertAt("first", first), AssertAt("second", second)] } };
        if (variant == "branch") plan.Root.Tasks = [new() { Id = "route", Kind = "conditional", Objective = "Select work", Condition = first, Body = new() { Tasks = [plan.Root.Tasks[1]] }, Otherwise = new() }];
        var request = PlannerFixture.HistoricalSession().Request;
        Assert.Empty(new TaskPlanCompiler().Compile(plan, catalog, request).Diagnostics);
        request.Options["planning_validation_profile"] = "explicit-assertions-v1";
        var diagnostic = Assert.Single(new TaskPlanCompiler().Compile(plan, catalog, request).Diagnostics);
        Assert.Equal("TASK_GUARD_CONTRADICTION", diagnostic.Code);
        Assert.Equal("/tasks/second/requires", diagnostic.Location);
        Assert.Contains(variant == "branch" ? "/tasks/route/condition" : "/tasks/first/requires", diagnostic.Message);
        Assert.Contains("/inputs/value", diagnostic.Message);
        plan.Root.Tasks = [new() { Id = "route", Kind = "conditional", Objective = "Select alternatives", Condition = first,
            Body = new() { Tasks = [AssertAt("first", first)] }, Otherwise = new() { Tasks = [AssertAt("second", second)] } }];
        Assert.Empty(new TaskPlanCompiler().Compile(plan, catalog, request).Diagnostics);
        plan.Root.Tasks.Insert(0, AssertAt("before", first));
        Assert.Empty(new TaskPlanCompiler().Compile(plan, catalog, request).Diagnostics);
    }
}
