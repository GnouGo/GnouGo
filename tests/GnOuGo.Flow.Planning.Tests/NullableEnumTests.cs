using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class NullableEnumTests
{
    [Theory]
    [InlineData("LEFT", "RIGHT")]
    [InlineData("GAUCHE", "DROITE")]
    public async Task NullableEnumsRoundTripAndAcceptNullDuringExecution(string first, string second)
    {
        var prep = Preparation(); var schema = new PlanningSchema { Type = "string", Nullable = true, Enum = [first, second] };
        var json = PlanningGraphCompiler.ToJsonSchema(schema, prep);
        Assert.Empty(PlanningContractValidation.ValidateInstance(null, json));
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonValue.Create(first), json));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonValue.Create("invented"), json));
        var restored = PlanningGraphImporter.Schema(json);
        Assert.True(restored.Nullable); Assert.Equal(schema.Enum, restored.Enum);
        Assert.True(JsonNode.DeepEquals(json, PlanningGraphCompiler.ToJsonSchema(restored, prep)));
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Steps[0].Input = Obj(("message", new() { Kind = "null" }));
        workflow.Steps[0].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Required = true, Schema = schema }] };
        workflow.Outputs[0].Schema = schema;
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, prep)));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Null(result.Outputs!["message"]);
        schema.Nullable = false;
        Assert.Contains(PlanningGraphValidation.Validate(graph, prep), d => d.Code == "SET_OUTPUT_INVALID");
    }

    [Fact]
    public void ImportPreservesTheIntersectionOfTypeAndEnumInsteadOfWideningNullability()
    {
        var source = new JsonObject { ["type"] = new JsonArray("string", "null"), ["enum"] = new JsonArray("one", "two") };
        var imported = PlanningGraphImporter.Schema(source);
        Assert.False(imported.Nullable); Assert.NotEmpty(PlanningContractValidation.ValidateInstance(null, PlanningGraphCompiler.ToJsonSchema(imported, Preparation())));
        source["enum"] = new JsonArray((JsonNode?)null);
        Assert.Throws<InvalidOperationException>(() => PlanningGraphImporter.Schema(source));
    }
}
