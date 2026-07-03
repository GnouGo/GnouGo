using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class WorkflowPlanContractNormalizerTests
{
    [Fact]
    public void BuildWorkflowOutputFromDescriptor_IncludesExpressionAndConcreteSchema()
    {
        var descriptor = FlowTypeDescriptor.Array(FlowTypeDescriptor.Object(
            new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
            {
                ["id"] = new(FlowTypeDescriptor.String, Required: true),
                ["score"] = new(FlowTypeDescriptor.Number, Required: true)
            }));

        var output = WorkflowPlanContractNormalizer.BuildWorkflowOutputFromDescriptor(
            descriptor,
            "${data.steps.collect.records}");

        Assert.NotNull(output);
        Assert.Equal("${data.steps.collect.records}", output.GetScalar("expr"));
        Assert.Equal("array", output.GetScalar("type"));
        var items = output.GetMapping("items");
        Assert.NotNull(items);
        Assert.Equal("object", items.GetScalar("type"));
        Assert.NotNull(items.GetMapping("properties")?.GetMapping("id"));
        Assert.NotNull(items.GetMapping("properties")?.GetMapping("score"));
    }

    [Fact]
    public void BuildSkillOutputFromWorkflowOutputYaml_StripsExpression()
    {
        var workflowOutput = WorkflowPlanContractNormalizer.BuildWorkflowOutputFromDescriptor(
            FlowTypeDescriptor.String,
            "${data.steps.render.text}");

        Assert.NotNull(workflowOutput);
        var skillOutput = WorkflowPlanContractNormalizer.BuildSkillOutputFromWorkflowOutputYaml(workflowOutput);

        Assert.NotNull(skillOutput);
        Assert.False(skillOutput.Children.ContainsKey(new YamlScalarNode("expr")));
        Assert.Equal("string", skillOutput.GetScalar("type"));
    }

    [Fact]
    public void IsWeakDescriptor_RejectsVagueOutputShapes()
    {
        Assert.True(WorkflowPlanContractNormalizer.IsWeakDescriptor(FlowTypeDescriptor.Any));
        Assert.True(WorkflowPlanContractNormalizer.IsWeakDescriptor(FlowTypeDescriptor.Array()));
        Assert.True(WorkflowPlanContractNormalizer.IsWeakDescriptor(FlowTypeDescriptor.Object()));
        Assert.True(WorkflowPlanContractNormalizer.IsWeakDescriptor(FlowTypeDescriptor.Dictionary()));

        Assert.False(WorkflowPlanContractNormalizer.IsWeakDescriptor(FlowTypeDescriptor.String));
        Assert.False(WorkflowPlanContractNormalizer.IsWeakDescriptor(FlowTypeDescriptor.Array(FlowTypeDescriptor.String)));
        Assert.False(WorkflowPlanContractNormalizer.IsWeakDescriptor(FlowTypeDescriptor.Dictionary(FlowTypeDescriptor.Number)));
        Assert.False(WorkflowPlanContractNormalizer.IsWeakDescriptor(FlowTypeDescriptor.Object(
            new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
            {
                ["name"] = new(FlowTypeDescriptor.String, Required: true)
            })));
    }

    [Fact]
    public void CollectWeakOutputSchemaDiagnostics_ReportsExactNestedPaths()
    {
        var output = new OutputDef
        {
            Type = "array",
            Items = new OutputDef
            {
                Type = "object",
                Properties = new Dictionary<string, OutputDef>(StringComparer.Ordinal)
                {
                    ["id"] = new() { Type = "any" }
                },
                RequiredProperties = new List<string> { "id" }
            }
        };
        var diagnostics = new JsonArray();

        WorkflowPlanContractNormalizer.CollectWeakOutputSchemaDiagnostics(
            output,
            "workflows.main.outputs.records",
            diagnostics,
            allowSkillScalarTypeShorthand: false);

        var diagnostic = Assert.IsType<JsonObject>(Assert.Single(diagnostics));
        Assert.Equal("WEAK_OUTPUT_SCHEMA", diagnostic["code"]?.GetValue<string>());
        Assert.Equal("workflows.main.outputs.records.items.properties.id", diagnostic["location"]?.GetValue<string>());
    }

    [Fact]
    public void BuildCanonicalSchemaYaml_ExpandsNestedScalarProperties()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id": "string",
              "handled": { "type": "boolean" }
            },
            "required_properties": ["id", "handled"]
          }
        }
        """);

        var yaml = WorkflowPlanContractNormalizer.BuildCanonicalSchemaYaml(schema);
        var json = WorkflowParser.YamlToJson(yaml);
        var idSchema = json?["items"]?["properties"]?["id"];

        Assert.IsType<JsonObject>(idSchema);
        Assert.Equal("string", idSchema?["type"]?.GetValue<string>());
    }
}
