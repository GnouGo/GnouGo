using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningOwnershipTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CompetingCoordinatorsLeaveAnActiveRequestRunningAndDispatchOnlyOnce()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = CapturedSession();
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var client = new PausedClient();
        var planner = new OneCallPlanner();
        using var first = PlanningSessionLifecycleTests.Create(fixture, planner, PlanningSessionLifecycleTests.AgentCatalog(), client);
        using var second = PlanningSessionLifecycleTests.Create(fixture, planner, PlanningSessionLifecycleTests.AgentCatalog(), client);
        await first.StartAsync(Ct);
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        await second.StartAsync(Ct);
        var pending = (await second.GetAsync(state.Request.SessionId, Ct))!;
        Assert.Equal("active", (await second.InspectRequestAsync(pending, Ct))!.State);
        await Assert.ThrowsAsync<PlanningConflictException>(() => second.SubmitAsync(state.Request.SessionId,
            new() { Kind = "advance", ExpectedRevision = pending.Revision }, Ct));
        Assert.Empty((await second.GetAsync(state.Request.SessionId, Ct))!.Diagnostics);
        client.Release.TrySetResult();
        var completed = await WaitForAsync(second, state.Request.SessionId, PlanningStatus.FinalReview);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, completed.ModelCalls);
        Assert.Equal(1, completed.Usage!.Calls);
        Assert.Equal(15, completed.Usage.TotalTokens);
        await first.StopAsync(Ct);
        await second.StopAsync(Ct);
    }

    [Fact]
    public async Task CancellationFromAnotherCoordinatorPreservesLateReceiptAndAccounting()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = CapturedSession();
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var client = new PausedClient { CompleteOnCancellation = true };
        using var first = PlanningSessionLifecycleTests.Create(fixture, new OneCallPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client);
        using var second = PlanningSessionLifecycleTests.Create(fixture, new OneCallPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client);
        await first.StartAsync(Ct);
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        var pending = (await second.GetAsync(state.Request.SessionId, Ct))!;
        await Assert.ThrowsAsync<PlanningConflictException>(() => second.SubmitAsync(state.Request.SessionId, new() { Kind = "cancel", ExpectedRevision = pending.Revision - 1 }, Ct));
        await second.SubmitAsync(state.Request.SessionId, new() { Kind = "cancel", ExpectedRevision = pending.Revision }, Ct);
        var cancelled = await WaitForAsync(second, state.Request.SessionId, PlanningStatus.Cancelled);
        Assert.Equal(1, client.Calls);
        Assert.Equal(15, cancelled.Usage!.TotalTokens);
        Assert.NotNull(await fixture.Records.GetAsync(PlanningModelJournal.Collection, "planning-tests",
            state.Request.SessionId + ":" + pending.PendingCall!.Id, EfPlanningSessionStore.Author, Ct));
        Assert.Null(cancelled.ApprovedHash);
        await first.StopAsync(Ct);
    }

    [Fact]
    public async Task ReleasedLeaseCanBeRecoveredButDifferentTenantsDoNotContend()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var lease = await PlanningSessionLease.TryAcquireAsync(fixture, "one", "same", Ct);
        Assert.NotNull(lease);
        Assert.Null(await PlanningSessionLease.TryAcquireAsync(fixture, "one", "same", Ct));
        await using var other = await PlanningSessionLease.TryAcquireAsync(fixture, "two", "same", Ct);
        Assert.NotNull(other);
        await lease.DisposeAsync();
        await using var recovered = await PlanningSessionLease.TryAcquireAsync(fixture, "one", "same", Ct);
        Assert.NotNull(recovered);
    }

    [Fact]
    public async Task FinancialAdmissionSessionSaveInspectionAndCancellationSurviveTheSameSqliteAuditContention()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = CapturedSession();
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var client = new PausedClient { CompleteOnCancellation = true };
        using var owner = PlanningSessionLifecycleTests.Create(fixture, new OneCallPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client);
        using var controller = PlanningSessionLifecycleTests.Create(fixture, new OneCallPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client);
        await owner.StartAsync(Ct);
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        var pending = (await controller.GetAsync(state.Request.SessionId, Ct))!;
        var campaignRecords = new KeyVaultRecordStore(Path.Combine(fixture.Root, "vault.db"));
        var campaign = new BenchmarkCampaign(campaignRecords, "contention");
        var financial = new BenchmarkHttpJournal(campaign, "financial-session:1", 100, 100, 1m);
        await financial.PrepareAsync(Ct);
        await using var blocker = new SqliteConnection("Data Source=" + Path.Combine(fixture.Root, "vault.db"));
        await blocker.OpenAsync(Ct);
        // Hold a physical write reservation without inspecting KeyVault tables.
        await using var reservation = blocker.BeginTransaction();
        var other = CapturedSession(); other.Status = PlanningStatus.Stopped;
        var admission = Task.Run(() => financial.SaveAsync(new() { Fingerprint = new string('f', 64),
            Attempts = [new() { Id = "financial-session:1:attempt:1" }] }, Ct), Ct);
        var saving = Task.Run(() => fixture.Store.TrySaveAsync(other, null, Ct), Ct);
        var inspection = Task.Run(() => campaignRecords.QueryMetadataAsync(PlanningModelJournal.RequestCollection, "planning-tests",
            state.Request.SessionId + ":", EfPlanningSessionStore.Author, ct: Ct), Ct);
        var cancellation = Task.Run(() => controller.StopAsync(state.Request.SessionId, Ct), Ct);
        await Task.Delay(1400, Ct); // Force a real BUSY audit/commit across the independent stores.
        Assert.False(admission.IsCompleted);
        await reservation.CommitAsync(Ct);
        await Task.WhenAll(admission, saving, inspection, cancellation).WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.True(await saving);
        Assert.Single((await inspection).Records);
        var cancelled = await WaitForAsync(controller, state.Request.SessionId, PlanningStatus.Cancelled);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, cancelled.Usage!.Calls);
        Assert.Equal(15, cancelled.Usage.TotalTokens);
        Assert.NotNull(await fixture.Records.GetAsync(PlanningModelJournal.Collection, "planning-tests",
            state.Request.SessionId + ":" + pending.PendingCall!.Id, EfPlanningSessionStore.Author, Ct));
        Assert.Equal(1m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        await owner.StopAsync(Ct);
        Assert.False(owner.ExecuteTask!.IsFaulted);
    }

    private static async Task<PlanningSession> WaitForAsync(PlanningSessionService service, string id, string status)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var state = (await service.GetAsync(id, timeout.Token))!;
            if (state.Status == status) return state;
            Assert.DoesNotContain(state.Diagnostics, d => d.Code == "PLANNING_HOST_FAILURE");
            await Task.Delay(25, timeout.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatcherStorageFailureSuspendsOnlyItsOwnerAndColdRestartRecoversTheExactReceiptOnce(bool cancelDuringOutage)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = CapturedSession();
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var client = new PausedClient { CompleteOnCancellation = true };
        var records = new WatcherFailureRecords(fixture.Records);
        using (var service = PlanningSessionLifecycleTests.Create(fixture, new OneCallPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client, records: records))
        {
            await service.StartAsync(Ct);
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            var readsBefore = records.SessionReads;
            await Task.Delay(800, Ct);
            Assert.Equal(readsBefore, records.SessionReads); // Polling never hydrates a session.
            records.Fail = true;
            await records.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await service.StopAsync(Ct);
            Assert.False(service.ExecuteTask!.IsFaulted);
        }
        var retained = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
        Assert.Equal(PlanningStatus.Generating, retained.Status);
        Assert.NotNull(retained.PendingCall);
        var receiptKey = state.Request.SessionId + ":" + retained.PendingCall.Id;
        var before = (await fixture.Records.GetAsync(PlanningModelJournal.Collection, "planning-tests", receiptKey, EfPlanningSessionStore.Author, Ct))!;
        Assert.NotNull(before);
        if (cancelDuringOutage)
            await fixture.Records.UpsertAsync("agent-planning-cancellations-v1", "planning-tests", state.Request.SessionId,
                new JsonObject { ["revision"] = retained.Revision, ["requestId"] = retained.PendingCall.Id }.ToJsonString(), EfPlanningSessionStore.Author, Ct);
        var coldRecords = new KeyVaultRecordStore(Path.Combine(fixture.Root, "vault.db"));
        using var cold = PlanningSessionLifecycleTests.Create(fixture, new OneCallPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client, records: coldRecords);
        client.Release.TrySetResult();
        await cold.StartAsync(Ct);
        var completed = await WaitForAsync(cold, state.Request.SessionId, cancelDuringOutage ? PlanningStatus.Cancelled : PlanningStatus.FinalReview);
        await cold.StopAsync(Ct);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, completed.Usage!.Calls);
        Assert.Equal(15, completed.Usage.TotalTokens);
        Assert.Equal(before, await coldRecords.GetAsync(PlanningModelJournal.Collection, "planning-tests", receiptKey, EfPlanningSessionStore.Author, Ct));
    }

    private sealed class WatcherFailureRecords(IKeyVaultRecordStore inner) : IKeyVaultRecordStore
    {
        internal volatile bool Fail;
        internal int SessionReads;
        internal readonly TaskCompletionSource Failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default)
        {
            if (c == EfPlanningSessionStore.Collection) Interlocked.Increment(ref SessionReads);
            if (Fail && c == "agent-planning-cancellations-v1")
            {
                Failed.TrySetResult();
                throw new KeyVaultAccessException("Injected exhausted contention", new IOException(), true);
            }
            return inner.GetAsync(c, t, k, a, ct);
        }
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default) => inner.UpsertAsync(c, t, k, v, a, ct);
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default) => inner.ListAsync(c, t, a, ct);
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default) => inner.DeleteAsync(c, t, k, a, ct);
    }

    [Fact]
    public async Task ReceiptAfterWatcherFailureRecoversAcrossSeparateProcessesWithoutDispatchOrDoubleAccounting()
    {
        const string phaseVariable = "GNOUGO_TEST_WATCHER_RECEIPT_PHASE";
        const string directoryVariable = "GNOUGO_TEST_WATCHER_RECEIPT_DIRECTORY";
        if (Environment.GetEnvironmentVariable(phaseVariable) is { } phase)
        {
            var directory = Environment.GetEnvironmentVariable(directoryVariable)!;
            await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(directory);
            var client = new PausedClient { CompleteOnCancellation = true };
            PlanningSession state;
            if (phase == "capture")
            {
                state = CapturedSession();
                Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
                var records = new WatcherFailureRecords(fixture.Records);
                using var owner = PlanningSessionLifecycleTests.Create(fixture, new OneCallPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client, records: records);
                await owner.StartAsync(Ct);
                await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
                records.Fail = true;
                await records.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
                await owner.StopAsync(Ct);
                Assert.False(owner.ExecuteTask!.IsFaulted);
                state = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
                Assert.Equal(PlanningStatus.Generating, state.Status);
                Assert.NotNull(state.PendingCall);
                Assert.Equal(1, client.Calls);
            }
            else
            {
                Assert.Equal("continue", phase);
                var captured = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "capture.json"), Ct))!;
                state = (await fixture.Store.LoadAsync("planning-tests", captured["session"]!.ToString(), Ct))!;
                client.Release.TrySetResult();
                using var cold = PlanningSessionLifecycleTests.Create(fixture, new OneCallPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client);
                await cold.StartAsync(Ct);
                state = await WaitForAsync(cold, state.Request.SessionId, PlanningStatus.FinalReview);
                await cold.StopAsync(Ct);
                Assert.Equal(0, client.Calls);
                Assert.Equal(1, state.Usage!.Calls);
                Assert.Equal(15, state.Usage.TotalTokens);
            }
            var receipts = await fixture.Records.ListAsync(PlanningModelJournal.Collection, "planning-tests", EfPlanningSessionStore.Author, Ct);
            Assert.Single(receipts);
            await File.WriteAllTextAsync(Path.Combine(directory, phase + ".json"), new JsonObject {
                ["process"] = Environment.ProcessId, ["session"] = state.Request.SessionId, ["dispatches"] = client.Calls,
                ["receiptHash"] = PlanningGraphCompiler.Fingerprint(receipts[0].Value), ["status"] = state.Status }.ToJsonString(), Ct);
            return;
        }
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var retained = Path.Combine(repository, "artifacts/storage-recovery-process-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(retained);
        foreach (var childPhase in new[] { "capture", "continue" })
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("vstest"); start.ArgumentList.Add(typeof(PlanningOwnershipTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(PlanningOwnershipTests).FullName + "." +
                nameof(ReceiptAfterWatcherFailureRecoversAcrossSeparateProcessesWithoutDispatchOrDoubleAccounting));
            start.Environment[phaseVariable] = childPhase; start.Environment[directoryVariable] = retained;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(Ct); var stderr = process.StandardError.ReadToEndAsync(Ct);
            try { await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            var log = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(retained, childPhase + ".log"), log, Ct);
            Assert.True(process.ExitCode == 0, log);
        }
        var before = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(retained, "capture.json"), Ct))!;
        var after = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(retained, "continue.json"), Ct))!;
        Assert.NotEqual(before["process"]!.GetValue<int>(), after["process"]!.GetValue<int>());
        Assert.Equal(before["receiptHash"]!.ToString(), after["receiptHash"]!.ToString());
        Assert.Equal(0, after["dispatches"]!.GetValue<int>());
    }

    private static PlanningSession CapturedSession()
    {
        var state = new PlanningSession { Request = new() { TenantId = "planning-tests", Prompt = "Return a value" } };
        state.Request.Options["construction_limits"] = JsonSerializer.SerializeToNode(
            new PlanningConstructionOptions { PhasePolicy = new() }, PlanningJsonContext.Default.PlanningConstructionOptions);
        PlanningTransportPolicies.Capture(state.Request, new() { MaxAttempts = 1 });
        return state;
    }

    private sealed class PausedClient : ILLMClient, ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium", "low"]);

        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal bool CompleteOnCancellation;
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) when (CompleteOnCancellation) { }
            return new() { Json = new JsonObject(), Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } };
        }
    }

    private sealed class OneCallPlanner : IWorkflowPlanner
    {
        public async Task<PlanningSession> AdvanceAsync(PlanningSession state, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
        {
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
            if (command.Kind == "cancel") { state.Status = PlanningStatus.Cancelled; state.Revision++; return state; }
            const string purpose = "construction_draft";
            var request = PlanningConstructionPhasePolicies.Apply(new LLMRequest
            {
                Provider = "openai", Model = "gpt-4o-mini", Prompt = "Return a value",
                StructuredOutputSchema = new JsonObject { ["title"] = purpose, ["type"] = "object" },
                RequiredReviewCalls = 0
            }, state.Request, purpose);
            request.ClientRequestId = state.Request.SessionId + ":1:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            state.PendingCall = new() { Id = request.ClientRequestId, Purpose = purpose, Request = request };
            state.ModelCalls = 1;
            state.Status = PlanningStatus.Generating;
            state.Revision++;
            await runtime.CheckpointAsync(state, ct);
            await runtime.CallAsync(request, purpose, ct);
            ct.ThrowIfCancellationRequested();
            state.PendingCall = null;
            state.Status = PlanningStatus.FinalReview;
            state.Revision++;
            await runtime.CheckpointAsync(state, ct);
            return state;
        }
    }
}
