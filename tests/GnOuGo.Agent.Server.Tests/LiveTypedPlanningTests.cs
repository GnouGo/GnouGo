using System.Text.Json.Nodes;
using Bunit;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Components.Pages;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
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

    private static void ConfigureV2Campaign(IServiceCollection services, LLMUsageBudgetScope budget, PlanningPersistenceTests.StoreFixture? isolatedStore)
    {
        if (isolatedStore is not null) services.AddSingleton<IPlanningSessionStore>(isolatedStore.Store);
        services.AddSingleton(sp =>
        {
            var original = sp.GetRequiredService<SecureWorkflowRuntimeFactory>();
            var runtime = new SecureWorkflowRuntimeFactory(
                sp.GetRequiredService<LLMRuntimeOptionsStore>(), sp.GetRequiredService<IKeyVaultRuntimeConfigStore>(),
                sp.GetRequiredService<ILoggerFactory>(), llmClientOverride: new CampaignPlanningClient(original, budget, sp.GetRequiredService<IExchangeRateProvider>()),
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

    private static async Task ResumeV2QuestionsAsync(IServiceProvider services, CancellationToken ct)
    {
        var service = services.GetRequiredService<PlanningSessionService>();
        var id = Environment.GetEnvironmentVariable("GNOU_GO_LIVE_TYPED_PLANNING_SESSION_ID")!;
        var state = await service.GetAsync(id, ct) ?? throw new InvalidOperationException("The requested session does not exist in the configured tenant.");
        Assert.Equal(2, state.SchemaVersion);
        var answerCount = state.Answers.Count;
        if (state.Status is PlanningStatus.Failed or PlanningStatus.Recovery)
        {
            state = await service.SubmitAsync(id, new() { Kind = "retry", ExpectedRevision = state.Revision }, ct);
        }
        while (state.Status == PlanningStatus.Created)
            state = await service.SubmitAsync(id, new() { Kind = "advance", ExpectedRevision = state.Revision }, ct);
        Assert.Equal(answerCount, state.Answers.Count);
        Assert.Null(state.ReviewedGraph);
        Assert.Null(state.ApprovedHash);
        if (state.Status == PlanningStatus.Clarification)
        {
            await AssertVisibleV2QuestionsAsync(service, state);
            WriteLiveProgress("v2_user_questions_visible");
        }
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
        // No hosted service was started, no answer/approval is submitted, and the
        // encrypted pending form remains available to the real designer on restart.
    }

    private static async Task GenerateV2AgentAsync(IServiceProvider services, string name, CancellationToken ct)
    {
        var service = services.GetRequiredService<PlanningSessionService>();
        var state = (await service.ListAsync(ct)).SingleOrDefault(s => s.Request.Name == name)
            ?? await service.StartAsync(name, AcceptancePrompt, false, ct);
        if (state.Status is PlanningStatus.Recovery or PlanningStatus.Failed or PlanningStatus.Unsupported)
            state = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "retry", ExpectedRevision = state.Revision }, ct);
        if (state.Status == PlanningStatus.Saved && await TryGetAgentForCleanupAsync(services.GetRequiredService<IMcpClientFactory>(), name, ct) is null)
        {
            // Recreate a previously validated temporary artifact after failure cleanup;
            // this is still the same generation, never counted as another successful run.
            var revision = state.Revision++; state.Status = PlanningStatus.Approved; state.SavedAgentId = null;
            if (!await services.GetRequiredService<IPlanningSessionStore>().TrySaveAsync(state, revision, ct)) throw new PlanningConflictException("The campaign session changed.");
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
    private sealed class CampaignPlanningClient(SecureWorkflowRuntimeFactory factory, LLMUsageBudgetScope budget, IExchangeRateProvider exchangeRates) : ILLMClient
    {
        private readonly SemaphoreSlim _dispatch = new(1, 1);
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            await _dispatch.WaitAsync(ct);
            try
            {
                await using var runtime = await factory.CreateAsync(ct);
                var estimator = new ModelMetadataUsageCostEstimator(runtime.Options);
                if (ExistingConfigurationAuthorized)
                {
                    var metadata = new LLMModelMetadataResolver(runtime.Options).Resolve(request.Provider, request.Model);
                    var inputCeiling = metadata.MaxInputTokens ?? metadata.ContextWindowTokens;
                    var outputCeiling = request.MaxTokens ?? metadata.MaxOutputTokens;
                    if (inputCeiling is not > 0 || outputCeiling is not > 0) throw new InvalidOperationException("Live dispatch requires known token ceilings for conservative budget reservation.");
                    var ceiling = estimator.EstimateCostWithCurrency(request.Model, inputCeiling, outputCeiling, request.Provider)
                        ?? throw new InvalidOperationException("Live dispatch requires verifiable pricing.");
                    var snapshot = budget.Snapshot;
                    var rate = ceiling.Currency == snapshot.EstimatedCostCurrency ? 1m
                        : (snapshot.ExchangeRates.FirstOrDefault(q => q.SourceCurrency == ceiling.Currency && q.TargetCurrency == snapshot.EstimatedCostCurrency)
                            ?? await exchangeRates.GetQuoteAsync(ceiling.Currency, snapshot.EstimatedCostCurrency, ct))?.Rate
                            ?? throw new InvalidOperationException("Live dispatch requires a verified currency conversion.");
                    if (snapshot.EstimatedCost + ceiling.Amount * rate > budget.Limits.MaxEstimatedCost!.Amount)
                        throw new InvalidOperationException("The remaining campaign budget cannot cover a maximum-size model request. No request was dispatched.");
                }
                return await budget.CallAsync(runtime.LlmClient, estimator, request, "live.typed_planning", ct);
            }
            finally { _dispatch.Release(); }
        }
    }
}
