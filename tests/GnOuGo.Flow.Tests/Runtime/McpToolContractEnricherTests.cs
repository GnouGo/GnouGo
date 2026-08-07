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
            Meta = meta
        };

        var enriched = McpToolContractEnricher.EnrichTool(tool);

        Assert.NotSame(tool, enriched);
        Assert.True(JsonNode.DeepEquals(meta, enriched.Meta));
        Assert.NotSame(meta, enriched.Meta);
        Assert.NotNull(enriched.OutputSchema);
    }
}
