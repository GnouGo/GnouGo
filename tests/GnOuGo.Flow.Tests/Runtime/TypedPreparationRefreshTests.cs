using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

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
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory, McpCache = cache });
        var request = Request(server, method);
        var checkpoint = new PlanningPreparationCheckpoint();
        var persisted = 0;
        Task Persist(CancellationToken _) { persisted++; return Task.CompletedTask; }
        await runtime.AdvancePreparationAsync(request, checkpoint, Persist, Ct);
        checkpoint.ValidatedResults["inventory"] = new JsonObject { ["retained"] = true };
        checkpoint.ValidatedResults["selection"] = new JsonArray("retained selection");
        checkpoint.ValidatedResults["matching_candidate"] = new JsonObject { ["retained"] = true };
        checkpoint.RequestHashes.Add("retained-receipt");
        checkpoint.RefreshDiscovery = true;
        var state = new PlanningSnapshot { PreparationCheckpoint = checkpoint };
        checkpoint = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!.PreparationCheckpoint!;
        factory.RegisterServer(server, Server(method, changed ? "newObservation" : "original"));

        var resumed = await runtime.AdvancePreparationAsync(request, checkpoint, Persist, Ct);

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
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory });
        var checkpoint = new PlanningPreparationCheckpoint();
        await runtime.AdvancePreparationAsync(Request("provider", "inspect"), checkpoint, _ => Task.CompletedTask, Ct);
        var retained = checkpoint.ValidatedResults.DeepClone();
        checkpoint.RefreshDiscovery = true;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.AdvancePreparationAsync(Request("provider", "inspect"), checkpoint, _ => Task.CompletedTask, cancelled.Token));
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
        TenantId = "tenant", Prompt = "Inspect the resource", Options = new()
        {
            ["generator"] = new JsonObject { ["model"] = "unused" },
            ["capability_preflight"] = new JsonObject { ["mode"] = "explicit", ["requirements"] = new JsonArray(new JsonObject
            { ["id"] = "inspect", ["description"] = "Inspect", ["required"] = true,
                ["alternatives"] = new JsonArray(new JsonObject { ["server"] = server, ["kind"] = "tool", ["method"] = method }) }) }
        }
    };
}
