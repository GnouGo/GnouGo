using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
using static GnOuGo.Planning.Examples.ProductTransformationPlan;
using static GnOuGo.Flow.Planning.Tests.LookupCompilationTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class MappingStatusCompositionTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("missing_fact")]
    [InlineData("failure")]
    [InlineData("absent")]
    [InlineData("capture_truncated")]
    [InlineData("manifest_truncated")]
    public async Task DeterministicCompositionSeparatesFactsAndProducerOutcomeWithNoStatusInference(string variant)
    {
        var rowType = Obj(("name", new() { Nullable = true }), ("description", new() { Nullable = true }), ("price", new() { Nullable = true }));
        var snapshotType = Obj(("record", rowType), ("captureTruncated", new() { Kind = "boolean" }), ("manifestTruncated", new() { Kind = "boolean" }));
        snapshotType.Nullable = true;
        var producerType = Obj(("success", new() { Kind = "boolean" }), ("observationSnapshot", snapshotType), ("error_message", new() { Nullable = true }));
        var statusType = Obj(("success", new() { Kind = "boolean" }), ("availability", new() { Kind = "boolean" }),
            ("completeness", new() { Kind = "boolean" }), ("message", new() { Nullable = true }));
        var row = new JsonObject { ["name"] = " Observed\tname ", ["description"] = variant == "missing_fact" ? null : "Whole observed block\n二ième ligne", ["price"] = "001.20 €" };
        var response = new JsonObject { ["success"] = variant != "failure", ["error_message"] = variant == "failure" ? "Producer failure" : null,
            ["observationSnapshot"] = variant == "absent" ? null : new JsonObject { ["record"] = row.DeepClone(), ["captureTruncated"] = variant == "capture_truncated", ["manifestTruncated"] = variant == "manifest_truncated" } };
        var reads = 0;
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("generic", new() { Tools = [new() { Name = "acquire", InputSchema = TaskPlanCompiler.TypeSchema(new() { Kind = "object" }), OutputSchema = TaskPlanCompiler.TypeSchema(producerType), EffectKind = "none" }],
            ToolHandlers = new() { ["acquire"] = _ => { reads++; return new() { Content = response.DeepClone() }; } } });
        var model = new Facts();
        var engine = new WorkflowEngine { LLMUsageBudget = new(new() { MaxCalls = 2 }), McpClientFactory = factory, LLMClient = model, LlmDefaults = new() { Model = "offline" } };
        var runtime = new TestRuntime(engine);
        var state = PlannerFixture.Session(); state.Catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual); state.Catalog.Policy.RequireExternalConfirmation = false;
        var operation = TaskOperations.Describe(state.Catalog.Capabilities.Single(c => c.Method == "acquire")).Id;
        var snapshot = Field(Ref("acquire_source"), "observationSnapshot");
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = "acquire_source", Operation = operation, Objective = "Acquire observed data" },
            new() { Id = "adapt_facts", Kind = "transform", Mode = "extract", Objective = "Extract row and outcome", Inputs = [new("record", snapshot)],
                Requires = new() { Kind = "predicate", Predicate = "and", Items = [Field(Ref("acquire_source"), "success"), new() { Kind = "predicate", Predicate = "not_equal", Items = [snapshot, new() { Kind = "null" }] }] },
                ResultType = Obj(("row", rowType), ("status", statusType)) }
        ], Outputs = [new("row", Ref("adapt_facts", "row")), new("status", Ref("adapt_facts", "status"))] } };
        var baseline = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var revisedPlan = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        MappingStatusComposition.Separate(revisedPlan.Root, "acquire_source", "adapt_facts", "assemble_outcome");
        Assert.Equal(baseline, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(0, reads); Assert.Equal(0, model.Calls);
        var compilation = new TaskPlanCompiler().Compile(revisedPlan, state.Catalog, state.Request);
        Assert.Empty(compilation.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, state.Catalog);
        var result = await Execute(yaml, new(), engine);
        var success = variant is "complete" or "missing_fact";
        Assert.Equal(success, result.Success); Assert.Equal(1, reads); Assert.Equal(success ? 1 : 0, model.Calls);
        if (success)
        {
            Assert.True(JsonNode.DeepEquals(row, result.Outputs!["row"]));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"success":true,"availability":true,"completeness":true,"message":null}"""), result.Outputs["status"]));
        }
        else Assert.Equal(variant == "failure" ? "MCP_CALL_ERROR" : "INPUT_VALIDATION", result.Error!.Code);
        Assert.Empty(runtime.Calls); // Mapping behavior is checked independently of model planning.
    }
    private static TaskValue Field(TaskValue value, string field) => new() { Kind = "field", Port = field, Items = [value] };
    private sealed class Facts : ILLMClient, ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium", "low"]);

        public int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = "({row:source.record.record})" } });
        }
    }
}
