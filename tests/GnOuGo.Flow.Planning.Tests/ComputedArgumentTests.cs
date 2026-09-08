using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ComputedArgumentTests
{
    [Theory]
    [InlineData("quantity", "flag ? amount : null", true)]
    [InlineData("quantite", "(() => { if (flag) return amount; return null; })()", true)]
    [InlineData("quantity", "flag ? amount : 0", false)]
    [InlineData("quantity", "(() => { const helper = () => null; return flag ? amount : 0; })()", false)]
    public void ExplicitNullBranchesCannotSatisfyAnOptionalNonNullableArgument(string name, string expression, bool invalid)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        workflow.Outputs.Clear();
        workflow.Inputs = [new() { Name = "enabled", Schema = new() { Type = "boolean" } }, new() { Name = "count", Schema = new() { Type = "number" } }];
        var expected = new JsonObject { ["type"] = "number" };
        preparation.Capabilities.Add(new() { Id = "reader", StepType = "mcp.call", InputSchema = new()
            { ["type"] = "object", ["properties"] = new JsonObject { [name] = expected }, ["additionalProperties"] = false } });
        var compute = new PlanningValue { Kind = "compute", Text = expression, Members =
            [new("flag", new() { Kind = "input", Source = "enabled" }), new("amount", new() { Kind = "input", Source = "count" })] };
        workflow.Steps = [new() { Key = "read", Type = "mcp.call", CapabilityId = "reader", Input = Obj(("request", Obj((name, compute)))) }];
        var findings = PlanningGraphValidation.Validate(graph, preparation).Where(d => d.Code == "CAPABILITY_ARGUMENT_INVALID").ToArray();
        if (invalid)
        {
            var finding = Assert.Single(findings);
            Assert.Equal("/workflows/0/steps/0/input/members/0/value/members/0/value", finding.Location);
            Assert.Contains("omission and null are distinct", finding.Message);
            preparation.Capabilities[0].InputSchema["properties"]!["unchanged"] = new JsonObject { ["type"] = "string" };
            workflow.Steps[0].Input.Members[0].Value.Members.Add(new("unchanged", Str(new string('x', 20_000))));
            var unit = new PlanningConstructionUnit { Key = "repair", WorkflowKey = workflow.Key, Kind = "implementation", NodeKeys = ["read"],
                ContractVersion = PlanningDataflow.ContractVersion, Diagnostics = [finding] };
            unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit, preparation), preparation);
            var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, preparation, graph), preparation);
            Assert.Equal("nodes/read/arguments/" + name, Assert.Single(patch.Context(unit.Candidate)).Key);
            Assert.True(PlanningConstruction.EstimateInputTokens(patch.Context(unit.Candidate).ToJsonString(), patch.Schema) < 12_000);
            var repaired = patch.Apply(unit.Candidate, new() { ["changes"] = new JsonObject { ["nodes/read/arguments/" + name] = new JsonObject { ["kind"] = "omit" } }, ["remove"] = new JsonArray() });
            Assert.True(JsonNode.DeepEquals(unit.Candidate["nodes"]!["read"]!["arguments"]!["unchanged"], repaired["nodes"]!["read"]!["arguments"]!["unchanged"]));
        }
        else Assert.Empty(findings);
        expected["type"] = new JsonArray("number", "null");
        Assert.DoesNotContain(PlanningGraphValidation.Validate(graph, preparation), d => d.Code == "CAPABILITY_ARGUMENT_INVALID");
        // A type union does not override an enum that still excludes null.
        expected["enum"] = new JsonArray(1, 2);
        Assert.Equal(invalid, PlanningGraphValidation.Validate(graph, preparation).Any(d => d.Code == "CAPABILITY_ARGUMENT_INVALID"));
        workflow.Steps[0].Input = Obj(("request", Obj()));
        Assert.DoesNotContain(PlanningGraphValidation.Validate(graph, preparation), d => d.Code is "CAPABILITY_ARGUMENT_MISSING" or "CAPABILITY_ARGUMENT_INVALID");
    }
}
