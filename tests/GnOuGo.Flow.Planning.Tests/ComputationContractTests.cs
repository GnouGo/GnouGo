using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ComputationContractTests
{
    [Theory]
    [InlineData("payload", "actual", "missing")]
    [InlineData("réponse", "déclaré", "inventé")]
    public void AliasesCannotInventFieldsOnDeclaredProducerContracts(string parameter, string declared, string missing)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        workflow.Inputs.Add(new() { Name = "source", Schema = new() { Type = "object", Properties = [new() { Name = declared, Required = true, Schema = new() { Type = "string" } }] } });
        workflow.Steps[0].Input = Obj(("message", new() { Kind = "compute", Text = $"const alias = {parameter} || {{}}; return alias['{missing}'] || '';",
            Members = [new(parameter, new() { Kind = "input", Source = "source" })] }));
        var finding = Assert.Single(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "COMPUTATION_FIELD_UNDECLARED");
        Assert.Contains(missing, finding.Message); Assert.Contains(declared, finding.Message);
        workflow.Steps[0].Input.Members[0].Value.Text = $"const alias = {parameter} || {{}}; return alias['{declared}'].trim();";
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "COMPUTATION_FIELD_UNDECLARED");
        workflow.Steps[0].Input.Members[0].Value.Text = $"const alias = {parameter}; {{ const alias = {{'{missing}':'local'}}; alias['{missing}']; }} return alias['{declared}'];";
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "COMPUTATION_FIELD_UNDECLARED");
    }

    [Fact]
    public void OpaqueResultsAllowWholeSerializationButNoInventedTextField()
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        preparation.Capabilities.Add(new() { Id = "opaque", StepType = "mcp.call", Server = "renamed", Method = "observe", Kind = "tool" });
        workflow.Steps.Insert(0, new() { Key = "observe", Type = "mcp.call", CapabilityId = "opaque", Input = Obj(("request", Obj())) });
        var value = new PlanningValue { Kind = "compute", Text = "JSON.stringify(payload)", Members = [new("payload", new() { Kind = "output", Source = "observe" })] };
        workflow.Steps[1].Input = Obj(("message", value));
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "COMPUTATION_FIELD_UNDECLARED");
        value.Text = "payload.text";
        Assert.Contains(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "COMPUTATION_FIELD_UNDECLARED");
    }
}
