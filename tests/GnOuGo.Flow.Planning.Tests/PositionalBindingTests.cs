using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Scripting;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PositionalBindingTests
{
    private static TaskValue At(TaskValue list, TaskValue index) => new() { Kind = "at_index", Items = [list, index] };

    private static TaskPlan Plan() => new()
    {
        Inputs = [
            new() { Name = "observations", Type = LookupCompilationTests.ArrayOf(new() { Kind = "string" }) },
            new() { Name = "flags", Type = LookupCompilationTests.ArrayOf(ProductTransformationPlan.Obj(("flag", new() { Kind = "boolean" }))) }
        ],
        Root = new()
        {
            Tasks = [new()
            {
                Id = "ordered", Kind = "foreach", Objective = "Attach the corresponding checked decision to each original observation",
                Items = LookupCompilationTests.Input("observations"),
                Body = new() { Outputs = [
                    new("flag", TaskArtifactBindingTests.Field(At(LookupCompilationTests.Input("flags"), new() { Kind = "index" }), "flag")),
                    new("observation", new() { Kind = "item" })
                ] }
            }],
            Outputs = [new("flags", ProductTransformationPlan.Ref("ordered", "flag")), new("observations", ProductTransformationPlan.Ref("ordered", "observation"))]
        }
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ParallelArraysUseTheIterationIndexAndRetainOrder(bool shortArray, bool compact)
    {
        var state = PlannerFixture.Session();
        var definitions = PlanningSchemas.TaskPlanGrammar(state)["$defs"]!.AsObject();
        var grammar = definitions["value"]!.DeepClone().AsObject();
        grammar["$defs"] = definitions.DeepClone();
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""{"kind":"at_index","items":[{"kind":"input","source":"flags"},{"kind":"index"}]}"""), grammar));
        var engine = new WorkflowEngine();
        state.Catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Request.Options["compilation_profile"] = compact ? TaskPlanCompiler.CompactProfile : "compact-bindings-v2";
        var compilation = new TaskPlanCompiler().Compile(Plan(), state.Catalog, state.Request);
        Assert.Empty(compilation.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compilation.Graph!, state.Catalog));
        var compiler = new PlanningGraphCompiler();
        var yaml = compact
            ? compiler.Compile(compilation.Graph!, state.Catalog, "generated", descriptions: true, fuseBindings: true,
                consumerBindings: true, directProjections: true, nativeMappings: true, readableMappings: true, leanMappings: true,
                compactProjections: true, inlineConsumers: true, directValues: true, checkedControls: true, readableBranches: true, simplifiedLocals: true)
            : compiler.Compile(compilation.Graph!, state.Catalog);
        var inputs = new JsonObject { ["observations"] = new JsonArray("repeat", "repeat", "last"),
            ["flags"] = JsonNode.Parse(shortArray ? """[{"flag":false}]""" : """[{"flag":false},{"flag":true},{"flag":false}]""") };
        var result = await LookupCompilationTests.Execute(yaml, inputs, engine);
        if (shortArray) { Assert.False(result.Success); Assert.Null(result.Outputs); return; }
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("[false,true,false]", result.Outputs!["flags"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(inputs["observations"], result.Outputs["observations"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTheOriginalArrayCanEstablishArtifactOwnership(bool mixed)
    {
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        var original = TaskArtifactBindingTests.Reference("make", "handle");
        var data = new TaskValue { Kind = "array", Items = [original, mixed ? new() { Kind = "string", Text = "unverified" } : original] };
        plan.Root.Tasks[1].Inputs[0] = new("location", At(data, new() { Kind = "number", Number = 0 }));
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        if (mixed) { Assert.Contains(compiled.Diagnostics, x => x.Code == "TASK_ARTIFACT_BINDING"); return; }
        Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
    }

    [Theory]
    [InlineData(0d, true)]
    [InlineData(0.5d, false)]
    [InlineData(9007199254740992d, false)]
    public async Task LiteralIterationPositionsKeepTheirCheckedType(double last, bool integral)
    {
        var plan = Plan();
        var loop = plan.Root.Tasks.Single();
        loop.Items = new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }, new() { Kind = "number", Number = last }] };
        loop.Body!.Outputs[1] = new("observation", At(LookupCompilationTests.Input("observations"), new() { Kind = "item" }));
        var state = PlannerFixture.Session();
        var engine = new WorkflowEngine();
        var catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).DiscoverAsync(state.Request, PlannerFixture.Ct);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, state.Request);
        if (!integral)
        {
            Assert.Contains(compiled.Diagnostics, diagnostic => diagnostic.Code == "TASK_INDEX_INVALID");
            return;
        }
        Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        var result = await LookupCompilationTests.Execute(yaml, JsonNode.Parse("""{"observations":["first","second"],"flags":[{"flag":true},{"flag":false}]}""")!.AsObject(), engine);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("""["second","first"]""", result.Outputs!["observations"]!.ToJsonString());
    }

    [Theory]
    [InlineData("string", null)]
    [InlineData("number", -1d)]
    [InlineData("number", 1.25d)]
    public async Task InvalidIndexContractsFailCompilation(string kind, double? literal)
    {
        var state = PlannerFixture.Session();
        var plan = Plan();
        var reference = plan.Root.Tasks[0].Body!.Outputs[0].Value.Items[0];
        reference.Items[1] = new() { Kind = kind, Number = literal, Text = kind == "string" ? "1" : null };
        var catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_INDEX_INVALID");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.00000000000000000001")]
    [InlineData("2")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    public void InvalidPositionsFailInTheCheckedHelper(string index)
    {
        const string expression = "m.atIndex(source.rows,source.index)";
        Assert.Throws<WorkflowRuntimeException>(() => JintSandbox.ValidateMapping(expression));
        var values = JsonNode.Parse("""{"rows":[null,{"number":9007199254740993}]}""")!.AsObject();
        values["index"] = JsonNode.Parse(index);
        var sandbox = new JintSandbox();
        Assert.Equal("CONTRACT_UNSATISFIED", Assert.Throws<WorkflowRuntimeException>(() => sandbox.ExecuteMapping(expression, values, PlannerFixture.Ct)).Code);
        values["index"] = 0;
        Assert.Null(sandbox.ExecuteMapping(expression, values, PlannerFixture.Ct));
        values["index"] = 1;
        Assert.Equal(values["rows"]![1]!.ToJsonString(), sandbox.ExecuteMapping(expression, values, PlannerFixture.Ct)!.ToJsonString());
    }
}
