using System.Text.Json.Nodes;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RuntimeExecutionBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal const string AdaptiveYaml = """
        version: 1
        workflows:
          main:
            steps:
              - id: earlier
                type: mcp.call
                input: {server: fixture, method: effect, arguments: {}}
              - id: map
                type: mapping.dynamic
                input:
                  sources: {records: [{label: first}, {label: second}, {label: first}]}
                  objective: Copy every observed label.
                  binding: runtime-budget-fixture
                  producer_contract: fixture
                  adaptive_each: true
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
            outputs:
              labels: {type: array, expr: '${data.steps.map.value.rows}'}
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("2")]
    public async Task NormalHostRegistrationEnforcesExplicitPolicyBeforeAdaptiveEffects(string? calls)
    {
        var model = new BudgetModel(); var effects = 0;
        var mcp = new FakeMcpClientFactory(new FakeMcpSession("fixture").OnTool("effect", (_, _) =>
        { effects++; return Task.FromResult(new McpCallResult { Content = new JsonObject() }); }));
        await using var host = BudgetHost.Create(AdaptiveYaml, model, mcp, calls is null ? [] : ["--Flow:Execution:LlmBudget:MaxCalls=" + calls]);
        var factory = host.App.Services.GetRequiredService<ILLMUsageBudgetScopeFactory>();
        Assert.Equal(calls is null, factory.CreateScope() is null);
        var events = await host.ExecuteAsync("budget-run", Ct);
        Assert.DoesNotContain(events, e => e.Type == "human_input_request" || e.Type == "planner_session");
        var run = (await host.Store.ReadAsync("local", "budget-run", Ct))!;
        if (calls is null)
        {
            var error = Assert.Single(events, e => e.Type == "error");
            Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, error.ErrorCode); Assert.False(error.Retryable);
            Assert.Contains("Flow:Execution:LlmBudget", error.Text); Assert.Contains("not_started", error.Text);
            Assert.DoesNotContain("INTERNAL_ERROR", error.Text); Assert.Equal(0, model.Calls); Assert.Equal(0, effects);
            Assert.Null(run.ModelBudget); Assert.Empty(run.Invocations);
        }
        else
        {
            Assert.DoesNotContain(events, e => e.Type == "error"); Assert.Equal(1, model.Calls); Assert.Equal(1, effects);
            Assert.Equal(2, run.ModelBudget!.MaxCalls); Assert.Equal(1, run.ModelUsage!.Calls);
            Assert.True(JsonNode.DeepEquals(new JsonArray("first", "second", "first"), run.Result!.Outputs!["labels"]));
            var resumed = await host.App.Services.GetRequiredService<WorkflowRunService>().CommandAsync("budget-run", "resume", new(run.Revision), Ct);
            Assert.True(resumed.Result!.Success); Assert.Equal(1, model.Calls); Assert.Equal(1, effects);
        }
    }

    [Theory]
    [InlineData("MaxCalls", "0")]
    [InlineData("MaxCalls", "not-a-number")]
    [InlineData("MaxTotalTokens", "-1")]
    [InlineData("MaxElapsed", "00:00:00")]
    [InlineData("MaxEstimatedCost:Amount", "-1")]
    [InlineData("UnknownLimit", "5")]
    [InlineData("MaxCalls:Nested", "2")]
    [InlineData("MaxEstimatedCost:Unknown", "1")]
    [InlineData("MaxEstimatedCost:Currency:Nested", "EUR")]
    [InlineData("MaxEstimatedCost", "1")]
    public void InvalidExplicitPolicyFailsAtHostStartup(string field, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => BudgetHost.Create(AdaptiveYaml, new BudgetModel(),
            new FakeMcpClientFactory(), ["--Flow:Execution:LlmBudget:" + field + "=" + value]));
        Assert.Contains("Flow:Execution:LlmBudget", error.Message);
    }

    [Fact]
    public async Task AllExistingLimitsBindAndEachRootGetsAnIndependentScope()
    {
        await using var host = BudgetHost.Create(AdaptiveYaml, new BudgetModel(), new FakeMcpClientFactory(),
            ["--Flow:Execution:LlmBudget:MaxCalls=4", "--Flow:Execution:LlmBudget:MaxTotalTokens=40000",
             "--Flow:Execution:LlmBudget:MaxElapsed=00:02:00", "--Flow:Execution:LlmBudget:MaxEstimatedCost:Amount=0.25",
             "--Flow:Execution:LlmBudget:MaxEstimatedCost:Currency=EUR"]);
        var factory = host.App.Services.GetRequiredService<ILLMUsageBudgetScopeFactory>();
        var first = factory.CreateScope()!; var second = factory.CreateScope()!;
        Assert.NotSame(first, second); Assert.Equal(4, first.Limits.MaxCalls); Assert.Equal(40000, first.Limits.MaxTotalTokens);
        Assert.Equal(TimeSpan.FromMinutes(2), first.Limits.MaxElapsed); Assert.Equal(new MonetaryAmount(0.25m, "EUR"), first.Limits.MaxEstimatedCost);
        Assert.Equal(0, second.Snapshot.Calls);
    }

    [Fact]
    public async Task ExplicitCustomFactoryIsPreserved()
    {
        var factory = new CustomFactory();
        await using var host = BudgetHost.Create(AdaptiveYaml, new BudgetModel(), new FakeMcpClientFactory(), [],
            configure: services => services.AddSingleton<ILLMUsageBudgetScopeFactory>(factory));
        Assert.Same(factory, host.App.Services.GetRequiredService<ILLMUsageBudgetScopeFactory>());
    }

    [Fact]
    public async Task UnbudgetedHandwrittenDeterministicWorkflowStillExecutes()
    {
        await using var host = BudgetHost.Create("""
            version: 1
            workflows:
              main:
                steps: [{id: value, type: set, input: {text: unchanged}}]
                outputs: {answer: {type: string, expr: '${data.steps.value.text}'}}
            """, new BudgetModel(), new FakeMcpClientFactory(), []);
        Assert.Contains(await host.ExecuteAsync("manual", Ct), e => e.Type == "answer" && e.Text == "unchanged");
    }

    [Theory]
    [InlineData(ErrorCodes.LlmBudgetExceeded, "budget_exceeded", "calls")]
    [InlineData(ErrorCodes.LlmBudgetUnverifiable, "budget_unverifiable", "estimated_cost")]
    public void BudgetPresentationKeepsStructuredCauseWithoutPayloads(string code, string classification, string kind)
    {
        var presentation = WorkflowFailureFormatter.Format(new WorkflowError
        {
            Code = code, Message = "Runtime admission stopped.", Details = new JsonObject
            {
                ["classification"] = classification, ["limit_kind"] = kind, ["dispatch_status"] = "not_started",
                ["stage"] = "before_dispatch", ["recommended_action"] = "Review the configured policy and pricing.",
                ["observation"] = new string('x', 10000), ["credential"] = "PRIVATE_CREDENTIAL"
            }
        });
        Assert.Contains(classification, presentation.UserMessage); Assert.Contains(kind, presentation.TraceDetails);
        Assert.DoesNotContain("Configure explicit finite limits", presentation.UserMessage);
        Assert.DoesNotContain("PRIVATE_CREDENTIAL", presentation.TraceDetails); Assert.DoesNotContain(new string('x', 20), presentation.TraceDetails);
    }

    private sealed class CustomFactory : ILLMUsageBudgetScopeFactory
    { public LLMUsageBudgetScope CreateScope() => new(new() { MaxCalls = 1 }); }

    private const string TwoCallsYaml = """
        version: 1
        workflows:
          main:
            steps:
              - {id: first, type: llm.call, input: {model: gpt-4o-mini, prompt: observed}}
              - id: child
                type: workflow.call
                input: {ref: {kind: local, name: child}}
          child:
            steps:
              - {id: second, type: llm.call, input: {model: gpt-4o-mini, prompt: observed}}
        """;

    [Fact]
    public async Task ConcurrentRootsAreIndependentButChildCallsShareTheirRootAllowance()
    {
        var model = new BudgetModel();
        await using var host = BudgetHost.Create(TwoCallsYaml, model, new FakeMcpClientFactory(), ["--Flow:Execution:LlmBudget:MaxCalls=1"]);
        var outcomes = await Task.WhenAll(host.ExecuteAsync("one", Ct), host.ExecuteAsync("two", Ct));
        foreach (var events in outcomes)
        {
            Assert.Contains(events, e => e.Type == "error" && e.ErrorCode == ErrorCodes.LlmBudgetExceeded);
            Assert.DoesNotContain(events, e => e.Type == "human_input_request");
        }
        Assert.Equal(2, model.Calls);
        foreach (var id in new[] { "one", "two" })
            Assert.Equal(1, (await host.Store.ReadAsync("local", id, Ct))!.ModelUsage!.Calls);
    }

    [Fact]
    public async Task RoutedBudgetFailureIsReportedWithoutOfferingDesignerRevision()
    {
        var directory = Directory.CreateTempSubdirectory("runtime-budget-agents-").FullName;
        try
        {
            var repository = new AgentRepository(directory);
            await repository.AddAgentAsync("child-budget", TwoCallsYaml, ct: Ct);
            const string yaml = """
                version: 1
                workflows:
                  main:
                    steps:
                      - id: route
                        type: workflow.route
                        input:
                          prompt: Run the requested child.
                          candidates:
                            - id: database:child-budget
                              ref: {kind: database, agent: child-budget}
                              description: Requested child.
                          selection: {mode: single, min: 1, max: 1}
                          combine: {strategy: raw}
                """;
            var model = new BudgetModel { Route = true };
            await using var host = BudgetHost.Create(yaml, model, new FakeMcpClientFactory(), ["--Flow:Execution:LlmBudget:MaxCalls=1"],
                configure: services => services.AddSingleton<IAgentRepository>(repository));
            var events = await host.ExecuteAsync("routed", Ct);
            Assert.Contains(events, e => e.Type == "error" && e.ErrorCode == ErrorCodes.LlmBudgetExceeded);
            Assert.DoesNotContain(events, e => e.Type == "human_input_request"); Assert.Equal(1, model.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HandwrittenBudgetHandlerRetainsItsExplicitOutcomeWithoutRevision()
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - {id: first, type: llm.call, input: {model: gpt-4o-mini, prompt: observed}}
                  - id: bounded
                    type: llm.call
                    input: {model: gpt-4o-mini, prompt: observed}
                    on_error:
                      cases:
                        - if: '${error.code == "LLM_BUDGET_EXCEEDED"}'
                          action: continue
                          set_output: {text: budget-handled}
                outputs: {answer: {type: string, expr: '${data.steps.bounded.text}'}}
            """;
        var model = new BudgetModel();
        await using var host = BudgetHost.Create(yaml, model, new FakeMcpClientFactory(), ["--Flow:Execution:LlmBudget:MaxCalls=1"]);
        var events = await host.ExecuteAsync("handled", Ct);
        Assert.Contains(events, e => e.Type == "answer" && e.Text == "budget-handled");
        Assert.DoesNotContain(events, e => e.Type is "error" or "human_input_request"); Assert.Equal(1, model.Calls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MoneyAdmissionUsesHostPricingAndCurrencyProvider(bool pricing, bool rates)
    {
        var model = new BudgetModel();
        var options = new LLMOptions { DefaultProvider = "OpenAi", DefaultModel = "budget-test-model", ModelOverrides = new()
        {
            ["budget-test-model"] = new() { Pricing = pricing ? new() { Currency = "USD", InputPer1MTokens = 1, OutputPer1MTokens = 1 } : null }
        } };
        await using var host = BudgetHost.Create(TwoCallsYaml.Replace("gpt-4o-mini", "budget-test-model", StringComparison.Ordinal), model,
            new FakeMcpClientFactory(), ["--Flow:Execution:LlmBudget:MaxEstimatedCost:Amount=1", "--Flow:Execution:LlmBudget:MaxEstimatedCost:Currency=EUR"],
            configure: services =>
            {
                SmartFlowTestFactory.SetRuntimeOptions(services.Last(d => d.ServiceType == typeof(LLMRuntimeOptionsStore)).ImplementationInstance as LLMRuntimeOptionsStore
                    ?? throw new InvalidOperationException(), options);
                if (!rates) services.AddSingleton<IExchangeRateProvider>(new MissingRates());
            });
        var events = await host.ExecuteAsync("money", Ct);
        Assert.DoesNotContain(events, e => e.Type == "human_input_request");
        var run = (await host.Store.ReadAsync("local", "money", Ct))!;
        if (pricing && rates)
        {
            Assert.DoesNotContain(events, e => e.Type == "error"); Assert.Equal(2, model.Calls);
            Assert.Equal(0.00006m, run.ModelUsage!.EstimatedCost); Assert.Equal("EUR", run.ModelUsage.EstimatedCostCurrency);
            Assert.Single(run.ModelUsage.ExchangeRates);
        }
        else
        { Assert.Equal(0, model.Calls); Assert.Contains(events, e => e.ErrorCode == ErrorCodes.LlmBudgetUnverifiable); }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task RestartUsesStoredAllowanceAndCommittedCallDespiteDifferentHostPolicy(int savedCalls, bool success)
    {
        var store = new InMemoryWorkflowRunStore(); var model = new BudgetModel();
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(TwoCallsYaml)).Workflows["main"];
        var crashing = new WorkflowEngine { RunStore = new BudgetFaultStore(store), LLMClient = model,
            LLMUsageBudget = new(new() { MaxCalls = savedCalls }), Limits = new() { TenantId = "local", RunId = "restart" } };
        await Assert.ThrowsAnyAsync<Exception>(() => crashing.ExecuteAsync(workflow, null, Ct));
        var saved = (await store.ReadAsync("local", "restart", Ct))!;
        Assert.Equal(1, model.Calls); Assert.Equal(1, saved.ModelUsage!.Calls);
        await using var host = BudgetHost.Create(TwoCallsYaml, model, new FakeMcpClientFactory(),
            ["--Flow:Execution:LlmBudget:MaxCalls=99"], store);
        var resumed = await host.App.Services.GetRequiredService<WorkflowRunService>().CommandAsync("restart", "resume", new(saved.Revision), Ct);
        Assert.Equal(success, resumed.Result!.Success); Assert.Equal(savedCalls, resumed.ModelBudget!.MaxCalls);
        Assert.Equal(savedCalls, model.Calls); Assert.Equal(savedCalls, resumed.ModelUsage!.Calls);
        Assert.Equal(saved.ModelUsage.StartedAtUtc, resumed.ModelUsage.StartedAtUtc);
        if (!success) Assert.Equal(ErrorCodes.LlmBudgetExceeded, resumed.Result.Error!.Code);
    }

    [Fact]
    public async Task UnknownCompletionRemainsBlockedWithoutCleanupOrReplay()
    {
        var model = new BudgetModel { Unknown = true }; var effects = 0;
        var transport = new FakeMcpClientFactory(new FakeMcpSession("fixture").OnTool("effect", (_, _) =>
        { effects++; return Task.FromResult(new McpCallResult { Content = new JsonObject() }); }));
        var yaml = """
            version: 1
            workflows:
              main:
                steps: [{id: uncertain, type: llm.call, input: {model: gpt-4o-mini, prompt: observed}}]
                finally: [{id: cleanup, type: mcp.call, input: {server: fixture, method: effect, arguments: {}}}]
            """;
        await using var host = BudgetHost.Create(yaml, model, transport, ["--Flow:Execution:LlmBudget:MaxCalls=2"]);
        // The existing revision prompt for transport failures is not answered or advanced.
        await foreach (var evt in host.App.Services.GetRequiredService<SmartFlowService>().ExecuteAsync("fixture", "uncertain", "budget-fixture", Ct))
            if (evt.Type == "human_input_request") break;
        var saved = (await host.Store.ReadAsync("local", "uncertain", Ct))!;
        Assert.Equal(WorkflowRunStatus.NeedsReconciliation, saved.Status); Assert.Equal(1, model.Calls); Assert.Equal(0, effects);
        var resumed = await host.App.Services.GetRequiredService<WorkflowRunService>().CommandAsync("uncertain", "resume", new(saved.Revision), Ct);
        Assert.False(resumed.Result!.Success); Assert.Equal(1, model.Calls); Assert.Equal(0, effects);
        Assert.Equal(1, resumed.ModelUsage!.Calls);
    }

    [Fact]
    public async Task NewPolicyCannotGiveAnUnbudgetedHistoricalRunAnAllowance()
    {
        var model = new BudgetModel(); var store = new InMemoryWorkflowRunStore();
        await using (var original = BudgetHost.Create(AdaptiveYaml, model, new FakeMcpClientFactory(), [], store))
            Assert.Contains(await original.ExecuteAsync("unbudgeted", Ct), e => e.ErrorCode == ErrorCodes.LlmBudgetUnverifiable);
        var saved = (await store.ReadAsync("local", "unbudgeted", Ct))!;
        await using var restarted = BudgetHost.Create(AdaptiveYaml, model, new FakeMcpClientFactory(), ["--Flow:Execution:LlmBudget:MaxCalls=5"], store);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => restarted.App.Services.GetRequiredService<WorkflowRunService>()
            .CommandAsync("unbudgeted", "resume", new(saved.Revision), Ct));
        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, error.Code); Assert.Equal(0, model.Calls);
        Assert.Null((await store.ReadAsync("local", "unbudgeted", Ct))!.ModelBudget);
    }

    private sealed class MissingRates : IExchangeRateProvider
    {
        public ValueTask<CurrencyExchangeQuote?> GetQuoteAsync(string sourceCurrency, string targetCurrency, CancellationToken ct)
            => ValueTask.FromResult<CurrencyExchangeQuote?>(null);
    }

    private sealed class BudgetFaultStore(IWorkflowRunStore inner) : IWorkflowRunStore
    {
        private bool _fired;
        public Task<WorkflowRun?> ReadAsync(string tenantId, string runId, CancellationToken ct = default) => inner.ReadAsync(tenantId, runId, ct);
        public Task<IReadOnlyList<WorkflowRun>> ListAsync(string tenantId, CancellationToken ct = default) => inner.ListAsync(tenantId, ct);
        public Task CreateAsync(WorkflowRun run, CancellationToken ct = default) => inner.CreateAsync(run, ct);
        public Task<WorkflowRun> RequestInputAsync(string tenantId, string runId, long expectedRevision, HumanInputRequest request, CancellationToken ct = default) => inner.RequestInputAsync(tenantId, runId, expectedRevision, request, ct);
        public Task<WorkflowRun> AnswerAsync(string tenantId, string runId, long expectedRevision, string invocationId, JsonNode? response, CancellationToken ct = default) => inner.AnswerAsync(tenantId, runId, expectedRevision, invocationId, response, ct);
        public Task<WorkflowRun> CancelAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default) => inner.CancelAsync(tenantId, runId, expectedRevision, ct);
        public async Task<IWorkflowRunLease> AcquireAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default)
            => new Lease(this, await inner.AcquireAsync(tenantId, runId, expectedRevision, ct));
        private sealed class Lease(BudgetFaultStore owner, IWorkflowRunLease inner) : IWorkflowRunLease
        {
            public WorkflowRun Run => inner.Run;
            public async Task SaveAsync(CancellationToken ct = default)
            {
                await inner.SaveAsync(ct);
                if (!owner._fired && Run.Invocations.Values.Any(i => i.StepType == "llm.call" && i.Status == "completed"))
                { owner._fired = true; throw new IOException("Injected crash after committed model result."); }
            }
            public Task<bool> IsCancellationRequestedAsync(CancellationToken ct = default) => inner.IsCancellationRequestedAsync(ct);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    internal sealed class BudgetModel : ILLMClient, ILLMCapabilityResolver
    {
        private int _calls;
        internal int Calls => _calls;
        internal bool Unknown { get; set; }
        internal bool Route { get; set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Unknown) throw new LLMClientException(LLMClientFailureKind.Timeout, "Unknown test completion", true);
            return Task.FromResult(new LLMResponse { Text = "exact", Json = Route
                ? JsonNode.Parse("""{"selected":[{"id":"database:child-budget","reason":"Requested child","confidence":1}]}""")
                : new JsonObject { ["script"] = "item.label" },
                Usage = new JsonObject { ["input_tokens"] = 20, ["output_tokens"] = 10, ["total_tokens"] = 30 } });
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(12000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
    }
}

/// <summary>Uses production DI; only model/transport/storage dependencies are replaced.</summary>
internal sealed class BudgetHost(WebApplication app, IWorkflowRunStore store, string directory) : IAsyncDisposable
{
    internal WebApplication App => app;
    internal IWorkflowRunStore Store => store;
    internal static BudgetHost Create(string yaml, ILLMClient model, IMcpClientFactory transport, string[] budgetArgs,
        IWorkflowRunStore? store = null, Action<IServiceCollection>? configure = null)
    {
        var args = TelemetryTestHostArgs.Create(["--OpenTelemetry:TenantId=local", .. budgetArgs]);
        var directory = Path.GetDirectoryName(args.Single(a => a.StartsWith("--Database:Path=", StringComparison.Ordinal))["--Database:Path=".Length..])!;
        store ??= new InMemoryWorkflowRunStore();
        var agent = new FakeMcpSession(AgentMcpHostingExtensions.ServerName).OnTool("agent_get_by_name", new JsonObject
        {
            ["success"] = true, ["agent"] = new JsonObject { ["id"] = "12345678-1234-1234-1234-1234567890ab", ["name"] = "budget-fixture",
                ["workflow"] = yaml, ["created_at"] = "2026-10-01T00:00:00Z", ["updated_at"] = "2026-10-01T00:00:00Z" }
        });
        var runtime = SmartFlowTestFactory.CreateRuntimeOptionsStore();
        try
        {
            var app = GnOuGoAgentWebHost.Build(args, urls: "http://127.0.0.1:0", contentRoot: Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GnOuGo.Agent.Server")), enableHttpsRedirection: false,
                configureServices: services =>
                {
                    services.AddSingleton<IWorkflowRunStore>(store);
                    services.AddSingleton<ILLMClient>(model);
                    services.AddSingleton(runtime);
                    services.AddSingleton<IExchangeRateProvider>(new TestExchangeRateProvider());
                    services.AddSingleton(new SecureWorkflowRuntimeFactory(runtime, new FakeKeyVaultRuntimeConfigStore(),
                        llmClientOverride: model, mcpClientFactoryOverride: new AgentTransport(agent, transport), llmCapabilityResolver: model as ILLMCapabilityResolver));
                    // No file exports or background ingestion in a disposable host.
                    services.RemoveAll<IWorkflowTraceFileExporter>();
                    configure?.Invoke(services);
                });
            return new(app, store, directory);
        }
        catch { Directory.Delete(directory, true); throw; }
    }
    internal async Task<List<SmartFlowEvent>> ExecuteAsync(string runId, CancellationToken ct, JsonObject? inputs = null)
    {
        var events = new List<SmartFlowEvent>();
        await foreach (var evt in app.Services.GetRequiredService<SmartFlowService>().ExecuteAsync("fixture", runId,
            "budget-fixture", filesIds: null, workflowInputs: inputs, ct))
        {
            Assert.False(evt.Type == "human_input_request", evt.Text);
            events.Add(evt);
        }
        return events;
    }
    public async ValueTask DisposeAsync()
    { await app.DisposeAsync(); Directory.Delete(directory, true); }
    private sealed class AgentTransport(IMcpSession agent, IMcpClientFactory other) : IMcpClientFactory
    {
        public IReadOnlyList<McpServerMetadata> ServerMetadata => other.ServerMetadata;
        public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct)
            => serverName == AgentMcpHostingExtensions.ServerName ? Task.FromResult(agent) : other.GetClientAsync(serverName, ct);
    }
}
