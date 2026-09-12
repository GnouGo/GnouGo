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
            request = Request(session.Snapshot, "one");
            session.Snapshot.Construction.PendingCalls.Add(new() { Id = request.ClientRequestId!, Phase = "intent", Request = request });
            await session.Runtime.CheckpointAsync(session.Snapshot, Ct);
            await session.Runtime.CallAsync(request, "intent", Ct);
        }
        await using (var reopened = await Factory().OpenAsync(Context(client), Initial(), Ct))
        {
            Assert.Single(reopened.Snapshot.Construction.PendingCalls);
            Assert.Equal(1, reopened.Snapshot.Usage!.Calls);
            var replay = await reopened.Runtime.CallAsync(request, "intent", Ct);
            Assert.Equal("secret result", replay.Json!["value"]!.ToString());
            await reopened.Runtime.CheckpointAsync(reopened.Snapshot, Ct);
            Assert.Equal(1, reopened.Snapshot.Usage.Calls);
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
    public async Task UnverifiableDispatchStopsAcrossRestartAndRetainsItsBudget()
    {
        var client = new Client { Fail = true }; LLMRequest request;
        await using (var session = await Factory().OpenAsync(Context(client), Initial(), Ct))
        {
            request = Request(session.Snapshot, "one");
            await Assert.ThrowsAsync<IOException>(() => session.Runtime.CallAsync(request, "intent", Ct));
        }
        client.Fail = false;
        await using var reopened = await Factory().OpenAsync(Context(client), Initial(), Ct);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => reopened.Runtime.CallAsync(request, "intent", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, error.Code);
        Assert.Equal(1, client.Calls); Assert.Equal(1, reopened.Snapshot.Usage!.Calls);
    }

    [Fact]
    public async Task LeaseRejectsConcurrentWritersAndTenantsRemainIndependent()
    {
        var client = new Client();
        await using var first = await Factory().OpenAsync(Context(client), Initial(), Ct);
        await Assert.ThrowsAsync<PlanningConflictException>(() => Factory().OpenAsync(Context(client), Initial(), Ct));
        var other = Initial(); other.Request.TenantId = "other";
        await using var second = await Factory().OpenAsync(Context(client, "other"), other, Ct);
        await first.Runtime.CallAsync(Request(first.Snapshot, "one"), "intent", Ct);
        await second.Runtime.CallAsync(Request(second.Snapshot, "one"), "intent", Ct);
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
                try { await session.Runtime.CallAsync(Request(session.Snapshot, i.ToString()), "intent", Ct); return true; }
                catch (WorkflowRuntimeException e) when (e.Code == ErrorCodes.LlmBudgetExceeded) { return false; }
            });
            Assert.Single(await Task.WhenAll(calls), passed => passed);
        }
        await using var reopened = await Factory().OpenAsync(Context(client), Initial(), Ct);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => reopened.Runtime.CallAsync(Request(reopened.Snapshot, "next"), "intent", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetExceeded, error.Code); Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task ChangedRequestCannotResetAnExistingSession()
    {
        await using (var session = await Factory().OpenAsync(Context(new Client()), Initial(), Ct)) { }
        var changed = Initial(); changed.Request.Options["llm_budget"]!["max_calls"] = 10;
        await Assert.ThrowsAsync<PlanningConflictException>(() => Factory().OpenAsync(Context(new Client()), changed, Ct));
    }

    private static PlanningSnapshot Initial() => new()
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
    private static LLMRequest Request(PlanningSnapshot snapshot, string attempt)
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
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Fail) throw new IOException("interrupted");
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["value"] = "secret result" }, Usage = new JsonObject { ["total_tokens"] = 5 } });
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
