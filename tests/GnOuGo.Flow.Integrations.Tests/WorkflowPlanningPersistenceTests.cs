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
    public async Task ChangedRequestCannotResetAnExistingSession()
    {
        await using (var session = await Factory().OpenAsync(Context(new Client()), Initial(), Ct)) { }
        var changed = Initial(); changed.Request.Options["llm_budget"]!["max_calls"] = 10;
        await Assert.ThrowsAsync<PlanningConflictException>(() => Factory().OpenAsync(Context(new Client()), changed, Ct));
    }

    [Fact]
    public async Task SchemaEightRequestWithoutModeDefaultsToInteractiveAfterRestart()
    {
        string id;
        await using (var session = await Factory().OpenAsync(Context(new Client()), Initial(), Ct)) { id = session.Session.Request.SessionId; }
        var records = new KeyVaultRecordStore(Path.Combine(_directory, "keyvault.db"));
        foreach (var collection in new[] { "flow-planning-definitions-v8", "flow-planning-sessions-v8" })
        {
            var record = (await records.GetAsync(collection, "tenant", id, "test", Ct))!;
            var json = JsonNode.Parse(record.Value)!.AsObject();
            (json["request"]?.AsObject() ?? json).Remove("mode");
            await records.UpsertAsync(collection, "tenant", id, json.ToJsonString(), "test", Ct);
        }
        await using var restored = await Factory().OpenAsync(Context(new Client()), Initial(), Ct);
        Assert.Equal(PlanningMode.Interactive, restored.Session.Request.Mode);
        Assert.Empty(restored.Session.Decisions); Assert.Null(restored.Session.PendingDecision);
    }

    private static PlanningSession Initial() => new()
    {
        Request = new() { TenantId = "tenant", Prompt = "secret intent", Options = new() { ["llm_budget"] = new JsonObject { ["max_calls"] = 1 } } }
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
        var request = PlanningGenerationPolicy.Apply(new()
        {
            Prompt = "secret intent",
            Model = "test",
            Reasoning = "low",
            StructuredOutputStrict = true,
            StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")
        }, snapshot.Request.Generation);
        request.ClientRequestId = snapshot.Request.SessionId + ":" + attempt + ":intent:workflow:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        return request;
    }
    private sealed class Client : ILLMClient, ILLMCapabilityResolver
    {
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["low", "medium"]);
        private int _calls;
        public int Calls => _calls;
        public bool Fail { get; set; }
        public bool Truncate { get; set; }
        public List<int?> Ceilings { get; } = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            lock (Ceilings) Ceilings.Add(request.MaxTokens);
            if (Fail) throw new IOException("interrupted");
            return Task.FromResult(Truncate
                ? new LLMResponse { CompletionStatus = "output_limit", Text = "", Usage = JsonNode.Parse("""{"prompt_tokens":12,"completion_tokens":8192,"completion_tokens_details":{"reasoning_tokens":8192},"total_tokens":8204}""") }
                : new LLMResponse { CompletionStatus = "completed", Json = new JsonObject { ["value"] = "secret result" }, Usage = new JsonObject { ["total_tokens"] = 5 } });
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
