using System.Text.Json.Nodes;
using System.Text.Json;
using Bunit;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Components.Pages;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LiveIntentAgentGenerationTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task TypedV2_GeneratesAndSavesThreeAgents_ThenExecutesDisposableFixture()
    {
        if (Environment.GetEnvironmentVariable("GNOU_GO_LIVE_TYPED_PLANNING_E2E") != "1") return;
        await RunCampaignAsync(plannerVersion: 2);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task TypedV2_ResumeFailedSessionUntilReviewOrQuestionsAreVisible_WithoutAnswering()
    {
        if (Environment.GetEnvironmentVariable("GNOU_GO_LIVE_TYPED_PLANNING_RESUME") != "1") return;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GNOU_GO_LIVE_TYPED_PLANNING_SESSION_ID")))
            throw new InvalidOperationException("An explicit session ID is required for live recovery.");
        await RunCampaignAsync(plannerVersion: 2, resumeOnly: true);
    }

    private static void ConfigureV2Campaign(IServiceCollection services, LLMUsageBudgetScope budget, PlanningPersistenceTests.StoreFixture? isolatedStore, LiveBudgetLedger ledger)
    {
        if (isolatedStore is not null) services.AddSingleton<IPlanningSessionStore>(isolatedStore.Store);
        services.AddSingleton(sp =>
        {
            var original = sp.GetRequiredService<SecureWorkflowRuntimeFactory>();
            var runtime = new SecureWorkflowRuntimeFactory(
                sp.GetRequiredService<LLMRuntimeOptionsStore>(), sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(),
                sp.GetRequiredService<ILoggerFactory>(), llmClientOverride: new CampaignPlanningClient(original, budget, sp.GetRequiredService<IExchangeRateProvider>(), ledger),
                llmCapabilityResolver: sp.GetService<ILLMCapabilityResolver>(), humanInputProvider: sp.GetRequiredService<AgentHumanInputProvider>());
            return new PlanningSessionService(
                isolatedStore?.Store ?? sp.GetRequiredService<IPlanningSessionStore>(),
                (IDbContextFactory<PlanningDbContext>?)isolatedStore ?? sp.GetRequiredService<IDbContextFactory<PlanningDbContext>>(),
                isolatedStore?.Records ?? sp.GetRequiredService<IKeyVaultRecordStore>(), runtime, sp.GetRequiredService<IWorkflowPlanner>(),
                sp.GetRequiredService<IExchangeRateProvider>(), sp.GetRequiredService<IOptions<WorkflowPlanningBudgetSettings>>(),
                sp.GetRequiredService<IOptions<TypedWorkflowPlanningSettings>>(), sp.GetRequiredService<IOptions<OpenTelemetrySettings>>(),
                sp.GetRequiredService<ILogger<PlanningSessionService>>());
        });
    }

    private static async Task ReconcileV2UnverifiedCallsAsync(IServiceProvider services, PlanningPersistenceTests.StoreFixture? isolatedStore,
        LiveBudgetLedger ledger, LLMUsageBudgetSnapshot snapshot, bool resumeOnly, CancellationToken ct)
    {
        var contexts = (IDbContextFactory<PlanningDbContext>?)isolatedStore ?? services.GetRequiredService<IDbContextFactory<PlanningDbContext>>();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var tenant = WorkflowExecutionTenant.Resolve(services.GetRequiredService<IOptions<OpenTelemetrySettings>>());
        var session = resumeOnly ? Environment.GetEnvironmentVariable("GNOU_GO_LIVE_TYPED_PLANNING_SESSION_ID") : null;
        var key = PlanningGraphCompiler.Fingerprint(db.Database.GetDbConnection().DataSource + ":" + tenant + ":" + session);
        if (ledger.ReconciledStores.Contains(key)) return;
        var records = isolatedStore?.Records ?? services.GetRequiredService<IKeyVaultRecordStore>();
        var calls = await db.Calls.AsNoTracking().Where(c => c.TenantId == tenant && c.Status != "completed" && (session == null || c.SessionId == session)).ToListAsync(ct);
        await using var runtime = await services.GetRequiredService<SecureWorkflowRuntimeFactory>().CreateAsync(ct);
        foreach (var call in calls)
        {
            var record = await records.GetAsync(PlanningModelJournal.RequestCollection, tenant, call.PayloadKey, EfPlanningSessionStore.Author, ct)
                ?? throw new InvalidOperationException("An incomplete model call has no encrypted request for budget reconciliation.");
            var request = JsonSerializer.Deserialize(record.Value, PlanningJsonContext.Default.LLMRequest)!;
            var maximum = await MaximumCallCostAsync(runtime.Options, request, snapshot, services.GetRequiredService<IExchangeRateProvider>(), ct);
            ledger.ReserveCall(maximum, PlanningGraphCompiler.Fingerprint(key + ":" + call.RequestHash));
        }
        ledger.ReconciledStores.Add(key);
        await ledger.PersistAsync(ledger.Snapshot, ct);
    }

    private static async Task<decimal> MaximumCallCostAsync(LLMOptions options, LLMRequest request, LLMUsageBudgetSnapshot snapshot, IExchangeRateProvider rates, CancellationToken ct)
    {
        var metadata = new LLMModelMetadataResolver(options).Resolve(request.Provider, request.Model);
        var inputCeiling = metadata.MaxInputTokens ?? metadata.ContextWindowTokens;
        var outputCeiling = request.MaxTokens ?? metadata.MaxOutputTokens;
        if (inputCeiling is not > 0 || outputCeiling is not > 0) throw new InvalidOperationException("Live dispatch requires known token ceilings for conservative budget reservation.");
        if (request.RequireOutputTokenLimit && request.DisableTransportRetries)
            inputCeiling = checked((int)Math.Min(inputCeiling.Value, ConservativeTextInputReservation(request)));
        var ceiling = new ModelMetadataUsageCostEstimator(options).EstimateCostWithCurrency(request.Model, inputCeiling, outputCeiling, request.Provider)
            ?? throw new InvalidOperationException("Live dispatch requires verifiable pricing.");
        var rate = ceiling.Currency == snapshot.EstimatedCostCurrency ? 1m
            : (snapshot.ExchangeRates.FirstOrDefault(q => q.SourceCurrency == ceiling.Currency && q.TargetCurrency == snapshot.EstimatedCostCurrency)
                ?? await rates.GetQuoteAsync(ceiling.Currency, snapshot.EstimatedCostCurrency, ct))?.Rate
                ?? throw new InvalidOperationException("Live dispatch requires a verified currency conversion.");
        return ceiling.Amount * rate;
    }

    // This harness sends text-only requests. Reserve one token per serialized UTF-8
    // byte (including schemas/tools), plus 4,096 for framing. This is deliberately
    // much larger than the construction unit's heuristic token estimate. Legacy
    // requests without enforced ceilings retain their original full-context reserve.
    internal static long ConservativeTextInputReservation(LLMRequest request) => checked(
        System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) + 4_096L);

    private static async Task ResumeV2QuestionsAsync(IServiceProvider services, CancellationToken ct)
    {
        var service = services.GetRequiredService<PlanningSessionService>();
        var id = Environment.GetEnvironmentVariable("GNOU_GO_LIVE_TYPED_PLANNING_SESSION_ID")!;
        var state = await service.GetAsync(id, ct) ?? throw new InvalidOperationException("The requested session does not exist in the configured tenant.");
        Assert.Equal(2, state.SchemaVersion);
        var answerCount = state.Answers.Count;
        var acceptedBehavior = state.ApprovedBehaviorHash;
        state = await ConfigureLiveGenerationAsync(service, state, ct);
        if (state.Preparation is not null)
            await new WorkflowPlanningRuntime(new WorkflowEngine()).EnrichPreparationAsync(state.Preparation, ct);
        var unsafePriorBehavior = state.BehaviorPlan is not null && state.Preparation is not null && PlanningBehaviorPlans.Validate(state.BehaviorPlan, state.Preparation).Count != 0;
        var invalidReview = state.Status == PlanningStatus.BehaviorReview && state.BehaviorPlan is not null && state.Preparation is not null &&
            PlanningBehaviorPlans.Validate(state.BehaviorPlan, state.Preparation).Count != 0;
        if (invalidReview || PlanningPreparationCheckpoint.IsObsoleteMatchingQuestion(state) || state.Status is PlanningStatus.Failed or PlanningStatus.Recovery)
        {
            state = await service.SubmitAsync(id, new() { Kind = "retry", ExpectedRevision = state.Revision }, ct);
        }
        while (!PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status))
            state = await service.SubmitAsync(id, new() { Kind = "advance", ExpectedRevision = state.Revision }, ct);
        Assert.Equal(answerCount, state.Answers.Count);
        if (unsafePriorBehavior) Assert.Null(state.ApprovedBehaviorHash);
        else Assert.Equal(acceptedBehavior, state.ApprovedBehaviorHash);
        Assert.Null(state.ApprovedHash);
        if (state.Status == PlanningStatus.Clarification)
        {
            await AssertVisibleV2QuestionsAsync(service, state);
            WriteLiveProgress("v2_user_questions_visible");
        }
        else if (state.Status == PlanningStatus.FinalReview)
        {
            Assert.NotNull(state.Yaml); Assert.Empty(state.Diagnostics);
            await using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
            context.Services.AddSingleton(service);
            var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, id));
            page.WaitForAssertion(() => Assert.Single(page.FindAll("button"), b => b.TextContent == "Approve this revision and save"));
            WriteLiveProgress("v2_user_final_review_visible");
        }
        else if (state.Status is PlanningStatus.Recovery or PlanningStatus.Failed or PlanningStatus.Unsupported)
            throw new InvalidOperationException("V2 user-session resume blocked in " + PlanningPhase.Resolve(state) + ": " + string.Join(",", state.Diagnostics.Select(d => d.Code)));
        else
        {
            Assert.Equal(PlanningStatus.BehaviorReview, state.Status);
            Assert.Empty(state.Diagnostics);
            await using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            context.Services.AddSingleton(service);
            var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, id));
            page.WaitForAssertion(() => Assert.Single(page.FindAll("button"), b => b.TextContent == "Accept behavior and generate"));
            Assert.Contains("Planner v2", page.Markup);
            WriteLiveProgress("v2_user_behavior_review_visible");
        }
        // Automatic planning is disabled; no answer/approval is submitted, and the
        // encrypted pending form remains available to the real designer on restart.
    }

    private static async Task GenerateV2AgentAsync(IServiceProvider services, string name, CancellationToken ct)
    {
        var service = services.GetRequiredService<PlanningSessionService>();
        var revision = Environment.GetEnvironmentVariable("GNOU_GO_LIVE_TYPED_PLANNING_REVISION");
        var prompt = string.IsNullOrWhiteSpace(revision) ? AcceptancePrompt : AcceptancePrompt + "\n\nRequested revision:\n" + revision;
        var state = (await service.ListAsync(ct)).SingleOrDefault(s => s.Request.Name == name)
            ?? await service.StartAsync(name, prompt, false, ct);
        state = await ConfigureLiveGenerationAsync(service, state, ct);
        if (!string.IsNullOrWhiteSpace(revision) && state.BehaviorPlan is not null &&
            state.Status is PlanningStatus.Recovery or PlanningStatus.Failed or PlanningStatus.Unsupported &&
            !state.Request.Prompt.EndsWith("\n\nRequested revision:\n" + revision, StringComparison.Ordinal))
            state = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "revise", Text = revision, ExpectedRevision = state.Revision }, ct);
        else if (state.Status is PlanningStatus.Recovery or PlanningStatus.Failed or PlanningStatus.Unsupported)
            state = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "retry", ExpectedRevision = state.Revision }, ct);
        if (state.Status == PlanningStatus.Saved && await TryGetAgentForCleanupAsync(services.GetRequiredService<IMcpClientFactory>(), name, ct) is null)
        {
            // Recreate a previously validated temporary artifact after failure cleanup;
            // this is still the same generation, never counted as another successful run.
            var previousRevision = state.Revision++; state.Status = PlanningStatus.Approved; state.SavedAgentId = null;
            if (!await services.GetRequiredService<IPlanningSessionStore>().TrySaveAsync(state, previousRevision, ct)) throw new PlanningConflictException("The campaign session changed.");
        }
        long reportedRevision = -1;
        while (state.Status != PlanningStatus.Saved)
        {
            ct.ThrowIfCancellationRequested();
            if (state.Revision != reportedRevision)
            {
                WriteLiveProgress("v2_phase_" + PlanningPhase.Resolve(state) + "_" + state.Status);
                reportedRevision = state.Revision;
            }
            Assert.Equal(2, state.SchemaVersion);
            PlanningCommand? command = state.Status switch
            {
                PlanningStatus.Clarification => new() { Kind = "answer", Answers = ScriptedV2Answers(state) },
                PlanningStatus.BehaviorReview => new() { Kind = "accept_behavior" },
                PlanningStatus.FinalReview => new() { Kind = "approve" },
                PlanningStatus.Approved => new() { Kind = "save" },
                PlanningStatus.Recovery or PlanningStatus.Failed or PlanningStatus.Unsupported or PlanningStatus.Cancelled
                    => throw new InvalidOperationException("V2 generation blocked in " + PlanningPhase.Resolve(state) + ": " + string.Join(",", state.Diagnostics.Select(d => d.Code))),
                _ => null
            };
            if (state.Status == PlanningStatus.Clarification) await AssertVisibleV2QuestionsAsync(service, state);
            if (command is not null)
            {
                command.ExpectedRevision = state.Revision;
                command.ArtifactHash = state.ArtifactHash;
                state = await service.SubmitAsync(state.Request.SessionId, command, ct);
            }
            else
            {
                await Task.Delay(200, ct);
                state = (await service.GetAsync(state.Request.SessionId, ct))!;
            }
        }
        Assert.NotNull(state.SavedAgentId);
        Assert.Equal(state.ArtifactHash, state.ApprovedHash);
        Assert.NotEmpty(state.Scenarios);
        Assert.All(state.Scenarios, scenario => Assert.Equal("passed", scenario.Outcome));
        WriteLiveProgress("v2_generation_saved");
    }

    private static Task<PlanningSnapshot> ConfigureLiveGenerationAsync(PlanningSessionService service, PlanningSnapshot state, CancellationToken ct)
    {
        if (state.Request.Generation.Reasoning == "low" || !(PlanningStatus.IsWaiting(state.Status) || state.Status is PlanningStatus.Failed or PlanningStatus.Unsupported))
            return Task.FromResult(state);
        return service.SubmitAsync(state.Request.SessionId, new() { Kind = "configure_generation", ExpectedRevision = state.Revision,
            Generation = new() { Reasoning = "low", MaxNodesPerUnit = 4, MaxInputTokensPerUnit = 12_000, MaxOutputTokens = 8_192 } }, ct);
    }

    private static JsonObject ScriptedV2Answers(PlanningSnapshot state)
    {
        var result = new JsonObject();
        foreach (var field in state.Question!.Fields!)
        {
            Assert.True(field.AllowCustomAnswer);
            result[field.Name] = "Use the caller's review instructions as mandatory approval criteria. Restore dependencies and execute relevant local unit tests and linters in one disposable checkout. Missing or inconclusive required checks block approval. Review all changed code, publish only high-confidence inline findings and one formal runtime APPROVE or REQUEST_CHANGES review after explicit human confirmation. Declining confirmation publishes nothing. Always clean workflow-created directories, including failure or cancellation. Never push code changes.";
        }
        return result;
    }

    private static async Task AssertVisibleV2QuestionsAsync(PlanningSessionService service, PlanningSnapshot state)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(service);
        var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
        page.WaitForAssertion(() => Assert.Equal(state.Question!.Fields!.Count, page.FindAll("fieldset").Count));
        Assert.Contains("Planner v2", page.Markup);
        Assert.Empty(page.FindAll("input[type=radio][checked]"));
        Assert.True(Assert.Single(page.FindAll("button"), b => b.TextContent == "Submit answers").HasAttribute("disabled"));
    }

    // The planner's own durable per-session journal still records local usage. This
    // outer scope charges every actual dispatch (including capability assessment)
    // once to the existing cumulative campaign ledger; receipt replay skips dispatch.
    private sealed class CampaignPlanningClient(SecureWorkflowRuntimeFactory factory, LLMUsageBudgetScope budget, IExchangeRateProvider exchangeRates, LiveBudgetLedger ledger) : ILLMClient
    {
        // The ledger and usage scope serialize their own durable reservations.
        // Keep provider work concurrent within the planner's authorized ceiling.
        private readonly SemaphoreSlim _dispatch = new(4, 4);
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            await _dispatch.WaitAsync(ct);
            try
            {
                if (budget.Limits.MaxCalls is { } maxCalls && budget.Snapshot.Calls >= maxCalls ||
                    budget.Limits.MaxTotalTokens is { } maxTokens && budget.Snapshot.TotalTokens >= maxTokens)
                    throw new InvalidOperationException("The cumulative campaign call/token limit is exhausted; no provider request was sent.");
                await using var runtime = await factory.CreateAsync(ct);
                // One budget reservation covers one potentially billable dispatch.
                // Provider HTTP retries would otherwise hide an unreceipted attempt
                // behind a later successful receipt. Planner recovery owns retries.
                foreach (var provider in runtime.Options.Models.Values) provider.RetryPolicy.MaxAttempts = 1;
                var estimator = new ModelMetadataUsageCostEstimator(runtime.Options);
                string? reservation = null;
                if (ExistingConfigurationAuthorized)
                {
                    reservation = ledger.ReserveCall(await MaximumCallCostAsync(runtime.Options, request, budget.Snapshot, exchangeRates, ct));
                }
                // Persist the maximum before dispatch. A missing receipt retains that
                // reserve across restarts instead of counting the unknown call as free.
                var response = await budget.CallAsync(runtime.LlmClient, estimator, request, "live.typed_planning", ct);
                if (reservation is not null) ledger.CompleteCall(reservation);
                return response;
            }
            finally { _dispatch.Release(); }
        }
    }

    private sealed class ProviderOperationalLogger : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new OperationalLogger(categoryName);
        public void Dispose() { }
        private sealed class OperationalLogger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => category.StartsWith("GnOuGo.AI.Core", StringComparison.Ordinal) && logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel) || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
                var data = new JsonObject();
                foreach (var (key, value) in fields)
                {
                    var name = key switch { "OperationName" => "operation", "FailureType" => "failure_type", "AttemptDurationMs" => "attempt_duration_ms", "ElapsedMs" => "elapsed_ms", "UseBackgroundMode" => "background", "Status" => "response_status", "StatusCode" => "status_code", _ => null };
                    if (name is null) continue;
                    data[name] = value switch { string text => JsonValue.Create(text), int number => JsonValue.Create(number), double number => JsonValue.Create(number), bool boolean => JsonValue.Create(boolean), _ => null };
                }
                if (data.Count != 0) WriteLiveProgress("provider_operation", providerDiagnostics: data);
                // Never format the message or exception: either could contain response content.
            }
        }
    }
}
