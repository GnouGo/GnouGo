using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class DynamicMappingTests
{
    [Theory]
    [InlineData("({amount:source.price})", "{\"price\":7922816251426433759354395033.5}", "{\"amount\":7922816251426433759354395033.5}")]
    [InlineData("source.rows.map(r=>({name:r.title}))", "{\"rows\":[{\"title\":\"a\"},{\"title\":\"a\"}]}", "[{\"name\":\"a\"},{\"name\":\"a\"}]")]
    [InlineData("m.parse(source.text)", "{\"text\":\"{\\\"x\\\":7}\"}", "{\"x\":7}")]
    [InlineData("m.parse(source.content[0].text)", "{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"x\\\":7}\"},{\"type\":\"image\",\"data\":\"retained\"}]}", "{\"x\":7}")]
    [InlineData("({name:m.decode(m.text(source.html,'<h1>([^<]+)</h1>'))})", "{\"html\":\"<h1>A &amp; B</h1>\"}", "{\"name\":\"A & B\"}")]
    [InlineData("({price:m.number(m.text(source.text,'price: ([0-9.]+)'))})", "{\"text\":\"price: 17.25\"}", "{\"price\":17.25}")]
    public void ExtractionPreservesObservedValues(string script, string source, string expected)
        => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), new JintSandbox().ExecuteMapping(script, JsonNode.Parse(source), TestContext.Current.CancellationToken)));

    [Theory]
    [InlineData("({price:99})")]
    [InlineData("({price:null})")]
    [InlineData("({price:m.test(source.text,'not-json')})")]
    [InlineData("({price:m.number('99')})")]
    [InlineData("({price:m.text('price: 99','([0-9]+)')})")]
    [InlineData("({price:source.x || 99})")]
    [InlineData("source.constructor.constructor('return process')()")]
    [InlineData("eval('source')")]
    [InlineData("Date.now()")]
    [InlineData("(()=>{source.x=7;return source})()")]
    [InlineData("m.parse(source.text)")]
    public void FabricationUnsafeCodeAndMalformedJsonFail(string script)
        => Assert.Equal("CONTRACT_UNSATISFIED", Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping(script, JsonNode.Parse("{\"text\":\"not-json\"}"), TestContext.Current.CancellationToken)).Code);

    [Theory]
    [InlineData("{}", "({name:m.optional(source,['name'])})", true, "\"declared\"")]
    [InlineData("{\"name\":null}", "({name:m.optional(source,['name'])})", true, "null")]
    [InlineData("{\"name\":null}", "({name:m.optional({},['name'])})", false, null)]
    [InlineData("{\"nested\":null}", "({name:m.optional(source,['nested','name'])})", false, null)]
    public void DefaultsRequireObservedAbsence(string source, string script, bool success, string? expected)
    {
        var target = JsonNode.Parse("""{"type":"object","properties":{"name":{"type":["string","null"],"default":"declared"}},"required":["name"]}""")!.AsObject();
        if (success) Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected!), new JintSandbox().ExecuteMapping(script, JsonNode.Parse(source), TestContext.Current.CancellationToken, target: target)!["name"]));
        else Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping(script, JsonNode.Parse(source), TestContext.Current.CancellationToken, target: target));
    }

    [Fact]
    public void TextSelectionStopsAtItsBoundBeforeMaterializingAllMatches()
    {
        var source = new JsonObject { ["text"] = new string('x', 20000) };
        var sandbox = new JintSandbox();
        Assert.Equal("x", sandbox.ExecuteMapping("m.text(source.text,'(.)')", source, TestContext.Current.CancellationToken)!.GetValue<string>());
        Assert.Contains("collection limit", Assert.Throws<WorkflowRuntimeException>(() =>
            sandbox.ExecuteMapping("m.texts(source.text,'(.)')", source, TestContext.Current.CancellationToken)).Message);
    }

    [Fact]
    public void UnsupportedExtractionIdentifiesTheResultRequirement()
    {
        var error = Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping(
            "({records:[{flag:m.test(source.text,'observed')}]})", new JsonObject { ["text"] = "observed" }, TestContext.Current.CancellationToken));
        Assert.Contains("$/records/0/flag", error.Message);
        Assert.Equal("CONTRACT_UNSATISFIED", error.Code);
    }

    [Fact]
    public async Task RegexLiteralFailureExplainsTheHelperContractAndCanRepair()
    {
        const string invalid = "({name:m.trim(m.text(source.observed.title,/(.+)/,1))})";
        var finding = Assert.Throws<WorkflowRuntimeException>(() => JintSandbox.ValidateMapping(invalid));
        Assert.Equal("CONTRACT_UNSATISFIED", finding.Code);
        Assert.Contains("quoted JavaScript strings", finding.Message);
        var model = new Model(invalid, "({name:m.trim(m.text(source.observed.title,'(.+)',1))})");
        Assert.True((await Run(model, new Store())).Success);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task InvalidMappingRepairsOnceAndPublishesOnlyValidatedData()
    {
        var model = new Model("({name:'fabricated'})", "({name:source.observed.title})");
        var result = await Run(model, new Store());
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(2, model.Calls);
        Assert.Equal("observed", result.StepResults[0].Output!["value"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExhaustionStopsBeforeConsumerAndDoesNotCache()
    {
        var model = new Model("({name:'fabricated'})"); var store = new Store();
        var result = await Run(model, store);
        Assert.False(result.Success); Assert.Equal("CONTRACT_UNSATISFIED", result.Error!.Code);
        Assert.Equal(2, model.Calls); Assert.Empty(store.Values); Assert.Single(result.StepResults);
    }

    [Fact]
    public async Task CacheSurvivesEngineRestartAndUsesStructureButIsolatesTenantsAndSchemas()
    {
        var model = new Model("({name:source.observed.title})"); var store = new Store();
        Assert.True((await Run(model, store)).Success); Assert.Equal(1, model.Calls);
        Assert.True((await Run(model, store, title: "changed")).Success); Assert.Equal(1, model.Calls);
        Assert.True((await Run(model, store, tenant: "other")).Success); Assert.Equal(2, model.Calls);
        Assert.True((await Run(model, store, extraSource: true)).Success); Assert.Equal(3, model.Calls);
        Assert.True((await Run(model, store, maxLength: 20)).Success); Assert.Equal(4, model.Calls);
        foreach (var key in store.Values.Keys.ToArray()) store.Values[key] = store.Values[key] with { Script = "({name:'invalid'})" };
        Assert.True((await Run(model, store)).Success); Assert.Equal(5, model.Calls);
        foreach (var key in store.Values.Keys.ToArray()) store.Values[key] = store.Values[key] with { ProfileVersion = JintSandbox.MappingProfileVersion - 1 };
        Assert.True((await Run(model, store)).Success); Assert.Equal(6, model.Calls);
    }

    [Theory]
    [InlineData("({name:m.trim(source.observed.title)})")]
    [InlineData("({name:m /* extraction */ . trim(source.observed.title)})")]
    [InlineData("({name:m.\\u0074rim(source.observed.title)})")]
    public async Task InterpretedTextCacheRequiresTheSameContentRegardlessOfScriptSpelling(string script)
    {
        var model = new Model(script); var store = new Store();
        Assert.True((await Run(model, store, title: " first ")).Success); Assert.Equal(1, model.Calls);
        Assert.True((await Run(model, store, title: " first ")).Success); Assert.Equal(1, model.Calls);
        Assert.True((await Run(model, store, title: " second ")).Success); Assert.Equal(2, model.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OuterRetriesReuseTheSameMappingCompletion(bool durable)
    {
        var document = Document(); document.Workflows["main"].Steps[0].Retry = new() { Max = 2, BackoffMs = 0 };
        var model = new Model("({name:source.observed.title})");
        var cache = new Store { FailFirstWrite = true };
        var engine = new WorkflowEngine { RunStore = durable ? new InMemoryWorkflowRunStore() : null,
            MappingArtifacts = cache, LLMClient = model, LlmDefaults = new() { Model = "test" },
            Limits = new() { TenantId = "tenant", RunId = "retry-mapping" } };
        var result = await engine.ExecuteAsync(new WorkflowCompiler().Compile(document).Workflows["main"],
            new JsonObject { ["observed"] = new JsonObject { ["title"] = "observed" } }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(1, model.Calls); Assert.Single(cache.Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableAttemptsReplayReceiptsAndNeverRedispatchUnknownCompletion(bool unknown)
    {
        var model = new ReceiptModel(unknown); var store = new InMemoryWorkflowRunStore();
        var workflow = new WorkflowCompiler().Compile(Document()).Workflows["main"];
        var engine = new WorkflowEngine { RunStore = store, LLMClient = model, LlmDefaults = new() { Model = "test" },
            Limits = new() { TenantId = "tenant", RunId = "mapping-run" } };
        var ct = TestContext.Current.CancellationToken;
        var result = await engine.ExecuteAsync(workflow, new JsonObject { ["observed"] = new JsonObject { ["title"] = "observed" } }, ct);
        Assert.Equal(!unknown, result.Success);
        Assert.Equal(unknown ? 1 : 2, model.Calls);
        var saved = (await store.ReadAsync("tenant", "mapping-run", ct))!;
        var attempts = saved.Invocations.Values.Where(i => i.Id.Contains("/mapping/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(unknown ? 1 : 2, attempts.Length);
        if (unknown) Assert.Equal("RUN_NEEDS_RECONCILIATION", result.Error!.Code);
        else Assert.All(attempts, a => { Assert.Equal("completed", a.Status); Assert.NotNull(a.Output); });
        var restarted = new WorkflowEngine { RunStore = store, LLMClient = model, LlmDefaults = new() { Model = "test" } };
        var replay = await restarted.ResumeAsync("tenant", "mapping-run", saved.Revision, workflow, ct);
        Assert.Equal(!unknown, replay.Success); Assert.Equal(unknown ? 1 : 2, model.Calls);
    }

    private sealed class ReceiptModel(bool unknown) : ILLMClient
    {
        public int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            if (unknown) throw new LLMClientException(LLMClientFailureKind.Timeout, "Transport completion unknown", true);
            return Task.FromResult(Calls == 1 ? new LLMResponse { Text = "malformed JSON" } :
                new LLMResponse { Json = new JsonObject { ["script"] = "({name:source.observed.title})" } });
        }
    }

    [Fact]
    public void CancelledOrOversizedMappingPublishesNothing()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new JintSandbox().ExecuteMapping("source", new JsonObject(), cancellation.Token));
        Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox(memoryLimitBytes: 128).ExecuteMapping("source", JsonValue.Create(new string('x', 100)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task McpErrorsPreventMappingInference()
    {
        var document = Document(); var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("source", new() { Tools = [new() { Name = "read", InputSchema = new JsonObject { ["type"] = "object" } }],
            ToolHandlers = new() { ["read"] = _ => new() { IsError = true, Content = JsonValue.Create("Retained source failure") } } });
        document.Workflows["main"].Steps.Insert(0, new() { Id = "source", Type = "mcp.call", Input = new JsonObject { ["server"] = "source", ["method"] = "read" } });
        var model = new Model("source"); var engine = new WorkflowEngine { McpClientFactory = factory, LLMClient = model };
        var result = await engine.ExecuteAsync(new WorkflowCompiler().Compile(document).Workflows["main"], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.False(result.Success); Assert.Equal("MCP_CALL_ERROR", result.Error!.Code); Assert.Equal(0, model.Calls);
        Assert.Contains("Retained source failure", result.Error.Message);
    }

    [Fact]
    public async Task EncodedReferencesRepairAgainstAnAuthoritativeConsumerContract()
    {
        var model = new Model("({name:m.decode(source.observed.title)})", "({name:m.percentDecode(source.observed.title)})");
        var result = await Run(model, new Store(), title: "https%3A%2F%2Fexample.invalid%2Fitems%2Fitem-7", pattern: "^https://[^/]+/");
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(2, model.Calls);
    }

    [Theory]
    [InlineData("m.percentDecode(source)", "\"caf%C3%A9%2Bitem\"", "\"café+item\"")]
    [InlineData("m.resolveUri(source.reference,source.base)", "{\"reference\":\"../items/item-7\",\"base\":\"https://example.invalid/search/list\"}", "\"https://example.invalid/items/item-7\"")]
    public void ReferenceNormalizationUsesOnlyObservedValues(string script, string source, string expected)
        => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), new JintSandbox().ExecuteMapping(script, JsonNode.Parse(source), TestContext.Current.CancellationToken)));

    [Theory]
    [InlineData("m.percentDecode(source)", "\"bad%2\"")]
    [InlineData("m.percentDecode('invented%20value')", "\"observed\"")]
    [InlineData("m.resolveUri(source.reference,'https://invented.invalid')", "{\"reference\":\"/items/7\"}")]
    [InlineData("m.resolveUri(source.reference,source.base)", "{\"reference\":\"/items/7\",\"base\":\"not-an-absolute-uri\"}")]
    public void ReferenceNormalizationRejectsInvalidOrInventedSources(string script, string source)
        => Assert.Equal("CONTRACT_UNSATISFIED", Assert.Throws<WorkflowRuntimeException>(() =>
            new JintSandbox().ExecuteMapping(script, JsonNode.Parse(source), TestContext.Current.CancellationToken)).Code);

    private static Task<RunResult> Run(Model model, Store store, string tenant = "tenant", string title = "observed", bool extraSource = false, int? maxLength = null, string? pattern = null)
    {
        var document = Document();
        if (maxLength is not null) document.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["name"]!["maxLength"] = maxLength;
        if (pattern is not null) document.Workflows["main"].Steps[0].OutputSchema!["properties"]!["value"]!["properties"]!["name"]!["pattern"] = pattern;
        var source = new JsonObject { ["title"] = title }; if (extraSource) source["added"] = true;
        var engine = new WorkflowEngine { Limits = new() { TenantId = tenant }, LLMClient = model, MappingArtifacts = store, LlmDefaults = new() { Model = "test" } };
        return engine.ExecuteAsync(new WorkflowCompiler().Compile(document).Workflows["main"], new JsonObject { ["observed"] = source },
            TestContext.Current.CancellationToken);
    }
    private static WorkflowDocument Document()
    {
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: map
                    type: mapping.dynamic
                    input:
                      sources: {observed: '${data.inputs.observed}'}
                      objective: Extract the observed title as name.
                      binding: approved-binding
                      producer_contract: opaque-contract-v1
                    output_schema:
                      type: object
                      properties:
                        value:
                          type: object
                          properties: {name: {type: string}}
                          required: [name]
                          additionalProperties: false
                      required: [value]
                      additionalProperties: false
                  - {id: consume, type: set, input: {done: true}}
            """);
        return document;
    }
    private sealed class Model(params string[] scripts) : ILLMClient
    {
        public int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
            => Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = scripts[Math.Min(Calls++, scripts.Length - 1)] } });
    }
    private sealed class Store : IMappingArtifactStore
    {
        internal bool FailFirstWrite;
        public Dictionary<(string Tenant, string Key), MappingArtifact> Values { get; } = [];
        public Task<MappingArtifact?> ReadAsync(string tenant, string key, CancellationToken ct) => Task.FromResult(Values.GetValueOrDefault((tenant, key)));
        public Task WriteAsync(string tenant, MappingArtifact artifact, CancellationToken ct)
        {
            if (FailFirstWrite) { FailFirstWrite = false; throw new WorkflowRuntimeException("TRANSIENT_STORE", "Retry cache persistence", retryable: true); }
            Values[(tenant, artifact.Key)] = artifact; return Task.CompletedTask;
        }
        public Task RemoveAsync(string tenant, string key, CancellationToken ct) { Values.Remove((tenant, key)); return Task.CompletedTask; }
    }
}
