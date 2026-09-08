using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ArgumentBindingTests
{
    [Theory]
    [InlineData("source", "Read resource metadata")]
    [InlineData("renamed-source", "Lire les metadonnees")]
    public void MistypedBindingsCannotEstablishDependenciesButExplicitSerializationCan(string source, string purpose)
    {
        var state = ProducerRepairTests.Fixture(source, purpose); var graph = state.Graph!; var preparation = state.Preparation!;
        var unit = state.ConstructionUnits.Single(u => u.Key == "consumer");
        var workflow = graph.Workflows[0];
        var binding = PlanningDataflow.Index(workflow, preparation, graph, "consume").Values.Single(b => b.Value.Source == "observation" && b.Value.Path.Count == 0);
        var fields = unit.Candidate!.DeepClone().AsObject();
        JsonObject Reference() => new() { ["kind"] = "binding", ["reference"] = binding.Id };
        fields["nodes"]!["consume"]!["arguments"]!["revision"] = Reference();
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(fields, schema, unit));
        Assert.Throws<InvalidOperationException>(() => PlanningConstruction.Apply(graph, unit, fields, preparation));
        var invalid = PlanningConstruction.Preview(graph, unit, fields, preparation);
        Assert.Contains(PlanningGraphValidation.Validate(invalid, preparation), d => d.Code == "CAPABILITY_ARGUMENT_TYPE");
        Assert.Contains(PlanningDataflow.OperationInputFindings(invalid, preparation), d => d.Code == "OPERATION_INPUT_BINDING_MISSING");
        fields["nodes"]!["consume"]!["arguments"]!["revision"] = new JsonObject { ["kind"] = "compute", ["text"] = "JSON.stringify(value)",
            ["members"] = new JsonArray(new JsonObject { ["name"] = "value", ["value"] = Reference() }) };
        Assert.Empty(PlanningConstruction.ShapeFindings(fields, schema, unit));
        var valid = PlanningConstruction.Apply(graph, unit, fields, preparation);
        Assert.DoesNotContain(PlanningGraphValidation.Validate(valid, preparation), d => d.Code == "CAPABILITY_ARGUMENT_TYPE");
        Assert.DoesNotContain(PlanningDataflow.OperationInputFindings(valid, preparation), d => d.Code == "OPERATION_INPUT_BINDING_MISSING");
    }

    [Fact]
    public void NullableDestinationsKeepNullAndOmissionDistinctAndReuseBindingEnums()
    {
        var state = ProducerRepairTests.Fixture(); var workflow = state.Graph!.Workflows[0]; var preparation = state.Preparation!;
        var contract = preparation.Capabilities.Single(c => c.Id == "destination").InputSchema;
        var nullable = new JsonObject { ["type"] = new JsonArray("string", "null") };
        contract["properties"]!["revision"] = nullable;
        contract["properties"]!["optional"] = nullable.DeepClone();
        var unit = state.ConstructionUnits.Single(u => u.Key == "consumer");
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, state.Graph);
        var arguments = schema["properties"]!["nodes"]!["properties"]!["consume"]!["properties"]!["arguments"]!["properties"]!;
        Assert.Equal(arguments["revision"]!["$ref"]!.ToString(), arguments["optional"]!["anyOf"]![0]!["$ref"]!.ToString());
        var candidate = unit.Candidate!.DeepClone().AsObject();
        candidate["nodes"]!["consume"]!["arguments"]!["revision"] = new JsonObject { ["kind"] = "null" };
        candidate["nodes"]!["consume"]!["arguments"]!["optional"] = new JsonObject { ["kind"] = "omit" };
        Assert.Empty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        var result = PlanningConstruction.Apply(state.Graph, unit, candidate, preparation);
        var request = PlanningGraphValidation.Member(result.Workflows[0].Steps[^1].Input, "request")!;
        Assert.Equal("null", Assert.Single(request.Members).Value.Kind);
    }
}
