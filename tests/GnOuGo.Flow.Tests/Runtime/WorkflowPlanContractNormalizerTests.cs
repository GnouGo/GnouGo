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
    public void BuildWorkflowOutputFromDescriptor_PreservesNullableNestedProperties()
    {
        var descriptor = FlowTypeDescriptor.Array(FlowTypeDescriptor.Object(
            new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
            {
                ["path"] = new(FlowTypeDescriptor.String, Required: true),
                ["previousPath"] = new(
                    FlowTypeDescriptor.Union([FlowTypeDescriptor.String, FlowTypeDescriptor.Null]),
                    Required: true),
                ["metadata"] = new(FlowTypeDescriptor.Object(
                    new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
                    {
                        ["status"] = new(FlowTypeDescriptor.String, Required: true),
                        ["cursor"] = new(
                            FlowTypeDescriptor.Union([FlowTypeDescriptor.String, FlowTypeDescriptor.Null]),
                            Required: true)
                    }), Required: true)
            }));

        var output = WorkflowPlanContractNormalizer.BuildWorkflowOutputFromDescriptor(
            descriptor,
            "${data.steps.compare.files}");

        Assert.NotNull(output);
        var items = Assert.IsType<YamlMappingNode>(output.Children[new YamlScalarNode("items")]);
        var properties = Assert.IsType<YamlMappingNode>(items.Children[new YamlScalarNode("properties")]);
        Assert.True(properties.Children.ContainsKey(new YamlScalarNode("path")));
        Assert.True(Assert.IsType<YamlMappingNode>(properties.Children[new YamlScalarNode("previousPath")]).GetBool("nullable"));
        var metadata = Assert.IsType<YamlMappingNode>(properties.Children[new YamlScalarNode("metadata")]);
        var metadataProperties = Assert.IsType<YamlMappingNode>(metadata.Children[new YamlScalarNode("properties")]);
        Assert.True(metadataProperties.Children.ContainsKey(new YamlScalarNode("status")));
        Assert.True(Assert.IsType<YamlMappingNode>(metadataProperties.Children[new YamlScalarNode("cursor")]).GetBool("nullable"));

        var required = Assert.IsType<YamlSequenceNode>(items.Children[new YamlScalarNode("required_properties")]);
        Assert.Equal(["path", "previousPath", "metadata"], required.Children.OfType<YamlScalarNode>().Select(static item => item.Value));
    }

    [Fact]
    public void BuildWorkflowOutputFromDescriptor_PreservesNullableRootAndNullableArrayItems()
    {
        var nullableString = FlowTypeDescriptor.Union([FlowTypeDescriptor.String, FlowTypeDescriptor.Null]);

        var nullableRoot = WorkflowPlanContractNormalizer.BuildWorkflowOutputFromDescriptor(
            nullableString,
            "${data.steps.read.cursor}");
        var nullableItems = WorkflowPlanContractNormalizer.BuildWorkflowOutputFromDescriptor(
            FlowTypeDescriptor.Array(nullableString),
            "${data.steps.read.values}");

        Assert.NotNull(nullableRoot);
        Assert.True(nullableRoot.GetBool("nullable"));
        Assert.NotNull(nullableItems);
        Assert.True(Assert.IsType<YamlMappingNode>(nullableItems.Children[new YamlScalarNode("items")]).GetBool("nullable"));
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

    [Fact]
    public void PruneWeakNestedOutputProperties_RemovesOnlyUnverifiableChildrenAndRequiredNames()
    {
        var schema = Assert.IsType<YamlMappingNode>(WorkflowPlanContractNormalizer.JsonToYaml(JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "kept": { "type": "string" },
            "paths": { "type": "array" },
            "optional": {
              "type": "object",
              "properties": {
                "anyOf": { "type": "any" }
              }
            }
          },
          "required_properties": ["kept", "paths", "optional"]
        }
        """)));

        Assert.True(WorkflowPlanContractNormalizer.PruneWeakNestedOutputProperties(schema));

        var properties = Assert.IsType<YamlMappingNode>(schema.Children[new YamlScalarNode("properties")]);
        Assert.True(properties.Children.ContainsKey(new YamlScalarNode("kept")));
        Assert.False(properties.Children.ContainsKey(new YamlScalarNode("paths")));
        Assert.False(properties.Children.ContainsKey(new YamlScalarNode("optional")));
        var required = Assert.IsType<YamlSequenceNode>(schema.Children[new YamlScalarNode("required_properties")]);
        Assert.Equal(["kept"], required.Children.OfType<YamlScalarNode>().Select(static item => item.Value));
        Assert.False(WorkflowPlanContractNormalizer.IsWeakYamlOutputSchema(schema));
    }

    [Fact]
    public void PruneWeakNestedOutputProperties_DoesNotInventRootArrayItems()
    {
        var schema = Assert.IsType<YamlMappingNode>(WorkflowPlanContractNormalizer.JsonToYaml(JsonNode.Parse("""
        { "type": "array" }
        """)));

        Assert.False(WorkflowPlanContractNormalizer.PruneWeakNestedOutputProperties(schema));
        Assert.True(WorkflowPlanContractNormalizer.IsWeakYamlOutputSchema(schema));
    }

    [Fact]
    public void PruneWeakNestedOutputProperties_CanonicalizesNullableScalar()
    {
        var schema = Assert.IsType<YamlMappingNode>(WorkflowPlanContractNormalizer.JsonToYaml(JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "cursor": {
              "anyOf": [
                { "type": "string" },
                { "type": "null" }
              ]
            }
          }
        }
        """)));

        Assert.True(WorkflowPlanContractNormalizer.PruneWeakNestedOutputProperties(schema));

        var properties = Assert.IsType<YamlMappingNode>(schema.Children[new YamlScalarNode("properties")]);
        var cursor = Assert.IsType<YamlMappingNode>(properties.Children[new YamlScalarNode("cursor")]);
        Assert.Equal("string", cursor.GetScalar("type"));
        Assert.True(cursor.GetBool("nullable"));
        Assert.False(WorkflowPlanContractNormalizer.IsWeakYamlOutputSchema(schema));
    }


    [Fact]
    public void PruneWeakNestedOutputProperties_CanonicalizesRepresentableNonNullableUnion()
    {
        var schema = Assert.IsType<YamlMappingNode>(WorkflowPlanContractNormalizer.JsonToYaml(JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "value": {
              "anyOf": [
                { "type": "string" },
                { "type": "string" }
              ]
            }
          }
        }
        """)));

        Assert.True(WorkflowPlanContractNormalizer.PruneWeakNestedOutputProperties(schema));

        var value = Assert.IsType<YamlMappingNode>(
            Assert.IsType<YamlMappingNode>(schema.Children[new YamlScalarNode("properties")])
                .Children[new YamlScalarNode("value")]);
        Assert.Equal("string", Assert.IsType<YamlScalarNode>(value.Children[new YamlScalarNode("type")]).Value);
        Assert.False(value.Children.ContainsKey(new YamlScalarNode("anyOf")));
        Assert.False(WorkflowPlanContractNormalizer.IsWeakYamlOutputSchema(schema));
    }

    [Fact]
    public void NormalizeSetOutputSchema_ConvertsWorkflowDictionaryShorthandToJsonSchema()
    {
        var schema = Assert.IsType<YamlMappingNode>(WorkflowPlanContractNormalizer.JsonToYaml(JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "additional_code_context": {
              "type": "dictionary",
              "additional_properties": "string"
            }
          },
          "required_properties": ["additional_code_context"]
        }
        """)));

        Assert.True(WorkflowPlanContractNormalizer.NormalizeSetOutputSchema(schema));

        var normalized = Assert.IsType<JsonObject>(WorkflowParser.YamlToJson(schema));
        var dictionary = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(normalized["properties"])["additional_code_context"]);
        Assert.Equal("object", dictionary["type"]?.GetValue<string>());
        Assert.Equal("string", Assert.IsType<JsonObject>(dictionary["additionalProperties"])["type"]?.GetValue<string>());
        Assert.Null(dictionary["additional_properties"]);
        Assert.NotNull(normalized["required"]);
        Assert.Null(normalized["required_properties"]);
        Assert.Empty(JsonSchemaContractValidator.ValidateSchema(normalized, strictProfile: false));
    }
}
