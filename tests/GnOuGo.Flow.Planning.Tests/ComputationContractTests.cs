using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ComputationContractTests
{
    [Theory]
    [InlineData("records", "index", "message", "body")]
    [InlineData("éléments", "position", "explication", "inventé")]
    public void IndexedAliasesRetainTheirDeclaredItemContract(string records, string index, string declared, string missing)
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Inputs = [new() { Name = "items", Schema = new() { Type = "array",
            Items = new() { Type = "object", Properties = [new() { Name = declared, Schema = new() { Type = "string" } }] } } },
            new() { Name = "position", Schema = new() { Type = "integer" } }];
        var value = new PlanningValue { Kind = "compute", Text = $"const item = {records}[{index}] || {{}}; return item.{missing} ?? item.{declared};",
            Members = [new(records, new() { Kind = "input", Source = "items" }), new(index, new() { Kind = "input", Source = "position" })] };
        workflow.Steps[0].Input = Obj(("message", value));
        var finding = Assert.Single(PlanningExecutableValidation.Validate(graph, Preparation()), d => d.Code == "COMPUTATION_FIELD_UNDECLARED");
        Assert.Contains(missing, finding.Message); Assert.Contains(declared, finding.Message);
        value.Text = $"const item = {records}[{index}] ?? {{}}; return item.{declared};";
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(graph, Preparation()), d => d.Code == "COMPUTATION_FIELD_UNDECLARED");
        value.Text = $"const item = {records}[{index}] || {{}}; {{ const item = {{ {missing}: 'local' }}; item.{missing}; }} return item.{declared};";
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(graph, Preparation()), d => d.Code == "COMPUTATION_FIELD_UNDECLARED");
    }

    [Theory]
    [InlineData("records.message", true)]
    [InlineData("const alias = records; return alias['message'];", true)]
    [InlineData("records.length", false)]
    [InlineData("records[0].message", false)]
    [InlineData("records['0'].message", false)]
    [InlineData("records.map(row => row.message).join(',')", false)]
    public void CollectionsCannotBeUsedAsIndividualItems(string expression, bool invalid)
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Inputs = [new() { Name = "records", Schema = new() { Type = "array", Nullable = true,
            Items = new() { Type = "object", Properties = [new() { Name = "message", Schema = new() { Type = "string" } }] } } }];
        var value = new PlanningValue { Kind = "compute", Text = expression, Members = [new("records", new() { Kind = "input", Source = "records" })] };
        workflow.Steps[0].Input = Obj(("message", value));
        var findings = PlanningExecutableValidation.Validate(graph, Preparation());
        Assert.Equal(invalid, findings.Any(d => d.Code == "COMPUTATION_COLLECTION_FIELD_INVALID"));
        value.Text = "records[0].invented";
        Assert.Contains(PlanningExecutableValidation.Validate(graph, Preparation()), d => d.Code == "COMPUTATION_FIELD_UNDECLARED");
    }

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
