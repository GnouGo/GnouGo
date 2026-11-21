using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RealProductContractsTests
{
    [Fact]
    public async Task ExplicitInterpretationCanFeedOriginalBrowserUrlContractWithoutAUtilityCapability()
    {
        var root = Path.GetTempPath();
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(
            new DocumentPolicy(new() { DefaultWorkingDirectory = root }, root)));
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var ct = TestContext.Current.CancellationToken;
        var catalog = await runtime.DiscoverAsync(new(), ct);
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(ct))
        {
            var page = await runtime.Capabilities.ListAsync(source.Id, null, ct);
            Assert.Null(page.UnavailableReason);
            foreach (var summary in page.Capabilities) catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(summary, ct));
        }
        var reader = TaskOperations.Describe(catalog.Capabilities.Single(capability => capability.Method == "browser_get_content"));
        var plan = new TaskPlan
        {
            Inputs = [new() { Name = "query", Type = new() { Kind = "string" }, Required = true }],
            Root = new() { Tasks = [
                new() { Id = "derive_address", Kind = "transform", Mode = "interpret",
                    Objective = "Derive an absolute HTTPS search URL from the supplied query; preserve its value through query-component encoding.",
                    Inputs = [new("query", new() { Kind = "input", Source = "query" })],
                    ResultType = new() { Kind = "object", Fields = [new() { Name = "url", Type = new() { Kind = "string" } }] } },
                new() { Id = "read", Kind = "operation", Operation = reader.Id, Objective = "Read the derived address through the original observation contract.",
                    Inputs = [new("url", new() { Kind = "output", Source = "derive_address", Port = "url" }),
                        new("format", new() { Kind = "string", Text = "observation_pages" })] }
            ] }
        };
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Graph);
        Assert.Contains(result.Graph.Workflows.SelectMany(workflow => workflow.Steps), step => step.Type == "llm.call");
        Assert.Equal("derive_address", plan.Root.Tasks[1].Inputs.Single(input => input.Name == "url").Value.Source);
        Assert.Equal(0, factory.InvocationAttempts);
        Assert.Null(plan.Inputs.Single().Default);
        catalog.AllowedStepTypes.Remove("llm.call");
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, diagnostic => diagnostic.Code == "TASK_TRANSFORM_DENIED");
    }

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
