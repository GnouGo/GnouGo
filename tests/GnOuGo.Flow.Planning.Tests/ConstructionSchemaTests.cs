using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstructionSchemaTests
{
    [Fact]
    public void TypeSpecificResponsesReduceSizeWithoutDroppingNestedConstraints()
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        var schema = new PlanningSchema { Type = "object", Properties =
        [new() { Name = "message", Required = true, Schema = new() { Type = "string", Enum = ["one", "two"], Nullable = true } },
         new() { Name = "rows", Required = false, Schema = new() { Type = "array", Items = new() { Type = "object", Properties =
             [new() { Name = "count", Required = true, Schema = new() { Type = "integer" } }, new() { Name = "ok", Required = true, Schema = new() { Type = "boolean" } }] } } }],
         AdditionalProperties = new() { Type = "number" } };
        workflow.Steps[0].OutputSchema = schema;
        var unit = new PlanningConstructionUnit { WorkflowKey = "main", Kind = "contracts", NodeKeys = ["greeting"], ContractVersion = 28 };
        var legacy = PlanningConstruction.Values(workflow, unit, preparation);
        unit.ContractVersion = PlanningDataflow.ContractVersion;
        var compact = PlanningConstruction.Values(workflow, unit, preparation);
        Assert.True(compact.ToJsonString().Length < legacy.ToJsonString().Length * .8);
        var responseSchema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        Assert.Empty(PlanningContractValidation.ValidateSchema(responseSchema, strict: true));
        Assert.Empty(PlanningConstruction.ShapeFindings(compact, responseSchema, unit));
        Assert.Empty(PlanningConstruction.ShapeFindings(legacy, responseSchema, unit));
        foreach (var candidate in new[] { legacy, compact })
        {
            var applied = PlanningConstruction.Apply(graph, unit, candidate, preparation);
            Assert.True(JsonNode.DeepEquals(PlanningGraphCompiler.ToJsonSchema(schema, preparation), PlanningGraphCompiler.ToJsonSchema(applied.Workflows[0].Steps[0].OutputSchema!, preparation)));
        }
    }

    [Theory]
    [InlineData("items")]
    [InlineData("additionalProperties")]
    [InlineData("properties")]
    public void CompactionNeverDiscardsContradictoryNonemptySchemaFields(string field)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        workflow.Steps[0].OutputSchema = new() { Type = "string", Enum = ["allowed"] };
        var unit = new PlanningConstructionUnit { WorkflowKey = "main", Kind = "contracts", NodeKeys = ["greeting"], ContractVersion = PlanningDataflow.ContractVersion };
        var candidate = PlanningConstruction.Values(workflow, unit, preparation);
        candidate["nodes"]!["greeting"]!["outputSchema"]![field] = field == "properties" ? new JsonArray(new JsonObject { ["name"] = "undeclared" }) : new JsonObject { ["kind"] = "inline", ["type"] = "integer" };
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(candidate, PlanningConstruction.Schema(workflow, unit, preparation, graph), unit));
    }
}
