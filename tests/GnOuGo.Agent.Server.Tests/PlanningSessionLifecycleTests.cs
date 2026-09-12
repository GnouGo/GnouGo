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
    public async Task PreparedRetryHasCatalogAccessWithoutCallingTheModel()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = State(PlanningStatus.Stopped);
        state.Preparation!.Capabilities = [new() { Id = "selected", StepType = "mcp.call", Kind = "tool", Server = "renamed", Method = "inspect",
            DeclarationFingerprint = "old-contract" }];
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var planner = new DelegatePlanner(async (snapshot, command, runtime, ct) =>
        {
            Assert.Equal("retry", command.Kind);
            var findings = await runtime.ValidateCatalogAsync(snapshot.Preparation!, ct);
            Assert.Contains(findings, d => d.Code == "CATALOG_CHANGED");
            Assert.DoesNotContain(findings, d => d.Code == "CATALOG_UNAVAILABLE");
            var next = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
            next.Revision++; next.Diagnostics = findings.ToList(); return next;
        });
        using var service = Create(fixture, planner, new FakeMcpSession("renamed").WithTool("inspect"));
        var result = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "retry", ExpectedRevision = state.Revision }, Ct);
        Assert.Equal(PlanningStatus.Stopped, result.Status); Assert.NotNull(result.Usage); Assert.Equal(0, result.Usage.Calls);
    }

    [Fact]
    public async Task PreparationProgressIsDurableBeforeThePhaseCompletes()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var checkpointed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPlanner = new DelegatePlanner(async (snapshot, _, runtime, ct) =>
        {
            var next = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
            next.PreparationCheckpoint = new() { Stage = "inventory", ValidatedResults = new JsonObject { ["inventory"] = "PRIVATE_PREPARATION" } };
            await runtime.CheckpointAsync(next, ct); checkpointed.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); return next;
        });
        string id;
        using (var first = Create(fixture, firstPlanner, AgentCatalog()))
        {
            await first.StartAsync(Ct);
            var state = await first.StartAsync("Checkpoint test", "Inspect a resource", false, Ct); id = state.Request.SessionId;
            await checkpointed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            var stored = (await fixture.Store.LoadAsync(state.Request.TenantId, id, Ct))!;
            Assert.True(stored.Revision > state.Revision); Assert.Equal("inventory", stored.PreparationCheckpoint!.Stage);
            await first.StopAsync(Ct);
        }
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPlanner = new DelegatePlanner((snapshot, _, _, _) =>
        {
            Assert.Equal("inventory", snapshot.PreparationCheckpoint!.Stage);
            var next = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
            next.Revision++; next.Status = PlanningStatus.BehaviorReview; resumed.TrySetResult(); return Task.FromResult(next);
        });
        using var second = Create(fixture, secondPlanner, AgentCatalog());
        await second.StartAsync(Ct);
        try { await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); await WaitForStatus(second, id, PlanningStatus.BehaviorReview); }
        finally { await second.StopAsync(Ct); }
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NaturalLanguageCommand_IsPersistedBeforeDispatch_AndResumesAfterRestart(bool stagedBehavior)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = State(PlanningStatus.FinalReview);
        if (stagedBehavior)
        {
            state.Status = PlanningStatus.Stopped; state.Graph = null; state.BehaviorPlan = null;
            state.BehaviorAssessment.Candidate = new JsonObject { ["summary"] = "Retained invalid behavior" };
        }
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var planner = new DelegatePlanner((snapshot, command, _, _) =>
        {
            Assert.Equal(stagedBehavior ? PlanningStatus.Stopped : PlanningStatus.FinalReview, snapshot.Status);
            if (stagedBehavior) Assert.Equal("Retained invalid behavior", snapshot.BehaviorAssessment.Candidate!["summary"]!.ToString());
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
        state.Preparation!.StepContracts["set"] = new JsonObject { ["input"] = new JsonObject { ["type"] = "string" }, ["output"] = new JsonObject() };
        ApproveFixture(state);
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        using var service = Create(fixture, new TypedWorkflowPlanner(), AgentCatalog());
        var result = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "save", ExpectedRevision = 0, ArtifactHash = state.ArtifactHash }, Ct);
        Assert.Equal(PlanningStatus.Unsupported, result.Status);
        Assert.Null(result.ApprovedHash);
        Assert.Contains(result.Diagnostics, d => d.Code == "CATALOG_CHANGED");
        Assert.Null(result.SavedAgentId);
        Assert.Equal(result.Revision, (await service.GetAsync(state.Request.SessionId, Ct))!.Revision);
    }

    private static PlanningSnapshot State(string status)
    {
        var state = new PlanningSnapshot
        {
            Request = new() { TenantId = "planning-tests", Prompt = "Return a greeting", Name = "test-agent" },
            Status = status,
            Preparation = new() { AllowedStepTypes = ["set"] },
            Graph = new() { Workflows = [new() { Key = "main" }] },
            BehaviorPlan = new() { Workflows = [new() { Key = "main", Purpose = "Return a greeting" }] }
        };
        if (status == PlanningStatus.Approved) ApproveFixture(state);
        return state;
    }
    private static void ApproveFixture(PlanningSnapshot state)
    {
        // Models prior completed validation; individual host tests exercise save ownership and conflicts.
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan!);
        state.Validation = new()
        {
            Stage = 5,
            Inputs = new(),
            FixturesEstablished = true,
            Scenarios = [new("nominal", "passed", "Fixture", [])],
            GraphFingerprint = PlanningGraphCompiler.Fingerprint(state.Graph!)
        };
        state.Validation.ContractFingerprint = PlanningArtifactApproval.ContractFingerprint(state);
        state.Validation.FixtureFingerprint = PlanningArtifactApproval.FixtureFingerprint(state);
        state.Yaml = new PlanningGraphCompiler().Compile(state.Graph!, state.Preparation!, state.Request.Name);
        state.ApprovedHash = state.ArtifactHash = PlanningGraphCompiler.Fingerprint(state.Yaml);
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
    internal static async Task WaitForStatus(PlanningSessionService service, string id, string status)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while ((await service.GetAsync(id, timeout.Token))?.Status != status) await Task.Delay(20, timeout.Token);
    }
    internal sealed class DelegatePlanner(Func<PlanningSnapshot, PlanningCommand, IPlanningRuntime, CancellationToken, Task<PlanningSnapshot>> handler) : IWorkflowPlanner
    {
        public Task<PlanningSnapshot> AdvanceAsync(PlanningSnapshot snapshot, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct) => handler(snapshot, command, runtime, ct);
    }
}
