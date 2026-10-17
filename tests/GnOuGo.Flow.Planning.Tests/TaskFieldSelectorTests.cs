using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TaskFieldBindingTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskFieldSelectorTests
{
    [Theory]
    [InlineData("event", "first-source", "send")]
    [InlineData("renamed_selector", "different-source", "apply")]
    public async Task TransformFieldsRemainCheckedInsideIterationAndNestedScopes(string selector, string server, string method)
    {
        var calls = new List<string>(); var model = new Model(); var factory = new InMemoryMcpClientFactory();
        var schema = new JsonObject { ["type"] = "object", ["discriminator"] = new JsonObject { ["propertyName"] = selector },
            ["properties"] = new JsonObject { [selector] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("allow", "deny", "comment") } }, ["required"] = new JsonArray(selector) };
        factory.RegisterServer(server, new() { Tools = [new() { Name = method, InputSchema = schema }], ToolHandlers = new()
            { [method] = request => { calls.Add(request![selector]!.ToString()); return new() { Content = new JsonObject() }; } } });
        var engine = new WorkflowEngine { McpClientFactory = factory, LLMClient = model, LlmDefaults = new() { Model = "mock" } };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "selected", Version = "1", Kind = "tool", StepType = "mcp.call", Server = server, Method = method,
            InputSchema = schema, OutputSchema = new() { ["type"] = "object" }, EffectKind = "none" });
        var plan = new TaskPlan { Root = new() { Tasks =
        [
            new() { Id = "interpret", Kind = "transform", Objective = "Interpret the supplied text", Inputs = [new("text", new() { Kind = "string", Text = "Fixture input" })], ResultType = new()
                { Kind = "object", Fields = [new() { Name = "records", Type = new() { Kind = "array", Items = new() { Kind = "object", Fields =
                    [new() { Name = "nested", Type = new() { Kind = "object", Fields = [new() { Name = "picked", Type = new() { Kind = "string", Enum = ["allow", "deny"] } }] } }] } } }] } },
            new() { Id = "loop", Kind = "foreach", Objective = "Consume decisions in order", Items = new() { Kind = "output", Source = "interpret", Port = "records" }, MaxItems = 3,
                Body = new() { Tasks = [new() { Id = "scope", Kind = "sequence", Objective = "Use the item", Body = new() { Tasks =
                    [new() { Id = "consume", Kind = "operation", Objective = "Consume the checked decision", Operation = "selected", Inputs = [new(selector, Field(Field(new() { Kind = "item" }, "nested"), "picked"))] }] } }] } }
        ] } };
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics); Assert.NotNull(compilation.Graph);
        var graph = compilation.Graph;
        var nodes = graph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps)).ToArray();
        Assert.Single(nodes, n => n.Type == "llm.call"); Assert.Contains(nodes, n => n.Type == "set" && n.Input.Kind == "projection");
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog);
        var diagnostics = await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(graph)), PlannerFixture.Ct);
        Assert.True(diagnostics.Count == 0, JsonSerializer.Serialize(diagnostics));
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        Assert.Equal(yaml, new PlanningGraphCompiler().Compile(new TaskPlanCompiler().Compile(copy, catalog).Graph!, catalog));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var variant in new[] { "valid", "empty", "invalid", "null", "absent" })
        {
            model.Variant = variant; calls.Clear(); var before = model.Calls;
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
            Assert.Equal(variant is "valid" or "empty", result.Success);
            Assert.Equal(before + 1, model.Calls); // Field selection adds no inference.
            Assert.Equal(variant == "valid" ? new[] { "allow", "deny" } : [], calls);
        }
    }

    private sealed class Model : ILLMClient
    {
        internal string Variant = "valid"; internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++; var records = new JsonArray();
            if (Variant != "empty")
                foreach (var value in Variant == "valid" ? new[] { "allow", "deny" } : new[] { "outside" })
                {
                    var nested = new JsonObject();
                    if (Variant != "absent") nested["picked"] = Variant == "null" ? null : JsonValue.Create(value);
                    records.Add(new JsonObject { ["nested"] = nested });
                }
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["records"] = records } });
        }
    }
}
