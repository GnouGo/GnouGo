using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed partial class DynamicMappingCollectionTests
{
    private static WorkflowDocument AdaptiveDocument()
    { var doc = Document(); doc.Workflows["main"].Steps[0].Input!["adaptive_each"] = true; return doc; }

    private static WorkflowEngine AdaptiveEngine(Model model, Store? store = null, int calls = 6, string tenant = "tenant") => new()
    {
        LLMClient = model, MappingArtifacts = store, LlmDefaults = new() { Model = "test" },
        LLMUsageBudget = new(new() { MaxCalls = calls }), Limits = new() { TenantId = tenant }
    };
    private static Task<RunResult> AdaptiveRun(WorkflowEngine engine, JsonArray items, WorkflowDocument? doc = null)
        => engine.ExecuteAsync(new WorkflowCompiler().Compile(doc ?? AdaptiveDocument()).Workflows["main"], new JsonObject { ["items"] = items.DeepClone() }, Ct);

    [Fact]
    public async Task AdaptiveHomogeneousCachesByShapeNotCollectionPosition()
    {
        var store = new Store(); var model = new Model("item.label");
        var values = JsonNode.Parse("[{\"label\":\"a\"},{\"label\":\"b\"},{\"label\":\"a\"}]")!.AsArray();
        var run = await AdaptiveRun(AdaptiveEngine(model, store), values);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal(new[] { "a", "b", "a" }, Result(run).Select(v => v!.GetValue<string>()));
        run = await AdaptiveRun(AdaptiveEngine(model, store), JsonNode.Parse("[{\"label\":\"different\"},{\"label\":\"a\"}]")!.AsArray());
        Assert.True(run.Success, run.Error?.Message); Assert.Single(model.Requests); Assert.Single(store.Values);
        run = await AdaptiveRun(AdaptiveEngine(model, store, tenant: "other"), values);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task AdaptiveMixedShapesRetainSuccessesAndOnlySpecializeFailures()
    {
        var store = new Store(); var model = new Model("item.label", "item.name", "item.title");
        var values = JsonNode.Parse("[{\"label\":\"first\"},{\"name\":\"second\"},{\"label\":\"first\"},{\"title\":\"third\"}]")!.AsArray();
        var run = await AdaptiveRun(AdaptiveEngine(model, store), values);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal(new[] { "first", "second", "first", "third" }, Result(run).Select(v => v!.GetValue<string>()));
        Assert.Equal(3, model.Requests.Count);
        Assert.Contains("\"failing_index\":1", model.Requests[1].Prompt);
        Assert.DoesNotContain("\"label\":\"first\"", model.Requests[1].Prompt);
        Assert.DoesNotContain("\"title\":\"third\"", model.Requests[1].Prompt);
        Assert.Contains("\"failing_index\":3", model.Requests[2].Prompt);
        Assert.True((await AdaptiveRun(AdaptiveEngine(model, store), values)).Success); Assert.Equal(3, model.Requests.Count);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(5, true)]
    public async Task AdaptiveHeterogeneousCanExceedTwoCallsOnlyWithinSharedBudget(int limit, bool success)
    {
        var model = new Model("m.text(item,'A:(.*)')", "m.text(item,'B:(.*)')", "m.text(item,'C:(.*)')", "m.text(item,'D:(.*)')");
        var store = new Store(); var engine = AdaptiveEngine(model, store, limit);
        var values = JsonNode.Parse("[\"A:one\",\"B:two\",\"C:three\",\"D:four\"]")!.AsArray();
        var run = await AdaptiveRun(engine, values);
        Assert.Equal(success, run.Success); Assert.Equal(success ? 4 : 2, model.Requests.Count);
        Assert.Equal(model.Requests.Count, engine.LLMUsageBudget!.Snapshot.Calls);
        if (success)
        {
            Assert.Equal(new[] { "one", "two", "three", "four" }, Result(run).Select(v => v!.GetValue<string>()));
            Assert.True((await AdaptiveRun(AdaptiveEngine(model, store), values)).Success); Assert.Equal(4, model.Requests.Count);
        }
        else { Assert.Equal(ErrorCodes.LlmBudgetExceeded, run.Error!.Code); Assert.Empty(store.Values); Assert.DoesNotContain(run.StepResults, s => s.StepId == "consumer"); }
    }

    [Fact]
    public async Task AdaptiveRetainedScalePacksOptionalShapesAndProcessesEveryPage()
    {
        var pages = new JsonArray(); var expected = new JsonArray(); var record = 0;
        for (var page = 0; page < 53; page++)
        {
            var records = new JsonArray(); var labels = new JsonArray();
            for (var j = 0; j < (page == 52 ? 48 : 28); j++, record++)
            {
                var label = "observed-" + record;
                records.Add(new JsonObject { ["label"] = label, ["reference"] = new string('r', 500) + record,
                    ["shape_" + page] = page }); labels.Add(label);
            }
            pages.Add(new JsonObject { ["records"] = records, ["page_shape_" + page] = page }); expected.Add(labels);
        }
        Assert.Equal(1504, record);
        var doc = AdaptiveDocument(); doc.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!["items"] = JsonNode.Parse("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}");
        var model = new Model("item.records.map(r=>r.label)") { Allowance = 96000 };
        var engine = AdaptiveEngine(model); engine.Limits = new() { TenantId = "tenant", MaxMappingInputTokens = 96000 };
        var run = await AdaptiveRun(engine, pages, doc);
        Assert.True(run.Success, run.Error?.Message); Assert.True(JsonNode.DeepEquals(expected, Result(run)));
        var request = Assert.Single(model.Requests);
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) + 4096 <= 96000);
        Assert.DoesNotContain("observed-1503", request.Prompt); // Omitted pages still execute, including the final record.
        Assert.Contains("observed-0", request.Prompt);
    }

    [Fact]
    public async Task AdaptiveSkipsOversizedInitialRepresentativeButRequiresActualRepairItem()
    {
        var values = JsonNode.Parse("[{\"label\":\"a\",\"huge\":\"\"},{\"label\":\"b\"}]")!.AsArray(); values[0]!["huge"] = new string('x', 20000);
        var valid = new Model("item.label");
        Assert.True((await AdaptiveRun(AdaptiveEngine(valid), values)).Success); Assert.DoesNotContain(new string('x', 100), Assert.Single(valid.Requests).Prompt);
        var invalid = new Model("item.missing");
        var failed = await AdaptiveRun(AdaptiveEngine(invalid), values);
        Assert.False(failed.Success); Assert.Equal("CONTRACT_UNSATISFIED", failed.Error!.Code); Assert.Single(invalid.Requests);
        Assert.Contains("required complete", failed.Error.Message);
    }

    [Fact]
    public async Task AdaptiveNeedsFittingExampleContextAndExplicitBudget()
    {
        var values = new JsonArray(new string('x', 20000)); var model = new Model("item");
        var run = await AdaptiveRun(AdaptiveEngine(model), values);
        Assert.False(run.Success); Assert.Empty(model.Requests); Assert.Contains("No complete initial", run.Error!.Message);
        var doc = AdaptiveDocument(); doc.Workflows["main"].Steps[0].Input!["sources"]!["context"] = new string('x', 20000);
        run = await AdaptiveRun(AdaptiveEngine(model), new JsonArray("valid"), doc);
        Assert.False(run.Success); Assert.Empty(model.Requests); Assert.Contains("context and target", run.Error!.Message);
        var calls = 0; var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("fixture", new() { Tools = [new() { Name = "effect", InputSchema = new JsonObject { ["type"] = "object" } }],
            ToolHandlers = new() { ["effect"] = _ => { calls++; return new(); } } });
        doc = AdaptiveDocument();
        doc.Workflows["main"].Steps.Insert(0, new() { Id = "earlier", Type = "mcp.call", Input = new JsonObject { ["server"] = "fixture", ["method"] = "effect" } });
        var exception = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => AdaptiveRun(new() { LLMClient = model, McpClientFactory = factory }, new JsonArray("valid"), doc));
        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, exception.Code); Assert.Empty(model.Requests); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AdaptiveRejectsRepeatedFailureAndDoesNotPublishPartialResults()
    {
        var model = new Model("item.label", "'invented'"); var store = new Store();
        var run = await AdaptiveRun(AdaptiveEngine(model, store), JsonNode.Parse("[{\"label\":\"real\"},{\"missing\":\"value\"}]")!.AsArray());
        Assert.False(run.Success); Assert.Equal(3, model.Requests.Count); Assert.Empty(store.Values);
        Assert.Contains("identical", run.Error!.Message); Assert.DoesNotContain(run.StepResults, s => s.StepId == "consumer");
    }

    [Fact]
    public async Task AdaptiveAggregateConstraintsCannotTriggerFiltering()
    {
        var doc = AdaptiveDocument(); doc.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!["uniqueItems"] = true;
        var store = new Store(); var model = new Model("item");
        var run = await AdaptiveRun(AdaptiveEngine(model, store), new JsonArray("duplicate", "duplicate"), doc);
        Assert.False(run.Success); Assert.Single(model.Requests); Assert.Empty(store.Values); Assert.Contains("complete mapped collection", run.Error!.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdaptiveDurableReceiptsNeverRepeatInference(bool unknown)
    {
        var journal = new InMemoryWorkflowRunStore(); var model = new Model("item.label", "item.other") { Unknown = unknown };
        var engine = AdaptiveEngine(model); engine.RunStore = journal; engine.Limits = new() { TenantId = "tenant", RunId = "adaptive" };
        var values = JsonNode.Parse("[{\"label\":\"a\"},{\"other\":\"b\"}]")!.AsArray();
        var run = await AdaptiveRun(engine, values);
        Assert.Equal(!unknown, run.Success);
        var count = model.Requests.Count; var saved = (await journal.ReadAsync("tenant", "adaptive", Ct))!;
        model.Allowance = null;
        var replay = await new WorkflowEngine { RunStore = journal, LLMClient = model }.ResumeAsync("tenant", "adaptive", saved.Revision,
            new WorkflowCompiler().Compile(AdaptiveDocument()).Workflows["main"], Ct);
        Assert.Equal(!unknown, replay.Success); Assert.Equal(count, model.Requests.Count);
        Assert.Equal(count, saved.ModelUsage!.Calls);
        if (unknown) Assert.Equal("RUN_NEEDS_RECONCILIATION", replay.Error!.Code);
    }
    [Fact]
    public async Task AdaptiveRestartPinsAssignmentsAndReusesCommittedSpecializations()
    {
        var store = new InMemoryWorkflowRunStore(); var cache = new Store();
        var model = new Model("item.label", "item.name", "item.title");
        var fault = new AdaptiveFaultStore(store);
        var engine = AdaptiveEngine(model, cache); engine.RunStore = fault; engine.Limits = new() { TenantId = "tenant", RunId = "restart" };
        var values = JsonNode.Parse("[{\"label\":\"a\"},{\"name\":\"b\"},{\"title\":\"c\"}]")!.AsArray();
        await Assert.ThrowsAsync<IOException>(() => AdaptiveRun(engine, values));
        Assert.Equal(2, model.Requests.Count);
        var saved = (await store.ReadAsync("tenant", "restart", Ct))!;
        var issued = saved.Invocations["/workflow/main/step/map/mapping/1"].ResolvedInput!.ToJsonString();
        var recovered = AdaptiveEngine(model, cache); recovered.RunStore = store;
        var result = await recovered.ResumeAsync("tenant", "restart", saved.Revision, new WorkflowCompiler().Compile(AdaptiveDocument()).Workflows["main"], Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(3, model.Requests.Count);
        Assert.Equal(new[] { "a", "b", "c" }, Result(result).Select(v => v!.GetValue<string>()));
        saved = (await store.ReadAsync("tenant", "restart", Ct))!;
        Assert.Equal(3, saved.ModelUsage!.Calls);
        Assert.Equal(issued, saved.Invocations["/workflow/main/step/map/mapping/1"].ResolvedInput!.ToJsonString());
    }

    [Fact]
    public async Task AdaptiveEmptyNullableDefaultsAndChangedTargetsKeepContracts()
    {
        var model = new Model("m.optional(item,['label'])"); var store = new Store(); var doc = AdaptiveDocument();
        var schema = doc.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!["items"]!.AsObject();
        schema["type"] = new JsonArray("string", "null"); schema["default"] = "authorized";
        var run = await AdaptiveRun(AdaptiveEngine(model, store), new JsonArray(), doc);
        Assert.True(run.Success); Assert.Empty(model.Requests);
        run = await AdaptiveRun(AdaptiveEngine(model, store), JsonNode.Parse("[{}, {\"label\":null}, {\"label\":\"present\"}]")!.AsArray(), doc);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal("authorized", Result(run)[0]!.GetValue<string>()); Assert.Null(Result(run)[1]);
        schema["type"] = "string";
        run = await AdaptiveRun(AdaptiveEngine(model, store, calls: 1), JsonNode.Parse("[{\"label\":null}]")!.AsArray(), doc);
        Assert.False(run.Success); Assert.Equal(ErrorCodes.LlmBudgetExceeded, run.Error!.Code);
    }

    [Fact]
    public async Task AdaptiveSpecializationCannotResetSandboxStatements()
    {
        var model = new Model("item.label", "item.name", "item.title"); var engine = AdaptiveEngine(model);
        engine.Limits.MaxExpressionStatements = 3;
        var result = await AdaptiveRun(engine, JsonNode.Parse("[{\"label\":\"a\"},{\"name\":\"b\"},{\"title\":\"c\"}]")!.AsArray());
        Assert.False(result.Success); Assert.Single(model.Requests); Assert.Contains("allowance", result.Error!.Message);
    }


    [Fact]
    public async Task AdaptiveRevalidatesInvalidCacheAndUnknownCapacityStopsBeforeDispatch()
    {
        var model = new Model("item.label"); var store = new Store(); var values = JsonNode.Parse("[{\"label\":\"a\"}]")!.AsArray();
        Assert.True((await AdaptiveRun(AdaptiveEngine(model, store), values)).Success);
        foreach (var key in store.Values.Keys.ToArray()) store.Values[key] = store.Values[key] with { Script = "'invented'" };
        Assert.True((await AdaptiveRun(AdaptiveEngine(model, store), values)).Success); Assert.Equal(2, model.Requests.Count);
        var warm = AdaptiveEngine(model, store); warm.LLMClient = null;
        Assert.True((await AdaptiveRun(warm, values)).Success);
        var unknown = new Model("item.label") { Allowance = null };
        var result = await AdaptiveRun(AdaptiveEngine(unknown), values);
        Assert.False(result.Success); Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, result.Error!.Code); Assert.Empty(unknown.Requests);
    }

    [Fact]
    public async Task AdaptiveCommittedFinalSpecializationNeedsNoModelDuringRecovery()
    {
        var store = new InMemoryWorkflowRunStore(); var model = new Model("item.label", "item.name");
        var engine = AdaptiveEngine(model); engine.RunStore = new AdaptiveFaultStore(store); engine.Limits = new() { TenantId = "tenant", RunId = "final-receipt" };
        await Assert.ThrowsAsync<IOException>(() => AdaptiveRun(engine, JsonNode.Parse("[{\"label\":\"a\"},{\"name\":\"b\"}]")!.AsArray()));
        var saved = (await store.ReadAsync("tenant", "final-receipt", Ct))!;
        var result = await new WorkflowEngine { RunStore = store }.ResumeAsync("tenant", "final-receipt", saved.Revision,
            new WorkflowCompiler().Compile(AdaptiveDocument()).Workflows["main"], Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task AdaptiveRequiredItemTakesPriorityOverOptionalPreviousProgram()
    {
        var model = new Model("'" + new string('x', 15000) + "'", "item.label");
        var result = await AdaptiveRun(AdaptiveEngine(model), JsonNode.Parse("[{\"label\":\"observed\"}]")!.AsArray());
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(2, model.Requests.Count);
        Assert.Contains("\"failing_index\":0", model.Requests[1].Prompt);
        Assert.Contains("\"label\":\"observed\"", model.Requests[1].Prompt);
        Assert.Contains("\"previous_script\":null", model.Requests[1].Prompt);
        Assert.Contains("assigned unresolved item", model.Requests[1].Prompt);
    }

    [Fact]
    public async Task AdaptiveCancellationDoesNotStartSpecializationsOrPublishResults()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new Model("item.label", "item.name") { OnCall = cancellation.Cancel };
        var cache = new Store(); var engine = AdaptiveEngine(model, cache);
        var result = await engine.ExecuteAsync(new WorkflowCompiler().Compile(AdaptiveDocument()).Workflows["main"],
            JsonNode.Parse("{\"items\":[{\"label\":\"a\"},{\"name\":\"b\"}]}"), cancellation.Token);
        Assert.False(result.Success); Assert.Single(model.Requests); Assert.Empty(cache.Values);
        Assert.DoesNotContain(result.StepResults, s => s.StepId == "consumer");
    }

    private sealed class AdaptiveFaultStore(IWorkflowRunStore inner) : IWorkflowRunStore
    {
        private bool _fired;
        public Task<WorkflowRun?> ReadAsync(string tenantId, string runId, CancellationToken ct = default) => inner.ReadAsync(tenantId, runId, ct);
        public Task<IReadOnlyList<WorkflowRun>> ListAsync(string tenantId, CancellationToken ct = default) => inner.ListAsync(tenantId, ct);
        public Task CreateAsync(WorkflowRun run, CancellationToken ct = default) => inner.CreateAsync(run, ct);
        public Task<WorkflowRun> RequestInputAsync(string tenantId, string runId, long expectedRevision, HumanInputRequest request, CancellationToken ct = default) => inner.RequestInputAsync(tenantId, runId, expectedRevision, request, ct);
        public Task<WorkflowRun> AnswerAsync(string tenantId, string runId, long expectedRevision, string invocationId, JsonNode? response, CancellationToken ct = default) => inner.AnswerAsync(tenantId, runId, expectedRevision, invocationId, response, ct);
        public Task<WorkflowRun> CancelAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default) => inner.CancelAsync(tenantId, runId, expectedRevision, ct);
        public async Task<IWorkflowRunLease> AcquireAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default) => new Lease(this, await inner.AcquireAsync(tenantId, runId, expectedRevision, ct));
        private sealed class Lease(AdaptiveFaultStore owner, IWorkflowRunLease inner) : IWorkflowRunLease
        {
            public WorkflowRun Run => inner.Run;
            public async Task SaveAsync(CancellationToken ct = default)
            {
                await inner.SaveAsync(ct);
                if (!owner._fired && Run.Invocations.Values.Any(i => i.Id.EndsWith("/mapping/1", StringComparison.Ordinal) && i.CompletedAt is not null))
                { owner._fired = true; throw new IOException("Crash after durable specialization receipt."); }
            }
            public Task<bool> IsCancellationRequestedAsync(CancellationToken ct = default) => inner.IsCancellationRequestedAsync(ct);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

}
