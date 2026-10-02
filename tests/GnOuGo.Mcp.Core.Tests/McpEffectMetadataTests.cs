using System.Text.Json.Nodes;

namespace GnOuGo.Mcp.Core.Tests;

public sealed class McpEffectMetadataTests
{
    [Fact]
    public void PublicationPreservesOtherMetadataAndRejectsContradictionsAtomically()
    {
        var tool = new ModelContextProtocol.Protocol.Tool { Name = "arbitrary", Meta = JsonNode.Parse("""{"gnougo":{"artifacts":{"version":1},"management":{"visibility":"management_only"}}}""")!.AsObject() };
        var original = tool.Meta.DeepClone();
        McpEffectMetadata.Publish(tool, "read");
        Assert.Equal("read", McpEffectMetadata.Resolve(tool.Meta));
        Assert.True(JsonNode.DeepEquals(original["gnougo"]!["artifacts"], tool.Meta["gnougo"]!["artifacts"]));
        Assert.True(JsonNode.DeepEquals(original["gnougo"]!["management"], tool.Meta["gnougo"]!["management"]));
        tool.Annotations = new() { ReadOnlyHint = true }; var before = tool.Meta.ToJsonString();
        Assert.Throws<ArgumentException>(() => McpEffectMetadata.Publish(tool, "write"));
        Assert.Equal(before, tool.Meta.ToJsonString());
    }
    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("execute")]
    [InlineData("lifecycle")]
    [InlineData("none")]
    public void ResolvesDeclaredEffectWithoutNamesOrPermissionGrants(string kind)
    {
        var meta = new JsonObject { ["gnougo"] = new JsonObject { ["effect"] = new JsonObject { ["version"] = 1, ["kind"] = kind } } };
        Assert.Equal(kind, McpEffectMetadata.Resolve(meta));
        if (kind is not ("read" or "none")) Assert.Throws<ArgumentException>(() => McpEffectMetadata.Resolve(meta, true));
    }

    [Theory]
    [InlineData("{\"gnougo\":{\"effect\":null}}")]
    [InlineData("{\"gnougo\":{\"effect\":{\"version\":2,\"kind\":\"read\"}}}")]
    [InlineData("{\"gnougo\":{\"effect\":{\"version\":1,\"kind\":\"unknown\"}}}")]
    [InlineData("{\"gnougo\":{\"effect\":{\"version\":1,\"kind\":\"write\",\"approve\":true}}}")]
    [InlineData("{\"gnougo\":[]}")]
    public void MalformedDeclarationsDoNotFallBackToHints(string metadata)
        => Assert.Throws<ArgumentException>(() => McpEffectMetadata.Resolve(JsonNode.Parse(metadata), true));

    [Fact]
    public void MissingDeclarationPreservesReadOnlyFallback()
    {
        Assert.Equal("read", McpEffectMetadata.Resolve(null, true));
        Assert.Equal("unknown", McpEffectMetadata.Resolve(new JsonObject(), false));
        Assert.Equal("unknown", McpEffectMetadata.Resolve(JsonNode.Parse("""{"gnougo":{"unrelated":{}}}""")));
    }
}
