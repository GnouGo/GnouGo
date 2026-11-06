using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Agent.Server.Tests;

public sealed class DiscoveryRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualServerAdaptersRestoreEncryptedSnapshotAtFifthPageWithoutRefetchOrInference(bool recovering)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var source = new ManyTools();
        using var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(),
            settings: new() { BackgroundProcessingEnabled = false }, mcpFactory: source);
        var state = await service.StartAsync("snapshot-restart", "Read the exact requested record", false, Ct);
        ConfigureBlocked(state); state.ModelCalls = 0; state.Status = PlanningStatus.Generating; state.Diagnostics.Clear(); state.Discovery.Pages.Clear();
        state.Discovery.Sources = [new(source.Id, "Requested record operations")];
        var step = state.Construction!.Compact!.CurrentStep!; step.DiscoveryProtocol = 2;
        var focus = PlanningDiscoveryFocus.Choices(state)[0];
        var request = new PlanningDiscoveryRequest(source.Id) { FocusId = focus.Id, Justification = "The declared source provides requested record operations." };
        step.PendingDiscovery = new() { Id = Guid.NewGuid().ToString("N"), Requests = [request],
            ActionHash = HybridWorkflowPlanner.DiscoveryActionHash(step, [request]), Pending = [request with { Query = focus.Query }] };
        state.Discovery.ActiveSourceActions = new() { [PlanningDiscoveryFocus.SourceKey(source.Id, focus.Query, focus.Id)] = step.PendingDiscovery.Id };
        var previous = state.Revision++; Assert.True(await fixture.Store.TrySaveAsync(state, previous, Ct));
        state = await service.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = state.Revision }, Ct);
        Assert.Equal(4, state.Discovery.Pages.Count); Assert.Equal(1, source.Reads);
        Assert.NotNull(state.Construction!.Compact!.CurrentStep!.PendingDiscovery!.Snapshots);
        Assert.Null(state.Construction.Compact.CurrentStep.PendingDiscovery.InlineSnapshots);
        PlanningDiscoveryRecovery? recovery = null;
        if (recovering)
        {
            step = state.Construction.Compact.CurrentStep;
            var failure = new CapabilityDiscoveryFailure("retained-failure", source.Id, focus.Id, focus.Query, null,
                "transport_unavailable", "Retained source failure", null, null, request.Justification, DateTimeOffset.UtcNow) { StepId = step.Id };
            state.Discovery.Failures = [failure]; state.Status = PlanningStatus.DiscoveryRecovery;
            step.PendingDiscovery!.Recovery = recovery = new(failure.Id, "refresh_source");
            previous = state.Revision++; Assert.True(await fixture.Store.TrySaveAsync(state, previous, Ct));
            var interrupted = await service.ToDtoAsync(state, Ct);
            Assert.True(interrupted.CanRecoverDiscovery); Assert.False(interrupted.CanResumeDiscovery);
            Assert.Equal(recovery.FailureId, interrupted.PendingDiscoveryRecovery!.FailureId);
        }
        source.Tools[0].Description = "Changed remote schema after the fourth page";
        using var restarted = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(),
            settings: new() { BackgroundProcessingEnabled = false }, mcpFactory: source);
        state = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
        state = await restarted.SubmitAsync(state.Request.SessionId, new() { Kind = recovering ? "recover_discovery" : "advance",
            ExpectedRevision = state.Revision, DiscoveryRecovery = recovery }, Ct, recovering ? new("operator") : null);
        Assert.Empty(state.Diagnostics); Assert.Equal(6, state.Discovery.Pages.Count); Assert.Equal(1, source.Reads);
        Assert.Equal(0, state.ModelCalls); Assert.Null(state.Construction!.Compact!.CurrentStep!.PendingDiscovery);
        Assert.Single(state.Discovery.Pages.Select(p => p.SnapshotVersion).Distinct());
        if (recovering)
        {
            Assert.Equal(PlanningStatus.DiscoveryRecovery, state.Status);
            var ready = await restarted.ToDtoAsync(state, Ct);
            Assert.False(ready.CanRecoverDiscovery); Assert.True(ready.CanResumeDiscovery); Assert.Null(ready.PendingDiscoveryRecovery);
        }
        Assert.Empty(await fixture.Records.ListAsync(PlanningModelJournal.RequestCollection, "planning-tests", EfPlanningSessionStore.Author, Ct));
    }

    [Fact]
    public async Task OperatorRecoveryUsesOriginalSessionAndWaitsBeforeNewInference()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        using var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), settings: new() { BackgroundProcessingEnabled = false });
        var state = await BlockedAsync(service, fixture);
        var request = JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest);
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.True(await service.CanRecoverDiscoveryAsync(state, Ct));
        var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state));
        var command = new PlanningCommand { Kind = "recover_discovery", ExpectedRevision = state.Revision, DiscoveryRecovery = new(failure.Id, "continue_available") };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SubmitAsync(state.Request.SessionId, command, Ct));
        state = await service.SubmitAsync(state.Request.SessionId, command, Ct, new("operator"));
        Assert.Equal(PlanningStatus.DiscoveryRecovery, state.Status); Assert.Equal(70, state.ModelCalls);
        Assert.Equal(request, JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest));
        Assert.Equal(2, state.Construction!.Checkpoints.Count); Assert.Single(state.Discovery.Pages);
        var reloaded = await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct);
        Assert.Equal(PlanningStatus.DiscoveryRecovery, reloaded!.Status);
        Assert.True((await service.ToDtoAsync(state, Ct)).CanResumeDiscovery);
        Assert.NotEqual(original, JsonSerializer.Serialize(reloaded, PlanningJsonContext.Default.PlanningSession));
        // Old immutable revision remains present; no request or receipt is created by metadata recovery.
        Assert.Equal(70, (await fixture.Records.ListAsync(PlanningModelJournal.RequestCollection, "planning-tests", EfPlanningSessionStore.Author, Ct)).Count);
        Assert.Equal(70, (await fixture.Records.ListAsync(PlanningModelJournal.Collection, "planning-tests", EfPlanningSessionStore.Author, Ct)).Count);
    }

    [Theory]
    [InlineData("unresolved_model")]
    [InlineData("missing_receipt")]
    [InlineData("cancelled")]
    [InlineData("approved")]
    [InlineData("wrong_failure")]
    [InlineData("stale_revision")]
    public async Task UnsafeOrSubstitutedRecoveryIsRejectedWithoutChangingStoredState(string condition)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        using var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), settings: new() { BackgroundProcessingEnabled = false });
        var state = await BlockedAsync(service, fixture);
        if (condition == "cancelled") state.Status = PlanningStatus.Cancelled;
        if (condition == "approved") state.ApprovedHash = "approved-artifact";
        if (condition == "missing_receipt")
            await fixture.Records.DeleteAsync(PlanningModelJournal.Collection, "planning-tests", state.Request.SessionId + ":controlled-receipt-0", EfPlanningSessionStore.Author, Ct);
        if (condition == "unresolved_model")
        {
            await using var db = fixture.CreateDbContext();
            db.Calls.Add(new() { TenantId = "planning-tests", SessionId = state.Request.SessionId, RequestHash = "uncertain", PayloadKey = "uncertain", Status = "reserved" });
            await db.SaveChangesAsync(Ct);
        }
        if (condition is "cancelled" or "approved")
        { var previous = state.Revision++; Assert.True(await fixture.Store.TrySaveAsync(state, previous, Ct)); }
        var before = JsonSerializer.Serialize((await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!, PlanningJsonContext.Default.PlanningSession);
        var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state));
        await Assert.ThrowsAsync<PlanningConflictException>(() => service.SubmitAsync(state.Request.SessionId,
            new() { Kind = "recover_discovery", ExpectedRevision = state.Revision + (condition == "stale_revision" ? 1 : 0),
                DiscoveryRecovery = new(condition == "wrong_failure" ? "other-session-failure" : failure.Id, "continue_available") }, Ct, new("operator")));
        Assert.Equal(before, JsonSerializer.Serialize((await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task ExistingOwnersPermanentRetentionBarrierRejectsRecovery()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        using var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(),
            settings: new() { BackgroundProcessingEnabled = false }, recoveryGuards: [new RetainedOwner()]);
        var state = await BlockedAsync(service, fixture);
        Assert.False(await service.CanRecoverDiscoveryAsync(state, Ct));
        var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state));
        await Assert.ThrowsAsync<PlanningConflictException>(() => service.SubmitAsync(state.Request.SessionId,
            new() { Kind = "recover_discovery", ExpectedRevision = state.Revision, DiscoveryRecovery = new(failure.Id, "continue_available") }, Ct, new("operator")));
        Assert.Equal(PlanningStatus.Stopped, (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!.Status);
    }
    private sealed class RetainedOwner : IPlanningDiscoveryRecoveryGuard
    {
        public Task VerifyAsync(PlanningSession session, CancellationToken ct) => throw new InvalidOperationException("Existing permanent retention forbids old continuation.");
    }
    private sealed class ManyTools : IMcpClientFactory
    {
        internal string Id => CapabilityDiscovery.SourceId("record-source");
        internal int Reads;
        internal List<McpToolInfo> Tools = Enumerable.Range(0, 48).Select(i => new McpToolInfo { Name = "record_" + i, Description = "Read an exact record",
            EffectKind = "read", InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, OutputSchema = new JsonObject { ["type"] = "string" } }).ToList();
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [new() { Name = "record-source", Description = "Exact record operations" }];
        public Task<IMcpSession> GetClientAsync(string name, CancellationToken ct)
        {
            if (name != "record-source") return Task.FromResult<IMcpSession>(PlanningSessionLifecycleTests.AgentCatalog());
            Reads++; var factory = new InMemoryMcpClientFactory(); factory.RegisterServer(name, new() { Tools = Tools }); return factory.GetClientAsync(name, ct);
        }
    }

    internal static async Task<PlanningSession> BlockedAsync(PlanningSessionService service, PlanningPersistenceTests.StoreFixture fixture)
    {
        var state = await service.StartAsync("retained-discovery", "Return the requested result", false, Ct);
        ConfigureBlocked(state);
        await SeedModelHistoryAsync(fixture.Records, fixture, state);
        var previous = state.Revision++; Assert.True(await fixture.Store.TrySaveAsync(state, previous, Ct));
        return state;
    }

    internal static async Task SeedModelHistoryAsync(GnOuGo.KeyVault.Core.Services.IKeyVaultRecordStore records,
        Microsoft.EntityFrameworkCore.IDbContextFactory<PlanningDbContext> contexts, PlanningSession state)
    {
        await using var db = contexts.CreateDbContext();
        for (var i = 0; i < state.ModelCalls; i++)
        {
            var key = state.Request.SessionId + ":controlled-receipt-" + i;
            db.Calls.Add(new() { TenantId = state.Request.TenantId, SessionId = state.Request.SessionId, PayloadKey = key,
                RequestHash = key, Status = "completed" });
            foreach (var collection in new[] { PlanningModelJournal.RequestCollection, PlanningModelJournal.Collection })
                await records.UpsertAsync(collection, state.Request.TenantId, key, "{}", EfPlanningSessionStore.Author, Ct);
        }
        await db.SaveChangesAsync(Ct);
    }

    internal static void ConfigureBlocked(PlanningSession state)
    {
        state.Status = PlanningStatus.Stopped; state.Phase = PlanningCompactConstruction.DiscoveryPurpose;
        state.ModelCalls = 70; state.Plan = new(); state.Requirements = new() { Inputs = [], Outputs = [], Outcomes = [new("req_0", state.Request.Prompt)] };
        state.Catalog ??= new() { AllowedStepTypes = ["mcp.call"] };
        PlanningCompactConstruction.InitializeSources(state);
        state.Construction!.Compact!.Sources.ForEach(s => s.Indexed = true);
        state.Construction.Compact.Sources.Add(new() { Id = "request:0:clause:0", Version = PlanningGraphCompiler.Fingerprint(state.Request.Prompt),
            Text = state.Request.Prompt, Kind = "requirement", Inputs = [], Outputs = [], Indexed = true });
        state.Construction.Compact.RequirementSources["req_0"] = ["request:0:clause:0"];
        state.Construction.Requirements = [new() { Id = "req_0" }];
        state.Construction.Checkpoints = [new() { Sequence = 1 }, new() { Sequence = 2 }];
        state.Construction.Compact!.Blocks = [new() { Id = "block", Objective = state.Request.Prompt, RequirementIds = ["req_0"] }];
        state.Construction.Compact.CurrentBlockId = "block";
        state.Construction.Compact.CurrentStep = new() { Id = "step", BlockId = "block", Kind = null, Objective = state.Request.Prompt,
            RequirementIds = ["req_0"], BaseHash = PlanningConstruction.Hash(state.Plan), PendingDiscovery = new()
            { Requests = [new("exploratory")], Pending = [new("exploratory", "old-cursor", state.Request.Prompt)] } };
        state.Discovery.Sources = [new("exploratory", "Unrelated exploratory source")];
        state.Discovery.Pages = [new("exploratory", "old-cursor", [], null, "Historical technical cause unavailable", state.Request.Prompt)];
        state.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "Historical discovery failure")];
    }
}
