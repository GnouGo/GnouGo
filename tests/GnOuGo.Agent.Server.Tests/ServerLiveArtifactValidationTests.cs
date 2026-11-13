using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Planning.Capabilities;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ServerLiveArtifactValidationTests
{
    [Fact]
    public async Task ApprovalRevalidatesActualSelectedMcpContractsAndRejectsAChangedContract()
    {
        var factory = new Metadata(); var policy = new PlanningPolicy();
        var runtime = ServerLiveArtifactValidation.Runtime(factory, policy, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = policy }, TestContext.Current.CancellationToken);
        var page = await runtime.Capabilities.ListAsync(CapabilityDiscovery.SourceId("test-mcp"), null, TestContext.Current.CancellationToken);
        catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(Assert.Single(page.Capabilities), TestContext.Current.CancellationToken));
        var empty = ServerLiveArtifactValidation.Runtime(null, policy, (_, _) => Task.CompletedTask);
        Assert.Contains(await empty.ValidateCatalogAsync(catalog, TestContext.Current.CancellationToken), d => d.Code == "CATALOG_CHANGED");
        Assert.Empty(await runtime.ValidateCatalogAsync(catalog, TestContext.Current.CancellationToken));
        factory.Input["description"] = "Changed selected contract";
        Assert.Contains(await runtime.ValidateCatalogAsync(catalog, TestContext.Current.CancellationToken), d => d.Code == "CATALOG_CHANGED");
        Assert.Equal(0, factory.BusinessCalls);
    }

    [Fact]
    public async Task CompletedCatalogFailureRecompilesTheExactArtifactWithoutInferenceOrAccountingReset()
    {
        var (reviewed, stopped) = await Stopped(); var count = 0;
        var runtime = ServerLiveArtifactValidation.Runtime(null, reviewed.Request.Policy, (_, _) => { count++; return Task.CompletedTask; });
        var original = JsonSerializer.Serialize(reviewed, PlanningJsonContext.Default.PlanningSession);
        var restored = await ServerLiveArtifactValidation.RevalidateAsync(stopped, reviewed, stopped.Revision, reviewed.ComputeArtifactHash()!, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.FinalReview, restored.Status);
        Assert.Equal(reviewed.ComputeArtifactHash(), restored.ComputeArtifactHash());
        Assert.Equal(reviewed.ModelCalls, restored.ModelCalls);
        Assert.Equal(JsonSerializer.Serialize(reviewed.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), JsonSerializer.Serialize(restored.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Equal(JsonSerializer.Serialize(reviewed.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(restored.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(original, JsonSerializer.Serialize(reviewed, PlanningJsonContext.Default.PlanningSession));
        Assert.True(count >= 2);
        PlanningArtifactApproval.Verify(restored);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("hash")]
    [InlineData("plan")]
    [InlineData("policy")]
    [InlineData("generation")]
    [InlineData("calls")]
    [InlineData("complete")]
    [InlineData("diagnostic")]
    [InlineData("approved")]
    [InlineData("pending")]
    [InlineData("question")]
    public async Task RecoveryCannotChangeAuthorityOrResumeAnUnrelatedFailure(string defect)
    {
        var (reviewed, stopped) = await Stopped(); var revision = stopped.Revision; var hash = reviewed.ComputeArtifactHash()!;
        switch (defect)
        {
            case "revision": revision++; break;
            case "hash": hash = new string('a', 64); break;
            case "plan": stopped.Plan!.Root.Tasks[0].Objective += "changed"; break;
            case "policy": stopped.Request.Policy.Instructions += "changed"; break;
            case "generation": stopped.Request.Generation = new("low", 6000, 1000); break;
            case "calls": stopped.ModelCalls++; break;
            case "complete": stopped.Construction!.Complete = false; break;
            case "diagnostic": stopped.Diagnostics = [new("MODEL_DISPATCH_UNVERIFIABLE", "/", "Unknown model outcome")]; break;
            case "approved": stopped.ApprovedHash = hash; break;
            case "pending": stopped.PendingCall = new() { Id = "unresolved", Purpose = "construction_draft", Request = new() { Model = "test" } }; break;
            case "question": stopped.PendingQuestions = [new("unanswered", "Retain the pending question", [], null)]; break;
        }
        var checkpoints = 0;
        var runtime = ServerLiveArtifactValidation.Runtime(null, reviewed.Request.Policy, (_, _) => { checkpoints++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<PlanningConflictException>(() => ServerLiveArtifactValidation.RevalidateAsync(stopped, reviewed, revision, hash, runtime, TestContext.Current.CancellationToken));
        Assert.Equal(0, checkpoints);
    }

    private static async Task<(PlanningSession Reviewed, PlanningSession Stopped)> Stopped()
    {
        var reviewed = await CurrentPlanningFixture.LiteralAsync("benchmark", "catalog-revalidation", policy: null, ct: TestContext.Current.CancellationToken);
        var stopped = JsonSerializer.Deserialize(JsonSerializer.Serialize(reviewed, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        stopped.Status = PlanningStatus.Stopped; stopped.Revision++; stopped.Yaml = null;
        stopped.Diagnostics = [new("CATALOG_CHANGED", "selected-contract", "Approval had no selected MCP source.")];
        return (reviewed, stopped);
    }

    private sealed class Metadata : IMcpClientFactory, IMcpSourceConfigurationFingerprint
    {
        internal readonly JsonObject Input = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };
        internal int BusinessCalls;
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [new() { Name = "test-mcp" }];
        public string SourceConfigurationFingerprint(string serverName) => "exact-test-metadata";
        public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct) => Task.FromResult<IMcpSession>(new Session(this));
        private sealed class Session(Metadata owner) : IMcpSession
        {
            public string ServerName => "test-mcp";
            public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>>([new()
            { Name = "read", InputSchema = owner.Input, OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }]);
            public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);
            public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);
            public Task<McpGetPromptResult> GetPromptAsync(string name, JsonNode? arguments, CancellationToken ct) => throw new InvalidOperationException("No prompt call authorized");
            public Task<McpCallResult> CallToolAsync(string name, JsonNode? arguments, CancellationToken ct) { owner.BusinessCalls++; throw new InvalidOperationException("No business call authorized"); }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
