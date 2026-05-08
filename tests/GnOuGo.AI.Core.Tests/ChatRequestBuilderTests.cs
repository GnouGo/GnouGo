
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GnOuGo.AI.Core.Tests;

public sealed class ChatRequestBuilderTests
{
    [Fact]
    public void NormalizeJsonSchemaForOpenAi_ConvertsNullTypeKeywordToStringNull()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "issue_number": {
              "anyOf": [
                { "type": "number" },
                { "type": null }
              ]
            }
          }
        }
        """)!;

        var normalized = ChatRequestBuilder.NormalizeJsonSchemaForOpenAi(schema.DeepClone()) as JsonObject;

        var anyOf = normalized!["properties"]!["issue_number"]!["anyOf"]!.AsArray();
        Assert.Equal("number", anyOf[0]!["type"]!.GetValue<string>());
        Assert.Equal("null", anyOf[1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void NormalizeJsonSchemaForOpenAi_ConvertsNullEntriesOnlyInsideTypeArrays()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "body": {
              "type": ["string", null],
              "enum": ["", null]
            }
          }
        }
        """)!;

        var normalized = ChatRequestBuilder.NormalizeJsonSchemaForOpenAi(schema.DeepClone()) as JsonObject;
        var body = normalized!["properties"]!["body"]!.AsObject();
        var type = body["type"]!.AsArray();
        var enumValues = body["enum"]!.AsArray();

        Assert.Equal("string", type[0]!.GetValue<string>());
        Assert.Equal("null", type[1]!.GetValue<string>());
        Assert.Equal("", enumValues[0]!.GetValue<string>());
        Assert.Null(enumValues[1]);
    }

    [Fact]
    public void NormalizeJsonSchemaForOpenAi_CoercesQuotedSchemaKeywordScalars()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": "false",
          "properties": {
            "issues": {
              "type": "array",
              "maxItems": "10",
              "minItems": "1",
              "uniqueItems": "true",
              "items": {
                "type": "object",
                "additionalProperties": "false",
                "properties": {
                  "score": {
                    "type": "number",
                    "minimum": "0.5",
                    "maximum": "1"
                  }
                }
              }
            }
          }
        }
        """)!;

        var normalized = ChatRequestBuilder.NormalizeJsonSchemaForOpenAi(schema.DeepClone()) as JsonObject;
        var issues = normalized!["properties"]!["issues"]!.AsObject();
        var score = issues["items"]!["properties"]!["score"]!.AsObject();

        Assert.False(normalized["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(10, issues["maxItems"]!.GetValue<int>());
        Assert.Equal(1, issues["minItems"]!.GetValue<int>());
        Assert.True(issues["uniqueItems"]!.GetValue<bool>());
        Assert.False(issues["items"]!["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(0.5, score["minimum"]!.GetValue<double>());
        Assert.Equal(1d, score["maximum"]!.GetValue<double>());
    }

    [Fact]
    public void OpenAiFull_NormalizesYamlDerivedNullTypeAndPatchesAdditionalProperties()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "issues": {
              "type": "array",
              "maxItems": "10",
              "items": {
                "type": "object",
                "additionalProperties": "true",
                "properties": {
                  "issue_link": { "type": "string" },
                  "issue_number": {
                    "anyOf": [
                      { "type": "number" },
                      { "type": null }
                    ]
                  }
                },
                "required": ["issue_link", "issue_number"]
              }
            }
          },
          "required": ["issues"]
        }
        """)!;

        var payload = ChatRequestBuilder.OpenAiFull(
            model: "gpt-4o-mini",
            prompt: "Extract issues",
            structuredOutputSchema: schema,
            structuredOutputStrict: true);

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var responseSchema = root
            .GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("schema");
        var issueNumberAnyOf = responseSchema
            .GetProperty("properties")
            .GetProperty("issues")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("issue_number")
            .GetProperty("anyOf");

        Assert.Equal("null", issueNumberAnyOf[1].GetProperty("type").GetString());
        Assert.Equal(10, responseSchema.GetProperty("properties").GetProperty("issues").GetProperty("maxItems").GetInt32());
        Assert.False(responseSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(responseSchema.GetProperty("properties").GetProperty("issues").GetProperty("items").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void OpenAiFull_DoesNotMutateOriginalSchema()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "value": {
              "anyOf": [
                { "type": "string" },
                { "type": null }
              ]
            }
          },
          "required": ["value"]
        }
        """)!;

        _ = ChatRequestBuilder.OpenAiFull(
            model: "gpt-4o-mini",
            prompt: "test",
            structuredOutputSchema: schema,
            structuredOutputStrict: true);

        var anyOf = schema["properties"]!["value"]!["anyOf"]!.AsArray();
        Assert.Null(anyOf[1]!["type"]);
        Assert.False(schema.AsObject().ContainsKey("additionalProperties"));
    }
}

