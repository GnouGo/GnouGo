using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class NullableSchemaContractTests
{
    [Fact]
    public void NullableObjectsRetainNestedContractsWhenAssignedToWorkflowOutputs()
    {
        var source = FlowTypeDescriptorConverter.FromJsonSchema(JsonNode.Parse("""
            {"type":"array","items":{"type":"object","properties":{
              "observation":{"type":["object","null"],"properties":{
                "exitCode":{"type":["integer","null"]}},"required":["exitCode"],"additionalProperties":false}
            },"required":["observation"],"additionalProperties":false}}
            """));
        var destination = FlowTypeDescriptorConverter.FromOutputDef(new OutputDef
        {
            Type = "array", Items = new()
            {
                Type = "object", RequiredProperties = ["observation"], Properties = new()
                {
                    ["observation"] = new()
                    {
                        Type = "object", Nullable = true, RequiredProperties = ["exitCode"],
                        Properties = new() { ["exitCode"] = new() { Type = "integer", Nullable = true } }
                    }
                }
            }
        });
        Assert.Null(source.FindAssignmentIssue(destination));
        var observation = source.Items!.Properties["observation"].Type.RemoveNull();
        Assert.True(observation.Properties["exitCode"].Required);
        Assert.False(observation.AllowsAdditionalProperties);
        Assert.Equal("integer or null", observation.Properties["exitCode"].Type.Describe());

        var missingField = FlowTypeDescriptor.Object();
        Assert.NotNull(missingField.FindAssignmentIssue(source.Items.Properties["observation"].Type));
        var wrongField = FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>
        { ["exitCode"] = new(FlowTypeDescriptor.String, Required: true) });
        Assert.NotNull(wrongField.FindAssignmentIssue(source.Items.Properties["observation"].Type));
    }

    [Fact]
    public void NullableArraysRetainItemTypesAndRejectIncompatibleElements()
    {
        var schema = JsonNode.Parse("""{"type":["array","null"],"items":{"type":"integer"}}""")!;
        var before = schema.ToJsonString();
        var type = FlowTypeDescriptorConverter.FromJsonSchema(schema);
        Assert.Equal(FlowTypeKind.Integer, type.RemoveNull().Items!.Kind);
        Assert.Null(FlowTypeDescriptor.Array(FlowTypeDescriptor.Integer).FindAssignmentIssue(type));
        Assert.Null(FlowTypeDescriptor.Null.FindAssignmentIssue(type));
        Assert.NotNull(FlowTypeDescriptor.Array(FlowTypeDescriptor.String).FindAssignmentIssue(type));
        Assert.Equal(before, schema.ToJsonString());
    }

    [Fact]
    public void NullablePrimitivesRetainEnumAndNumericConstraints()
    {
        var status = FlowTypeDescriptorConverter.FromJsonSchema(JsonNode.Parse("""{"type":["string","null"],"enum":["passed","failed",null]}"""));
        Assert.Equal(new[] { "passed", "failed" }, status.RemoveNull().EnumValues);
        Assert.Null(FlowTypeDescriptor.Enum("passed").FindAssignmentIssue(status));
        Assert.NotNull(FlowTypeDescriptor.Enum("invented").FindAssignmentIssue(status));
        var number = FlowTypeDescriptorConverter.FromJsonSchema(JsonNode.Parse("""{"type":["integer","null"],"minimum":0}"""));
        Assert.Equal(0m, number.RemoveNull().Minimum);
    }
}
