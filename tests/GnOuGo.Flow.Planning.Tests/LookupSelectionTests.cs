using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LookupSelectionTests(ITestOutputHelper output)
{
    private static TaskValue Ref(string source, string port) => ProductTransformationPlan.Ref(source, port);
    private static TaskValue Field(string name, TaskValue? value = null) => new() { Kind = "field", Port = name, Items = [value ?? new() { Kind = "item" }] };
    private static TaskValue Flatten(TaskValue value) => FlattenCompilationTests.Flatten(value);
    private static TaskType Array(TaskType item) => LookupCompilationTests.ArrayOf(item);

    [Theory]
    [InlineData("key", false, false, 54, 1578)]
    [InlineData("identity", true, false, 54, 1578)]
    [InlineData("entry", false, true, 54, 1578)]
    [InlineData("another", false, false, 58, 1602)]
    public async Task EveryCandidateReachesDecisionWhileOriginalActionDataStaysOutsideInference(string key, bool invalidSelection, bool oversized, int pageCount, int recordCount)
    {
        var recordType = ProductTransformationPlan.Obj((key, new()), ("kind", new()), ("label", new()),
            ("destination", new()), ("token", new()), ("verbs", Array(new())));
        var candidateType = ProductTransformationPlan.Obj((key, new()), ("label", new()));
        var plan = new TaskPlan
        {
            Inputs = [new() { Name = "batches", Type = Array(ProductTransformationPlan.Obj(("records", Array(recordType)))) },
                new() { Name = "complete", Type = new() { Kind = "boolean" } }],
            Root = new() { Tasks = [
                new() { Id = "observe_views", Kind = "transform", Mode = "extract", Each = new("batches", "views"),
                    Objective = "Copy every observed candidate identity and label. Examine every record; empty candidate arrays mean checked absence. Original action arguments stay at source.",
                    Requires = LookupCompilationTests.Input("complete"), Inputs = [new("batches", LookupCompilationTests.Input("batches"))],
                    ResultType = ProductTransformationPlan.Obj(("views", Array(Array(candidateType)))) },
                new() { Id = "offered", Kind = "value", Objective = "Concatenate complete candidate views once",
                    Outputs = [new("candidates", Flatten(Ref("observe_views", "views")))] },
                new() { Id = "choose", Kind = "transform", Mode = "interpret", Objective = "Choose candidate identities in the requested order, permitting repeated selections. Return identities only.",
                    Inputs = [new("candidates", Ref("offered", "candidates"))], ResultType = ProductTransformationPlan.Obj(("ids", Array(new()))) },
                new() { Id = "verify_offered", Kind = "value", Objective = "Reconnect only identities actually offered to the decision",
                    Outputs = [new("selected", LookupCompilationTests.Lookup(Ref("offered", "candidates"), Ref("choose", "ids"), key))] },
                new() { Id = "verified_ids", Kind = "foreach", MaxItems = 10, Objective = "Retain selected identities without renumbering or deduplication",
                    Items = Ref("verify_offered", "selected"), Body = new() { Outputs = [new("id", Field(key))] } },
                new() { Id = "original_pages", Kind = "foreach", MaxItems = 100, Objective = "Retain exact original record collections separately",
                    Items = LookupCompilationTests.Input("batches"), Body = new() { Outputs = [new("records", Field("records"))] } },
                new() { Id = "resolve_actions", Kind = "value", Objective = "Resolve original action arguments deterministically",
                    Outputs = [new("records", LookupCompilationTests.Lookup(Flatten(Ref("original_pages", "records")), Ref("verified_ids", "id"), key))] }
            ], Outputs = [new("records", Ref("resolve_actions", "records"))] }
        };
        var pages = new JsonArray(); var all = new List<JsonObject>(); var candidates = new List<JsonObject>();
        for (var page = 0; page < pageCount; page++)
        {
            var records = new JsonArray();
            for (var i = 0; i < recordCount / pageCount + (page < recordCount % pageCount ? 1 : 0); i++)
            {
                var index = all.Count; var candidate = index % 10 == 0 && index / 10 < 147;
                var record = new JsonObject { [key] = "record-" + index, ["kind"] = candidate ? "candidate" : "note",
                    ["label"] = oversized && candidate ? new string('x', 3000) : "Observed " + index,
                    ["destination"] = "https://fixture.invalid/observed/" + index + "?opaque=" + new string('u', 1215),
                    ["token"] = "exact/" + index + new string('r', 100), ["verbs"] = new JsonArray("read") };
                all.Add(record); if (candidate) candidates.Add(record); records.Add(record);
            }
            pages.Add(new JsonObject { ["records"] = records });
        }
        Assert.Equal(recordCount, all.Count); Assert.Equal(147, candidates.Count);
        var chosen = new[] { candidates[^1], candidates[0], candidates[^1], candidates[70] };
        var ids = chosen.Select(r => r[key]!.ToString()).ToList();
        // An existing source record that was never offered is not selectable.
        if (invalidSelection) ids.Add("record-1");
        var model = new SelectionModel(key, ids); var engine = new WorkflowEngine { LLMClient = model,
            LLMUsageBudget = new(new() { MaxCalls = 4 }), LlmDefaults = new() { Model = "deterministic" }, Limits = new() { MaxMappingInputTokens = 96000 } };
        var values = new JsonObject { ["batches"] = pages, ["complete"] = true }; var original = values.ToJsonString();
        var yaml = await LookupCompilationTests.Compile(plan, engine);
        var result = await LookupCompilationTests.Execute(yaml, values, engine);
        Assert.Single(model.Mapping);
        Assert.Equal(original, values.ToJsonString());
        if (oversized)
        {
            Assert.False(result.Success); Assert.Equal(ErrorCodes.LlmBudgetExceeded, result.Error!.Code); Assert.Empty(model.Decisions); return;
        }
        var request = Assert.Single(model.Decisions); var offered = Business(request)["candidates"]!.AsArray();
        Assert.Equal(candidates.Select(c => c[key]!.ToString()), offered.Select(c => c![key]!.ToString()));
        Assert.All(offered, c => Assert.Equal(new[] { key, "label" }, c!.AsObject().Select(p => p.Key)));
        Assert.DoesNotContain("destination", request.Prompt); Assert.DoesNotContain("token", request.Prompt); Assert.DoesNotContain("fixture.invalid", request.Prompt);
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) + 4096;
        Assert.True(bytes < 24000, bytes.ToString());
        if (invalidSelection) { Assert.False(result.Success); Assert.Equal("CONTRACT_UNSATISFIED", result.Error!.Code); Assert.Null(result.Outputs); }
        else
        {
            Assert.True(result.Success, result.Error?.Message);
            Assert.True(JsonNode.DeepEquals(new JsonArray(chosen.Select(c => c.DeepClone()).ToArray()), result.Outputs!["records"]));
        }
        output.WriteLine($"pages={pageCount}; records={recordCount}; candidates={offered.Count}; mapping_calls={model.Mapping.Count}; selected={ids.Count}; global_bytes_with_framing={bytes}; estimated_input={Estimate(request)}; yaml_bytes={Encoding.UTF8.GetByteCount(yaml)}");
        values["complete"] = false;
        var stoppedModel = new SelectionModel(key, ids); engine.LLMClient = stoppedModel;
        var stopped = await LookupCompilationTests.Execute(yaml, values, engine);
        Assert.False(stopped.Success); Assert.Empty(stoppedModel.Mapping); Assert.Empty(stoppedModel.Decisions);
    }

    [Fact]
    public async Task OriginalCollectionIndicesRemainStableAcrossSelectionAndDuplicatePayloads()
    {
        var plan = LookupCompilationTests.Plan("integer");
        plan.Inputs[0] = new() { Name = "records", Type = Array(new()) };
        plan.Root.Tasks.Insert(0, new() { Id = "identify", Kind = "foreach", MaxItems = 10,
            Objective = "Attach original collection indices before any selection", Items = LookupCompilationTests.Input("records"),
            Body = new() { Outputs = [new("record", new() { Kind = "object", Members = [
                new("key", new() { Kind = "index" }), new("payload", new() { Kind = "item" })] })] } });
        plan.Root.Tasks[1].Outputs[0].Value.Items[0] = Ref("identify", "record");
        var engine = new WorkflowEngine(); var yaml = await LookupCompilationTests.Compile(plan, engine);
        var result = await LookupCompilationTests.Execute(yaml, new() { ["records"] = new JsonArray("duplicate", "middle", "duplicate"), ["selected"] = new JsonArray(2, 0, 2) }, engine);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new[] { 2, 0, 2 }, result.Outputs!["rows"]!.AsArray().Select(r => r!["key"]!.GetValue<int>()));
        Assert.All(result.Outputs["rows"]!.AsArray(), r => Assert.Equal("duplicate", r!["payload"]!.ToString()));
        Assert.DoesNotContain("llm.call", yaml); Assert.DoesNotContain("mapping.dynamic", yaml);
    }

    private static JsonNode Business(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf("Business data (JSON):", StringComparison.Ordinal) + "Business data (JSON):".Length)..])!;
    private static int Estimate(LLMRequest request) => PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject());
    private sealed class SelectionModel(string key, IReadOnlyList<string> selected) : ILLMClient, ILLMCapabilityResolver
    {
        internal readonly List<LLMRequest> Mapping = [], Decisions = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            if (Estimate(request) > 96000) throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetExceeded, "Complete request exceeds the unchanged input allowance.");
            if (request.StructuredOutputSchema?["properties"]?["script"] is not null)
            {
                Mapping.Add(request); return Task.FromResult(new LLMResponse { Json = new JsonObject {
                    ["script"] = "item.records.filter(r=>m.test(r.kind,'^candidate$')).map(r=>({" + key + ":r." + key + ",label:r.label}))" } });
            }
            Decisions.Add(request); return Task.FromResult(new LLMResponse { Json = new JsonObject { ["ids"] = new JsonArray(selected.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) } });
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(96000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
    }
}
