using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace GnOuGo.Agent.Server.Tests;
public sealed class PlanningSessionLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task SaveRequiresApprovalAndReconcilesCommittedWriteAfterRestart()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var state = new PlanningSession { Request = new() { TenantId = "planning-tests", Name = "test", Prompt = "Return value" }, Status = PlanningStatus.FinalReview,
            IntentPlan = new() { Operations = [new CalculateIntentOperation { Id = "value", Value = new() { Kind = "string", Text = "hello" } }] }, Scenarios = [new("nominal", "passed", "Executed", [])] };
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        state.Yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Catalog, state.Request.Name);
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var writes = 0; string? saved = null;
        var agents = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", (_, _) => Task.FromResult(new McpCallResult { Content = saved is null ? new JsonObject { ["success"] = false, ["error_code"] = "NOT_FOUND" }
                : new JsonObject { ["success"] = true, ["agent"] = new JsonObject { ["id"] = "saved", ["workflow"] = saved } } }))
            .OnTool("agent_add", (input, _) => { writes++; saved = input!["workflow"]!.GetValue<string>(); return Task.FromResult(new McpCallResult { Content = new JsonObject { ["success"] = true, ["agent"] = new JsonObject { ["id"] = "saved" } } }); });
        using var service = Create(fixture, new TypedWorkflowPlanner(), agents, settings: new() { BackgroundProcessingEnabled = false });
        await Assert.ThrowsAsync<PlanningConflictException>(() => service.SubmitAsync(state.Request.SessionId, new() { Kind = "save", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, Ct));
        state = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, Ct);
        Assert.True(state.Status == PlanningStatus.Approved, string.Join("; ", state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        state = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "save", ExpectedRevision = state.Revision, ArtifactHash = state.ApprovedHash }, Ct);
        Assert.Equal(PlanningStatus.Saved, state.Status); Assert.Equal(1, writes);
        var previous = state.Revision++; state.Status = PlanningStatus.Saving;
        Assert.True(await fixture.Store.TrySaveAsync(state, previous, Ct));
        using var reopened = Create(fixture, new TypedWorkflowPlanner(), agents, settings: new() { BackgroundProcessingEnabled = false });
        state = await reopened.SubmitAsync(state.Request.SessionId, new() { Kind = "save", ExpectedRevision = state.Revision, ArtifactHash = state.ApprovedHash }, Ct);
        Assert.Equal(PlanningStatus.Saved, state.Status); Assert.Equal(1, writes);
    }
    [Theory]
    [InlineData(PlanningStatus.Generating)]
    [InlineData(PlanningStatus.Clarification)]
    [InlineData(PlanningStatus.FinalReview)]
    public async Task EncryptedSessionRestartRetainsAuthorityBudgetsAndTenantIsolation(string status)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = new PlanningSession { Request = new() { TenantId = "one", SessionId = "same", Prompt = "PRIVATE_INTENT" }, Status = status, ModelCalls = 3, RepairAttempts = 1, ActiveMilliseconds = 100,
            PendingCall = status == PlanningStatus.Generating ? new() { Id = "reserved", Purpose = "intent", Request = new() { Prompt = "PRIVATE_MODEL_REQUEST" } } : null };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var other = new PlanningSession { Request = new() { TenantId = "two", SessionId = "same", Prompt = "OTHER" } };
        Assert.True(await fixture.Store.TrySaveAsync(other, null, Ct));
        var restored = (await fixture.Store.LoadAsync("one", "same", Ct))!;
        Assert.Equal(status, restored.Status); Assert.Equal(3, restored.ModelCalls); Assert.Equal(1, restored.RepairAttempts); Assert.Equal(100, restored.ActiveMilliseconds);
        Assert.Equal(state.PendingCall?.Id, restored.PendingCall?.Id);
        restored.Revision++; Assert.True(await fixture.Store.TrySaveAsync(restored, 0, Ct));
        restored.Revision++; Assert.False(await fixture.Store.TrySaveAsync(restored, 0, Ct));
        Assert.Equal("OTHER", (await fixture.Store.LoadAsync("two", "same", Ct))!.Request.Prompt);
        restored.SchemaVersion = 5;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.TrySaveAsync(restored, 1, Ct));
        foreach (var file in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("PRIVATE_INTENT", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
    }
    internal static FakeMcpSession AgentCatalog() => new FakeMcpSession("GnOuGo.Agent.Mcp")
        .OnTool("agent_get_by_name", (_, _) => Task.FromResult(new McpCallResult { Content = new JsonObject { ["success"] = false, ["error_code"] = "NOT_FOUND" } }));
    internal static PlanningSessionService Create(PlanningPersistenceTests.StoreFixture fixture, IWorkflowPlanner planner, IMcpSession agents, ILLMClient? llm = null, TypedWorkflowPlanningSettings? settings = null)
    {
        var options = new LLMOptions { DefaultProvider = "openai", DefaultModel = "gpt-4o-mini" };
        var runtime = new SecureWorkflowRuntimeFactory(SmartFlowTestFactory.CreateRuntimeOptionsStore(options), new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options),
            mcpClientFactoryOverride: new FakeMcpClientFactory(agents), llmClientOverride: llm, llmCapabilityResolver: new DeclaredTestModel());
        return new(fixture.Store, fixture, fixture.Records, runtime, planner, new TestExchangeRateProvider(), Options.Create(new WorkflowPlanningBudgetSettings()),
            Options.Create(settings ?? new TypedWorkflowPlanningSettings()), Options.Create(new OpenTelemetrySettings { TenantId = "planning-tests" }), NullLogger<PlanningSessionService>.Instance);
    }
    private sealed class DeclaredTestModel : ILLMCapabilityResolver
    {
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["low", "medium"]);
    }
}
