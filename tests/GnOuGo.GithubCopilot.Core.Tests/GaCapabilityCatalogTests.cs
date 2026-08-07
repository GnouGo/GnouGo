using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class GaCapabilityCatalogTests
{
    [Fact]
    public void Catalog_ContainsOnlyGaCapabilitiesAndStableMcpVersions()
    {
        var catalog = GaCapabilityCatalog.Describe();

        Assert.Equal("2.0.0", catalog.McpPackageVersion);
        Assert.Equal("2026-07-28", catalog.RequiredMcpRevision);
        Assert.Equal("2025-11-25", catalog.FallbackMcpRevision);
        Assert.All(catalog.Capabilities, capability => Assert.Equal("ga", capability.Stability));
        Assert.DoesNotContain(catalog.Capabilities, capability => catalog.ExplicitlyExcluded.Any(excluded => capability.Name.Contains(excluded, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("experimental")]
    [InlineData("cloud")]
    [InlineData("fleet")]
    [InlineData("manual-compaction")]
    [InlineData("unknown.rpc")]
    public void RequireAllowed_RejectsAnythingNotExplicitlyClassified(string capability)
        => Assert.Throws<InvalidOperationException>(() => GaCapabilityCatalog.RequireAllowed(capability));
}
