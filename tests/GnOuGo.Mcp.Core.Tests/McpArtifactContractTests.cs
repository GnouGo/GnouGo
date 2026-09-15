using System.Text.Json.Nodes;
using GnOuGo.Mcp.Core;

namespace GnOuGo.Mcp.Core.Tests;

public sealed class McpArtifactContractTests
{
    [Theory]
    [InlineData("{\"gnougo\":true}")]
    [InlineData("{\"gnougo\":{\"artifacts\":[]}}")]
    public void ParseAndValidate_RejectsMalformedMetadataContainers(string json)
    {
        var result = McpArtifactContractParser.ParseAndValidate(JsonNode.Parse(json), null, null);

        Assert.True(result.IsDeclared);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ParseAndValidate_AcceptsWorkspaceProducer()
    {
        var result = McpArtifactContractParser.ParseAndValidate(
            ToolMeta(McpArtifactContractMetadata.WorkspaceDirectoryProducerProjectRootRelativeJson),
            null,
            JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": { "projectRootRelative": { "type": "string" } },
                  "required": ["projectRootRelative"]
                }
                """));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        var produced = Assert.Single(result.Contract!.Produces);
        Assert.Equal(McpArtifactContractMetadata.WorkspaceDirectoryKind, produced.Kind);
        Assert.Equal("/projectRootRelative", produced.Pointer);
        Assert.Equal(McpArtifactContractMetadata.MaterializeMode, produced.Mode);
    }

    [Fact]
    public void ParseAndValidate_AcceptsRequiredWorkspaceConsumer()
    {
        var result = McpArtifactContractParser.ParseAndValidate(
            ToolMeta(McpArtifactContractMetadata.WorkspaceDirectoryConsumerProjectRootJson),
            JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": { "projectRoot": { "type": "string" } },
                  "required": ["projectRoot"]
                }
                """),
            null);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        var consumed = Assert.Single(result.Contract!.Consumes);
        Assert.True(consumed.Required);
        Assert.Equal("/projectRoot", consumed.Pointer);
    }

    [Fact]
    public void ParseAndValidate_RejectsUnknownOrOptionalSchemaField()
    {
        var result = McpArtifactContractParser.ParseAndValidate(
            ToolMeta(McpArtifactContractMetadata.WorkspaceDirectoryConsumerProjectRootJson),
            JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": { "projectRoot": { "type": "string" } }
                }
                """),
            null);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("required schema property", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseAndValidate_RejectsNonStringArtifact()
    {
        var result = McpArtifactContractParser.ParseAndValidate(
            ToolMeta(McpArtifactContractMetadata.WorkspaceDirectoryProducerProjectRootRelativeJson),
            null,
            JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": { "projectRootRelative": { "type": "integer" } },
                  "required": ["projectRootRelative"]
                }
                """));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("string-compatible", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseAndValidate_TreatsMissingMetadataAsUndeclared()
    {
        var result = McpArtifactContractParser.ParseAndValidate(new JsonObject(), null, null);

        Assert.False(result.IsDeclared);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("json_array", true)]
    [InlineData("unknown", false)]
    public void DeclaredCollectionEncodingIsValidated(string encoding, bool valid)
    {
        var result = McpArtifactContractParser.ParseAndValidate(ToolMeta("""
            {"artifacts":{"version":1,"produces":[{"kind":"records","pointer":"/value","mode":"materialize","encoding":"ENCODING"}],"consumes":[]}}
            """.Replace("ENCODING", encoding, StringComparison.Ordinal)), null,
            JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}"""));
        Assert.Equal(valid, result.IsValid);
        if (valid) Assert.Equal(encoding, Assert.Single(result.Contract!.Produces).Encoding);
    }

    private static JsonObject ToolMeta(string gnougoJson)
        => new() { [McpArtifactContractMetadata.MetaPropertyName] = JsonNode.Parse(gnougoJson) };
}
