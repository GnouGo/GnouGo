using System.Text.Json;
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
    public async Task CancelInterruptsAnActivePlanningPhase()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var planner = new DelegatePlanner(async (state, command, runtime, ct) =>
        {
            if (command.Kind == "cancel") return await new TypedWorkflowPlanner().AdvanceAsync(state, command, runtime, ct);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Cancellation did not interrupt the phase.");
        });
        using var service = Create(fixture, planner, AgentCatalog());
        await service.StartAsync(Ct);
        try
        {
            var state = await service.StartAsync("New agent", "Return a greeting", false, Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            var cancelled = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "cancel", ExpectedRevision = state.Revision }, Ct);
            Assert.Equal(PlanningStatus.Cancelled, cancelled.Status);
            Assert.Equal(PlanningStatus.Cancelled, (await service.GetAsync(state.Request.SessionId, Ct))!.Status);
        }
        finally { await service.StopAsync(Ct); }
    }

    [Fact]
    public async Task NaturalLanguageCommand_IsPersistedBeforeDispatch_AndResumesAfterRestart()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = State(PlanningStatus.FinalReview);
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var planner = new DelegatePlanner((snapshot, command, _, _) =>
        {
            Assert.Equal(PlanningStatus.FinalReview, snapshot.Status);
            Assert.Equal("revise", command.Kind);
            var next = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
            next.Revision++; next.Status = PlanningStatus.BehaviorReview;
            seen.TrySetResult(command.Text!);
            return Task.FromResult(next);
        });
        using (var first = Create(fixture, planner, AgentCatalog()))
        {
            var queued = await first.SubmitAsync(state.Request.SessionId, new() { Kind = "revise", ExpectedRevision = 0, Text = "Change the greeting" }, Ct);
            Assert.Equal(PlanningStatus.Revising, queued.Status);
            Assert.Equal("Change the greeting", queued.PendingCommand!.Command.Text);
            Assert.False(seen.Task.IsCompleted);
        }
        using var restarted = Create(fixture, planner, AgentCatalog());
        await restarted.StartAsync(Ct);
        try
        {
            Assert.Equal("Change the greeting", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            await WaitForStatus(restarted, state.Request.SessionId, PlanningStatus.BehaviorReview);
            Assert.Null((await restarted.GetAsync(state.Request.SessionId, Ct))!.PendingCommand);
        }
        finally { await restarted.StopAsync(Ct); }
    }

    [Fact]
    public async Task SavingIsRevisionGuardedAndDuplicateCompletionDoesNotWriteAgain()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = State(PlanningStatus.Approved);
        state.Yaml = "validated artifact";
        state.ApprovedHash = state.ArtifactHash = PlanningGraphCompiler.Fingerprint(state.Yaml);
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var writes = 0;
        var agents = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", (_, _) => Task.FromResult(new McpCallResult { Content = new JsonObject { ["success"] = false, ["error_code"] = "NOT_FOUND" } }))
            .OnTool("agent_add", (_, _) => { writes++; return Task.FromResult(new McpCallResult { Content = new JsonObject { ["success"] = true, ["agent"] = new JsonObject { ["id"] = "saved-agent" } } }); });
        using var service = Create(fixture, new TypedWorkflowPlanner(), agents);
        var saved = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "save", ExpectedRevision = 0, ArtifactHash = state.ArtifactHash }, Ct);
        Assert.Equal(PlanningStatus.Saved, saved.Status);
        Assert.Equal(1, writes);
        var repeated = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "save", ExpectedRevision = saved.Revision, ArtifactHash = saved.ArtifactHash }, Ct);
        Assert.Equal(saved.Revision, repeated.Revision);
        await Assert.ThrowsAsync<PlanningConflictException>(() => service.SubmitAsync(state.Request.SessionId, new() { Kind = "save", ExpectedRevision = 0, ArtifactHash = saved.ArtifactHash }, Ct));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task CatalogChangeAfterApproval_InvalidatesApprovalWithoutSaving()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = State(PlanningStatus.Approved);
        state.Yaml = "validated artifact";
        state.ApprovedHash = state.ArtifactHash = PlanningGraphCompiler.Fingerprint(state.Yaml);
        state.Preparation!.StepContracts["set"] = new JsonObject { ["input"] = new JsonObject { ["type"] = "string" }, ["output"] = new JsonObject() };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        using var service = Create(fixture, new TypedWorkflowPlanner(), AgentCatalog());
        var result = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "save", ExpectedRevision = 0, ArtifactHash = state.ArtifactHash }, Ct);
        Assert.Equal(PlanningStatus.Unsupported, result.Status);
        Assert.Null(result.ApprovedHash);
        Assert.Contains(result.Diagnostics, d => d.Code == "CATALOG_CHANGED");
        Assert.Null(result.SavedAgentId);
        Assert.Equal(result.Revision, (await service.GetAsync(state.Request.SessionId, Ct))!.Revision);
    }

    private static PlanningSnapshot State(string status) => new()
    {
        Request = new() { TenantId = "planning-tests", Prompt = "Return a greeting", Name = "test-agent" }, Status = status,
        Preparation = new() { AllowedStepTypes = ["set"] }, Graph = new() { Workflows = [new() { Key = "main" }] }
    };
    internal static FakeMcpSession AgentCatalog() => new FakeMcpSession("GnOuGo.Agent.Mcp")
        .OnTool("agent_get_by_name", (_, _) => Task.FromResult(new McpCallResult { Content = new JsonObject { ["success"] = false, ["error_code"] = "NOT_FOUND" } }));
    internal static PlanningSessionService Create(PlanningPersistenceTests.StoreFixture fixture, IWorkflowPlanner planner, IMcpSession agents)
    {
        var options = new LLMOptions { DefaultProvider = "openai", DefaultModel = "gpt-4o-mini" };
        var runtime = new SecureWorkflowRuntimeFactory(SmartFlowTestFactory.CreateRuntimeOptionsStore(options), new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options),
            mcpClientFactoryOverride: new FakeMcpClientFactory(agents));
        return new(fixture.Store, fixture, fixture.Records, runtime, planner, new TestExchangeRateProvider(), Options.Create(new WorkflowPlanningBudgetSettings()),
            Options.Create(new TypedWorkflowPlanningSettings()), Options.Create(new OpenTelemetrySettings { TenantId = "planning-tests" }), NullLogger<PlanningSessionService>.Instance);
    }
    private static async Task WaitForStatus(PlanningSessionService service, string id, string status)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while ((await service.GetAsync(id, timeout.Token))?.Status != status) await Task.Delay(20, timeout.Token);
    }
    private sealed class DelegatePlanner(Func<PlanningSnapshot, PlanningCommand, IPlanningRuntime, CancellationToken, Task<PlanningSnapshot>> handler) : IWorkflowPlanner
    {
        public Task<PlanningSnapshot> AdvanceAsync(PlanningSnapshot snapshot, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct) => handler(snapshot, command, runtime, ct);
    }
}
