using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;
using Xunit;

namespace GnOuGo.Flow.Persistence.Tests;

public sealed class SplitJournalTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flow-split-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private KeyVaultRecordStore Records() => new(Path.Combine(_root, "vault.db"));
    private EncryptedWorkflowRunStore Store(IKeyVaultRecordStore? records = null) => new(records ?? Records(), Path.Combine(_root, "index.db"), Path.Combine(_root, "locks"));
    private static WorkflowRun NewRun() => new() { TenantId = "tenant", RunId = "run", Limits = new() { TenantId = "tenant", RunId = "run" } };
    private static string Json(WorkflowRun run) => JsonSerializer.Serialize(run, WorkflowRunJsonContext.Default.WorkflowRun);

    [Fact]
    public async Task CustomExecutorMutationCannotChangeCapturedEvidenceOrRepeatAfterRestart()
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - { id: mutate, type: test.mutate }
                outputs:
                  result: "${data.inputs.original}"
            """;
        var calls = new List<string>();
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"];
        var engine = Engine(Store(), new Mutator(calls));
        var result = await engine.ExecuteAsync(workflow, new JsonObject { ["original"] = "before", ["removed"] = null }, Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("after", result.Outputs!["result"]!.ToString());
        var saved = (await Store().ReadAsync("tenant", "run", Ct))!;
        var invocation = Assert.Single(saved.Invocations).Value;
        Assert.False(invocation.TryGetMaterializedSnapshot(false, out _));
        Assert.Equal("before", invocation.DataBefore["inputs"]!["original"]!.ToString());
        Assert.True(invocation.DataBefore["inputs"]!.AsObject().ContainsKey("removed"));
        Assert.Equal("after", invocation.DataAfter!["inputs"]!["original"]!.ToString());
        Assert.False(invocation.DataAfter["inputs"]!.AsObject().ContainsKey("removed"));
        Assert.True((await Engine(Store(), new Mutator(calls)).ResumeAsync("tenant", "run", saved.Revision, workflow, Ct)).Success);
        Assert.Single(calls);
    }

    private sealed class Mutator(List<string> calls) : IStepExecutor
    {
        public string StepType => "test.mutate";
        public Task<JsonNode?> ExecuteAsync(StepExecutionContext context, CancellationToken ct)
        {
            calls.Add("executed");
            context.Data["inputs"]!["original"] = "after";
            context.Data["inputs"]!.AsObject().Remove("removed");
            return Task.FromResult<JsonNode?>(new JsonObject { ["changed"] = true });
        }
    }

    [Fact]
    public async Task FrozenSnapshotsCheckpointRepeatedObservationStateWithoutExpandingHistory()
    {
        var records = new MeasuredRecords(Records()); var store = Store(records);
        await store.CreateAsync(NewRun(), Ct);
        var observation = new JsonObject { ["pages"] = new JsonArray(Enumerable.Range(0, 52).Select(page => (JsonNode)new JsonObject
        { ["records"] = new JsonArray(Enumerable.Range(0, page == 51 ? 22 : 29).Select(i => (JsonNode)new JsonObject
          { ["id"] = page * 29 + i, ["text"] = new string('x', 780), ["price"] = JsonNode.Parse("1.2300"), ["missing"] = null }).ToArray()) }).ToArray()) };
        var state = new JsonObject { ["steps"] = new JsonObject { ["observed"] = observation } };
        // Retained shape: repeated switch envelopes, not inferred business data.
        for (var i = 0; i < 3; i++) state["steps"]!["branch" + i] = state["steps"]!.DeepClone();
        var logicalSnapshotBytes = Encoding.UTF8.GetByteCount(state.ToJsonString());
        long revision;
        await using (var owner = await store.AcquireAsync("tenant", "run", 0, Ct))
        {
            for (var i = 0; i < 12; i++)
            {
                state["iteration"] = i;
                var invocation = new WorkflowInvocation { Id = "step" + i, StepType = "set", Status = "completed", Output = new JsonObject { ["ok"] = true } };
                owner.Run.Invocations[invocation.Id] = invocation;
                var captureAllocated = GC.GetTotalAllocatedBytes(); var captureTimer = Stopwatch.StartNew();
                await owner.CaptureSnapshotAsync(invocation, state, false, Ct);
                await owner.CaptureSnapshotAsync(invocation, state, true, Ct);
                captureTimer.Stop(); captureAllocated = GC.GetTotalAllocatedBytes() - captureAllocated;
                if (i > 0) Assert.True(captureAllocated < logicalSnapshotBytes * 2L, $"unchanged observations were re-encoded: {captureAllocated}");
                Assert.False(invocation.TryGetMaterializedSnapshot(false, out _));
                Assert.False(invocation.TryGetMaterializedSnapshot(true, out _));
                var writes = records.BlockWrites; var bytes = records.WrittenBytes;
                var allocated = GC.GetTotalAllocatedBytes(); var timer = Stopwatch.StartNew();
                await owner.SaveAsync([invocation.Id], Ct);
                var checkpointAllocations = GC.GetTotalAllocatedBytes() - allocated;
                Assert.InRange(records.BlockWrites - writes, 1, 4);
                Assert.InRange(records.WrittenBytes - bytes, 0, 30000);
                Assert.True(checkpointAllocations < logicalSnapshotBytes, $"checkpoint allocations={checkpointAllocations}, snapshot={logicalSnapshotBytes}");
                output.WriteLine($"capture_ms={captureTimer.Elapsed.TotalMilliseconds:F2}; capture_allocation_bytes={captureAllocated}; working_set_bytes={Environment.WorkingSet}; checkpoint={i}; milliseconds={timer.Elapsed.TotalMilliseconds:F2}; allocation_bytes={checkpointAllocations}; written_bytes={records.WrittenBytes - bytes}");
            }
            var saved = owner.Run.Invocations["step0"];
            var oldValue = state["steps"]!["observed"];
            await owner.RestoreSnapshotAsync(saved, state, true, Ct);
            Assert.Same(oldValue, state["steps"]!["observed"]);
            // An arbitrary custom executor can mutate, remove and replace nodes.
            state["steps"]!["observed"]!["pages"]![0]!["records"]![0]!["price"] = JsonNode.Parse("1.23");
            state["steps"]!.AsObject().Remove("branch0"); state["unexpected"] = true;
            await owner.RestoreSnapshotAsync(saved, state, false, Ct);
            Assert.Null(state["unexpected"]); Assert.NotNull(state["steps"]!["branch0"]);
            Assert.Equal("1.2300", state["steps"]!["observed"]!["pages"]![0]!["records"]![0]!["price"]!.ToJsonString());
            Assert.False(saved.TryGetMaterializedSnapshot(false, out _));
            revision = owner.Run.Revision;
            output.WriteLine($"logical_snapshot_bytes={logicalSnapshotBytes}; logical_history_snapshot_bytes={24L * logicalSnapshotBytes}; unique_blocks={records.BlockWrites}; written_bytes={records.WrittenBytes}");
        }
        var inspected = (await Store(records).ReadAsync("tenant", "run", Ct))!;
        Assert.All(inspected.Invocations.Values, i => Assert.False(i.TryGetMaterializedSnapshot(false, out _)));
        var restartAllocation = GC.GetTotalAllocatedBytes(); records.ReadKeys.Clear();
        await using var restarted = await Store(records).AcquireAsync("tenant", "run", revision, Ct);
        Assert.All(restarted.Run.Invocations.Values, i => Assert.False(i.TryGetMaterializedSnapshot(false, out _)));
        Assert.All(records.ReadKeys.Values, count => Assert.Equal(1, count));
        Assert.True(GC.GetTotalAllocatedBytes() - restartAllocation < logicalSnapshotBytes * 4L);
        var reads = records.BlockReads;
        await restarted.RestoreSnapshotAsync(restarted.Run.Invocations["step11"], state, true, Ct);
        await restarted.SaveAsync([], Ct);
        Assert.Equal(reads, records.BlockReads);
        // Public access is detached. Explicit edits remain supported by full save.
        restarted.Run.Invocations["step0"].DataBefore["edited"] = true;
        await restarted.SaveAsync(Ct);
        await restarted.RestoreSnapshotAsync(restarted.Run.Invocations["step1"], state, false, Ct);
        Assert.Null(state["edited"]);
    }

    [Fact]
    public async Task SnapshotCaptureDoesNotCommitAReceiptBeforeTheCheckpoint()
    {
        var records = new MeasuredRecords(Records()); var store = Store(records);
        await store.CreateAsync(NewRun(), Ct);
        await using (var owner = await store.AcquireAsync("tenant", "run", 0, Ct))
        {
            var invocation = new WorkflowInvocation { Id = "external", StepType = "custom", Recovery = StepRecovery.External, Status = "dispatched", DispatchedAt = DateTimeOffset.UtcNow };
            owner.Run.Invocations[invocation.Id] = invocation;
            await owner.SaveAsync([invocation.Id], Ct);
            await owner.CaptureSnapshotAsync(invocation, new() { ["observed"] = new string('a', 10000) }, true, Ct);
            invocation.Status = "completed"; invocation.CompletedAt = DateTimeOffset.UtcNow;
            records.Fault = "before_head";
            await Assert.ThrowsAsync<IOException>(() => owner.SaveAsync([invocation.Id], Ct));
        }
        var saved = (await Store().ReadAsync("tenant", "run", Ct))!;
        Assert.Equal("dispatched", saved.Invocations["external"].Status);
        Assert.Null(saved.Invocations["external"].DataAfter);
    }

    [Fact]
    public async Task GrowingSnapshotsReusePayloadsAndCancellationNeverReadsThem()
    {
        var records = new MeasuredRecords(Records()); var store = Store(records); var run = NewRun();
        var payload = new JsonArray(Enumerable.Range(0, 80).Select(i => (JsonNode)new JsonObject
        { ["label"] = "record-" + i, ["reference"] = new string((char)('a' + i % 20), 5000), ["number"] = JsonNode.Parse("1.2300"), ["null"] = null }).ToArray());
        run.Inputs = payload.DeepClone();
        run.ModelBudget = new() { MaxCalls = 8, MaxTotalTokens = 100000, MaxEstimatedCost = new(12.50m, "EUR") };
        run.ModelUsage = new() { Calls = 3, InputTokens = 4300, OutputTokens = 200, TotalTokens = 4500, EstimatedCost = 0.123450m, EstimatedCostCurrency = "EUR", StartedAtUtc = DateTimeOffset.UtcNow };
        await store.CreateAsync(run, Ct);
        await using var owner = await store.AcquireAsync("tenant", "run", 0, Ct);
        var timings = new List<double>();
        for (var i = 0; i < 70; i++)
        {
            var id = "/step/" + i;
            owner.Run.Invocations[id] = new() { Id = id, StepType = "set", DataBefore = new() { ["observations"] = payload.DeepClone(), ["index"] = i },
                DataAfter = new() { ["observations"] = payload.DeepClone(), ["index"] = i + 1 }, Output = payload.DeepClone() };
            owner.Run.Events.Add(new(DateTimeOffset.UtcNow, "receipt", id));
            var timer = Stopwatch.StartNew(); await owner.SaveAsync([id], Ct); timings.Add(timer.Elapsed.TotalMilliseconds);
        }
        Assert.Equal(records.BlockWrites, records.WrittenBlockKeys.Count);
        var reads = records.BlockReads; var writes = records.BlockWrites;
        for (var i = 0; i < 10; i++) Assert.False(await owner.IsCancellationRequestedAsync(Ct));
        Assert.Equal(reads, records.BlockReads); Assert.Equal(writes, records.BlockWrites);
        records.ReadKeys.Clear();
        var reconstructed = (await Store(records).ReadAsync("tenant", "run", Ct))!;
        Assert.Equal(JsonNode.Parse(Json(owner.Run)), JsonNode.Parse(Json(reconstructed)), JsonNodeEqualityComparer.Instance);
        Assert.All(records.ReadKeys.Values, count => Assert.Equal(1, count));
        Assert.Contains("1.2300", Json(reconstructed));
        var physical = (await Records().ListAsync("flow-execution-blocks-v1", "tenant", "test", Ct)).Sum(r => Encoding.UTF8.GetByteCount(r.Value));
        var logical = Encoding.UTF8.GetByteCount(Json(reconstructed));
        Assert.True(physical < logical / 5, $"physical={physical}; logical={logical}");
        Assert.True(records.MaxHeadBytes < 60000, $"head={records.MaxHeadBytes}");
        output.WriteLine($"logical_bytes={logical}; physical_block_bytes={physical}; written_bytes={records.WrittenBytes}; blocks={records.BlockWrites}; largest_head={records.MaxHeadBytes}; checkpoint_median_ms={timings.Order().ElementAt(timings.Count / 2):F2}; checkpoint_p95_ms={timings.Order().ElementAt((int)(timings.Count * .95)):F2}");
    }

    [Theory]
    [InlineData("before_block")]
    [InlineData("before_head")]
    [InlineData("after_head")]
    public async Task InterruptedCheckpointPublishesAllOrNothing(string point)
    {
        var records = new MeasuredRecords(Records()); var store = Store(records);
        await store.CreateAsync(NewRun(), Ct);
        await using (var owner = await store.AcquireAsync("tenant", "run", 0, Ct))
        {
            owner.Run.Invocations["effect"] = new() { Id = "effect", StepType = "mcp.call", Recovery = StepRecovery.External,
                DispatchedAt = DateTimeOffset.UtcNow, Status = "dispatched" };
            await owner.SaveAsync(["effect"], Ct);
            owner.Run.Invocations["effect"].Output = new JsonObject { ["secret"] = new string('s', 10000) };
            owner.Run.Invocations["effect"].CompletedAt = DateTimeOffset.UtcNow;
            owner.Run.Invocations["effect"].Status = "completed";
            owner.Run.Invocations["effect"].ExternalCompletionObserved = true;
            records.Fault = point;
            await Assert.ThrowsAsync<IOException>(() => owner.SaveAsync(["effect"], Ct));
        }
        var saved = (await Store().ReadAsync("tenant", "run", Ct))!;
        Assert.Equal(point == "after_head" ? "completed" : "dispatched", saved.Invocations["effect"].Status);
        Assert.Equal(point == "after_head" ? 2 : 1, saved.Revision);
        Assert.Equal(point == "after_head", saved.Invocations["effect"].ExternalCompletionObserved);
    }

    [Fact]
    public async Task ConcurrentAnswersAndCancellationMergeWithoutLoadingUnchangedPayloads()
    {
        var records = new MeasuredRecords(Records()); var store = Store(records); var other = Store(); var run = NewRun();
        run.Invocations["/work"] = new() { Id = "/work", StepType = "agent.run", Recovery = StepRecovery.External,
            Status = "dispatched", DispatchedAt = DateTimeOffset.UtcNow, DataBefore = new() { ["large"] = new string('x', 10000) } };
        for (var i = 0; i < 31; i++) run.Events.Add(new(DateTimeOffset.UtcNow, "previous_" + i, null));
        await store.CreateAsync(run, Ct);
        await using var owner = await store.AcquireAsync("tenant", "run", 0, Ct);
        // A local pending checkpoint crosses a chunk boundary while a command
        // has committed a different tail. Neither event may be lost or duplicated.
        owner.Run.Events.Add(new(DateTimeOffset.UtcNow, "owner_pending", "/work"));
        var request = new HumanInputRequest { RunId = "run", StepId = "/work/human/one", ParentInvocationId = "/work", Prompt = "Approve?" };
        await other.RequestInputAsync("tenant", "run", 0, request, Ct);
        await other.AnswerAsync("tenant", "run", 1, request.StepId, null, Ct);
        await other.CancelAsync("tenant", "run", 2, Ct);
        records.ReadKeys.Clear();
        await owner.SaveAsync([], Ct);
        Assert.True(owner.Run.CancelRequested); Assert.Equal(4, owner.Run.Revision);
        Assert.True(owner.Run.Invocations[request.StepId].Control.ContainsKey("human_response"));
        Assert.Equal("completed", owner.Run.Invocations[request.StepId].Status);
        var saved = (await other.ReadAsync("tenant", "run", Ct))!;
        Assert.Equal(owner.Run.Events, saved.Events);
        Assert.Single(saved.Events, e => e.Kind == "owner_pending");
        Assert.Single(saved.Events, e => e.Kind == "human_answer");
        Assert.Single(saved.Events, e => e.Kind == "cancel_requested");
        await Assert.ThrowsAsync<WorkflowRunConflictException>(() => other.AnswerAsync("tenant", "run", 4, request.StepId, JsonValue.Create("again"), Ct));
    }

    [Fact]
    public async Task IncrementalMetadataSaveDoesNotPublishUnspecifiedHistoricalDialogChanges()
    {
        var store = Store(); var run = NewRun();
        run.Invocations["work"] = new() { Id = "work", CompletedAt = DateTimeOffset.UtcNow };
        run.Invocations["work/human/answer"] = new() { Id = "work/human/answer", ParentInvocationId = "work",
            CompletedAt = DateTimeOffset.UtcNow, Status = "completed", Output = JsonValue.Create("original"),
            Control = new() { ["human_response"] = JsonValue.Create("original") } };
        await store.CreateAsync(run, Ct);
        await using var owner = await store.AcquireAsync("tenant", "run", 0, Ct);
        owner.Run.Invocations["work/human/answer"].Output = JsonValue.Create("explicit edit");
        await owner.SaveAsync([], Ct);
        Assert.Equal("original", (await Store().ReadAsync("tenant", "run", Ct))!.Invocations["work/human/answer"].Output!.ToString());
        await owner.SaveAsync(Ct);
        Assert.Equal("explicit edit", (await Store().ReadAsync("tenant", "run", Ct))!.Invocations["work/human/answer"].Output!.ToString());
    }

    [Fact]
    public async Task LegacyLayoutRemainsUnchangedAndFullSaveSupportsArbitraryEdits()
    {
        var records = Records(); var run = NewRun(); var original = Json(run);
        await records.UpsertAsync(EncryptedWorkflowRunStore.Collection, "tenant", "run", original, "test", Ct);
        Assert.Equal(original, Json((await Store().ReadAsync("tenant", "run", Ct))!));
        Assert.Equal(original, (await records.GetAsync(EncryptedWorkflowRunStore.Collection, "tenant", "run", "test", Ct))!.Value);
        await using (var lease = await Store().AcquireAsync("tenant", "run", 0, Ct))
        { lease.Run.Inputs = JsonNode.Parse("{\"changed\":true}"); await lease.SaveAsync([], Ct); }
        var record = (await records.GetAsync(EncryptedWorkflowRunStore.Collection, "tenant", "run", "test", Ct))!;
        Assert.Null(JsonNode.Parse(record.Value)!["storageVersion"]);
        Assert.Equal(1, WorkflowRunStorage.Read(record.Value, "tenant", "run").Revision);
        var fresh = NewRun(); fresh.RunId = "new"; fresh.Limits.RunId = "new"; await Store().CreateAsync(fresh, Ct);
        await using (var lease = await Store().AcquireAsync("tenant", "new", 0, Ct))
        { lease.Run.Invocations["added"] = new() { Id = "added", Output = new JsonObject { ["added"] = true } }; await lease.SaveAsync(Ct); }
        Assert.True((await Store().ReadAsync("tenant", "new", Ct))!.Invocations.ContainsKey("added"));
        var head = (await records.GetAsync(EncryptedWorkflowRunStore.Collection, "tenant", "new", "test", Ct))!.Value;
        Assert.Throws<WorkflowRunConflictException>(() => WorkflowRunStorage.Read(head, "tenant", "new"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("tenant")]
    [InlineData("run")]
    public async Task InvalidReferencedBlocksFailClosed(string variant)
    {
        var records = Records(); var run = NewRun(); run.Inputs = JsonValue.Create(new string('z', 10000));
        await Store().CreateAsync(run, Ct);
        var block = (await records.ListAsync("flow-execution-blocks-v1", "tenant", "test", Ct)).First();
        if (variant == "missing") await records.DeleteAsync(block.Collection, "tenant", block.Key, "test", Ct);
        else
        {
            var body = JsonNode.Parse(block.Value)!;
            if (variant == "corrupt") body["body"]!["kind"] = "wrong";
            else body[variant == "tenant" ? "tenantId" : "runId"] = "another";
            await records.UpsertAsync(block.Collection, "tenant", block.Key, body.ToJsonString(), "test", Ct);
        }
        await Assert.ThrowsAsync<WorkflowRunConflictException>(() => Store().ReadAsync("tenant", "run", Ct));
        Assert.Null(await Store().ReadAsync("other", "run", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealFlowRecoveryDoesNotRepeatEffectsAndOnlyCleansUpAfterCommittedReceipt(bool committed)
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - { id: work, type: test.effect, input: { value: observed } }
                finally:
                  - { id: cleanup, type: test.effect, input: { value: cleanup } }
            """;
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"];
        var records = new MeasuredRecords(Records()); var effects = new List<string>();
        var first = Engine(Store(records), new Effect(effects, () => records.Fault = committed ? "after_head" : "before_head"));
        await Assert.ThrowsAnyAsync<Exception>(() => first.ExecuteAsync(workflow, null, Ct));
        Assert.Equal(["observed"], effects);
        var store = Store(); var saved = (await store.ReadAsync("tenant", "run", Ct))!;
        Assert.False(saved.FinalizationStarted);
        var recovered = Engine(store, new Effect(effects, null));
        var result = await recovered.ResumeAsync("tenant", "run", saved.Revision, workflow, Ct);
        Assert.Equal(committed, result.Success);
        Assert.Equal(committed ? new[] { "observed", "cleanup" } : ["observed"], effects);
        if (!committed) Assert.Equal("RUN_NEEDS_RECONCILIATION", result.Error!.Code);
        saved = (await store.ReadAsync("tenant", "run", Ct))!;
        Assert.Equal(committed, saved.FinalizationCompleted);
        Assert.Equal(1, saved.StepsStarted);
        if (committed)
        {
            var again = await Engine(Store(), new Effect(effects, null)).ResumeAsync("tenant", "run", saved.Revision, workflow, Ct);
            Assert.True(again.Success); Assert.Equal(2, effects.Count);
        }
    }

    private static WorkflowEngine Engine(IWorkflowRunStore store, IStepExecutor effect)
    {
        var engine = new WorkflowEngine { RunStore = store, Limits = new() { TenantId = "tenant", RunId = "run" } };
        engine.Registry.Register(effect); return engine;
    }
    private sealed class Effect(List<string> values, Action? after) : IStepExecutor
    {
        public string StepType => "test.effect";
        public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
        {
            var value = ctx.Engine.GetResolvedInput(ctx)!["value"]!.ToString();
            values.Add(value); after?.Invoke();
            return Task.FromResult<JsonNode?>(new JsonObject { ["value"] = value, ["evidence"] = new string('x', 10000) });
        }
    }

    private sealed class JsonNodeEqualityComparer : IEqualityComparer<JsonNode?>
    {
        internal static readonly JsonNodeEqualityComparer Instance = new();
        public bool Equals(JsonNode? x, JsonNode? y) => JsonNode.DeepEquals(x, y);
        public int GetHashCode(JsonNode? obj) => 0;
    }
    private sealed class MeasuredRecords(IKeyVaultRecordStore inner) : IKeyVaultRecordStore
    {
        internal int BlockReads, BlockWrites, MaxHeadBytes;
        internal long WrittenBytes;
        internal string? Fault;
        internal readonly HashSet<string> WrittenBlockKeys = [];
        internal readonly Dictionary<string, int> ReadKeys = [];
        public async Task<KeyVaultRecordValue?> GetAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
        {
            if (collection == "flow-execution-blocks-v1") { BlockReads++; ReadKeys[key] = ReadKeys.GetValueOrDefault(key) + 1; }
            return await inner.GetAsync(collection, tenantId, key, author, ct);
        }
        public async Task<KeyVaultRecordValue> UpsertAsync(string collection, string tenantId, string key, string value, string author, CancellationToken ct = default)
        {
            var block = collection == "flow-execution-blocks-v1";
            if (Fault == "before_block" && block || Fault == "before_head" && !block) { Fault = null; throw new IOException("Injected before commit."); }
            WrittenBytes += Encoding.UTF8.GetByteCount(value);
            if (block) { BlockWrites++; WrittenBlockKeys.Add(key); }
            else MaxHeadBytes = Math.Max(MaxHeadBytes, Encoding.UTF8.GetByteCount(value));
            var result = await inner.UpsertAsync(collection, tenantId, key, value, author, ct);
            if (Fault == "after_head" && !block) { Fault = null; throw new IOException("Injected lost acknowledgement."); }
            return result;
        }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string collection, string tenantId, string author, CancellationToken ct = default) => inner.ListAsync(collection, tenantId, author, ct);
        public Task<bool> DeleteAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default) => inner.DeleteAsync(collection, tenantId, key, author, ct);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
