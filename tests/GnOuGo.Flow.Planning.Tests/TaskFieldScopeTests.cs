using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TaskFieldBindingTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskFieldScopeTests
{
    private static TaskValue Input(string name) => new() { Kind = "input", Source = name };
    private static TaskValue Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    private static async Task<PlanningCatalog> Catalog() => await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
    private static TaskType Record() => new() { Kind = "object", Fields = [new() { Name = "number", Type = new() { Kind = "integer" } }] };
    private static async Task<JsonNode?> Execute(TaskPlan plan, JsonObject inputs)
    {
        var catalog = await Catalog();
        var result = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(result.Diagnostics);
        Assert.Empty(PlanningGeneratedGraph.Validate(result.Graph!, catalog)); Assert.Empty(PlanningExecutableValidation.Validate(result.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(result.Graph!, catalog);
        var runtime = new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, []), PlannerFixture.Ct));
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var run = await new WorkflowEngine().ExecuteAsync(doc.Workflows[doc.Entrypoint!], inputs, PlannerFixture.Ct);
        Assert.True(run.Success, run.Error?.Message); return run.Outputs!["result"];
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LoopRecordsPreserveOrderAndTypedFieldsAcrossReusableGroups(bool parallel, bool empty)
    {
        var plan = new TaskPlan
        {
            Inputs = [new() { Name = "records", Type = new() { Kind = "array", Items = Record() } }],
            Groups = [new() { Id = "extract", Inputs = [new() { Name = "record", Type = Record() }], Body = new() { Outputs = [new("number", Field(Input("record"), "number"))] } }],
            Root = new() { Tasks = [new() { Id = "loop", Kind = "foreach", Objective = "Read each record", Parallel = parallel, MaxConcurrency = 2,
                Items = Input("records"), Body = new() { Tasks = [new() { Id = "call", Kind = "call", Objective = "Read one record", Group = "extract", Inputs = [new("record", new() { Kind = "item" })] }],
                    Outputs = [new("values", Output("call", "number"))] } }], Outputs = [new("result", Output("loop", "values"))] }
        };
        var records = empty ? new JsonArray() : new JsonArray(new JsonObject { ["number"] = 41 }, new JsonObject { ["number"] = 7 }, new JsonObject { ["number"] = 23 });
        var result = await Execute(plan, new() { ["records"] = records });
        Assert.Equal(empty ? [] : new[] { 41, 7, 23 }, result!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Theory]
    [InlineData("a.b/é")]
    [InlineData("line['literal']")]
    public async Task NestedSelectionTreatsNamesLiterally(string name)
    {
        var plan = new TaskPlan { Inputs = [new() { Name = "record", Type = new() { Kind = "object", Fields = [new() { Name = name, Type = Record() }] } }],
            Root = new() { Outputs = [new("result", Field(Field(Input("record"), name), "number"))] } };
        Assert.Equal(31, (await Execute(plan, new() { ["record"] = new JsonObject { [name] = new JsonObject { ["number"] = 31 } } }))!.GetValue<int>());
    }

    [Fact]
    public async Task OptionalSelectionIsEvaluatedOnlyInTheChosenBranch()
    {
        var record = Record(); record.Fields[0].Required = false;
        var plan = new TaskPlan { Inputs = [new() { Name = "record", Type = record }], Root = new() { Tasks = [new()
        {
            Id = "choose", Kind = "conditional", Objective = "Read only when selected", Condition = new() { Kind = "boolean", Boolean = false },
            Body = new() { Outputs = [new("number", Field(Input("record"), "number"))] },
            Otherwise = new() { Outputs = [new("number", new() { Kind = "number", Number = 9 })] }
        }], Outputs = [new("result", Output("choose", "number"))] } };
        Assert.Equal(9, (await Execute(plan, new() { ["record"] = new JsonObject() }))!.GetValue<int>());
    }

    [Fact]
    public async Task FieldsSelectDeclaredBusinessOutputsAndLiteralObjectsWithoutInterpretation()
    {
        var record = new TaskValue { Kind = "object", Members = [new("number", new() { Kind = "number", Number = 31 })] };
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "assemble", Kind = "value", Objective = "Assemble a record", Outputs = [new("record", record)] }],
            Outputs = [new("result", Field(Output("assemble", "record"), "number"))] } };
        Assert.Equal(31, (await Execute(plan, new()))!.GetValue<int>());
        plan.Root.Tasks.Clear(); plan.Root.Outputs[0] = new("result", Field(record, "number"));
        Assert.Equal(31, (await Execute(plan, new()))!.GetValue<int>());
    }

    [Fact]
    public async Task NestedIterationUsesItsOwnTypedItemAndPreservesCollectionOrder()
    {
        var plan = new TaskPlan { Inputs = [new() { Name = "records", Type = new() { Kind = "array", Items = new() { Kind = "object", Fields =
            [new() { Name = "children", Type = new() { Kind = "array", Items = Record() } }] } } }], Root = new() { Tasks = [new()
        {
            Id = "outer", Kind = "foreach", Objective = "Visit parent records", Items = Input("records"), Body = new() { Tasks = [new()
            {
                Id = "inner", Kind = "foreach", Objective = "Visit child records", Items = Field(new() { Kind = "item" }, "children"),
                Body = new() { Outputs = [new("values", Field(new() { Kind = "item" }, "number"))] }
            }], Outputs = [new("groups", Output("inner", "values"))] }
        }], Outputs = [new("result", Output("outer", "groups"))] } };
        var result = await Execute(plan, JsonNode.Parse("""{"records":[{"children":[{"number":3},{"number":8}]},{"children":[]},{"children":[{"number":5}]}]}""")!.AsObject());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[[3,8],[],[5]]"), result));
    }

    [Fact]
    public async Task FieldPolicyOpacityAndApprovalStayFailClosed()
    {
        var plan = new TaskPlan { Inputs = [new() { Name = "record", Type = Record() }], Root = new() { Outputs = [new("result", Field(Input("record"), "number"))] } };
        plan.Inputs[0].Type.Fields.Add(new() { Name = "other", Type = new() { Kind = "integer" } });
        var catalog = await Catalog(); catalog.AllowedStepTypes.Remove("value.project");
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Contains(result.Diagnostics, d => d.Code == "TASK_OUTPUT_POLICY"); Assert.Null(result.Graph);
        catalog.AllowedStepTypes.Add("value.project");
        var graph = new TaskPlanCompiler().Compile(plan, catalog).Graph!;
        var session = PlannerFixture.Session(); session.Requirements = PlannerFixture.Requirements(); session.Requirements.Inputs = plan.Inputs; session.Plan = plan; session.Catalog = catalog; session.Graph = graph;
        session.Yaml = new PlanningGraphCompiler().Compile(graph, catalog, session.Request.Name);
        PlanningArtifactApproval.Verify(session); var hash = PlanningArtifactApproval.Hash(session);
        plan.Root.Outputs[0].Value.Port = "other";
        Assert.NotEqual(hash, PlanningArtifactApproval.Hash(session)); Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(session));
        plan.Root.Outputs[0] = new("result", Field(Input("record"), "number")); plan.Inputs[0].Type.Fields[0].Type = new() { Kind = "any" };
        result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_OUTPUT_CONTRACT");
    }

    [Fact]
    public void StrictSchemaAndCompactTransportPreserveExplicitFieldSelections()
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        var schema = PlanningSchemas.Proposal(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var plan = new TaskPlan { Inputs = [new() { Name = "record", Type = Record() }], Root = new() { Outputs = [new("result", Field(Input("record"), "number"))] } };
        var wire = TestRuntime.Response(new() { StructuredOutputSchema = schema }, new() { Plan = plan }).Json!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(wire, schema));
        var restored = wire.Deserialize(PlanningJsonContext.Default.PlanningProposal)!.Plan!;
        Assert.Equal("field", restored.Root.Outputs[0].Value.Kind); Assert.Equal("number", restored.Root.Outputs[0].Value.Port);
        var compact = PlanningJsonTransport.TaskPlanPrompt(plan)!;
        Assert.Equal(3, compact["root"]!["outputs"]![0]!["value"]!.AsObject().Count);
        Assert.Equal(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(compact.Deserialize(PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan));
        var field = wire["plan"]!["root"]!["outputs"]![0]!["value"]!;
        field["items"]!.AsArray().Add(new JsonObject { ["kind"] = "item" });
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(wire, schema));
    }
}
