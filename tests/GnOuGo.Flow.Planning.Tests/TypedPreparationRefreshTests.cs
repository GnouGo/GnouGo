using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TypedPreparationRefreshTests
{
    [Theory]
    [InlineData(false, "provider", "inspect")]
    [InlineData(true, "provider", "inspect")]
    [InlineData(true, "renamed", "observer")]
    public async Task RefreshAfterRestartBypassesMemoryCacheAndRetainsOnlyCompatibleResults(bool changed, string server, string method)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer(server, Server(method, "original"));
        var persisted = 0;
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory, McpCache = cache }, (_, _) => { persisted++; return Task.CompletedTask; });
        var request = Request(server, method);
        var checkpoint = new PlanningPreparationCheckpoint();

        await runtime.PrepareAsync(new() { Request = request, PreparationCheckpoint = checkpoint }, Ct);
        checkpoint.ValidatedResults["inventory"] = new JsonObject { ["retained"] = true };
        checkpoint.ValidatedResults["selection"] = new JsonArray("retained selection");
        checkpoint.ValidatedResults["matching_candidate"] = new JsonObject { ["retained"] = true };
        checkpoint.RequestHashes.Add("retained-receipt");
        checkpoint.RefreshDiscovery = true;
        var state = new PlanningSnapshot { PreparationCheckpoint = checkpoint };
        checkpoint = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!.PreparationCheckpoint!;
        factory.RegisterServer(server, Server(method, changed ? "newObservation" : "original"));

        var resumed = await runtime.PrepareAsync(new() { Request = request, PreparationCheckpoint = checkpoint }, Ct);

        Assert.NotNull(resumed.Preparation);
        Assert.False(checkpoint.RefreshDiscovery);
        Assert.True(checkpoint.ValidatedResults["inventory"]!["retained"]!.GetValue<bool>());
        Assert.Equal(!changed, checkpoint.ValidatedResults.ContainsKey("selection"));
        Assert.Equal(!changed, checkpoint.ValidatedResults.ContainsKey("matching_candidate"));
        Assert.Equal("retained-receipt", Assert.Single(checkpoint.RequestHashes));
        var discovery = checkpoint.ValidatedResults["discovery"]!.ToJsonString();
        Assert.Contains(changed ? "newObservation" : "original", discovery);
        var cached = Assert.Single(McpCacheHelper.GetCachedTools(cache, server)!);
        Assert.NotNull(cached.OutputSchema!["properties"]![changed ? "newObservation" : "original"]);
        Assert.True(persisted >= 2);
    }

    [Fact]
    public async Task InterruptedRefreshRetainsPreviousCatalogAndPendingRefresh()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("provider", Server("inspect", "original"));
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var checkpoint = new PlanningPreparationCheckpoint();
        await runtime.PrepareAsync(new() { Request = Request("provider", "inspect"), PreparationCheckpoint = checkpoint }, Ct);
        var retained = checkpoint.ValidatedResults.DeepClone();
        checkpoint.RefreshDiscovery = true;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.PrepareAsync(new() { Request = Request("provider", "inspect"), PreparationCheckpoint = checkpoint }, cancelled.Token));
        Assert.True(checkpoint.RefreshDiscovery);
        Assert.True(JsonNode.DeepEquals(retained, checkpoint.ValidatedResults));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static MockMcpServerConfig Server(string method, string field) => new()
    {
        Tools = [new() { Name = method, InputSchema = new JsonObject { ["type"] = "object" },
            OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { [field] = new JsonObject { ["type"] = "string" } } } }]
    };
    private static PlanningRequest Request(string server, string method) => new()
    {
        TenantId = "tenant",
        Prompt = "Inspect the resource",
        Options = new()
        {
            ["generator"] = new JsonObject { ["model"] = "unused" },
            ["capability_preflight"] = new JsonObject
            {
                ["requirements"] = new JsonArray(new JsonObject
                {
                    ["id"] = "inspect",
                    ["description"] = "Inspect",
                    ["required"] = true,
                    ["alternatives"] = new JsonArray(new JsonObject { ["server"] = server, ["kind"] = "tool", ["method"] = method })
                })
            }
        }
    };
}
