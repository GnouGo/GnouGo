using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using GnOuGo.AI.Core;
using Bunit;
using GnOuGo.Agent.Server.Components.Tracing;
using Microsoft.Extensions.DependencyInjection;

using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LlmInspectorDocumentsTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("eof")]
    [InlineData("exception")]
    [InlineData("cancelled")]
    [InlineData(null)]
    public async Task ExistingInspectorShowsReadEvidenceWithoutInventingLegacyOutcomes(string? outcome)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        Assert.True(await fixture.Store.TrySaveAsync(new() { Request = new() { SessionId = "session", TenantId = "tenant" } }, null, Ct));
        var store = Store(fixture.Records, fixture.Store);
        const string requestId = "session:1:hash";
        var request = new LLMRequest { ClientRequestId = requestId, Prompt = "controlled" };
        await fixture.Records.UpsertAsync("agent-planning-model-requests-v11", "tenant", "session:" + requestId,
            JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), "test", Ct);
        var attempt = new LLMHttpAttempt { Id = "physical", ObservedStatus = 200, Failure = "transport",
            ResponseBodyComplete = false, ResponseHttpVersion = outcome is null ? null : "1.1",
            CancellationSource = outcome == "cancelled" ? "caller" : null,
            ReadTermination = outcome is null ? null : new() { Observer = "response_stream", Outcome = outcome,
                AtUtc = DateTimeOffset.Parse("2026-11-10T01:38:05Z"),
                ErrorType = outcome == "exception" ? "http_io" : null, HttpError = outcome == "exception" ? "ResponseEnded" : null } };
        var saved = await fixture.Records.UpsertAsync("agent-planning-model-http-v11", "tenant", requestId,
            new JsonObject { ["tenant"] = "tenant", ["session"] = "session",
                ["request_hash"] = GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)),
                ["transport"] = JsonSerializer.SerializeToNode(new LLMHttpRetryState { Attempts = [attempt] }, LLMHttpRetryJsonContext.Default.LLMHttpRetryState) }.ToJsonString(), "test", Ct);
        await store.ObserveAsync(saved.Collection, "tenant", "session", requestId, saved.Value, saved.UpdatedAt.UtcTicks, Ct);
        var detail = await store.JournalDocumentsAsync("session", false, "session:" + requestId, Ct);
        Assert.Equal(outcome, detail["transport"]!["Attempts"]![0]!["ReadTermination"]?["Outcome"]?.ToString());
        await store.SaveAsync("read-reference", new LlmTraceContent { TenantId = "tenant", TraceId = "trace", SpanId = "span",
            SessionId = "session", Journal = "agent", RequestKey = "session:" + requestId }, Ct);
        Services.AddSingleton(store); JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<LlmCallDetail>(p => p.Add(c => c.Reference, "read-reference").Add(c => c.TraceId, "trace")
            .Add(c => c.SpanId, "span").Add(c => c.SessionId, "session").Add(c => c.InitiallyOpen, true));
        cut.WaitForAssertion(() => Assert.Contains("Read termination:", cut.Markup));
        Assert.Contains(outcome ?? "not observed / legacy", cut.Markup);
        if (outcome == "exception") Assert.Contains("ResponseEnded", cut.Markup);
        if (outcome == "cancelled") Assert.Contains("caller", cut.Markup);
        Assert.Contains("does not establish LLM completion", cut.Markup);
        Assert.Equal(saved, await fixture.Records.GetAsync(saved.Collection, "tenant", requestId, "test", Ct));
    }

    [Fact]
    public async Task StreamingInspectorPagesNativeAndAssembledEvidenceWithoutExposingRecoveryBytes()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        Assert.True(await fixture.Store.TrySaveAsync(new() { Request = new() { SessionId = "session", TenantId = "tenant" } }, null, Ct));
        var store = Store(fixture.Records, fixture.Store);
        const string requestId = "session:1:hash";
        var request = new LLMRequest { ClientRequestId = requestId, Prompt = "controlled" };
        await fixture.Records.UpsertAsync("agent-planning-model-requests-v11", "tenant", "session:" + requestId,
            JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), "test", Ct);
        var transport = new LLMHttpRetryState
        {
            Transport = new(LLMStreamingMode.Auto, LLMStreamingMode.Streaming, LLMTransportResolver.ChatCompletions, "exact deployment declaration"),
            Attempts = [new() { Id = "physical", ObservedStatus = 200, Status = 200, ProtocolComplete = true,
                UsageKnown = false, TerminationStatus = "stop", Body = "data: {\"content\":\"safe\"}\n\n",
                AssembledBody = "{\"result\":\"safe\"}", PartialBody = "Authorization: Bearer SUPER_SECRET",
                PartialBodyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("SUPER_SECRET")) }]
        };
        var saved = await fixture.Records.UpsertAsync("agent-planning-model-http-v11", "tenant", requestId,
            new JsonObject { ["tenant"] = "tenant", ["session"] = "session", ["request_hash"] = GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)),
                ["transport"] = JsonSerializer.SerializeToNode(transport, LLMHttpRetryJsonContext.Default.LLMHttpRetryState) }.ToJsonString(), "test", Ct);
        await store.ObserveAsync(saved.Collection, "tenant", "session", requestId, saved.Value, saved.UpdatedAt.UtcTicks, Ct);
        var detail = await store.JournalDocumentsAsync("session", false, "session:" + requestId, Ct);
        Assert.Equal("Streaming", detail["transport"]!["Transport"]!["EffectiveMode"]!.ToString());
        Assert.DoesNotContain("SUPER_SECRET", detail.ToJsonString());
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("SUPER_SECRET")), detail.ToJsonString());
        Assert.Contains(LlmTraceContentStore.Documents(detail), d => d.Title.EndsWith("AssembledBody", StringComparison.Ordinal));
        foreach (var doc in LlmTraceContentStore.Documents(detail))
            Assert.DoesNotContain("SUPER_SECRET", (await store.ReadDocumentAsync(detail, doc.Id, doc.Version, 0, 8192, Ct)).Text);
        Assert.Equal(saved, await fixture.Records.GetAsync(saved.Collection, "tenant", requestId, "test", Ct));
        await store.SaveAsync("stream-reference", new LlmTraceContent { TenantId = "tenant", TraceId = "trace", SpanId = "span",
            SessionId = "session", Journal = "agent", RequestKey = "session:" + requestId }, Ct);
        Services.AddSingleton(store); JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<LlmCallDetail>(p => p.Add(c => c.Reference, "stream-reference").Add(c => c.TraceId, "trace")
            .Add(c => c.SpanId, "span").Add(c => c.SessionId, "session"));
        cut.Find(".llm-call__expand").Click();
        cut.WaitForAssertion(() => Assert.Contains("Effective transport", cut.Markup));
        Assert.Contains("Streaming", cut.Markup);
        Assert.Contains("Native protocol complete", cut.Markup);
        Assert.DoesNotContain("SUPER_SECRET", cut.Markup);
        cut.Find(".llm-call__expand").Click();
        Assert.DoesNotContain("Effective transport", cut.Markup);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitFinancialRefusalUsesExactRequestWithoutCreatingAReceipt(bool workflow)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = new PlanningSession { Request = new() { SessionId = "session", TenantId = "tenant" }, Status = PlanningStatus.Stopped,
            RequestMeasurements = [new() { RequestId = "session:1:hash", Result = "financial_refusal_before_dispatch" }] };
        if (workflow) await fixture.Records.UpsertAsync("flow-planning-sessions-v11", "tenant", "session", JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), "test", Ct);
        else Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var store = Store(fixture.Records, fixture.Store);
        foreach (var id in new[] { "session:1:hash", "session:2:hash" })
        {
            var key = workflow ? id : "session:" + id;
            await fixture.Records.UpsertAsync((workflow ? "flow" : "agent") + "-planning-model-requests-v11", "tenant", key,
                JsonSerializer.Serialize(new LLMRequest { ClientRequestId = id, Prompt = "controlled" }, PlanningJsonContext.Default.LLMRequest), "test", Ct);
            var detail = await store.JournalDocumentsAsync("session", workflow, key, Ct);
            Assert.Equal(id == "session:1:hash", detail["responseStatus"]!.ToString().StartsWith("Not dispatched", StringComparison.Ordinal));
            Assert.Null(detail["usage"]); Assert.Null(detail["transport"]);
        }
        var calls = await store.ReadCallsAsync("session", workflow, null, Ct);
        Assert.Equal(LlmInspectionLabels.FinancialRefusal, calls.Calls.Single(c => c.RequestId == "session:1:hash").ResponseStatus);
        Assert.Contains("uncertain", calls.Calls.Single(c => c.RequestId == "session:2:hash").ResponseStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionRefreshIsMetadataOnlyAndExplicitHistoricalConversionOccursOnce(bool workflow)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = new PlanningSession { Request = new() { SessionId = "session", TenantId = "tenant" } };
        if (workflow) await fixture.Records.UpsertAsync("flow-planning-sessions-v11", "tenant", "session", JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), "test", Ct);
        else Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var spy = new CountingStore(fixture.Records); var store = Store(spy, fixture.Store);
        var collection = (workflow ? "flow" : "agent") + "-planning-model-requests-v11";
        var key = workflow ? "session:1:hash" : "session:session:1:hash";
        var request = new LLMRequest { ClientRequestId = "session:1:hash", Prompt = new string('x', 32760) + "\nAuthorization: Bearer SUPER_SECRET\n😀 needle tail" };
        var saved = await spy.UpsertAsync(collection, "tenant", key, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), "test", Ct);
        var first = await store.ReadCallsAsync("session", workflow, null, Ct);
        Assert.Single(first.Calls);
        Assert.Empty((await store.ReadCallsAsync("session", workflow, first.Cursor, Ct)).Calls);
        Assert.Equal(0, spy.Lists); Assert.Equal(0, spy.RequestHydrations);
        var opened = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.JournalDocumentsAsync("session", workflow, key, Ct)));
        var detail = opened[0];
        Assert.Equal(1, spy.RequestHydrations);
        var doc = Assert.Single(LlmTraceContentStore.Documents(detail));
        var result = ""; var offset = 0;
        do
        {
            var current = await Store(spy, fixture.Store).JournalDocumentsAsync("session", workflow, key, Ct);
            var page = await store.ReadDocumentAsync(current, doc.Id, doc.Version, offset, 8192, Ct);
            Assert.InRange(page.Text.Length, 0, 8192); result += page.Text; offset = page.NextOffset;
            if (page.Complete) break;
        } while (true);
        Assert.Equal(1, spy.RequestHydrations); Assert.Equal(0, spy.DocumentHydrations); Assert.True(spy.Ranges > 1);
        Assert.DoesNotContain("SUPER_SECRET", result); Assert.Contains("needle", result);
        Assert.NotNull(JsonDocument.Parse(result));
        Assert.Equal(saved, await fixture.Records.GetAsync(collection, "tenant", key, "test", Ct));
        var search = await store.SearchDocumentAsync(detail, doc.Id, doc.Version, "needle", 0, Ct);
        Assert.NotNull(search.Match);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ReadDocumentAsync(detail, "foreign", doc.Version, 0, 100, Ct));
        await Assert.ThrowsAsync<KeyVaultRecordVersionException>(() => store.ReadDocumentAsync(detail, doc.Id, "changed", 0, 100, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store(spy, fixture.Store, "foreign").JournalDocumentsAsync("session", workflow, key, Ct));
        await using var export = new MemoryStream(); await store.ExportDocumentsAsync(detail, export, Ct);
        using var json = JsonDocument.Parse(export.ToArray());
        Assert.Equal(result, json.RootElement.GetProperty("content").GetProperty(doc.Title).GetString());
    }

    [Fact]
    public async Task NewlyObservedEvidencePagesWithoutHydratingJournalBodiesAndCannotSubstituteRequests()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        Assert.True(await fixture.Store.TrySaveAsync(new() { Request = new() { SessionId = "session", TenantId = "tenant" } }, null, Ct));
        var spy = new CountingStore(fixture.Records); var store = Store(spy, fixture.Store);
        var details = new List<System.Text.Json.Nodes.JsonObject>();
        for (var i = 0; i < 2; i++)
        {
            var request = new LLMRequest { ClientRequestId = "session:" + i + ":hash", Prompt = new string('x', 100000) };
            var key = "session:" + request.ClientRequestId;
            var saved = await spy.UpsertAsync("agent-planning-model-requests-v11", "tenant", key, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), "test", Ct);
            await store.ObserveAsync(saved.Collection, "tenant", "session", key, saved.Value, saved.UpdatedAt.UtcTicks, Ct);
            details.Add(await store.JournalDocumentsAsync("session", false, key, Ct));
        }
        var doc = Assert.Single(LlmTraceContentStore.Documents(details[0]));
        Assert.Equal(100, (await store.ReadDocumentAsync(details[0], doc.Id, doc.Version, 0, 100, Ct)).Text.Length);
        Assert.Equal(0, spy.RequestHydrations); Assert.Equal(0, spy.DocumentHydrations);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ReadDocumentAsync(details[1], doc.Id, doc.Version, 0, 100, Ct));
    }

    [Fact]
    public async Task HistoricalTraceContentConvertsOnceUnderConcurrentAccessAndKeepsItsOriginalRecord()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var content = new LlmTraceContent { TenantId = "tenant", TraceId = "trace", SpanId = "span", Input = new string('x', 100000) };
        var saved = await fixture.Records.UpsertAsync(LlmTraceContentStore.Collection, "tenant", "old", JsonSerializer.Serialize(content, LlmTraceJsonContext.Default.LlmTraceContent), "test", Ct);
        var spy = new CountingStore(fixture.Records);
        var opened = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Store(spy, fixture.Store).TraceDocumentsAsync("old", "trace", "span", null, Ct)));
        var doc = Assert.Single(LlmTraceContentStore.Documents(opened[0]!));
        Assert.Equal(1, spy.TraceHydrations);
        Assert.Equal(100, (await Store(spy, fixture.Store).ReadDocumentAsync(opened[0]!, doc.Id, doc.Version, 0, 100, Ct)).Text.Length);
        Assert.NotNull(await Store(spy, fixture.Store).TraceDocumentsAsync("old", "trace", "span", null, Ct));
        Assert.Null(await Store(spy, fixture.Store).TraceDocumentsAsync("old", "other", "span", null, Ct));
        Assert.Equal(1, spy.TraceHydrations);
        Assert.Equal(saved, await fixture.Records.GetAsync(LlmTraceContentStore.Collection, "tenant", "old", "test", Ct));
    }

    [Fact]
    public async Task InitialHistoryAndEventDeltasKeepMetadataWorkBoundedForLongSessions()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = new PlanningSession { Request = new() { TenantId = "tenant", SessionId = "session" }, InspectionEvents = [] };
        for (var i = 1; i <= 350; i++)
        {
            var request = new LLMRequest { ClientRequestId = "session:" + i + ":hash", Prompt = "controlled" };
            await fixture.Records.UpsertAsync("agent-planning-model-requests-v11", "tenant", "session:" + request.ClientRequestId,
                JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), "test", Ct);
            state.InspectionEvents.Add(new(i, DateTimeOffset.UtcNow, "construction_draft", "received", "controlled evidence", request.ClientRequestId, null, null, null, null));
        }
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var spy = new CountingStore(fixture.Records); var store = Store(spy, fixture.Store);
        var first = await store.ReadCallsAsync("session", false, null, Ct);
        Assert.Equal(100, first.Calls.Count); Assert.True(first.HasMore); Assert.InRange(spy.MetadataReads, 0, 400);
        var ids = first.Calls.Select(c => c.RequestId).ToHashSet(StringComparer.Ordinal);
        var page = first;
        while (page.HasMore)
        {
            spy.MetadataReads = 0; page = await store.ReadCallsAsync("session", false, page.Cursor, Ct);
            Assert.InRange(page.Calls.Count, 0, 100); Assert.InRange(spy.MetadataReads, 0, 400);
            foreach (var call in page.Calls) Assert.True(ids.Add(call.RequestId));
        }
        Assert.Equal(350, ids.Count);
        Assert.Empty((await store.ReadCallsAsync("session", false, page.Cursor, Ct)).Calls);
        var selected = await store.ReadCallsAsync("session", false, null, Ct, "session:350:hash");
        Assert.Equal(101, selected.Calls.Count); Assert.Contains(selected.Calls, c => c.RequestId == "session:350:hash");
        Assert.Equal(0, spy.Lists); Assert.Equal(0, spy.RequestHydrations);
    }

    [Fact]
    public async Task ReceiptMetadataSuppliesMeasuredUsageAndIncompleteStatusWithoutHydratingContent()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        Assert.True(await fixture.Store.TrySaveAsync(new() { Request = new() { SessionId = "session", TenantId = "tenant" } }, null, Ct));
        var spy = new CountingStore(fixture.Records); var store = Store(spy, fixture.Store);
        const string key = "session:session:1:hash";
        var request = await spy.UpsertAsync("agent-planning-model-requests-v11", "tenant", key,
            JsonSerializer.Serialize(new LLMRequest { ClientRequestId = "session:1:hash", Prompt = "controlled" }, PlanningJsonContext.Default.LLMRequest), "test", Ct);
        var response = await spy.UpsertAsync("agent-planning-model-receipts-v11", "tenant", key,
            JsonSerializer.Serialize(new LLMResponse { Text = "incomplete", CompletionStatus = "output_limit", Usage = new System.Text.Json.Nodes.JsonObject
            { ["input_tokens"] = 7, ["output_tokens"] = 9, ["reasoning_tokens"] = 3 } }, PlanningJsonContext.Default.LLMResponse), "test", Ct);
        foreach (var source in new[] { request, response }) await store.ObserveAsync(source.Collection, "tenant", "session", key, source.Value, source.UpdatedAt.UtcTicks, Ct);
        var call = Assert.Single((await store.ReadCallsAsync("session", false, null, Ct)).Calls);
        Assert.Equal(7, call.InputTokens); Assert.Equal(9, call.OutputTokens); Assert.Contains("incomplete", call.ResponseStatus);
        Assert.Equal("Unavailable", call.TechnicalValidation); Assert.Equal("Unavailable", call.BusinessReview);
        Assert.Equal(0, spy.RequestHydrations); Assert.Equal(0, spy.ReceiptHydrations); Assert.Equal(0, spy.DocumentHydrations);
    }

    private static LlmTraceContentStore Store(IKeyVaultRecordStore records, IPlanningSessionStore sessions, string tenant = "tenant") => new(records,
        new TestOptionsMonitor<TraceDebugSettings>(new() { Enabled = true }), Options.Create(new OpenTelemetrySettings { TenantId = tenant }), sessions);

    private sealed class CountingStore(IKeyVaultRecordStore inner) : IKeyVaultRecordStore, IKeyVaultRecordReader
    {
        internal int Lists, RequestHydrations, ReceiptHydrations, DocumentHydrations, TraceHydrations, Ranges, MetadataReads;
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default)
        { if (c.Contains("-model-requests-", StringComparison.Ordinal)) RequestHydrations++; if (c.Contains("-model-receipts-", StringComparison.Ordinal)) ReceiptHydrations++; if (k.StartsWith("document:", StringComparison.Ordinal)) DocumentHydrations++; if (c == LlmTraceContentStore.Collection) TraceHydrations++; return inner.GetAsync(c, t, k, a, ct); }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default)
        { Lists++; throw new InvalidOperationException("Inspection must not list tenant bodies."); }
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default) => inner.UpsertAsync(c, t, k, v, a, ct);
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default) => inner.DeleteAsync(c, t, k, a, ct);
        public Task<KeyVaultRecordMetadata?> GetMetadataAsync(string c, string t, string k, string a, CancellationToken ct = default)
        { MetadataReads++; return ((IKeyVaultRecordReader)inner).GetMetadataAsync(c, t, k, a, ct); }
        public Task<KeyVaultRecordMetadataPage> QueryMetadataAsync(string c, string t, string p, string a, KeyVaultRecordCursor? after = null, int count = 100, CancellationToken ct = default) => ((IKeyVaultRecordReader)inner).QueryMetadataAsync(c, t, p, a, after, count, ct);
        public Task<KeyVaultRecordRange?> ReadRangeAsync(string c, string t, string k, string a, string? version, int offset, int count, CancellationToken ct = default)
        { Ranges++; return ((IKeyVaultRecordReader)inner).ReadRangeAsync(c, t, k, a, version, offset, count, ct); }
    }
}
