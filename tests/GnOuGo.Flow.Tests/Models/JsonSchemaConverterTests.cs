using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using Xunit;

namespace GnOuGo.Flow.Tests.Models;

public class JsonSchemaConverterTests
{
    [Fact]
    public void InputsToJsonSchema_SimpleTypes_ProducesValidSchema()
    {
        var inputs = new Dictionary<string, InputDef>
        {
            ["name"] = new() { Type = "string", Required = true, Description = "User name" },
            ["count"] = new() { Type = "number", Required = false, Default = "10" }
        };

        var schema = JsonSchemaConverter.InputsToJsonSchema(inputs) as JsonObject;
        Assert.NotNull(schema);
        Assert.Equal("object", schema!["type"]!.GetValue<string>());

        var props = schema["properties"] as JsonObject;
        Assert.NotNull(props);
        Assert.Equal("string", props!["name"]!["type"]!.GetValue<string>());
        Assert.Equal("User name", props["name"]!["description"]!.GetValue<string>());
        Assert.Equal("number", props["count"]!["type"]!.GetValue<string>());
        Assert.Equal(10d, props["count"]!["default"]!.GetValue<double>());

        var required = schema["required"] as JsonArray;
        Assert.NotNull(required);
        Assert.Single(required!);
        Assert.Equal("name", required[0]!.GetValue<string>());
    }

    [Fact]
    public void InputsToJsonSchema_ArrayWithItems_MapsCorrectly()
    {
        var inputs = new Dictionary<string, InputDef>
        {
            ["tags"] = new()
            {
                Type = "array",
                Required = true,
                Items = new InputDef { Type = "string" }
            }
        };

        var schema = JsonSchemaConverter.InputsToJsonSchema(inputs) as JsonObject;
        var props = schema!["properties"] as JsonObject;
        var tagsSchema = props!["tags"] as JsonObject;
        Assert.Equal("array", tagsSchema!["type"]!.GetValue<string>());
        Assert.Equal("string", tagsSchema["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void InputsToJsonSchema_ObjectWithProperties_MapsCorrectly()
    {
        var inputs = new Dictionary<string, InputDef>
        {
            ["config"] = new()
            {
                Type = "object",
                Required = true,
                Properties = new Dictionary<string, InputDef>
                {
                    ["host"] = new() { Type = "string", Required = true },
                    ["port"] = new() { Type = "number", Required = false }
                },
                RequiredProperties = new List<string> { "host" }
            }
        };

        var schema = JsonSchemaConverter.InputsToJsonSchema(inputs) as JsonObject;
        var props = schema!["properties"] as JsonObject;
        var configSchema = props!["config"] as JsonObject;
        Assert.Equal("object", configSchema!["type"]!.GetValue<string>());

        var configProps = configSchema["properties"] as JsonObject;
        Assert.NotNull(configProps);
        Assert.Equal("string", configProps!["host"]!["type"]!.GetValue<string>());
        Assert.Equal("number", configProps["port"]!["type"]!.GetValue<string>());

        var required = configSchema["required"] as JsonArray;
        Assert.NotNull(required);
        Assert.Equal("host", required![0]!.GetValue<string>());
    }

    [Fact]
    public void InputsToJsonSchema_DoesNotExposeInternalClosedObjectPolicy()
    {
        var inputs = new Dictionary<string, InputDef>
        {
            ["config"] = new()
            {
                Type = "object",
                Properties = new Dictionary<string, InputDef>
                {
                    ["host"] = new() { Type = "string" }
                }
            }
        };

        var schema = Assert.IsType<JsonObject>(JsonSchemaConverter.InputsToJsonSchema(inputs));
        var props = Assert.IsType<JsonObject>(schema["properties"]);
        var configSchema = Assert.IsType<JsonObject>(props["config"]);

        Assert.False(schema.ContainsKey("additionalProperties"));
        Assert.False(configSchema.ContainsKey("additionalProperties"));
    }

    [Fact]
    public void InputsToJsonSchema_DictionaryType_MapsToObjectWithAdditionalProperties()
    {
        var inputs = new Dictionary<string, InputDef>
        {
            ["scores"] = new()
            {
                Type = "dictionary",
                Required = true,
                AdditionalProperties = new InputDef { Type = "number" }
            }
        };

        var schema = JsonSchemaConverter.InputsToJsonSchema(inputs) as JsonObject;
        var props = schema!["properties"] as JsonObject;
        var scoresSchema = props!["scores"] as JsonObject;
        Assert.Equal("object", scoresSchema!["type"]!.GetValue<string>());
        Assert.Equal("number", scoresSchema["additionalProperties"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void OutputsToJsonSchema_SimpleTypes_ProducesValidSchema()
    {
        var outputs = new Dictionary<string, OutputDef>
        {
            ["result"] = new() { Type = "string", Description = "The result text" },
            ["count"] = new() { Type = "number", Description = "Number of items" }
        };

        var schema = JsonSchemaConverter.OutputsToJsonSchema(outputs) as JsonObject;
        Assert.NotNull(schema);
        Assert.Equal("object", schema!["type"]!.GetValue<string>());

        var props = schema["properties"] as JsonObject;
        Assert.NotNull(props);
        Assert.Equal("string", props!["result"]!["type"]!.GetValue<string>());
        Assert.Equal("The result text", props["result"]!["description"]!.GetValue<string>());
        Assert.Equal("number", props["count"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void OutputsToJsonSchema_ArrayOfObjects_NestedCorrectly()
    {
        var outputs = new Dictionary<string, OutputDef>
        {
            ["items"] = new()
            {
                Type = "array",
                Description = "Research items",
                Items = new OutputDef
                {
                    Type = "object",
                    Properties = new Dictionary<string, OutputDef>
                    {
                        ["name"] = new() { Type = "string" },
                        ["score"] = new() { Type = "number" }
                    }
                }
            }
        };

        var schema = JsonSchemaConverter.OutputsToJsonSchema(outputs) as JsonObject;
        var props = schema!["properties"] as JsonObject;
        var itemsSchema = props!["items"] as JsonObject;
        Assert.Equal("array", itemsSchema!["type"]!.GetValue<string>());

        var nestedItems = itemsSchema["items"] as JsonObject;
        Assert.Equal("object", nestedItems!["type"]!.GetValue<string>());
        var nestedProps = nestedItems["properties"] as JsonObject;
        Assert.Equal("string", nestedProps!["name"]!["type"]!.GetValue<string>());
        Assert.Equal("number", nestedProps["score"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void OutputsToJsonSchema_AnyType_OmitsTypeConstraint()
    {
        var outputs = new Dictionary<string, OutputDef>
        {
            ["data"] = new() { Type = "any", Description = "Arbitrary data" }
        };

        var schema = JsonSchemaConverter.OutputsToJsonSchema(outputs) as JsonObject;
        var props = schema!["properties"] as JsonObject;
        var dataSchema = props!["data"] as JsonObject;
        Assert.False(dataSchema!.ContainsKey("type")); // "any" means no type constraint
        Assert.Equal("Arbitrary data", dataSchema["description"]!.GetValue<string>());
    }
}
