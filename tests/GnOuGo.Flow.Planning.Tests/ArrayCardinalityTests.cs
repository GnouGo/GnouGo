using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ArrayCardinalityTests
{
    [Theory]
    [InlineData(null, null)][InlineData(1, null)][InlineData(null, 2)][InlineData(1, 1)][InlineData(0, 0)]
    public void OptionalBoundsRoundTripAndFreshStrictSchemasAcceptTheirExactShape(int? min, int? max)
    {
        var type = new TaskType { Kind = "array", Items = new(), MinItems = min, MaxItems = max };
        var json = JsonSerializer.SerializeToNode(type, PlanningJsonContext.Default.TaskType)!;
        Assert.Equal(min is not null, json.AsObject().ContainsKey("minItems"));
        Assert.Equal(max is not null, json.AsObject().ContainsKey("maxItems"));
        var schema = TaskPlanCompiler.TypeSchema(json.Deserialize(PlanningJsonContext.Default.TaskType)!);
        Assert.Equal(min, schema["minItems"]?.GetValue<int>()); Assert.Equal(max, schema["maxItems"]?.GetValue<int>());
        for (var count = 0; count < 4; count++)
            Assert.Equal((min is null || count >= min) && (max is null || count <= max),
                PlanningContractValidation.ValidateInstance(new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode?)JsonValue.Create("id" + i)).ToArray()), schema).Count == 0);
        var definitions = PlanningSchemas.FullProposal(PlannerFixture.Session(), compact: false)["$defs"]!.AsObject();
        var result = PlanningSchemas.Object(("result", PlanningSchemas.Ref("resultType"))); result["$defs"] = definitions.DeepClone();
        Assert.Empty(PlanningContractValidation.ValidateSchema(result, strict: true));
        var wire = PlanningJsonTransport.TaskPlanPart(json)!; wire["minItems"] = min; wire["maxItems"] = max;
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["result"] = wire }, result));
    }

    [Theory]
    [InlineData("array", -1, null)][InlineData("array", null, -1)][InlineData("array", 2, 1)]
    [InlineData("object", 0, 1)][InlineData("string", 1, null)][InlineData("any", null, 2)]
    public void InvalidDeclarationsDoNotBecomeRuntimePromises(string kind, int? min, int? max)
        => Assert.Throws<ArgumentException>(() => TaskPlanCompiler.TypeSchema(new() { Kind = kind, MinItems = min, MaxItems = max, Items = new() }));

    [Theory]
    [InlineData("[]", false)][InlineData("[\"candidate\"]", true)][InlineData("[\"a\",\"b\"]", false)]
    public async Task RuntimeValidatesSelectionCardinalityDespiteAClaimedGuarantee(string selected, bool valid)
    {
        var model = new SelectionModel(selected); var engine = new WorkflowEngine { LLMClient = model, LlmDefaults = new() { Model = "deterministic" } };
        var catalog = await TaskPlanCompilerTests.Catalog(new(engine, (_, _) => Task.CompletedTask));
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "select", Kind = "transform", Mode = "interpret", Objective = "Select exactly one supplied identity",
            Inputs = [new("offered", new() { Kind = "array", Items = [new() { Kind = "string", Text = "candidate" }] })],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "selected", Type = new() { Kind = "array", Items = new(), MinItems = 1, MaxItems = 1 } },
                new() { Name = "claimed", Type = new() { Kind = "boolean" } }] } }],
            Outputs = [new("selected", new() { Kind = "output", Source = "select", Port = "selected" })] } };
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog, "bounded", true, true, true);
        var result = await engine.ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"], new JsonObject(), PlannerFixture.Ct);
        Assert.True(valid == result.Success, result.Error?.Code + ": " + result.Error?.Message); Assert.True(model.Calls == 1, result.Error?.Message);
        if (valid) Assert.Equal(selected, result.Outputs!["selected"]!.ToJsonString());
        else Assert.Null(result.Outputs);
    }

    private sealed class SelectionModel(string selected) : ILLMClient, ILLMCapabilityResolver
    {
        public int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(1, request.StructuredOutputSchema!["properties"]!["selected"]!["minItems"]!.GetValue<int>());
            return Task.FromResult(new LLMResponse { Json = JsonNode.Parse("{\"selected\":" + selected + ",\"claimed\":true}") });
        }
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
    }
}
