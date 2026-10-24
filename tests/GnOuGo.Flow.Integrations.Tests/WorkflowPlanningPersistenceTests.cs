using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Flow.Integrations.Tests;

public sealed class WorkflowPlanningPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gnougo-runtime-planning-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private WorkflowPlanningRuntimeFactory Factory() => new(new KeyVaultRecordStore(Path.Combine(_directory, "keyvault.db")), Path.Combine(_directory, "leases"));

    [Fact]
    public async Task NewQueuedSessionCapturesProfilesWithoutChangingItsAuthoredDefinition()
    {
        var client = new Client();
        var initial = Initial();
        var authored = initial.Request.Options.ToJsonString();
        string id;
        await using (var session = await Factory().OpenAsync(Context(client), initial, Ct))
        {
            id = session.Session.Request.SessionId;
            Assert.Equal("1", session.Session.Request.Options["planning_diagnostics_version"]?.ToString());
            Assert.Equal("2", session.Session.Request.Options["planning_input_review_version"]?.ToString());
            Assert.Equal("2", session.Session.Request.Options["business_judge_review_version"]?.ToString());
            Assert.Equal(0, session.Session.Revision);
            Assert.Equal(0, session.Session.ModelCalls);
        }
        Assert.Equal(authored, initial.Request.Options.ToJsonString());
        var records = new KeyVaultRecordStore(Path.Combine(_directory, "keyvault.db"));
        var definition = (await records.GetAsync("flow-planning-definitions-v10", "tenant", id, "test", Ct))!;
        Assert.Equal(authored, JsonNode.Parse(definition.Value)!["options"]!.ToJsonString());
        Assert.Equal(1, JsonNode.Parse(definition.Value)!["creationCheckpoint"]!["version"]!.GetValue<int>());
        await using var restored = await Factory().OpenAsync(Context(client), Initial(), Ct);
        Assert.Equal("2", restored.Session.Request.Options["business_judge_review_version"]!.ToString());
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalZeroProgressDefinitionDoesNotAcquireCreationProfiles(bool sessionCheckpointExists)
    {
        var client = new Client();
        string id;
        await using (var session = await Factory().OpenAsync(Context(client), Initial(), Ct))
            id = session.Session.Request.SessionId;
        var records = new KeyVaultRecordStore(Path.Combine(_directory, "keyvault.db"));
        var definition = (await records.GetAsync("flow-planning-definitions-v10", "tenant", id, "test", Ct))!;
        var historicalDefinition = JsonNode.Parse(definition.Value)!.AsObject();
        historicalDefinition.Remove("creationCheckpoint");
        await records.UpsertAsync("flow-planning-definitions-v10", "tenant", id, historicalDefinition.ToJsonString(), "test", Ct);
        var saved = (await records.GetAsync("flow-planning-sessions-v10", "tenant", id, "test", Ct))!;
        if (sessionCheckpointExists)
        {
            var historical = JsonNode.Parse(saved.Value)!;
            historical.AsObject().Remove("construction");
            var options = historical["request"]!["options"]!.AsObject();
            foreach (var key in new[] { "compilation_profile", "mapping_profile", "planning_validation_profile",
                "planning_diagnostics_version", "planning_input_review_version", "business_judge_review_version",
                "construction_version", "construction_input_limits", "clarification_policy", "planning_transport_policy" }) options.Remove(key);
            await records.UpsertAsync("flow-planning-sessions-v10", "tenant", id, historical.ToJsonString(), "test", Ct);
        }
        else Assert.True(await records.DeleteAsync("flow-planning-sessions-v10", "tenant", id, "test", Ct));
        var reconstructed = Initial(); reconstructed.Construction = new();
        await using var restored = await Factory().OpenAsync(Context(client), reconstructed, Ct);
        Assert.Equal(0, restored.Session.Revision);
        Assert.Equal(0, restored.Session.ModelCalls);
        Assert.Null(restored.Session.Request.Options["planning_diagnostics_version"]);
        Assert.Null(restored.Session.Request.Options["planning_input_review_version"]);
        Assert.Null(restored.Session.Request.Options["business_judge_review_version"]);
        Assert.Null(restored.Session.Construction);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task InterruptedCreationRestoresExactCapturedLimitsBeforeItsFirstSessionCheckpoint()
    {
        var records = new KeyVaultRecordStore(Path.Combine(_directory, "keyvault.db"));
        var factory = new WorkflowPlanningRuntimeFactory(new InterruptFirstSession(records), Path.Combine(_directory, "leases"));
        var client = new Client(); var original = Initial();
        original.Construction = new() { Limits = new() { MaxTasksPerDraft = 4, DraftOutputTokens = 4096, ReviewOutputTokens = 1024 } };
        var authoredOptions = original.Request.Options.ToJsonString();
        await Assert.ThrowsAsync<IOException>(() => factory.OpenAsync(Context(client), original, Ct));
        var id = original.Request.SessionId;
        Assert.Null(await records.GetAsync("flow-planning-sessions-v10", "tenant", id, "test", Ct));
        var definition = (await records.GetAsync("flow-planning-definitions-v10", "tenant", id, "test", Ct))!;
        var changedDefaults = Initial(); changedDefaults.Construction = new();
        await using var restored = await Factory().OpenAsync(Context(client), changedDefaults, Ct);
        Assert.Equal(4, restored.Session.Construction!.Limits.MaxTasksPerDraft);
        Assert.Equal(4096, restored.Session.Construction.Limits.DraftOutputTokens);
        Assert.Equal(1024, restored.Session.Construction.Limits.ReviewOutputTokens);
        Assert.Equal("2", restored.Session.Request.Options["business_judge_review_version"]!.ToString());
        Assert.Equal("2", restored.Session.Request.Options["construction_version"]!.ToString());
        Assert.Equal(authoredOptions, original.Request.Options.ToJsonString());
        Assert.Equal(authoredOptions, changedDefaults.Request.Options.ToJsonString());
        Assert.Equal(definition, await records.GetAsync("flow-planning-definitions-v10", "tenant", id, "test", Ct));
        Assert.Equal(0, restored.Session.ModelCalls); Assert.Equal(0, restored.Session.ReplanAttempts);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCreationRejectsChangedAuthoredOrCapturedLimits(bool capturedCheckpoint)
    {
        var records = new KeyVaultRecordStore(Path.Combine(_directory, "keyvault.db"));
        var factory = new WorkflowPlanningRuntimeFactory(new InterruptFirstSession(records), Path.Combine(_directory, "leases"));
        var client = new Client(); var original = Initial();
        await Assert.ThrowsAsync<IOException>(() => factory.OpenAsync(Context(client), original, Ct));
        var id = original.Request.SessionId;
        var definition = (await records.GetAsync("flow-planning-definitions-v10", "tenant", id, "test", Ct))!;
        var resumed = Initial();
        if (capturedCheckpoint)
        {
            var changed = JsonNode.Parse(definition.Value)!;
            changed["creationCheckpoint"]!["session"]!["request"]!["generation"]!["maxOutputTokens"] = 16000;
            await records.UpsertAsync("flow-planning-definitions-v10", "tenant", id, changed.ToJsonString(), "test", Ct);
        }
        else resumed.Request.Generation.MaxOutputTokens = 16000;
        await Assert.ThrowsAsync<PlanningConflictException>(() => Factory().OpenAsync(Context(client), resumed, Ct));
        Assert.Null(await records.GetAsync("flow-planning-sessions-v10", "tenant", id, "test", Ct));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task MissingActiveCheckpointCannotResetToTheCreationSnapshot()
    {
        var client = new Client(); string id;
        await using (var session = await Factory().OpenAsync(Context(client), Initial(), Ct))
        {
            id = session.Session.Request.SessionId;
            await session.Runtime.CallAsync(Request(session.Session, "one"), "intent", Ct);
        }
        var records = new KeyVaultRecordStore(Path.Combine(_directory, "keyvault.db"));
        Assert.True(await records.DeleteAsync("flow-planning-sessions-v10", "tenant", id, "test", Ct));
        var error = await Assert.ThrowsAsync<PlanningConflictException>(() => Factory().OpenAsync(Context(client), Initial(), Ct));
        Assert.Contains("cannot reset retained usage", error.Message);
        Assert.Equal(1, client.Calls);
        Assert.Null(await records.GetAsync("flow-planning-sessions-v10", "tenant", id, "test", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredGuardRepairSettlesOnlyItsVerifiedReceipt(bool interrupted)
    {
        var client = new Client { Fail = interrupted }; LLMRequest request;
        await using (var session = await Factory().OpenAsync(Context(client), Initial(), Ct))
        {
            request = Request(session.Session, "retired");
            session.Session.IntentVersion = 2;
            session.Session.Diagnostics.Add(new("TASK_CONDITIONAL_REQUIREMENT", "/tasks/route/condition", "Retained proof repair"));
            session.Session.PendingCall = new() { Id = request.ClientRequestId!, Purpose = "replan", Request = request };
            await session.Runtime.CheckpointAsync(session.Session, Ct);
            if (interrupted) await Assert.ThrowsAsync<IOException>(() => session.Runtime.CallAsync(request, "replan", Ct));
            else await session.Runtime.CallAsync(request, "replan", Ct);
        }
        await using (var restored = await Factory().OpenAsync(Context(client), Initial(), Ct))
        {
            Assert.True(restored.Session.RequiresPlanningRevision);
            Assert.Equal(interrupted, restored.Session.PendingCall is not null);
            Assert.Equal(1, restored.Session.Usage!.Calls);
            Assert.Null(restored.Session.Plan);
            Assert.Single(restored.Session.Diagnostics, d => d.Code == "TASK_CONDITIONAL_REQUIREMENT");
            if (!interrupted) Assert.Equal(PlanningStatus.Stopped, restored.Session.Status);
        }
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task CapturedLowNavigationReplaysItsExactEncryptedRequestWithGlobalMedium()
    {
        var client = new Client(); var initial = Initial();
        initial.Construction = new() { Limits = new() { PhasePolicy = new()
        {
            Navigation = new() { TargetInputTokens = 3000, MaxInputTokens = 6000, Reasoning = "low", OutputTokens = 4096 }
        } } };
        LLMRequest request; string issued;
        await using (var session = await Factory().OpenAsync(Context(client), initial, Ct))
        {
            Assert.Equal("medium", session.Session.Request.Generation.Reasoning);
            request = Request(session.Session, "one");
            Assert.Equal("low", request.Reasoning); Assert.Equal(4096, request.MaxTokens);
            issued = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
            session.Session.PendingCall = new() { Id = request.ClientRequestId!, Purpose = "construction_intent", Request = request };
            await session.Runtime.CheckpointAsync(session.Session, Ct);
            await session.Runtime.CallAsync(request, "construction_intent", Ct);
        }
        await using (var restored = await Factory().OpenAsync(Context(client), Initial(), Ct))
        {
            Assert.Equal(issued, JsonSerializer.Serialize(restored.Session.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest));
            var response = await restored.Runtime.CallAsync(restored.Session.PendingCall.Request, "construction_intent", Ct);
            Assert.Equal("secret result", response.Json!["value"]!.ToString());
            Assert.Equal(issued, JsonSerializer.Serialize(restored.Session.PendingCall.Request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(1, restored.Session.Usage!.Calls);
        }
        Assert.Equal(1, client.Calls);
        Assert.Equal(4096, Assert.Single(client.Ceilings));
        Assert.Equal("low", Assert.Single(client.Reasoning));
    }

    [Fact]
    public async Task CompletedRequestReplaysFromEncryptedStorageAfterRestartWithoutChargingAgain()
    {
        var client = new Client(); var factory = Factory(); LLMRequest request;
        await using (var session = await factory.OpenAsync(Context(client), Initial(), Ct))
        {
            request = Request(session.Session, "one");
            session.Session.PendingCall = new() { Id = request.ClientRequestId!, Purpose = "intent", Request = request };
            await session.Runtime.CheckpointAsync(session.Session, Ct);
            await session.Runtime.CallAsync(request, "intent", Ct);
        }
        await using (var reopened = await Factory().OpenAsync(Context(client), Initial(), Ct))
        {
            Assert.NotNull(reopened.Session.PendingCall);
            Assert.True(reopened.Session.ActiveMilliseconds > 0);
            Assert.Equal(1, reopened.Session.Usage!.Calls);
            var replay = await reopened.Runtime.CallAsync(request, "intent", Ct);
            Assert.Equal("secret result", replay.Json!["value"]!.ToString());
            await reopened.Runtime.CheckpointAsync(reopened.Session, Ct);
            Assert.Equal(1, reopened.Session.Usage.Calls);
        }
        Assert.Equal(1, client.Calls);
        foreach (var file in Directory.GetFiles(_directory, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file, Ct);
            Assert.DoesNotContain("secret intent", System.Text.Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain("secret result", System.Text.Encoding.UTF8.GetString(bytes));
        }
    }

    [Fact]
    public async Task ReasoningOnlyOutputLimitReplaysItsReceiptAndUsageWithoutAnotherDispatch()
    {
        var client = new Client { Truncate = true }; LLMRequest request;
        await using (var session = await Factory().OpenAsync(Context(client), Initial(), Ct))
        {
            request = Request(session.Session, "one");
            session.Session.PendingCall = new() { Id = request.ClientRequestId!, Purpose = "intent", Request = request };
            await session.Runtime.CheckpointAsync(session.Session, Ct);
            var response = await session.Runtime.CallAsync(request, "intent", Ct);
            Assert.Equal("output_limit", response.CompletionStatus);
            Assert.Empty(response.Text); Assert.Null(response.Json);
            Assert.Equal(8192, response.Usage!["completion_tokens_details"]!["reasoning_tokens"]!.GetValue<int>());
        }
        await using (var reopened = await Factory().OpenAsync(Context(client), Initial(), Ct))
        {
            Assert.Equal(request.ClientRequestId, reopened.Session.PendingCall!.Id);
            Assert.True(JsonNode.DeepEquals(request.StructuredOutputSchema, reopened.Session.PendingCall.Request.StructuredOutputSchema));
            var replay = await reopened.Runtime.CallAsync(request, "intent", Ct);
            Assert.Equal("output_limit", replay.CompletionStatus); Assert.Empty(replay.Text); Assert.Null(replay.Json);
            Assert.Equal(1, reopened.Session.Usage!.Calls);
            Assert.Equal(12, reopened.Session.Usage.InputTokens);
            Assert.Equal(8192, reopened.Session.Usage.OutputTokens);
            Assert.Equal(8204, reopened.Session.Usage.TotalTokens);
        }
        Assert.Equal(1, client.Calls); Assert.Equal(8192, Assert.Single(client.Ceilings));
    }

    [Fact]
    public async Task UnverifiableDispatchStopsAcrossRestartAndRetainsItsBudget()
    {
        var client = new Client { Fail = true }; LLMRequest request;
        await using (var session = await Factory().OpenAsync(Context(client), Initial(), Ct))
        {
            request = Request(session.Session, "one");
            await Assert.ThrowsAsync<IOException>(() => session.Runtime.CallAsync(request, "intent", Ct));
        }
        client.Fail = false;
        await using var reopened = await Factory().OpenAsync(Context(client), Initial(), Ct);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => reopened.Runtime.CallAsync(request, "intent", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, error.Code);
        Assert.Equal(1, client.Calls); Assert.Equal(1, reopened.Session.Usage!.Calls);
    }

    [Fact]
    public async Task LeaseRejectsConcurrentWritersAndTenantsRemainIndependent()
    {
        var client = new Client();
        await using var first = await Factory().OpenAsync(Context(client), Initial(), Ct);
        await Assert.ThrowsAsync<PlanningConflictException>(() => Factory().OpenAsync(Context(client), Initial(), Ct));
        var other = Initial(); other.Request.TenantId = "other";
        await using var second = await Factory().OpenAsync(Context(client, "other"), other, Ct);
        await first.Runtime.CallAsync(Request(first.Session, "one"), "intent", Ct);
        await second.Runtime.CallAsync(Request(second.Session, "one"), "intent", Ct);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task ConcurrentReservationsRespectThePersistedCallLimit()
    {
        var client = new Client();
        await using (var session = await Factory().OpenAsync(Context(client), Initial(), Ct))
        {
            var calls = Enumerable.Range(0, 4).Select(async i =>
            {
                try { await session.Runtime.CallAsync(Request(session.Session, i.ToString()), "intent", Ct); return true; }
                catch (WorkflowRuntimeException e) when (e.Code == ErrorCodes.LlmBudgetExceeded) { return false; }
            });
            Assert.Single(await Task.WhenAll(calls), passed => passed);
        }
        await using var reopened = await Factory().OpenAsync(Context(client), Initial(), Ct);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => reopened.Runtime.CallAsync(Request(reopened.Session, "next"), "intent", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetExceeded, error.Code); Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task NewDefaultDoesNotReintroduceAnEightCallBudget()
    {
        var client = new Client(); var initial = Initial();
        initial.Request.Options.Clear(); initial.Request.MaxModelCalls = 12;
        await using var session = await Factory().OpenAsync(Context(client), initial, Ct);
        for (var i = 0; i < 9; i++) await session.Runtime.CallAsync(Request(session.Session, i.ToString()), "tasks", Ct);
        Assert.Equal(9, client.Calls);
        await session.Runtime.CheckpointAsync(session.Session, Ct);
        Assert.Equal(9, session.Session.Usage!.Calls);
    }

    [Fact]
    public async Task RuntimeResumeRetainsOmittedDefaultsButRejectsExplicitChanges()
    {
        var client = new Client(); var original = Initial();
        original.Request.Options = new() { ["raw_prompt"] = "secret intent", ["generator"] = new JsonObject() };
        original.Request.MaxModelCalls = 8; original.Request.MaxReplanAttempts = 2;
        original.Request.Generation = new() { MaxInputTokensPerRequest = 12000, MaxOutputTokens = 8192 };
        await using (var first = await Factory().OpenAsync(Context(client), original, Ct)) { }
        var current = Initial(); current.Request.Options = original.Request.Options.DeepClone().AsObject();
        current.Request.MaxModelCalls = 64; current.Request.MaxReplanAttempts = 4;
        current.Request.Generation = new() { MaxInputTokensPerRequest = 32000, MaxOutputTokens = 32768 };
        await using (var restored = await Factory().OpenAsync(Context(client), current, Ct))
        {
            Assert.Equal(8, restored.Session.Request.MaxModelCalls);
            Assert.Equal(12000, restored.Session.Request.Generation.MaxInputTokensPerRequest);
        }
        current.Request.Options["generator"]!["max_input_tokens"] = 32000;
        await Assert.ThrowsAsync<PlanningConflictException>(() => Factory().OpenAsync(Context(client), current, Ct));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ChangedRequestCannotResetAnExistingSession()
    {
        await using (var session = await Factory().OpenAsync(Context(new Client()), Initial(), Ct)) { }
        var changed = Initial(); changed.Request.Options["llm_budget"]!["max_calls"] = 10;
        await Assert.ThrowsAsync<PlanningConflictException>(() => Factory().OpenAsync(Context(new Client()), changed, Ct));
    }

    [Fact]
    public async Task IncompatibleEncryptedSessionCannotResumeAndRemainsUnchanged()
    {
        string id;
        await using (var session = await Factory().OpenAsync(Context(new Client()), Initial(), Ct)) { id = session.Session.Request.SessionId; }
        var records = new KeyVaultRecordStore(Path.Combine(_directory, "keyvault.db"));
        var record = (await records.GetAsync("flow-planning-sessions-v10", "tenant", id, "test", Ct))!;
        var json = JsonNode.Parse(record.Value)!.AsObject(); json["schemaVersion"] = 8;
        var original = await records.UpsertAsync("flow-planning-sessions-v10", "tenant", id, json.ToJsonString(), "test", Ct);
        var error = await Assert.ThrowsAsync<PlanningConflictException>(() => Factory().OpenAsync(Context(new Client()), Initial(), Ct));
        Assert.Contains("Regenerate and approve", error.Message);
        Assert.Equal(original, await records.GetAsync("flow-planning-sessions-v10", "tenant", id, "test", Ct));
    }

    private static PlanningSession Initial() => new()
    {
        Request = new() { TenantId = "tenant", Prompt = "secret intent", Generation = new() { MaxInputTokensPerRequest = 12000, MaxOutputTokens = 8192 }, Options = new() { ["llm_budget"] = new JsonObject { ["max_calls"] = 1 } } }
    };
    private static StepExecutionContext Context(ILLMClient client, string tenant = "tenant") => new()
    {
        Engine = new WorkflowEngine { LLMClient = client },
        Limits = new() { RunId = "run", TenantId = tenant },
        Data = new(),
        Step = new() { Source = new StepDef { Id = "plan", Type = "workflow.plan" } }
    };
    private static LLMRequest Request(PlanningSession snapshot, string attempt)
    {
        var request = new LLMRequest
        {
            Prompt = "secret intent",
            Model = "test",
            Reasoning = "low",
            StructuredOutputStrict = true,
            StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")
        };
        if (PlanningConstructionInputPolicy.IsCompact(snapshot.Request))
            request.StructuredOutputSchema!["title"] = "construction_intent";
        PlanningConstructionPhasePolicies.Apply(request, snapshot.Request, "construction_intent");
        if (PlanningTransportPolicies.Read(snapshot.Request) is not null) request.RequiredReviewCalls = 0;
        request.ClientRequestId = snapshot.Request.SessionId + ":" + attempt + ":intent:workflow:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        return request;
    }
    private sealed class InterruptFirstSession(IKeyVaultRecordStore inner) : IKeyVaultRecordStore
    {
        private bool _interrupted;
        public Task<KeyVaultRecordValue?> GetAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default) =>
            inner.GetAsync(collection, tenantId, key, author, ct);
        public Task<KeyVaultRecordValue> UpsertAsync(string collection, string tenantId, string key, string value, string author, CancellationToken ct = default)
        {
            if (collection == "flow-planning-sessions-v10" && !_interrupted)
            {
                _interrupted = true;
                throw new IOException("Interrupted after the authored definition, before the first session checkpoint.");
            }
            return inner.UpsertAsync(collection, tenantId, key, value, author, ct);
        }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string collection, string tenantId, string author, CancellationToken ct = default) =>
            inner.ListAsync(collection, tenantId, author, ct);
        public Task<bool> DeleteAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default) =>
            inner.DeleteAsync(collection, tenantId, key, author, ct);
    }
    private sealed class Client : ILLMClient, ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["low", "medium"]);
        private int _calls;
        public int Calls => _calls;
        public bool Fail { get; set; }
        public bool Truncate { get; set; }
        public List<int?> Ceilings { get; } = [];
        public List<string?> Reasoning { get; } = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            lock (Ceilings) Ceilings.Add(request.MaxTokens);
            lock (Reasoning) Reasoning.Add(request.Reasoning);
            if (Fail) throw new IOException("interrupted");
            return Task.FromResult(Truncate
                ? new LLMResponse { CompletionStatus = "output_limit", Text = "", Usage = JsonNode.Parse("""{"prompt_tokens":12,"completion_tokens":8192,"completion_tokens_details":{"reasoning_tokens":8192},"total_tokens":8204}""") }
                : new LLMResponse { CompletionStatus = "completed", Json = new JsonObject { ["value"] = "secret result" }, Usage = new JsonObject { ["input_tokens"] = 3, ["output_tokens"] = 2, ["total_tokens"] = 5 } });
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
