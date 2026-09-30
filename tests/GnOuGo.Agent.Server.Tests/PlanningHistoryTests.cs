using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningHistoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RecoveryListingExcludesIncompatibleRecordsWithoutChangingThem()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var legacy = await StoreLegacyAsync(fixture, false);
        var healthy = new PlanningSession { Request = new() { TenantId = "planning-tests", SessionId = "healthy" } };
        Assert.True(await fixture.Store.TrySaveAsync(healthy, null, Ct));
        Assert.Equal("healthy", Assert.Single(await fixture.Store.ListAsync("planning-tests", Ct)).Request.SessionId);
        var after = await fixture.Records.GetAsync(legacy.Collection, legacy.TenantId, legacy.Key, "test", Ct);
        Assert.Equal(legacy, after);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(legacy.Value, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task StartupRecoversHealthySessionAndNeverDispatchesForIncompatibleHistory()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var legacy = await StoreLegacyAsync(fixture, false);
        Assert.True(await fixture.Store.TrySaveAsync(new() { Request = new() { TenantId = "planning-tests", SessionId = "healthy" } }, null, Ct));
        var planner = new RecoveryPlanner(); var client = new RejectingClient();
        using var service = PlanningSessionLifecycleTests.Create(fixture, planner, PlanningSessionLifecycleTests.AgentCatalog(), client);
        await service.StartAsync(Ct);
        try
        {
            Assert.Equal("healthy", await planner.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally { await service.StopAsync(Ct); }
        Assert.Equal(["healthy"], planner.Ids);
        Assert.Equal(0, client.Calls);
        await using var db = fixture.CreateDbContext();
        Assert.Empty(await db.Calls.ToListAsync(Ct));
        Assert.Equal(legacy, await fixture.Records.GetAsync(legacy.Collection, legacy.TenantId, legacy.Key, "test", Ct));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("save")]
    [InlineData("retry_model")]
    public async Task CommandsCannotUseUnavailableDisplayMetadata(string command)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var legacy = await StoreLegacyAsync(fixture, false); var client = new RejectingClient();
        using var service = PlanningSessionLifecycleTests.Create(fixture, new RecoveryPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client);
        var error = await Assert.ThrowsAsync<PlanningConflictException>(() => service.SubmitAsync("legacy", new() { Kind = command, ExpectedRevision = 3, ArtifactHash = "stale" }, Ct));
        Assert.Equal(PlanningSessionInspection.UnavailableMessage, error.Message);
        Assert.Equal(0, client.Calls);
        Assert.Equal(legacy, await fixture.Records.GetAsync(legacy.Collection, legacy.TenantId, legacy.Key, "test", Ct));
    }

    [Fact]
    public async Task UnavailableChatCannotSupplyExecutableYaml()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var legacy = await StoreLegacyAsync(fixture, true);
        var client = new RejectingClient();
        var runtime = new WorkflowPlanningRuntimeFactory(fixture.Records, Path.Combine(fixture.Root, "leases"));
        var context = new StepExecutionContext { Engine = new WorkflowEngine { LLMClient = client }, Limits = new() { TenantId = "planning-tests" }, Data = new(), Step = new() { Source = new StepDef { Id = "execute", Type = "workflow.execute" } } };
        await Assert.ThrowsAsync<PlanningConflictException>(() => runtime.ReadApprovedYamlAsync(context, "legacy", "stale", Ct));
        Assert.Equal(0, client.Calls);
        Assert.Equal(legacy, await fixture.Records.GetAsync(legacy.Collection, legacy.TenantId, legacy.Key, "test", Ct));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unsupported_schema")]
    [InlineData("unknown_operation")]
    [InlineData("foreign_owner")]
    [InlineData("wrong_identity")]
    [InlineData("ambiguous_header")]
    public async Task UnreadableHistoryIsIsolatedAndUntrustedMetadataIsNotDisplayed(string defect)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var legacy = await StoreLegacyAsync(fixture, true);
        var json = JsonNode.Parse(legacy.Value)!;
        if (defect == "unsupported_schema") json["schemaVersion"] = 7;
        if (defect == "unknown_operation") json["groundedPlan"]!["operations"]![0]!["kind"] = "obsolete_kind";
        if (defect == "foreign_owner") json["request"]!["tenantId"] = "other";
        if (defect == "wrong_identity") json["request"]!["sessionId"] = "other";
        var payload = defect == "malformed" ? "{not json" : json.ToJsonString();
        if (defect == "ambiguous_header") payload = payload.Replace("\"tenantId\":\"planning-tests\"", "\"tenantId\":\"other\",\"tenantId\":\"planning-tests\"", StringComparison.Ordinal);
        await fixture.Records.UpsertAsync(legacy.Collection, legacy.TenantId, legacy.Key, payload, "test", Ct);
        await fixture.Records.UpsertAsync(legacy.Collection, "other", "foreign-only", legacy.Value, "test", Ct);
        using var service = PlanningSessionLifecycleTests.Create(fixture, new RecoveryPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        var entry = Assert.Single(await service.ListHistoryAsync(Ct));
        Assert.False(entry.Available); Assert.Equal("legacy", entry.SessionId);
        Assert.Equal(defect is "unsupported_schema" or "unknown_operation" ? "legacy session" : "Unavailable session", entry.Name);
        Assert.Null((await service.InspectAsync("legacy", true, Ct))!.Session);
        Assert.Null(await service.InspectAsync("foreign-only", true, Ct));
        await Assert.ThrowsAsync<PlanningConflictException>(() => service.GetWorkflowSessionAsync("legacy", Ct));
    }

    [Fact]
    public async Task StorageOutagesAndCancellationAreNotMisreportedAsIncompatibleRecords()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        await StoreLegacyAsync(fixture, false);
        foreach (var error in new Exception[] { new IOException("storage unavailable"), new OperationCanceledException(Ct), new JsonException("storage failure") })
        {
            var store = new EfPlanningSessionStore(fixture, new FailingRecords(error));
            Assert.Same(error, await Record.ExceptionAsync(() => store.ListAsync("planning-tests", Ct)));
            Assert.Same(error, await Record.ExceptionAsync(() => EfPlanningSessionStore.InspectAllAsync(fixture, new FailingRecords(error), "planning-tests", Ct)));
        }
    }

    internal static async Task<KeyVaultRecordValue> StoreLegacyAsync(PlanningPersistenceTests.StoreFixture fixture, bool workflow)
    {
        var state = new PlanningSession
        {
            Request = new() { TenantId = "planning-tests", SessionId = "legacy", Name = "legacy session", Prompt = "PRIVATE_HISTORY_PROMPT" },
            Status = PlanningStatus.Generating, Revision = 3, ModelCalls = 2, ReplanAttempts = 1,
            PendingCall = new() { Id = "uncertain", Request = new() { Prompt = "PRIVATE_UNCERTAIN_REQUEST" } },
            Graph = new()
        };
        var json = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!;
        json["schemaVersion"] = 8;
        json["groundedPlan"] = new JsonObject { ["operations"] = new JsonArray(new JsonObject { ["kind"] = "obsolete", ["resultType"] = new JsonObject { ["type"] = "string" } }) };
        var key = state.Request.SessionId;
        var collection = "flow-planning-sessions-v10";
        if (!workflow)
        {
            Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
            await using var db = fixture.CreateDbContext();
            key = (await db.Sessions.SingleAsync(s => s.SessionId == "legacy" && s.TenantId == "planning-tests", Ct)).PayloadKey;
            await fixture.Records.DeleteAsync(EfPlanningSessionStore.Collection, state.Request.TenantId, key, "test", Ct);
            collection = "agent-planning-sessions-v9";
        }
        return await fixture.Records.UpsertAsync(collection, state.Request.TenantId, key, json.ToJsonString(), "test", Ct);
    }

    private sealed class RecoveryPlanner : IWorkflowPlanner
    {
        internal List<string> Ids { get; } = [];
        internal TaskCompletionSource<string> Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PlanningSession> AdvanceAsync(PlanningSession session, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
        {
            Ids.Add(session.Request.SessionId); session.Status = PlanningStatus.Stopped;
            await runtime.CheckpointAsync(session, ct);
            Recovered.TrySetResult(session.Request.SessionId);
            return session;
        }
    }

    private sealed class RejectingClient : ILLMClient
    {
        internal int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("History inspection must not dispatch a model."); }
    }

    private sealed class FailingRecords(Exception error) : IKeyVaultRecordStore
    {
        public Task<KeyVaultRecordValue?> GetAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default) => Task.FromException<KeyVaultRecordValue?>(error);
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string collection, string tenantId, string author, CancellationToken ct = default) => Task.FromException<IReadOnlyList<KeyVaultRecordValue>>(error);
        public Task<KeyVaultRecordValue> UpsertAsync(string collection, string tenantId, string key, string value, string author, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
