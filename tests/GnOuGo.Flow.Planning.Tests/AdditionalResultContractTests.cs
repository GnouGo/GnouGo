using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class AdditionalResultContractTests
{
    [Theory]
    [InlineData("object", false)]
    [InlineData("array", false)]
    [InlineData("boolean", false)]
    [InlineData("string", true)]
    public void DynamicExtraFieldsMustSatisfyTheirDeclaredResultContract(string type, bool valid)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        workflow.Outputs.Clear();
        workflow.Inputs = [new() { Name = "source", Schema = new() { Type = type,
            Items = type == "array" ? new() { Type = "string" } : null,
            Properties = type == "object" ? [new() { Name = "value", Schema = new() { Type = "string" } }] : [] } }];
        var node = workflow.Steps[0];
        node.Input = Obj(("extra", new() { Kind = "input", Source = "source" }));
        node.OutputSchema = new() { Type = "object", AdditionalProperties = new() { Type = "string", Nullable = true } };
        var findings = PlanningGraphValidation.Validate(graph, preparation);
        Assert.Equal(!valid, findings.Any(d => d.Code == "SET_OUTPUT_INVALID" && d.Location == "/workflows/0/steps/0/input"));
        // Adding an unrelated declared field cannot exempt the dynamic extra field.
        node.OutputSchema.Properties.Add(new() { Name = "label", Required = false, Schema = new() { Type = "string" } });
        Assert.Equal(!valid, PlanningGraphValidation.Validate(graph, preparation).Any(d => d.Code == "SET_OUTPUT_INVALID"));
    }

    [Fact]
    public void ClosedObjectWithoutDeclaredPropertiesRejectsKnownExtraFields()
    {
        var actual = JsonNode.Parse("""{"type":"object","properties":{"extra":{"type":"string"}}}""")!.AsObject();
        var expected = JsonNode.Parse("""{"type":"object","additionalProperties":false}""")!.AsObject();
        Assert.False(PlanningGraphValidation.TypesFit(actual, expected));
        expected["additionalProperties"] = new JsonObject();
        Assert.True(PlanningGraphValidation.TypesFit(actual, expected));
    }
}
