using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CollectionLengthTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task ExactObservedCardinalityGuardsDelivery(int count, bool expected)
    {
        var state = PlannerFixture.Session();
        var defs = PlanningSchemas.TaskPlanGrammar(state)["$defs"]!.AsObject();
        var grammar = defs["guard"]!.DeepClone().AsObject();
        grammar["$defs"] = defs.DeepClone();
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""
            {"kind":"predicate","predicate":"equal","items":[{"kind":"array_length","items":[{"kind":"output","source":"collect","port":"rows"}]},{"kind":"number","number":2}]}
            """), grammar));

        var observations = ProductTransformationPlan.Ref("collect", "rows");
        var length = new TaskValue { Kind = "array_length", Items = [observations] };
        var plan = new TaskPlan { Inputs = [new() { Name = "rows", Type = LookupCompilationTests.ArrayOf(new() { Kind = "string" }) }],
            Root = new() { Tasks = [
                new() { Id = "collect", Kind = "foreach", Objective = "Collect each original entry in order.", Items = LookupCompilationTests.Input("rows"),
                    Body = new() { Outputs = [new("rows", new() { Kind = "item" })] } },
                new() { Id = "deliver", Kind = "value", Objective = "Return the rows after checking their actual count.",
                Requires = new() { Kind = "predicate", Predicate = "equal", Items = [length, new() { Kind = "number", Number = 2 }] },
                Outputs = [new("rows", observations)] }], Outputs = [new("rows", ProductTransformationPlan.Ref("deliver", "rows"))] } };
        var engine = new WorkflowEngine();
        var catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).DiscoverAsync(state.Request, PlannerFixture.Ct);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, state.Request);
        Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        var rows = new JsonArray(Enumerable.Repeat("repeated", count).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        var result = await LookupCompilationTests.Execute(yaml, new JsonObject { ["rows"] = rows }, engine);
        Assert.Equal(expected, result.Success);
        if (expected) Assert.True(JsonNode.DeepEquals(rows, result.Outputs!["rows"]));
        else Assert.Null(result.Outputs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullableArrayCountUsesTheRetainedShortCircuitGuard(bool present)
    {
        var state = PlannerFixture.Session();
        var source = LookupCompilationTests.Input("rows");
        var available = new TaskValue { Kind = "predicate", Predicate = "not_equal", Items = [source, new() { Kind = "null" }] };
        var count = new TaskValue { Kind = "array_length", Items = [source] };
        var match = new TaskValue { Kind = "predicate", Predicate = "equal", Items = [count, new() { Kind = "number", Number = 0 }] };
        var type = LookupCompilationTests.ArrayOf(new() { Kind = "string" });
        type.Nullable = true;
        var plan = new TaskPlan { Inputs = [new() { Name = "rows", Type = type }], Root = new() { Outputs = [
            new("empty", new() { Kind = "predicate", Predicate = "and", Items = [available, match] })] } };
        var catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog, state.Request);
        Assert.Empty(compilation.Diagnostics);
        var result = await LookupCompilationTests.Execute(new PlanningGraphCompiler().Compile(compilation.Graph!, catalog),
            new JsonObject { ["rows"] = present ? new JsonArray() : null }, new WorkflowEngine());
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(present, result.Outputs!["empty"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("array", true)]
    [InlineData("string", false)]
    [InlineData("object", false)]
    public async Task LengthRequiresAnEstablishedNonnullableArray(string kind, bool nullable)
    {
        var state = PlannerFixture.Session();
        var plan = new TaskPlan { Inputs = [new() { Name = "source", Type = new() { Kind = kind, Nullable = nullable,
            Items = kind == "array" ? new() { Kind = "string" } : null } }], Root = new() { Outputs = [
                new("count", new() { Kind = "array_length", Items = [new() { Kind = "input", Source = "source" }] })] } };
        var catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_ARRAY_LENGTH_INVALID");
    }
}
