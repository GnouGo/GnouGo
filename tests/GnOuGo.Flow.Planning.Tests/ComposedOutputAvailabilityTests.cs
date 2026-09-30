using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TaskFieldBindingTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ComposedOutputAvailabilityTests
{
    private static TaskType Record => new() { Kind = "object", Fields = [new() { Name = "nested", Type = new() { Kind = "object", Fields =
        [new() { Name = "event", Required = false, Type = new() { Kind = "string", Nullable = true, Enum = ["allow", "deny"] } }] } }] };
    private static TaskValue Input(string name) => new() { Kind = "input", Source = name };
    private static TaskValue Output(string source) => new() { Kind = "output", Source = source, Port = "publication" };
    private static TaskScope Export(TaskValue source, string label) => new() { Outputs = [new("publication", new() { Kind = "object", Members =
        [new("status", new() { Kind = "string", Text = label }), new("events", new() { Kind = "array", Items = [Field(Field(source, "nested"), "event")] }),
         new("nested", new() { Kind = "object", Members = [new("event", Field(Field(source, "nested"), "event"))] })] })] };
    private static TaskPlan Plan(string scope, string id)
    {
        var plan = new TaskPlan { Inputs = [new() { Name = "record", Type = Record }] };
        switch (scope)
        {
            case "root": plan.Root = Export(Input("record"), "root"); break;
            case "conditional":
                plan.Inputs.Add(new() { Name = "selected", Type = new() { Kind = "boolean" } });
                plan.Root.Tasks.Add(new() { Id = id, Kind = "conditional", Objective = "Choose one declared export", Condition = Input("selected"),
                    Body = Export(Input("record"), "first"), Otherwise = Export(Input("record"), "second") });
                plan.Root.Outputs.Add(new("publication", Output(id))); break;
            case "group":
                plan.Groups.Add(new() { Id = "reusable", Inputs = [new() { Name = "record", Type = Record }], Body = Export(Input("record"), "group") });
                plan.Root.Tasks.Add(new() { Id = id, Kind = "call", Group = "reusable", Objective = "Use the declared interface", Inputs = [new("record", Input("record"))] });
                plan.Root.Outputs.Add(new("publication", Output(id))); break;
            case "iteration":
                plan.Inputs = [new() { Name = "records", Type = new() { Kind = "array", Items = Record } }];
                plan.Root.Tasks.Add(new() { Id = id, Kind = "foreach", Objective = "Export records in order", MaxItems = 3, Items = Input("records"),
                    Body = new() { Tasks = [new() { Id = "nested_scope", Kind = "sequence", Objective = "Capture one iteration item", Body = Export(new() { Kind = "item" }, "iteration") }],
                        Outputs = [new("publication", Output("nested_scope"))] } });
                plan.Root.Outputs.Add(new("publication", Output(id))); break;
        }
        return plan;
    }

    [Theory]
    [InlineData("root", "first_id")]
    [InlineData("conditional", "first_id")]
    [InlineData("conditional", "renamed_case")]
    [InlineData("group", "call_id")]
    [InlineData("iteration", "renamed_loop")]
    public async Task ComposedExportsKeepNestedValuesScopeAndIterationOrder(string scope, string id)
    {
        var plan = Plan(scope, id); var runtime = new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics); Assert.NotNull(compiled.Graph);
        var graph = compiled.Graph;
        Assert.Contains(graph.Workflows.SelectMany(w => w.Finally), n => n.Type == "value.project");
        Assert.Contains(graph.Workflows.SelectMany(w => w.Finally), n => n.Type == "set" && n.If is not null);
        Assert.DoesNotContain(graph.Workflows.SelectMany(w => w.Steps.Concat(w.Finally)), n => n.Type is "llm.call" or "mcp.call");
        Assert.True(PlanningExecutableValidation.Validate(graph, catalog).Count == 0,
            string.Join("; ", PlanningExecutableValidation.Validate(graph, catalog).Select(d => compiled.Locate(d).Location + ": " + d.Message)));
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        Assert.Equal(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), JsonSerializer.Serialize(new TaskPlanCompiler().Compile(copy, catalog).Graph, PlanningJsonContext.Default.PlanningGraph));
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, []), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var variant in new[] { "allow", "deny", "null", "missing", "empty" })
        {
            var nested = new JsonObject();
            if (variant != "missing") nested["event"] = variant == "null" ? null : JsonValue.Create(variant == "empty" ? "allow" : variant);
            var record = new JsonObject { ["nested"] = nested };
            foreach (var selected in new[] { true, false })
            {
                var input = scope == "iteration"
                    ? new JsonObject { ["records"] = variant == "empty" ? new JsonArray() : new JsonArray(record.DeepClone(), new JsonObject { ["nested"] = new JsonObject { ["event"] = "deny" } }) }
                    : new JsonObject { ["record"] = record.DeepClone() };
                if (scope == "conditional") input["selected"] = selected;
                var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], input, PlannerFixture.Ct);
                Assert.Equal(variant != "missing", result.Success);
                if (!result.Success) { Assert.Null(result.Outputs); Assert.NotNull(result.Error); continue; }
                var expected = scope == "iteration" ? (JsonNode)(variant == "empty" ? new JsonArray() : new JsonArray(Expected("iteration", nested["event"]), Expected("iteration", JsonValue.Create("deny"))))
                    : Expected(scope == "conditional" ? selected ? "first" : "second" : scope, nested["event"]);
                Assert.True(JsonNode.DeepEquals(expected, result.Outputs!["publication"]), result.Outputs.ToJsonString());
            }
        }
    }
    private static JsonObject Expected(string status, JsonNode? value) => new() { ["status"] = status, ["events"] = new JsonArray(value?.DeepClone()), ["nested"] = new JsonObject { ["event"] = value?.DeepClone() } };

    [Theory]
    [InlineData("success", "observe")]
    [InlineData("success", "renamed_read")]
    [InlineData("missing", "observe")]
    [InlineData("invalid", "observe")]
    [InlineData("null", "observe")]
    [InlineData("failure", "observe")]
    [InlineData("cancelled", "observe")]
    public async Task CleanupRunsWithoutPublishingOutputsAfterFailureOrCancellation(string mode, string method)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
        var effects = new List<string>(); var factory = new InMemoryMcpClientFactory();
        var empty = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };
        factory.RegisterServer("renamed_source", new() { Tools =
            [new() { Name = method, InputSchema = empty, OutputSchema = TaskPlanCompiler.TypeSchema(Record), EffectKind = "none" },
             new() { Name = "finish", InputSchema = empty, OutputSchema = empty, EffectKind = "none" }], ToolHandlers = new()
            {
                [method] = _ =>
                {
                    effects.Add("read");
                    if (mode == "failure") throw new IOException("Injected producer failure");
                    if (mode == "cancelled") { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                    var nested = new JsonObject();
                    if (mode != "missing") nested["event"] = mode == "null" ? null : JsonValue.Create(mode == "invalid" ? "outside" : "allow");
                    return new() { Content = new JsonObject { ["nested"] = nested } };
                },
                ["finish"] = _ => { effects.Add("cleanup"); return new() { Content = new JsonObject() }; }
            } });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        var plan = new TaskPlan { Root = Export(new() { Kind = "output", Source = "producer" }, "observed") };
        plan.Root.Tasks.Add(new() { Id = "producer", Kind = "operation", Objective = "Read the declared record", Operation = catalog.Capabilities.Single(c => c.Method == method).Id });
        plan.Root.Always.Add(new() { Id = "cleanup", Kind = "operation", Objective = "Finish regardless of producer outcome", Operation = catalog.Capabilities.Single(c => c.Method == "finish").Id });
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(compilation.Graph!)), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), cancellation.Token);
        Assert.Equal(mode is "success" or "null", result.Success);
        Assert.Equal(new[] { "read", "cleanup" }, effects);
        if (result.Success) Assert.True(JsonNode.DeepEquals(Expected("observed", mode == "null" ? null : JsonValue.Create("allow")), result.Outputs!["publication"]));
        else
        {
            Assert.Null(result.Outputs); Assert.NotNull(result.Error);
            if (mode == "failure") Assert.Contains("Injected producer failure", result.Error.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrayProjectionEnvelopeKeepsOrderedNullableOrEmptyPayloads(bool empty)
    {
        var graph = JsonSerializer.Deserialize("""
            {"workflows":[{"key":"main","inputs":[{"name":"records","required":true,"schema":{"contract":{"type":"array","items":{"type":"object","required":["event"],"properties":{"event":{"type":["string","null"]}}}}}}],
            "steps":[{"key":"noop","type":"set","input":{"kind":"object"}}],
            "finally":[
              {"key":"project","type":"array.project","input":{"kind":"object","members":[{"name":"items","value":{"kind":"input","source":"records"}},{"name":"path","value":{"kind":"array","items":[{"kind":"string","text":"event"}]}}]},"outputSchema":{"contract":{"type":"object","required":["values"],"properties":{"values":{"type":"array","items":{"type":["string","null"]}}}}}},
              {"key":"export","type":"set","if":{"kind":"present","source":"project"},"input":{"kind":"object","members":[{"name":"events","value":{"kind":"output","source":"project","path":["values"]}}]}}
            ],"outputs":[{"name":"publication","value":{"kind":"output","source":"export"},"schema":{"contract":{"type":"object","required":["events"],"properties":{"events":{"type":"array","items":{"type":["string","null"]}}}}}}]}]}
            """, PlanningJsonContext.Default.PlanningGraph)!;
        var engine = new WorkflowEngine(); var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), PlannerFixture.Ct);
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, []), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var records = empty ? new JsonArray() : new JsonArray(new JsonObject { ["event"] = "deny" }, new JsonObject { ["event"] = null }, new JsonObject { ["event"] = "allow" });
        var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject { ["records"] = records }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["events"] = empty ? new JsonArray() : new JsonArray("deny", null, "allow") }, result.Outputs!["publication"]));
    }

    [Theory]
    [InlineData("value.project")]
    [InlineData("array.project")]
    [InlineData("value.validate")]
    public void SuccessfulProjectionEnvelopesSupportGuardedExportsButUnsafeChainsDoNot(string type)
    {
        var project = new PlanningNode { Key = "project", Type = type };
        var export = new PlanningNode { Key = "export", Type = "set", Input = new() { Kind = "object" }, If = new() { Kind = "present", Source = "project" } };
        var workflow = new PlanningWorkflow { Finally = [project, export] };
        Assert.True(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
        project.If = new() { Kind = "expression", Text = "false" };
        Assert.False(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
        project.If = new() { Kind = "present", Source = "absent" };
        Assert.False(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
        project.If.Source = "export";
        Assert.False(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
        project.If = null; project.OnError = [new(null, "continue", new() { Kind = "null" }, null)];
        Assert.False(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
        project.OnError.Clear(); export.If = new() { Kind = "expression", Text = "data.steps.project != null || true" };
        Assert.False(PlanningGraphTopology.FinalizerAvailableOnSuccess(export, workflow));
    }
}
