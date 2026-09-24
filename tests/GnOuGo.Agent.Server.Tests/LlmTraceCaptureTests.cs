using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Components.Tracing;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LlmTraceCaptureTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;
    private static LlmTraceContentStore Store(PlanningPersistenceTests.StoreFixture fixture, TraceDebugSettings? settings = null, string tenant = "tenant")
        => new(fixture.Records, new TestOptionsMonitor<TraceDebugSettings>(settings ?? new()), Options.Create(new OpenTelemetrySettings { TenantId = tenant }), fixture.Store);

    [Fact]
    public async Task CaptureIsEncryptedTenantBoundAndNeverExportsContentOrRawReasoning()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var store = Store(fixture);
        var harness = SmartFlowTestFactory.CreateTelemetryHarness();
        using var telemetry = harness.Telemetry;
        var local = new LocalTraceDebugStore(new TestOptionsMonitor<OpenTelemetrySettings>(new() { TenantId = "tenant" }));
        var capture = new LlmTraceCapture(telemetry, store, local, NullLogger<LlmTraceCapture>.Instance);
        string trace, span, reference;
        var options = new LLMOptions { DefaultProvider = "openai", DefaultModel = "gpt-4o", Models = new() { ["openai"] = new() { Type = "openai", ApiKey = "secret-key" } } };
        using (var root = telemetry.StartActivityScope("workflow"))
        {
            root.SetTag("gnougo.llm.stage", "workflow.plan.repair");
            var response = new LLMResponse { Text = "PRIVATE_OUTPUT secret-key", Raw = new JsonObject { ["reasoning"] = "PRIVATE_REASONING" } };
            var result = await capture.CallAsync(new() { Prompt = "PRIVATE_INPUT secret-key", UseBackgroundMode = true }, options, (_, _) => Task.FromResult(response), Ct);
            Assert.Same(response, result);
            var record = Assert.Single(await fixture.Records.ListAsync(LlmTraceContentStore.Collection, "tenant", "test", Ct));
            var content = JsonSerializer.Deserialize(record.Value, LlmTraceJsonContext.Default.LlmTraceContent)!;
            trace = content.TraceId; span = content.SpanId; reference = record.Key;
            Assert.Contains("PRIVATE_INPUT", content.Input);
            Assert.Contains("PRIVATE_OUTPUT", content.Output);
            Assert.DoesNotContain("PRIVATE_REASONING", content.Output);
            Assert.DoesNotContain("secret-key", record.Value);
        }
        var reopened = Store(fixture);
        Assert.NotNull(await reopened.LoadAsync(reference, trace, span, null, Ct));
        Assert.Null(await reopened.LoadAsync(reference, "wrong", span, null, Ct));
        Assert.Null(await reopened.LoadAsync(reference, trace, "wrong", null, Ct));
        Assert.Null(await reopened.LoadAsync(reference, trace, span, "wrong", Ct));
        Assert.Null(await Store(fixture, tenant: "other").LoadAsync(reference, trace, span, null, Ct));
        var group = local.GetTrace(trace)!;
        var call = Assert.Single(group.Spans);
        Assert.Equal("workflow.plan.repair", call.Attributes["gnougo.llm.stage"]);
        Assert.Equal("Responses (background)", call.Attributes["gnougo.llm.protocol"]);
        Assert.False(call.Attributes.ContainsKey("gen_ai.usage.input_tokens"));
        Assert.DoesNotContain("PRIVATE_INPUT", string.Join(";", call.Attributes.Values));
        foreach (var file in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
        {
            var bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct));
            Assert.DoesNotContain("PRIVATE_INPUT", bytes);
            Assert.DoesNotContain("PRIVATE_OUTPUT", bytes);
        }
    }

    [Theory]
    [InlineData(false, "uncertain")]
    [InlineData(true, "cancelled")]
    public async Task FailedCallsRemainInspectableWithoutInventedUsage(bool cancel, string status)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var harness = SmartFlowTestFactory.CreateTelemetryHarness(); using var telemetry = harness.Telemetry;
        var local = new LocalTraceDebugStore(new TestOptionsMonitor<OpenTelemetrySettings>(new() { TenantId = "tenant" }));
        var capture = new LlmTraceCapture(telemetry, Store(fixture), local, NullLogger<LlmTraceCapture>.Instance);
        using var cts = new CancellationTokenSource(); if (cancel) cts.Cancel();
        var failure = new LLMClientException(LLMClientFailureKind.Timeout, "private provider detail", true);
        await Assert.ThrowsAsync<LLMClientException>(() => capture.CallAsync(new() { Prompt = "input" }, new(), (_, _) => throw failure, cts.Token));
        var record = Assert.Single(await fixture.Records.ListAsync(LlmTraceContentStore.Collection, "tenant", "test", Ct));
        var content = JsonSerializer.Deserialize(record.Value, LlmTraceJsonContext.Default.LlmTraceContent)!;
        Assert.Contains(status, content.OutputStatus);
        Assert.Null(content.Output);
        Assert.Null(content.OutputBytes);
        Assert.DoesNotContain("private provider detail", record.Value);
    }

    [Fact]
    public async Task SizeLimitsAndDisabledCaptureAreExplicit()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var store = Store(fixture, new() { MaxDocumentBytes = 4 });
        var content = new LlmTraceContent { TenantId = "tenant", TraceId = "t", SpanId = "s" };
        store.SetInput(content, "12345"); store.SetOutput(content, "12345");
        await store.SaveAsync("id", content, Ct);
        var loaded = await store.LoadAsync("id", "t", "s", null, Ct);
        Assert.Null(loaded!.Input); Assert.Null(loaded.Output);
        Assert.Equal(5, loaded.InputBytes); Assert.Contains("oversized", loaded.InputStatus);
        Assert.Null(await Store(fixture, new() { Enabled = false }).LoadAsync("id", "t", "s", null, Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoricalJournalReadsAreOwnedReadOnlyAndExcludeRawProviderData(bool workflow)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = new PlanningSession { Request = new() { SessionId = "session", TenantId = "tenant" } };
        if (workflow) await fixture.Records.UpsertAsync("flow-planning-sessions-v10", "tenant", "session", JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), "test", Ct);
        else Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var prefix = workflow ? "flow" : "agent";
        var key = workflow ? "session:1:hash" : "session:session:1:hash";
        var request = new LLMRequest { ClientRequestId = "session:1:hash", Prompt = "original request", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        var record = await fixture.Records.UpsertAsync(prefix + "-planning-model-requests-v10", "tenant", key, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), "test", Ct);
        var store = Store(fixture);
        Assert.Contains("uncertain", Assert.Single(await store.HistoryAsync("session", workflow, Ct)).Status);
        var content = await store.LoadJournalAsync("session", workflow, key, Ct);
        Assert.Contains("original request", content!.Input);
        Assert.Contains("unknown", content.OutputStatus);
        Assert.Null(await store.LoadJournalAsync("unrelated", workflow, key, Ct));
        await store.PurgeAsync(Ct);
        Assert.Equal(record, await fixture.Records.GetAsync(prefix + "-planning-model-requests-v10", "tenant", key, "test", Ct));
        Assert.Empty(await fixture.Records.ListAsync(LlmTraceContentStore.Collection, "tenant", "test", Ct));
    }

    [Fact]
    public async Task PipelineExpansionPassesExactOwnershipAndRendersRetainedValuesLazily()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var store = Store(fixture); Services.AddSingleton(store); JSInterop.Mode = JSRuntimeMode.Loose;
        var now = DateTimeOffset.UtcNow;
        var content = new LlmTraceContent { TenantId = "tenant", TraceId = "trace", SpanId = "span" };
        store.SetInput(content, LlmTraceContentStore.RequestDocument(new() { Prompt = "RETAINED_PRIVATE_INPUT" }));
        store.SetOutput(content, LlmTraceContentStore.ResponseDocument(new() { Text = "<script>unsafe()</script>RETAINED_PRIVATE_OUTPUT" }));
        await store.SaveAsync("reference", content, Ct);
        var trace = new TraceGroupDto("trace", now, now, [new("span", null, "gen_ai.generate", 3, now, now, 1, 1, null,
            new() { ["gnougo.llm.call_id"] = "call", ["gnougo.llm.content_ref"] = "reference" }, [], [], [])]);
        var cut = Render<TracePipeline>(p => p.Add(c => c.Trace, trace));
        Assert.DoesNotContain("RETAINED_PRIVATE_INPUT", cut.Markup);
        cut.Find(".llm-call__expand").Click();
        cut.WaitForAssertion(() => Assert.Contains("RETAINED_PRIVATE_INPUT", cut.Markup));
        Assert.Contains("RETAINED_PRIVATE_OUTPUT", cut.Markup); Assert.Empty(cut.FindAll("script"));
        cut.Find(".llm-call__expand").Click(); Assert.DoesNotContain("RETAINED_PRIVATE_INPUT", cut.Markup);
        trace.Spans[0].Attributes["gnougo.llm.trace_id"] = "trace";
        cut.Render(p => p.Add(c => c.Trace, trace with { TraceId = "aggregate" }));
        cut.Find(".llm-call__expand").Click();
        cut.WaitForAssertion(() => Assert.Contains("RETAINED_PRIVATE_INPUT", cut.Markup));
        cut.Find(".llm-call__expand").Click();
        trace.Spans[0].Attributes.Remove("gnougo.llm.trace_id");
        cut.Render(p => p.Add(c => c.Trace, trace with { TraceId = "other" }));
        cut.Find(".llm-call__expand").Click();
        cut.WaitForAssertion(() => Assert.Contains("Content unavailable", cut.Markup));
        Assert.DoesNotContain("RETAINED_PRIVATE_INPUT", cut.Markup);
    }

    [Fact]
    public void PipelineCountsLogicalCallsNotParentsAndShowsUnknownUsage()
    {
        var now = DateTimeOffset.UtcNow;
        TraceSpanDto Span(string id, string? parent, Dictionary<string, object?> attributes) => new(id, parent, "model", 3, now, now.AddSeconds(1), 1000, 1, null, attributes, [], [], []);
        var trace = new TraceGroupDto("trace", now, now.AddSeconds(1), [
            Span("parent", null, new() { ["gen_ai.operation.name"] = "chat", ["gen_ai.request.model"] = "test" }),
            Span("call", "parent", new() { ["gen_ai.request.model"] = "test", ["gnougo.llm.call_id"] = "id", ["gnougo.llm.stage"] = "workflow.plan.repair", ["gen_ai.usage.input_tokens"] = 42L }),
            Span("provider-child", "call", new() { ["gen_ai.operation.name"] = "chat", ["gen_ai.request.model"] = "test", ["gen_ai.usage.input_tokens"] = 42L })]);
        var cut = Render<TracePipeline>(p => p.Add(c => c.Trace, trace));
        Assert.Contains("1 calls", cut.Markup); Assert.Contains("Repairs", cut.Markup);
        Assert.Contains("Output: unknown", cut.Markup); Assert.Contains("incomplete coverage", cut.Markup);
        Assert.Single(TraceDebugUiHelpers.BuildSummary(trace).LlmMetrics);
        Assert.False(TraceDebugUiHelpers.BuildSummary(trace).UsageComplete);
        cut.Find(".llm-call__expand").Click();
        Assert.Contains("unavailable", cut.Markup);
    }

    [Fact]
    public async Task JournalBackedCaptureReusesReservationAndReceiptWithoutDuplicatingContent()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = new PlanningSession { Request = new() { SessionId = "session", TenantId = "tenant" } };
        await fixture.Records.UpsertAsync("flow-planning-sessions-v10", "tenant", "session", JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), "test", Ct);
        var request = new LLMRequest { ClientRequestId = "session:1:hash", Prompt = "JOURNALED_PRIVATE_INPUT" };
        var reservation = await fixture.Records.UpsertAsync("flow-planning-model-requests-v10", "tenant", request.ClientRequestId, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), "test", Ct);
        var harness = SmartFlowTestFactory.CreateTelemetryHarness(); using var telemetry = harness.Telemetry;
        var local = new LocalTraceDebugStore(new TestOptionsMonitor<OpenTelemetrySettings>(new() { TenantId = "tenant" }));
        var store = Store(fixture);
        var capture = new LlmTraceCapture(telemetry, store, local, NullLogger<LlmTraceCapture>.Instance);
        var response = await capture.CallAsync(request, new(), (_, _) => Task.FromResult(new LLMResponse { Text = "JOURNALED_PRIVATE_OUTPUT" }), Ct);
        var diagnostic = Assert.Single(await fixture.Records.ListAsync(LlmTraceContentStore.Collection, "tenant", "test", Ct));
        Assert.DoesNotContain("JOURNALED_PRIVATE", diagnostic.Value);
        var record = JsonSerializer.Deserialize(diagnostic.Value, LlmTraceJsonContext.Default.LlmTraceContent)!;
        Assert.Equal("flow", record.Journal);
        await fixture.Records.UpsertAsync("flow-planning-model-receipts-v10", "tenant", request.ClientRequestId, JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse), "test", Ct);
        var reopened = await Store(fixture).LoadAsync(diagnostic.Key, record.TraceId, record.SpanId, "session", Ct);
        Assert.Contains("JOURNALED_PRIVATE_OUTPUT", reopened!.Output);
        Assert.Equal(reservation, await fixture.Records.GetAsync("flow-planning-model-requests-v10", "tenant", request.ClientRequestId, "test", Ct));
    }

    [Fact]
    public async Task ParallelCallsKeepTheirSuppliedRuntimeStagesAndIndependentContent()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var harness = SmartFlowTestFactory.CreateTelemetryHarness(); using var telemetry = harness.Telemetry;
        var local = new LocalTraceDebugStore(new TestOptionsMonitor<OpenTelemetrySettings>(new() { TenantId = "tenant" }));
        var capture = new LlmTraceCapture(telemetry, Store(fixture), local, NullLogger<LlmTraceCapture>.Instance);
        var stages = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new ActivityListener { ShouldListenTo = s => s.Name == AgentOTelTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { if (a.GetTagItem("gnougo.llm.call_id") is not null) stages.Add(a.GetTagItem("gnougo.llm.stage")!.ToString()!); } };
        ActivitySource.AddActivityListener(listener);
        await Task.WhenAll(new[] { "workflow.plan.intent", "workflow.plan.repair", "llm.call" }.Select(async stage =>
        {
            var context = new StepExecutionContext { Engine = new WorkflowEngine() };
            var client = new DelegateClient((request, ct) => capture.CallAsync(request, new(), async (_, token) => { await Task.Delay(1, token); return new LLMResponse { Text = stage }; }, ct));
            await context.CallLLMAsync(client, new() { Prompt = stage }, stage, Ct);
        }));
        Assert.Equal(new[] { "llm.call", "workflow.plan.intent", "workflow.plan.repair" }, stages.OrderBy(s => s, StringComparer.Ordinal));
        var records = await fixture.Records.ListAsync(LlmTraceContentStore.Collection, "tenant", "test", Ct);
        Assert.Equal(3, records.Count);
        Assert.Equal(3, records.Select(r => JsonSerializer.Deserialize(r.Value, LlmTraceJsonContext.Default.LlmTraceContent)!.SpanId).Distinct().Count());
    }

    [Fact]
    public async Task CaptureFailureDoesNotChangeInferenceOrDispatchAgain()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var records = new DiagnosticRecords { Fail = true };
        var store = new LlmTraceContentStore(records, new TestOptionsMonitor<TraceDebugSettings>(new()), Options.Create(new OpenTelemetrySettings { TenantId = "tenant" }), fixture.Store);
        var harness = SmartFlowTestFactory.CreateTelemetryHarness(); using var telemetry = harness.Telemetry;
        var local = new LocalTraceDebugStore(new TestOptionsMonitor<OpenTelemetrySettings>(new()));
        var capture = new LlmTraceCapture(telemetry, store, local, NullLogger<LlmTraceCapture>.Instance);
        var count = 0; var response = new LLMResponse { Text = "original" };
        Assert.Same(response, await capture.CallAsync(new() { Prompt = "private" }, new(), (_, _) => { count++; return Task.FromResult(response); }, Ct));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RetentionDeletesOnlyExpiredDiagnosticRecordsForTheCurrentTenant()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var records = new DiagnosticRecords();
        records.Values.Add(new(LlmTraceContentStore.Collection, "tenant", "expired", "{}", DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow));
        records.Values.Add(new(LlmTraceContentStore.Collection, "tenant", "recent", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        records.Values.Add(new(LlmTraceContentStore.Collection, "other", "expired", "{}", DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow));
        records.Values.Add(new("flow-planning-model-requests-v10", "tenant", "request", "{}", DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow));
        var store = new LlmTraceContentStore(records, new TestOptionsMonitor<TraceDebugSettings>(new()), Options.Create(new OpenTelemetrySettings { TenantId = "tenant" }), fixture.Store);
        Assert.Null(await store.LoadAsync("expired", "trace", "span", null, Ct));
        await store.PurgeAsync(Ct);
        Assert.Equal(3, records.Values.Count);
        Assert.Contains(records.Values, r => r.Collection == "flow-planning-model-requests-v10");
        Assert.Contains(records.Values, r => r.TenantId == "other");
    }

    private sealed class DiagnosticRecords : IKeyVaultRecordStore
    {
        public bool Fail { get; init; }
        public List<KeyVaultRecordValue> Values { get; } = [];
        public Task<KeyVaultRecordValue?> GetAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
            => Task.FromResult(Values.FirstOrDefault(v => v.Collection == collection && v.TenantId == tenantId && v.Key == key));
        public Task<KeyVaultRecordValue> UpsertAsync(string collection, string tenantId, string key, string value, string author, CancellationToken ct = default)
            => Fail ? Task.FromException<KeyVaultRecordValue>(new IOException("unavailable")) : throw new NotSupportedException();
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string collection, string tenantId, string author, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KeyVaultRecordValue>>(Values.Where(v => v.Collection == collection && v.TenantId == tenantId).ToArray());
        public Task<bool> DeleteAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
            => Task.FromResult(Values.RemoveAll(v => v.Collection == collection && v.TenantId == tenantId && v.Key == key) > 0);
    }

    private sealed class DelegateClient(Func<LLMRequest, CancellationToken, Task<LLMResponse>> call) : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) => call(request, ct);
    }

    [Fact]
    public void BreakdownUsesLogicalBytesWithoutPretendingToBeReportedUsage()
    {
        var request = new LLMRequest { Prompt = "Instructions\n{\"targets\":[\"é\"],\"diagnostics\":[\"bad\"]}", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        var parts = LlmTraceCapture.Breakdown(LlmTraceContentStore.RequestDocument(request));
        Assert.Equal(Encoding.UTF8.GetByteCount(request.Prompt) + Encoding.UTF8.GetByteCount(request.StructuredOutputSchema.ToJsonString()), parts.Sum(p => p.Bytes));
        Assert.Contains(parts, p => p.Name == "targets"); Assert.Contains(parts, p => p.Name == "Response schema");
    }
}
