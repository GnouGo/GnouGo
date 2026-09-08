using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstructionSchemaTests
{
    [Theory]
    [InlineData("structuredOutput")]
    [InlineData("outputSchema")]
    public void NestedUntypedSchemaRepairLocksValidSiblingsAndUsesOneSmallCoordinate(string field)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0]; workflow.Outputs.Clear();
        var schema = new PlanningSchema { Type = "object", Properties =
        [
            new() { Name = "retained", Required = true, Schema = new() { Type = "string", Enum = ["accepted", "rejected"], Description = new string('x', 40_000) } },
            new() { Name = "rows", Required = true, Schema = new() { Type = "array", Items = new() { Type = "object", Properties =
                [new() { Name = "payload", Required = true, Schema = new() { Type = "object" } }] } } }
        ] };
        var node = workflow.Steps[0];
        if (field == "structuredOutput")
        {
            node.Type = "mcp.call"; node.CapabilityId = "renamed-producer"; node.StructuredOutput = new(schema);
            preparation.Capabilities.Add(new() { Id = node.CapabilityId, StepType = "mcp.call" });
        }
        else node.OutputSchema = schema;
        var unit = new PlanningConstructionUnit { Key = "contract", WorkflowKey = "main", Kind = "contracts", NodeKeys = [node.Key], ContractVersion = PlanningDataflow.ContractVersion };
        unit.Candidate = PlanningConstruction.Values(workflow, unit, preparation);
        unit.Diagnostics = PlanningGraphValidation.Validate(graph, preparation).ToList();
        var suffix = field + (field == "structuredOutput" ? "/schema" : "") + "/properties/1/schema/items/properties/0/schema";
        var error = Assert.Single(unit.Diagnostics);
        Assert.Equal("/workflows/0/steps/0/" + suffix, error.Location);
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, preparation, graph), preparation);
        var coordinate = "nodes/greeting/" + suffix;
        Assert.Equal(coordinate, Assert.Single(patch.Context(unit.Candidate)).Key);
        Assert.InRange(PlanningConstruction.EstimateInputTokens(patch.Context(unit.Candidate).ToJsonString(), patch.Schema), 1, 4_000);
        var replacement = new JsonObject { ["kind"] = "inline", ["type"] = "string", ["nullable"] = false, ["description"] = "Serialized opaque payload", ["enum"] = new JsonArray() };
        var response = new JsonObject { ["changes"] = new JsonObject { [coordinate] = replacement }, ["remove"] = new JsonArray() };
        var repaired = patch.Apply(unit.Candidate, response);
        var originalSchema = field == "structuredOutput" ? unit.Candidate["nodes"]![node.Key]![field]!["schema"]! : unit.Candidate["nodes"]![node.Key]![field]!;
        var repairedSchema = field == "structuredOutput" ? repaired["nodes"]![node.Key]![field]!["schema"]! : repaired["nodes"]![node.Key]![field]!;
        Assert.True(JsonNode.DeepEquals(originalSchema["properties"]![0], repairedSchema["properties"]![0]));
        var result = PlanningConstruction.Apply(graph, unit, repaired, preparation);
        Assert.DoesNotContain(PlanningGraphValidation.Validate(result, preparation), d => d.Code is "STRUCTURED_OUTPUT_INVALID" or "SCHEMA_INVALID");
        var malicious = response.DeepClone(); malicious["changes"]!["nodes/greeting/" + field] = replacement.DeepClone();
        Assert.Throws<InvalidOperationException>(() => patch.Apply(unit.Candidate, malicious.AsObject()));
    }

    [Fact]
    public async Task RetainedArrayPlacementIsRevalidatedWithoutARepairCall()
    {
        var state = ConstructionUnitTests.ApprovedSkeleton();
        var workflow = Graph().Workflows[0];
        workflow.Steps[0].OutputSchema = new() { Type = "array", Items = new() { Type = "object", Properties = [] },
            Properties = [new() { Name = "record", Required = true, Schema = new() { Type = "string" } }] };
        var unit = new PlanningConstructionUnit { Key = "contract", WorkflowKey = "main", Kind = "contracts", NodeKeys = ["greeting"], ContractVersion = 28 };
        unit.Candidate = PlanningConstruction.Values(workflow, unit, state.Preparation); unit.ContractVersion = 29; unit.Calls = 3; unit.RepairCalls = 2;
        unit.Status = "invalid"; unit.Diagnostics = [new("UNIT_RESPONSE_INVALID", "/units/contract", "Previous ambiguous schema finding")];
        state.ConstructionUnits = [unit];
        var approval = state.ApprovedBehaviorHash;
        var runtime = new FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("The preserving schema repair requires no model call.") };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal("validated", state.ConstructionUnits[0].Status);
        Assert.Equal(3, state.ConstructionUnits[0].Calls); Assert.Equal(2, state.ConstructionUnits[0].RepairCalls);
        Assert.Equal(approval, state.ApprovedBehaviorHash);
        Assert.Empty(state.Graph!.Workflows[0].Steps[0].OutputSchema!.Properties);
        Assert.Equal("record", Assert.Single(state.Graph.Workflows[0].Steps[0].OutputSchema!.Items!.Properties).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrayFieldsMoveToExistingObjectItemsOnlyWhenDeclarationsAgree(bool conflict)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        var item = new PlanningSchema { Type = "object", Properties = [new() { Name = "code", Required = true, Schema = new() { Type = "string", Enum = ["a", "b"] } }] };
        var array = new PlanningSchema { Type = "array", Items = item, Properties =
            [new() { Name = "code", Required = true, Schema = new() { Type = conflict ? "integer" : "string", Enum = conflict ? [] : ["a", "b"] } },
             new() { Name = "optionalFlag", Required = false, Schema = new() { Type = "boolean", Nullable = true } }] };
        workflow.Steps[0].OutputSchema = array;
        var unit = new PlanningConstructionUnit { WorkflowKey = "main", Kind = "contracts", NodeKeys = ["greeting"], ContractVersion = 28 };
        var legacy = PlanningConstruction.Values(workflow, unit, preparation);
        unit.ContractVersion = PlanningDataflow.ContractVersion;
        var normalized = PlanningConstructionSchemas.Compact(legacy);
        var findings = PlanningConstruction.ShapeFindings(normalized, PlanningConstruction.Schema(workflow, unit, preparation, graph), unit);
        if (conflict)
        {
            Assert.True(JsonNode.DeepEquals(legacy, normalized) || normalized["nodes"]!["greeting"]!["outputSchema"]!["properties"] is JsonArray { Count: 2 });
            Assert.Contains(findings, d => d.Message.Contains(".properties", StringComparison.Ordinal)); return;
        }
        Assert.Empty(findings);
        var applied = PlanningConstruction.Apply(graph, unit, legacy, preparation).Workflows[0].Steps[0].OutputSchema!;
        Assert.Empty(applied.Properties); Assert.Equal(2, applied.Items!.Properties.Count);
        Assert.Equal(new[] { "a", "b" }, applied.Items.Properties[0].Schema.Enum);
        Assert.False(applied.Items.Properties[1].Required); Assert.True(applied.Items.Properties[1].Schema.Nullable);
    }

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
