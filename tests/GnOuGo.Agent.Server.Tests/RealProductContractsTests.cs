using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RealProductContractsTests
{
    [Fact]
    public async Task ActualProducerContractsDiscoverWithoutInvokingCapabilities()
    {
        var root = Path.GetTempPath();
        var snapshot = RealProductContracts.Capture(new DocumentPolicy(new DocumentServerSettings { DefaultWorkingDirectory = root }, root));
        Assert.Equal(9, snapshot["browser"]!.AsArray().Count);
        Assert.Equal(4, snapshot["document"]!.AsArray().Count);
        var factory = new RealProductContracts.Factory(snapshot);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), TestContext.Current.CancellationToken);
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(TestContext.Current.CancellationToken))
        {
            var page = await runtime.Capabilities.ListAsync(source.Id, null, TestContext.Current.CancellationToken);
            Assert.Null(page.UnavailableReason);
            foreach (var summary in page.Capabilities) catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(summary, TestContext.Current.CancellationToken));
        }
        var read = catalog.Capabilities.Single(c => c.Method == "browser_get_content");
        Assert.Equal("html", read.InputSchema["properties"]!["format"]!["default"]!.ToString());
        Assert.Contains(read.OutputSchema["required"]!.AsArray(), n => n!.ToString() == "content");
        var write = catalog.Capabilities.Single(c => c.Method == "document_write");
        Assert.Contains(write.InputSchema["required"]!.AsArray(), n => n!.ToString() == "filePath");
        Assert.DoesNotContain(write.OutputSchema["required"]!.AsArray(), n => n!.ToString() == "relativePath");
        Assert.False(TaskOperations.Describe(write).Outputs.Single(p => p.Name == "relativePath").Required);
        Assert.Equal(0, factory.InvocationAttempts);
        await using var session = await factory.GetClientAsync("browser", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CallToolAsync("browser_get_content", null, TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.InvocationAttempts);
    }
}
