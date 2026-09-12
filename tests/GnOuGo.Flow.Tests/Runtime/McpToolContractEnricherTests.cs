using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class McpToolContractEnricherTests
{
    [Fact]
    public void EnrichTool_PreservesMetadataWhenInferringOutputSchema()
    {
        var meta = JsonNode.Parse("""
            {"gnougo":{"artifacts":{"version":1,"produces":[{"kind":"workspace.directory","pointer":"/workspaceRoot","mode":"materialize"}]}}}
            """);
        var tool = new McpToolInfo
        {
            Name = "create_workspace",
            Description = "Returns response.workspaceRoot for an existing materialized directory.",
            Meta = meta,
            ArtifactContract = new McpArtifactContractResolution(
                new McpArtifactContract(
                    1,
                    [new McpProducedArtifact("workspace.directory", "/workspaceRoot", "materialize")],
                    []),
                []),
            CompositionContract = new McpCapabilityCompositionResolution(
                new McpCapabilityComposition(
                    1,
                    McpCapabilityCompositionConventions.CompleteOperationKind,
                    [new McpEncapsulatedCapability("tool", "create_workspace_start")]),
                [])
        };

        var enriched = McpToolContractEnricher.EnrichTool(tool);

        Assert.NotSame(tool, enriched);
        Assert.True(JsonNode.DeepEquals(meta, enriched.Meta));
        Assert.NotSame(meta, enriched.Meta);
        Assert.NotNull(enriched.OutputSchema);
        Assert.Equal(McpOutputContractSources.Description, enriched.OutputContract?.Source);
        Assert.False(enriched.OutputContract!.Authoritative);
        Assert.Same(tool.ArtifactContract, enriched.ArtifactContract);
        Assert.Same(tool.CompositionContract, enriched.CompositionContract);
    }

    [Fact]
    public void EnrichTool_MarksValidDeclaredSchemaAsAuthoritative()
    {
        var schema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": { "items": { "type": "array", "items": { "type": "string" } } },
              "required": ["items"],
              "additionalProperties": false
            }
            """);
        var enriched = McpToolContractEnricher.EnrichTool(new McpToolInfo
        {
            Name = "list_items",
            OutputSchema = schema
        });

        Assert.Equal(McpOutputContractSources.ProtocolSchema, enriched.OutputContract?.Source);
        Assert.True(enriched.OutputContract!.Authoritative);
        Assert.Empty(enriched.OutputContract!.Errors);
        Assert.True(JsonNode.DeepEquals(
            schema,
            McpToolContractEnricher.GetAuthoritativeOutputSchema(enriched)));
    }

    [Fact]
    public void EnrichTool_DowngradesInvalidDeclaredSchemaToOpaque()
    {
        var enriched = McpToolContractEnricher.EnrichTool(new McpToolInfo
        {
            Name = "get_item",
            OutputSchema = JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": { "id": { "type": "string" } },
                  "required": ["missing"]
                }
                """)
        });

        Assert.False(enriched.OutputContract!.Authoritative);
        Assert.Contains(enriched.OutputContract!.Errors, static error =>
            error.Contains("undeclared property 'missing'", StringComparison.Ordinal));
        Assert.Null(McpToolContractEnricher.GetAuthoritativeOutputSchema(enriched));
    }

    [Fact]
    public void EnrichTool_TreatsExampleShapeAsAdvisory()
    {
        var enriched = McpToolContractEnricher.EnrichTool(new McpToolInfo
        {
            Name = "get_item",
            ExampleResponse = JsonNode.Parse("{\"id\":\"example\"}")
        });

        Assert.Equal(McpOutputContractSources.Example, enriched.OutputContract?.Source);
        Assert.False(enriched.OutputContract!.Authoritative);
        Assert.NotNull(enriched.OutputSchema);
        Assert.Null(McpToolContractEnricher.GetAuthoritativeOutputSchema(enriched));
    }

    [Fact]
    public void EnrichTool_DoesNotTrustClaimedAuthorityForAdvisoryOrMismatchedSchema()
    {
        var protocolSchema = JsonNode.Parse("""
            {"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}
            """);
        var advisorySchema = JsonNode.Parse("""
            {"type":"object","properties":{"checks":{"type":"array","items":{"type":"string"}}},"required":["checks"]}
            """);
        var enriched = McpToolContractEnricher.EnrichTool(new McpToolInfo
        {
            Name = "get_item",
            OutputSchema = protocolSchema,
            OutputContract = new McpOutputContractResolution(
                advisorySchema,
                McpOutputContractSources.Example,
                Authoritative: true,
                Errors: Array.Empty<string>())
        });

        Assert.False(enriched.OutputContract!.Authoritative);
        Assert.Contains(enriched.OutputContract.Errors, static error =>
            error.Contains("does not match OutputSchema", StringComparison.Ordinal));
        Assert.Null(McpToolContractEnricher.GetAuthoritativeOutputSchema(enriched));
    }
}
