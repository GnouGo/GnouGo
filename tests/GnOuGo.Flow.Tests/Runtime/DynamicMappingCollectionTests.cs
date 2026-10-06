using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class DynamicMappingCollectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("source.records.label", "[{\"label\":\"x\"},{\"label\":\"y\"}]")]
    [InlineData("m.text(source.records,'Name: (.+)')", "[\"Name: x\",\"Name: y\"]")]
    [InlineData("m.text(source.records,'<h1>([^<]+)</h1>')", "[\"<h1>x</h1>\",\"<h1>y</h1>\"]")]
    public async Task NewBindingsInferOneUnambiguousCollection(string script, string observed)
    {
        var doc = Document(); var input = doc.Workflows["main"].Steps[0].Input!.AsObject();
        input.Remove("each"); input["infer_each"] = true;
        var model = new Model(script); var store = new Store();
        var items = JsonNode.Parse(observed)!.AsArray();
        var result = await Run(model, store, items, doc);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new[] { "x", "y" }, Result(result).Select(v => v!.GetValue<string>()));
        Assert.Contains("item_target", Assert.Single(model.Requests).Prompt);
        Assert.True((await Run(model, store, items, doc)).Success); Assert.Single(model.Requests);
        input["sources"]!["other"] = new JsonArray("ambiguous");
        result = await Run(model, store, items, doc);
        Assert.False(result.Success); Assert.Equal("CONTRACT_UNSATISFIED", result.Error?.Code); Assert.Single(model.Requests);
        input["each"] = new JsonObject { ["input"] = "records", ["output"] = "rows" };
        Assert.True((await Run(model, store, items, doc)).Success);
    }

    [Fact]
    public async Task InferredWholeArrayTargetAndLegacyOmissionRemainDistinct()
    {
        var doc = Document(); var step = doc.Workflows["main"].Steps[0];
        var input = step.Input!.AsObject(); input.Remove("each"); input["infer_each"] = true;
        step.OutputSchema!["properties"]!["value"] = JsonNode.Parse("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}");
        var model = new Model("source.records.label");
        var result = await Run(model, new Store(), JsonNode.Parse("[{\"label\":\"first\"},{\"label\":\"second\"}]")!.AsArray(), doc);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(JsonNode.DeepEquals(new JsonArray("first", "second"), result.StepResults[0].Output!["value"]));
        input.Remove("infer_each"); model = new Model("source.records.map(item=>item.label)");
        result = await Run(model, new Store(), JsonNode.Parse("[{\"label\":\"first\"}]")!.AsArray(), doc);
        Assert.True(result.Success, result.Error?.Message); Assert.DoesNotContain("item_target", Assert.Single(model.Requests).Prompt);
    }

    [Fact]
    public async Task SamplesLearnCodeButEveryItemIsValidatedInOrderAndCacheSurvivesRestart()
    {
        var model = new Model("source.records.label"); var store = new Store();
        var items = new JsonArray(Enumerable.Range(0, 160).Select(i => (JsonNode)new JsonObject { ["label"] = "row-" + i, ["unused"] = new string('x', 400) }).ToArray());
        var result = await Run(model, store, items);
        Assert.True(result.Success, result.Error?.Message); Assert.Single(model.Requests);
        Assert.DoesNotContain("row-80", model.Requests[0].Prompt);
        Assert.Contains("omitted_count", model.Requests[0].Prompt);
        Assert.Equal(Enumerable.Range(0, 160).Select(i => "row-" + i), Result(result).Select(v => v!.GetValue<string>()));
        items[80]!["label"] = "changed";
        result = await Run(model, store, items);
        Assert.True(result.Success, result.Error?.Message); Assert.Single(model.Requests);
        Assert.Equal("changed", Result(result)[80]!.GetValue<string>());
        await Run(model, store, items, tenant: "another"); Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task UnsampledFailureRepairsOnceThenRechecksEveryItem()
    {
        var model = new Model("m.text(source.records,'Name: (.+)')", "m.text(source.records,'(?:Name|Title): (.+)')");
        var result = await Run(model, new Store(), JsonNode.Parse("[\"Name: first\",\"Title: middle\",\"Name: last\"]")!.AsArray());
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(2, model.Requests.Count);
        Assert.DoesNotContain("Title: middle", model.Requests[0].Prompt);
        Assert.Contains("Title: middle", model.Requests[1].Prompt);
        Assert.Equal(new[] { "first", "middle", "last" }, Result(result).Select(v => v!.GetValue<string>()));
    }

    [Theory]
    [InlineData("m.parse(source.records).name", "[\"{\\\"name\\\":\\\"x\\\"}\",\"{\\\"name\\\":\\\"y\\\"}\"]")]
    [InlineData("m.text(source.records,'<h1>([^<]+)</h1>')", "[\"<h1>x</h1>\",\"<h1>y</h1>\"]")]
    [InlineData("m.select(source.records, [['label'],['title']],false)", "[{\"label\":\"x\"},{\"title\":\"y\"}]")]
    public async Task ExtractionHandlesDifferentObservedRepresentations(string script, string data)
    {
        var result = await Run(new Model(script), new Store(), JsonNode.Parse(data)!.AsArray());
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new[] { "x", "y" }, Result(result).Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public async Task NullDuplicatesAndNestedArraysRemainExact()
    {
        var items = JsonNode.Parse("[[1,null,1],[],[2]]")!.AsArray();
        var doc = Document(); doc.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!["items"] =
            JsonNode.Parse("{\"type\":\"array\",\"items\":{\"type\":[\"number\",\"null\"]}}");
        var result = await Run(new Model("source.records"), new Store(), items, doc);
        Assert.True(result.Success, result.Error?.Message); Assert.True(JsonNode.DeepEquals(items, Result(result)));
    }

    [Fact]
    public async Task AggregateFailureAndFabricationNeverPublishOrCache()
    {
        var model = new Model("source.records.label"); var store = new Store(); var doc = Document();
        doc.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!["uniqueItems"] = true;
        var result = await Run(model, store, JsonNode.Parse("[{\"label\":\"x\"},{\"label\":\"x\"}]")!.AsArray(), doc);
        Assert.False(result.Success); Assert.Equal(2, model.Requests.Count); Assert.Empty(store.Values); Assert.Single(result.StepResults);
        model = new Model("'invented'");
        result = await Run(model, store, JsonNode.Parse("[{}]")!.AsArray());
        Assert.False(result.Success); Assert.Equal("CONTRACT_UNSATISFIED", result.Error!.Code); Assert.Empty(store.Values);
    }

    [Fact]
    public async Task EmptyCollectionNeedsNoInferenceButStillChecksMinimum()
    {
        var model = new Model("source.records"); var doc = Document();
        Assert.True((await Run(model, new Store(), [], doc)).Success); Assert.Empty(model.Requests);
        doc.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!["minItems"] = 1;
        Assert.False((await Run(model, new Store(), [], doc)).Success); Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task OversizedExampleAndUnknownAllowanceStopBeforeDispatch()
    {
        var model = new Model("source.records");
        var result = await Run(model, new Store(), new JsonArray(JsonValue.Create(new string('x', 20000))));
        Assert.False(result.Success); Assert.Empty(model.Requests);
        model.Allowance = null;
        result = await Run(model, new Store(), new JsonArray(JsonValue.Create("x")));
        Assert.False(result.Success); Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, result.Error!.Code); Assert.Empty(model.Requests);
    }

    [Fact]
    public void ItemsShareStatementAndMemoryAllowanceAndObserveCancellation()
    {
        var source = new JsonObject { ["records"] = new JsonArray(Enumerable.Range(0, 200).Select(i => (JsonNode?)JsonValue.Create("row" + i)).ToArray()) };
        var schema = new JsonObject { ["type"] = "string" };
        Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox(maxStatements: 20).ExecuteMappingItems("source.records", source, "records", schema, Ct));
        Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox(memoryLimitBytes: 400).ExecuteMappingItems("source.records", source, "records", schema, Ct));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => new JintSandbox().ExecuteMappingItems("source.records", source, "records", schema, cancel.Token));
    }

    [Theory]
    [InlineData("<p data-field=\"label\">(.*)</p>")]
    [InlineData("<p data-field=\"label\">([^<]*)</p>")]
    public void RepeatedTextExtractionFitsOneUnchangedSandboxAllowance(string pattern)
    {
        var noise = string.Join(' ', Enumerable.Range(0, 3000));
        var source = new JsonObject { ["records"] = new JsonArray(Enumerable.Range(0, 80)
            .Select(i => (JsonNode?)JsonValue.Create("<p data-field=\"label\">Observed " + i + "</p><pre>" + noise + "</pre>")).ToArray()) };
        var expression = "m.text(source.records," + System.Text.Json.JsonSerializer.Serialize(pattern) + ",1)";
        var result = new JintSandbox().ExecuteMappingItems(expression, source, "records", new() { ["type"] = "string" }, Ct);
        Assert.Equal(Enumerable.Range(0, 80).Select(i => "Observed " + i), result.Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public async Task DefaultsRequireObservedAbsenceAndDoNotReplaceNull()
    {
        var doc = Document(); var item = doc.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!["items"]!;
        item["default"] = "authorized";
        var model = new Model("m.optional(source.records,['label'])");
        var result = await Run(model, new Store(), JsonNode.Parse("[{}, {\"label\":\"observed\"}]")!.AsArray(), doc);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("authorized", Result(result)[0]!.GetValue<string>());
        result = await Run(model, new Store(), JsonNode.Parse("[{\"label\":null}]")!.AsArray(), doc);
        Assert.False(result.Success);
    }

    [Fact]
    public void CompilerSelectionDoesNotImportUnrelatedSiblingResults()
    {
        var source = new JsonObject { ["rows"] = new JsonArray(Enumerable.Range(0, 80).Select(i => (JsonNode)new JsonObject
            { ["unrelated"] = new string('x', 200000), ["call"] = new JsonObject { ["outputs"] = new JsonObject { ["row"] = i } } }).ToArray()) };
        var result = new ExpressionEvaluator().Evaluate("checkedMapping('({value:m.select(source,[[\"call\",\"outputs\",\"row\"]],true)})',data.rows)", source);
        Assert.Equal(Enumerable.Range(0, 80), result!["value"]!.AsArray().Select(v => v!.GetValue<int>()));
        Assert.Equal(200000, source["rows"]![0]!["unrelated"]!.GetValue<string>().Length);
    }

    [Fact]
    public void CountedLoopSelectionDoesNotImportUnrelatedCapturedInputs()
    {
        var context = JsonNode.Parse("{\"inputs\":{\"rows\":[{\"amount\":7922816251426433759354395033.5},null]},\"index\":0}")!.AsObject();
        context["unrelated"] = new string('x', 20000000);
        var evaluator = new ExpressionEvaluator();
        var result = evaluator.Evaluate("data.inputs.rows[data.index]", context);
        Assert.Equal("7922816251426433759354395033.5", result!["amount"]!.ToJsonString());
        context["index"] = 1; Assert.Null(evaluator.Evaluate("data.inputs.rows[data.index]", context));
    }

    private static JsonArray Result(RunResult result) => result.StepResults[0].Output!["value"]!["rows"]!.AsArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableCompletionReplaysButUnknownCompletionNeverRedispatches(bool unknown)
    {
        var store = new InMemoryWorkflowRunStore(); var model = new Model("'fabricated'", "source.records.label") { Unknown = unknown };
        var workflow = new WorkflowCompiler().Compile(Document()).Workflows["main"];
        var engine = new WorkflowEngine { LLMClient = model, RunStore = store, LlmDefaults = new() { Model = "test" },
            Limits = new() { TenantId = "tenant", RunId = "collection" } };
        var result = await engine.ExecuteAsync(workflow, JsonNode.Parse("{\"items\":[{\"label\":\"x\"},{\"label\":\"y\"}]}")!, Ct);
        Assert.Equal(!unknown, result.Success); Assert.Equal(unknown ? 1 : 2, model.Requests.Count);
        if (unknown) Assert.Equal("RUN_NEEDS_RECONCILIATION", result.Error!.Code);
        var saved = (await store.ReadAsync("tenant", "collection", Ct))!;
        model.Allowance = null;
        var replay = await new WorkflowEngine { RunStore = store, LLMClient = model, LlmDefaults = new() { Model = "test" } }
            .ResumeAsync("tenant", "collection", saved.Revision, workflow, Ct);
        Assert.Equal(!unknown, replay.Success); Assert.Equal(unknown ? 1 : 2, model.Requests.Count);
    }

    [Fact]
    public async Task RepairCannotResetParentBudget()
    {
        var model = new Model("'invalid'", "source.records.label");
        var engine = new WorkflowEngine { LLMClient = model, LlmDefaults = new() { Model = "test" },
            LLMUsageBudget = new(new() { MaxCalls = 1 }) };
        var result = await engine.ExecuteAsync(new WorkflowCompiler().Compile(Document()).Workflows["main"],
            JsonNode.Parse("{\"items\":[{\"label\":\"x\"},{\"label\":\"y\"}]}")!, Ct);
        Assert.False(result.Success); Assert.Equal(ErrorCodes.LlmBudgetExceeded, result.Error!.Code);
        Assert.Single(model.Requests); Assert.Equal(1, engine.LLMUsageBudget.Snapshot.Calls);
    }

    [Fact]
    public async Task InvalidCacheAndChangedTargetStillShareTwoAttempts()
    {
        var model = new Model("source.records.label"); var store = new Store(); var items = JsonNode.Parse("[{\"label\":\"x\"}]")!.AsArray();
        Assert.True((await Run(model, store, items)).Success);
        foreach (var key in store.Values.Keys.ToArray()) store.Values[key] = store.Values[key] with { Script = "'invented'" };
        var repair = new Model("'invented'", "source.records.label");
        Assert.True((await Run(repair, store, items)).Success); Assert.Equal(2, repair.Requests.Count);
        var document = Document(); document.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["rows"]!["maxItems"] = 2;
        Assert.True((await Run(model, store, items, document)).Success); Assert.Equal(2, model.Requests.Count);
    }
    private static Task<RunResult> Run(Model model, Store store, JsonArray items, WorkflowDocument? document = null, string tenant = "tenant")
        => new WorkflowEngine { LLMClient = model, MappingArtifacts = store, LlmDefaults = new() { Model = "test" }, Limits = new() { TenantId = tenant } }
            .ExecuteAsync(new WorkflowCompiler().Compile(document ?? Document()).Workflows["main"], new JsonObject { ["items"] = items.DeepClone() }, Ct);

    private static WorkflowDocument Document() => WorkflowParser.Parse("""
        version: 1
        workflows:
          main:
            steps:
              - id: map
                type: mapping.dynamic
                input:
                  sources: {records: '${data.inputs.items}'}
                  objective: Extract each observed label independently.
                  binding: binding
                  producer_contract: contract
                  each: {input: records, output: rows}
                output_schema:
                  type: object
                  properties:
                    value:
                      type: object
                      properties: {rows: {type: array, items: {type: string}}}
                      required: [rows]
                      additionalProperties: false
                  required: [value]
                  additionalProperties: false
              - {id: consumer, type: set, input: {done: true}}
        """);

    private sealed class Model(params string[] scripts) : ILLMClient, ILLMCapabilityResolver
    {
        public List<LLMRequest> Requests { get; } = [];
        public int? Allowance { get; set; } = 12000;
        public bool Unknown;
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult(Allowance);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Unknown) throw new LLMClientException(LLMClientFailureKind.Timeout, "Unknown completion", true);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = scripts[Math.Min(Requests.Count - 1, scripts.Length - 1)] },
                Usage = new JsonObject { ["input_tokens"] = 20, ["output_tokens"] = 10, ["total_tokens"] = 30 } });
        }
    }
    private sealed class Store : IMappingArtifactStore
    {
        internal readonly Dictionary<(string, string), MappingArtifact> Values = [];
        public Task<MappingArtifact?> ReadAsync(string tenant, string key, CancellationToken ct) => Task.FromResult(Values.GetValueOrDefault((tenant, key)));
        public Task WriteAsync(string tenant, MappingArtifact artifact, CancellationToken ct) { Values[(tenant, artifact.Key)] = artifact; return Task.CompletedTask; }
        public Task RemoveAsync(string tenant, string key, CancellationToken ct) { Values.Remove((tenant, key)); return Task.CompletedTask; }
    }
}
