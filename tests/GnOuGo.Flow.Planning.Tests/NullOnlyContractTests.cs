using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class NullOnlyContractTests
{
    private static JsonObject NullSchema(bool arrayType) => new() { ["type"] = arrayType ? new JsonArray("null") : JsonValue.Create("null"), ["description"] = "No continuation", ["title"] = "Terminal value" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullOnlyConversionKeepsTheWholeContractAndDerivesNullability(bool arrayType)
    {
        var schema = NullSchema(arrayType);
        var lowered = PlanningGraphCompiler.ToFlowSchema(schema);
        Assert.Equal("any", lowered["type"]!.GetValue<string>());
        Assert.True(lowered["nullable"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(schema, lowered["schema"]));
        lowered["schema"]!["description"] = "changed copy";
        Assert.Equal("No continuation", schema["description"]!.GetValue<string>());
        // A null type does not bypass other authoritative constraints, even an empty domain.
        schema["enum"] = new JsonArray("unreachable");
        lowered = PlanningGraphCompiler.ToFlowSchema(schema);
        Assert.False(lowered["nullable"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(schema, lowered["schema"]));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(null, lowered["schema"]!));
    }

    [Theory]
    [InlineData(false, "root")]
    [InlineData(true, "root")]
    [InlineData(false, "property")]
    [InlineData(true, "property")]
    [InlineData(false, "items")]
    [InlineData(true, "items")]
    [InlineData(false, "dictionary")]
    [InlineData(true, "dictionary")]
    public async Task AuthoritativeNullContractsSurviveInputAndOutputBoundaries(bool arrayType, string position)
    {
        var leaf = NullSchema(arrayType);
        var schema = position switch
        {
            "property" => new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["entry"] = leaf.DeepClone() }, ["required"] = new JsonArray("entry") },
            "items" => new JsonObject { ["type"] = "array", ["items"] = leaf.DeepClone() },
            "dictionary" => new JsonObject { ["type"] = "object", ["additionalProperties"] = leaf.DeepClone() },
            _ => leaf.DeepClone().AsObject()
        };
        var runtime = new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var graph = new PlanningGraph { Workflows = [new() { Key = "main",
            Inputs = [new() { Name = "payload", Required = true, Schema = new() { Contract = schema.DeepClone().AsObject() } }],
            Steps = [new() { Key = "noop", Type = "set", Input = new() { Kind = "object" } }],
            Outputs = [new() { Name = "result", Value = new() { Kind = "input", Source = "payload" }, Schema = new() { Contract = schema } }] }] };
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, []), PlannerFixture.Ct));
        var parsed = WorkflowParser.Parse(yaml); Assert.Empty(new WorkflowValidator().Validate(parsed));
        JsonNode? Leaf(JsonNode node) => position switch { "property" => node["properties"]!["entry"], "items" => node["items"], "dictionary" => node["additionalProperties"], _ => node };
        Assert.True(JsonNode.DeepEquals(leaf, Leaf(JsonSchemaConverter.InputDefToSchema(parsed.Workflows["main"].Inputs!["payload"]))));
        Assert.True(JsonNode.DeepEquals(leaf, Leaf(JsonSchemaConverter.OutputDefToSchema(parsed.Workflows["main"].Outputs!["result"]))));
        var valid = position switch { "property" => "{\"entry\":null}", "items" => "[null,null]", "dictionary" => "{\"first\":null,\"second\":null}", _ => "null" };
        var invalid = position switch { "property" => "{\"entry\":\"unexpected\"}", "items" => "[null,42]", "dictionary" => "{\"first\":true}", _ => "\"unexpected\"" };
        var document = new WorkflowCompiler().Compile(parsed);
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], new JsonObject { ["payload"] = JsonNode.Parse(valid) }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.True(result.Outputs!.AsObject().ContainsKey("result"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(valid), result.Outputs!["result"]));
        var rejected = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => new WorkflowEngine().ExecuteAsync(document.Workflows["main"], new JsonObject { ["payload"] = JsonNode.Parse(invalid) }, PlannerFixture.Ct));
        Assert.Equal(ErrorCodes.InputValidation, rejected.Code);
        // Bypass no input checks: a wrong output must independently fail its exact contract.
        parsed.Workflows["main"].Outputs!["result"].Expr = "${" + invalid + "}";
        var wrong = new WorkflowCompiler().Compile(parsed);
        Assert.False((await new WorkflowEngine().ExecuteAsync(wrong.Workflows["main"], new JsonObject { ["payload"] = JsonNode.Parse(valid) }, PlannerFixture.Ct)).Success);
        if (position is "property" or "root")
        {
            var missing = position == "root" ? new JsonObject() : new JsonObject { ["payload"] = new JsonObject() };
            Assert.Equal(ErrorCodes.InputValidation, (await Assert.ThrowsAsync<WorkflowRuntimeException>(() => new WorkflowEngine().ExecuteAsync(document.Workflows["main"], missing, PlannerFixture.Ct))).Code);
        }
    }

    private static TaskValue Input(string name) => new() { Kind = "input", Source = name };
    private static TaskValue Output(string id) => new() { Kind = "output", Source = id, Port = "cursor" };
    private static TaskScope Export(TaskValue value) => new() { Outputs = [new("cursor", value)] };
    private static TaskPlan Plan(string shape)
    {
        var plan = new TaskPlan();
        switch (shape)
        {
            case "root": plan.Root = Export(new()); break;
            case "conditional":
                plan.Inputs = [new() { Name = "selected", Type = new() { Kind = "boolean" } }, new() { Name = "candidate", Type = new() { Kind = "string", Nullable = true } }];
                plan.Root.Tasks.Add(new() { Id = "choose", Kind = "conditional", Objective = "Select a continuation", Condition = Input("selected"), Body = Export(Input("candidate")), Otherwise = Export(new()) });
                plan.Root.Outputs.Add(new("cursor", Output("choose"))); break;
            case "capture":
                plan.Root.Tasks.Add(new() { Id = "terminal", Kind = "value", Objective = "Declare no continuation", Outputs = [new("cursor", new())] });
                plan.Root.Tasks.Add(new() { Id = "nested", Kind = "sequence", Objective = "Capture the declared result", Body = Export(Output("terminal")) });
                plan.Root.Outputs.Add(new("cursor", Output("nested"))); break;
            case "group":
                plan.Groups.Add(new() { Id = "terminal_group", Body = Export(new()) });
                plan.Root.Tasks.Add(new() { Id = "invoke", Kind = "call", Objective = "Return the group result", Group = "terminal_group" });
                plan.Root.Outputs.Add(new("cursor", Output("invoke"))); break;
            case "iteration":
                plan.Inputs.Add(new() { Name = "records", Type = new() { Kind = "array", Items = new() { Kind = "string" } } });
                plan.Root.Tasks.Add(new() { Id = "iterate", Kind = "foreach", Objective = "Return one terminal result per item", MaxItems = 3, MaxConcurrency = 1, Items = Input("records"), Body = Export(new()) });
                plan.Root.Outputs.Add(new("cursor", Output("iterate"))); break;
        }
        return plan;
    }

    [Theory]
    [InlineData("root")]
    [InlineData("conditional")]
    [InlineData("capture")]
    [InlineData("group")]
    [InlineData("iteration")]
    public async Task SemanticNullValuesSurviveScopesSerializationAndExecution(string shape)
    {
        var runtime = new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = Plan(shape);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, []), PlannerFixture.Ct));
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        Assert.Equal(yaml, new PlanningGraphCompiler().Compile(new TaskPlanCompiler().Compile(copy, catalog).Graph!, catalog));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var selected in new[] { false, true })
        {
            var input = shape switch
            {
                "conditional" => new JsonObject { ["selected"] = selected, ["candidate"] = "page-two" },
                "iteration" => new JsonObject { ["records"] = selected ? new JsonArray("first", "second") : new JsonArray() },
                _ => new JsonObject()
            };
            var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], input, PlannerFixture.Ct);
            Assert.True(result.Success, result.Error?.Message); Assert.True(result.Outputs!.AsObject().ContainsKey("cursor"));
            var expected = shape == "iteration" ? (JsonNode)(selected ? new JsonArray((JsonNode?)null, null) : new JsonArray())
                : shape == "conditional" && selected ? JsonValue.Create("page-two") : null;
            Assert.True(JsonNode.DeepEquals(expected, result.Outputs!["cursor"]), result.Outputs!.ToJsonString());
        }
    }

    [Fact]
    public void NullCannotSatisfyANonNullableBusinessInput()
    {
        var plan = new TaskPlan { Groups = [new() { Id = "requires_text", Inputs = [new() { Name = "text", Type = new() { Kind = "string" } }], Body = Export(Input("text")) }],
            Root = new() { Tasks = [new() { Id = "consume", Kind = "call", Group = "requires_text", Objective = "Consume declared text", Inputs = [new("text", new())] }] } };
        var compilation = new TaskPlanCompiler().Compile(plan, new());
        Assert.Null(compilation.Graph);
        Assert.Contains(compilation.Diagnostics, d => d.Code == "TASK_GROUP_INPUT_TYPE" && d.Location == "/tasks/consume/inputs/text");
    }

    [Theory]
    [InlineData("direct", true)]
    [InlineData("and", true)]
    [InlineData("not_or", true)]
    [InlineData("or_false", true)]
    [InlineData("or", false)]
    [InlineData("unrelated", false)]
    [InlineData("mixed", false)]
    [InlineData("null_only", false)]
    public async Task NullGuardsRefineConditionalExportsOnlyOnGuaranteedPaths(string guard, bool accepted)
    {
        var engine = new WorkflowEngine();
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var plan = Plan("conditional");
        plan.Inputs.Single(i => i.Name == "candidate").Type.Enum = ["observed"];
        if (guard == "mixed") plan.Root.Tasks[0].Otherwise = Export(new() { Kind = "number", Number = 12 });
        if (guard == "null_only") plan.Root.Tasks[0].Body = Export(new());
        plan.Inputs.Add(new() { Name = "enabled", Type = new() { Kind = "boolean" } });
        plan.Groups.Add(new() { Id = "require_text", Inputs = [new() { Name = "text", Type = new() { Kind = "string", Enum = ["observed"] } }], Body = Export(Input("text")) });
        TaskValue Predicate(string op, params TaskValue[] values) => new() { Kind = "predicate", Predicate = op, Items = [.. values] };
        var nonnull = Predicate("not_equal", Output("choose"), new());
        var absent = Predicate("equal", Output("choose"), new());
        var condition = guard switch
        {
            "and" => Predicate("and", Input("enabled"), nonnull),
            "not_or" => Predicate("not", Predicate("or", absent, Predicate("not", Input("enabled")))),
            "or_false" => Predicate("or", absent, Predicate("not", Input("enabled"))),
            "or" => Predicate("or", Input("enabled"), nonnull),
            "unrelated" => Input("enabled"),
            _ => nonnull
        };
        var consume = new TaskScope { Tasks = [new() { Id = "consume", Kind = "call", Group = "require_text", Objective = "Consume only the observed nonnull value", Inputs = [new("text", Output("choose"))] }],
            Outputs = [new("cursor", Output("consume"))] };
        plan.Root.Tasks.Add(new() { Id = "guard", Kind = "conditional", Objective = "Guard the nullable conditional export", Condition = condition,
            Body = guard == "or_false" ? Export(new()) : consume, Otherwise = guard == "or_false" ? consume : Export(new()) });
        plan.Root.Outputs = [new("cursor", Output("guard"))];
        var original = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Equal(original, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        if (!accepted) { Assert.Contains(compilation.Diagnostics, d => d.Code == "TASK_GROUP_INPUT_TYPE"); return; }
        Assert.Empty(compilation.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compilation.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        Assert.DoesNotContain("llm.call", yaml); Assert.DoesNotContain("mapping.dynamic", yaml);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var selected in new[] { true, false })
            foreach (var enabled in new[] { true, false })
                foreach (var observed in new string?[] { "observed", null })
                {
                    var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!],
                        new JsonObject { ["selected"] = selected, ["enabled"] = enabled, ["candidate"] = observed }, PlannerFixture.Ct);
                    Assert.True(result.Success, result.Error?.Message);
                    Assert.Equal(selected && (guard == "direct" || enabled) ? observed : null, result.Outputs!["cursor"]?.GetValue<string>());
                }
    }
}
