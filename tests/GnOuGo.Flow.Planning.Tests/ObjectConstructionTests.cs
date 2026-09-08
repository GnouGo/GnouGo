using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ObjectConstructionTests
{
    [Fact]
    public async Task LargeObjectGenerationCheckpointsSmallFieldGroupsAcrossRestart()
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); var node = state.Graph!.Workflows[0].Steps[0];
        node.OutputSchema = new() { Type = "object", Properties = Enumerable.Range(0, 12).Select(i => new PlanningPort
            { Name = i == 0 ? "message" : "field_" + i, Schema = new() { Type = "string" } }).ToList() };
        state.ConstructionUnits = [new() { Key = "fields", WorkflowKey = "main", Kind = "implementation", NodeKeys = [node.Key], ContractVersion = PlanningDataflow.ContractVersion }];
        var calls = 0;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("fragment_implementation", phase); calls++;
            var fields = request.StructuredOutputSchema!["properties"]!["changes"]!["properties"]!.AsObject();
            Assert.InRange(fields.Count, 1, 4);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["changes"] = new JsonObject(fields.Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                p.Key == "functions" ? null : p.Key.EndsWith("/onError", StringComparison.Ordinal) ? new JsonArray() : new JsonObject { ["kind"] = "string", ["text"] = "Hello" }))), ["remove"] = new JsonArray() } });
        } };
        for (var i = 0; i < 20 && state.ConstructionUnits[0].Status != "validated" && state.Status == PlanningStatus.Generating; i++)
        {
            state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        }
        Assert.Equal("validated", state.ConstructionUnits[0].Status);
        Assert.InRange(calls, 4, 7); Assert.Equal(0, state.ConstructionUnits[0].RepairCalls);
        Assert.Equal(12, state.Graph!.Workflows[0].Steps[0].Input.Members.Count);
    }

    [Theory]
    [InlineData("result", "decision")]
    [InlineData("resultat", "choix")]
    public void ExplicitFieldsKeepTypedDependenciesAndOptionalOmission(string key, string field)
    {
        var (graph, preparation, unit) = Fixture(key, field);
        var workflow = graph.Workflows[0];
        var candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), preparation);
        Assert.Null(candidate["nodes"]![key]!["input"]);
        Assert.Equal("omit", candidate["nodes"]![key]!["values"]!["optional"]!["kind"]!.ToString());
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        Assert.Empty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        var applied = PlanningConstruction.Apply(graph, unit, candidate, preparation);
        var value = Assert.Single(applied.Workflows[0].Steps[0].Input.Members).Value;
        Assert.Equal("input", value.Kind); Assert.Equal("resource", value.Source);
        candidate["nodes"]![key]!["values"]!["optional"] = new JsonObject { ["kind"] = "null" };
        applied = PlanningConstruction.Apply(graph, unit, candidate, preparation);
        Assert.Equal("null", applied.Workflows[0].Steps[0].Input.Members[1].Value.Kind);
        candidate["nodes"]![key]!["values"]!["invented"] = new JsonObject { ["kind"] = "null" };
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
    }

    [Fact]
    public void OpaqueLegacyComputationCannotBecomeAFieldProjectionAndRepairPreservesOtherFields()
    {
        var (graph, preparation, unit) = Fixture(); var workflow = graph.Workflows[0];
        workflow.Steps[0].Input = new() { Kind = "compute", Text = "({ decision: resource })", Members = [new("resource", new() { Kind = "input", Source = "resource" })] };
        var original = PlanningConstruction.Values(workflow, unit);
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, original, preparation);
        Assert.Equal("compute", original["nodes"]!["result"]!["input"]!["kind"]!.ToString());
        Assert.Empty(unit.Candidate["nodes"]!["result"]!["values"]!.AsObject());
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(unit.Candidate, schema, unit));
        var patch = PlanningUnitPatches.Create(graph, unit, schema, preparation);
        var input = PlanningDataflow.CompactIndex(workflow, preparation, graph, "result").Values.Single(b => b.Value.Kind == "input");
        var repaired = patch.Apply(unit.Candidate, new() { ["changes"] = new JsonObject
        {
            ["nodes/result/values/decision"] = new JsonObject { ["kind"] = "binding", ["reference"] = input.Id },
            ["nodes/result/values/optional"] = new JsonObject { ["kind"] = "omit" }
        }, ["remove"] = new JsonArray("nodes/result/input") });
        Assert.True(JsonNode.DeepEquals(unit.Candidate["nodes"]!["result"]!["onError"], repaired["nodes"]!["result"]!["onError"]));
        Assert.Equal("input", PlanningConstruction.Apply(graph, unit, repaired, preparation).Workflows[0].Steps[0].Input.Members[0].Value.Kind);
    }

    [Fact]
    public void FieldDiagnosticsSelectOnlyThatCoordinateAndContract()
    {
        var (graph, preparation, unit) = Fixture(); var workflow = graph.Workflows[0];
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), preparation);
        unit.Diagnostics = [new("INVALID_COMPUTATION", "/workflows/0/steps/0/input/members/0/value", "Repair this value.")];
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, preparation, graph), preparation);
        Assert.Equal("nodes/result/values/decision", Assert.Single(patch.Context(unit.Candidate)).Key);
        var context = TypedWorkflowPlanner.ComputedContractContext(workflow, preparation, patch.Context(unit.Candidate));
        Assert.Equal("string", Assert.Single(context).Value!["type"]!.ToString());
        Assert.DoesNotContain("optional", context.ToJsonString());
    }

    [Fact]
    public void NullableAndDictionaryObjectsRetainTheirWholeValueContract()
    {
        var (graph, preparation, unit) = Fixture(); var node = graph.Workflows[0].Steps[0];
        node.OutputSchema!.Nullable = true; Assert.Null(PlanningObjectConstruction.Contract(node, preparation));
        node.OutputSchema.Nullable = false; node.OutputSchema.AdditionalProperties = new() { Type = "string" };
        Assert.Null(PlanningObjectConstruction.Contract(node, preparation));
    }

    private static (PlanningGraph Graph, PlanningPreparation Preparation, PlanningConstructionUnit Unit) Fixture(string key = "result", string field = "decision")
    {
        var workflow = new PlanningWorkflow { Key = "main", Inputs = [new() { Name = "resource", Required = true, Schema = new() { Type = "string" } }], Steps = [new()
        {
            Key = key, Type = "set", OutputSchema = new() { Type = "object", Properties = [new() { Name = field, Required = true, Schema = new() { Type = "string" } }, new() { Name = "optional", Required = false, Schema = new() { Type = "string", Nullable = true } }] },
            Input = new() { Kind = "object", Members = [new(field, new() { Kind = "input", Source = "resource" })] }
        }] };
        return (new() { Workflows = [workflow] }, new(), new() { Key = "unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = [key], ContractVersion = PlanningDataflow.ContractVersion });
    }
}
