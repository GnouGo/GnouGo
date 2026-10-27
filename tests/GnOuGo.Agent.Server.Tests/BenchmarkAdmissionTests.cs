using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Persistence;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Integrations;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkAdmissionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static LLMOptions Options() => new() { DefaultProvider = "fixture", DefaultModel = "pinned",
        Models = { ["fixture"] = new() { Type = "openai" } } };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshRuntimeRecoversExactSuccessBeforeExpiredOrChangedDeploymentAdmission(bool interruptedAfterHttp)
    {
        using var fixture = new Fixture();
        var options = Options(); options.Models["fixture"].Url = "https://example.invalid/v1/chat/completions";
        options.ModelOverrides["openai/pinned"] = new()
        {
            Id = "pinned", ProviderType = "openai", MaxInputTokens = 128000, MaxOutputTokens = 65536, ContextWindowTokens = 200000,
            Capabilities = new() { SupportsStructuredOutput = true, SupportsReasoningEffort = true, SupportedReasoningEfforts = ["medium"] }
        };
        using var handler = new SuccessHandler(); using var http = new HttpClient(handler);
        var client = new RecoveringClient(new RoutingLLMClientAdapter(new RoutingLLMClient(http, options)))
            { InterruptAfterResponse = interruptedAfterHttp };
        var rates = new Rates(fixture.Time);
        var planning = LiveWorkflowEvaluation.FreshPlanningRequest([], "strict", "amazon", "Original", "fixture", "pinned");
        var request = PlanningConstructionPhasePolicies.Apply(new LLMRequest
        {
            ClientRequestId = "strict:1", Prompt = "Exact original", RequiredReviewCalls = 0, StructuredOutputStrict = true,
            StructuredOutputSchema = JsonNode.Parse("""{"title":"construction_local_judge","type":"object","properties":{"script":{"type":"string"}},"required":["script"],"additionalProperties":false}""")
        }, planning, "construction_local_judge");
        using (var model = new KeyVaultBenchmarkModel(fixture.Campaign, options, client, rates, new Price(), fixture.Time))
        {
            await model.CapturePlanningPolicyAsync(planning, Ct);
            if (interruptedAfterHttp) await Assert.ThrowsAsync<IOException>(() => Runtime(model).CallAsync(request, "construction_local_judge", Ct));
            else Assert.Equal("source.raw", (await Runtime(model).CallAsync(request, "construction_local_judge", Ct)).Json!["script"]!.ToString());
        }
        Assert.Equal(1, handler.Calls);
        var before = (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId!, Ct))!;
        Assert.Equal(interruptedAfterHttp, before["usage"] is null);
        Assert.NotNull(before["transport"]!["Attempts"]![0]!["Body"]);
        fixture.Time.Advance(TimeSpan.FromHours(6)); client.Unavailable = true;
        var capabilityCalls = client.CapabilityCalls;
        using var restarted = new KeyVaultBenchmarkModel(fixture.Campaign, options, client, rates, new Price(), fixture.Time);
        await restarted.CapturePlanningPolicyAsync(planning, Ct, activeMilliseconds: TimeSpan.FromHours(6).TotalMilliseconds);
        // Collections uses this exact pre-coordinator reconciliation; no planner deadline is extended.
        await restarted.RecoverSavedResponseAsync(request, Ct);
        Assert.Equal("source.raw", (await Runtime(restarted).CallAsync(request, "construction_local_judge", Ct)).Json!["script"]!.ToString());
        Assert.Equal(capabilityCalls, client.CapabilityCalls); Assert.Equal(1, handler.Calls);
        var after = (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId!, Ct))!;
        Assert.True(JsonNode.DeepEquals(before["transport"], after["transport"]));
        Assert.True(JsonNode.DeepEquals(before["effective_transport_policy"], after["effective_transport_policy"]));
        Assert.Equal(.09m, after["usage"]!["benchmark_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(0L, (await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct))["unknown_attempts"]!.GetValue<long>());
        request.Prompt = "Changed under an existing identity";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime(restarted).CallAsync(request, "construction_local_judge", Ct));
        request.Prompt = "Exact original";
        request.ClientRequestId = "strict:2";
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Runtime(restarted).CallAsync(request, "construction_local_judge", Ct));
        Assert.Equal(1, handler.Calls);
        client.Unavailable = false; request.ClientRequestId = "strict:3";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime(restarted).CallAsync(request, "construction_local_judge", Ct));
        Assert.Equal(1, handler.Calls);

        WorkflowPlanningRuntime Runtime(KeyVaultBenchmarkModel model) => new(new WorkflowEngine
        {
            LLMClient = new LiveWorkflowEvaluation.JournaledPlanningModel(model, new()),
            LLMUsageBudget = LiveWorkflowEvaluation.ModelBudget("plan", planning,
                new() { Request = planning, ActiveMilliseconds = TimeSpan.FromHours(6).TotalMilliseconds })
        }, (_, _) => Task.CompletedTask);
    }

    [Fact]
    public void FreshPhysicalOwnershipDoesNotChangeHistoricalOrRuntimeWrapping()
    {
        var request = LiveWorkflowEvaluation.FreshPlanningRequest([], "strict", "amazon", "Original", "fixture", "pinned");
        var elapsed = new PlanningSession { Request = request, ActiveMilliseconds = TimeSpan.FromHours(6).TotalMilliseconds };
        Assert.Null(LiveWorkflowEvaluation.ModelBudget("plan", request, elapsed));
        Assert.Equal(TimeSpan.FromMinutes(30), LiveWorkflowEvaluation.ModelBudget("execute", request, elapsed)!.Limits.MaxElapsed);
        request.Options.Remove(PlanningTransportPolicies.OptionName);
        Assert.Throws<InvalidOperationException>(() => LiveWorkflowEvaluation.ModelBudget("plan", request, elapsed));
        Assert.False(typeof(IModelAdmissionOwner).IsAssignableFrom(typeof(LiveWorkflowEvaluation.PlanningModel)));
        Assert.True(typeof(IModelAdmissionOwner).IsAssignableFrom(typeof(LiveWorkflowEvaluation.JournaledPlanningModel)));
    }

    [Theory]
    [InlineData(false, 0, 10)]
    [InlineData(false, 0, 11)]
    [InlineData(false, 1, 10)]
    [InlineData(false, 1, 11)]
    [InlineData(false, 2, 10)]
    [InlineData(false, 2, 11)]
    [InlineData(false, 3, 10)]
    [InlineData(false, 3, 11)]
    [InlineData(false, 4, 10)]
    [InlineData(false, 4, 11)]
    [InlineData(true, 1, 10)]
    [InlineData(true, 1, 11)]
    public async Task FreshCapturedTransportBindsActualReviewHeadroomAndStricterDisable(bool disabled, int reviews, int presentation)
    {
        using var fixture = new Fixture();
        var provider = new Provider(fixture.Campaign);
        using var model = fixture.Model(new Rates(fixture.Time), provider);
        var planning = LiveWorkflowEvaluation.FreshPlanningRequest([], "strict", "amazon", "Original", "fixture", "pinned");
        planning.Options["construction_presentation_version"] = presentation;
        planning.Options["llm_budget"]!["max_calls"] = 5;
        await model.CapturePlanningPolicyAsync(planning, Ct, activeMilliseconds: 120_000);
        var captured = await fixture.Campaign.LoadAsync("planning-evaluation-session-policy", planning.SessionId, Ct);
        Assert.Equal(presentation, captured!["construction_presentation_version"]!.GetValue<int>());
        var request = PlanningConstructionPhasePolicies.Apply(new LLMRequest
        {
            ClientRequestId = "strict:1", Prompt = "Exact bounded request", RequiredReviewCalls = reviews,
            DisableTransportRetries = disabled, StructuredOutputSchema = new JsonObject { ["title"] = "construction_local_judge" }
        }, planning, "construction_local_judge");
        await model.CallAsync(request, Ct);
        Assert.Equal(disabled, provider.LastDisableTransportRetries);
        var journal = (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, "strict:1", Ct))!;
        Assert.Equal(2, journal["effective_transport_policy"]!["version"]!.GetValue<int>());
        Assert.Equal(5, journal["session_attempt_limit"]!.GetValue<int>());
        Assert.Equal(disabled ? 1 : 3, journal["effective_transport_policy"]!["effective_retry_policy"]!["MaxAttempts"]!.GetValue<int>());
        Assert.Equal(reviews, journal["construction_review_attempt_headroom"]?.GetValue<int>() ?? 0);
        Assert.Equal(reviews * (24_000L + 2_048), journal["effective_transport_policy"]!["review_token_headroom"]!.GetValue<long>());
        Assert.Equal(fixture.Time.GetUtcNow().AddHours(5).AddMinutes(-2), journal["effective_transport_policy"]!["deadline_utc"]!.GetValue<DateTimeOffset>());
        var before = journal.ToJsonString();
        using var restarted = fixture.Model(new Rates(fixture.Time), provider);
        await restarted.CapturePlanningPolicyAsync(planning, Ct, activeMilliseconds: 180_000);
        await restarted.CallAsync(request, Ct); // Exact receipt, no send or deadline rewrite.
        Assert.Equal(1, provider.Calls);
        Assert.Equal(before, (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, "strict:1", Ct))!.ToJsonString());
        request.ClientRequestId = "strict:2"; request.RequiredReviewCalls = null;
        await Assert.ThrowsAsync<PlanningConflictException>(() => restarted.CallAsync(request, Ct));
        Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    public async Task ConstructionFinancialPreflightIncludesReviewsAndPreservesUnknownAccounting(int reviews)
    {
        using var fixture = new Fixture();
        var rates = new Rates(fixture.Time); var provider = new Provider(fixture.Campaign);
        using var model = fixture.Model(rates, provider);
        var measured = new LiveWorkflowEvaluation.PlanningModel(model, new());
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = measured }, (_, _) => Task.CompletedTask);
        var request = new LLMRequest { ClientRequestId = "unissued:1", Prompt = "Exact original", RequiredReviewCalls = reviews };
        var original = System.Text.Json.JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var ceilings = Enumerable.Repeat(2048, reviews + 1).ToArray(); ceilings[0] = 8192;
        var emptyAccounting = await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct);
        var mutations = fixture.Records.MutationAttempts;
        await runtime.AdmitConstructionAsync(request, 32000, ceilings, Ct);
        Assert.Equal(mutations, fixture.Records.MutationAttempts);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        Assert.True(JsonNode.DeepEquals(emptyAccounting, await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct)));

        var journal = new BenchmarkHttpJournal(fixture.Campaign, "retained:1", 100, 20, 49.8m);
        await journal.SaveAsync(new() { Attempts = [new() { Id = "uncertain" }] }, Ct);
        var before = await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct);
        var retained = (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, "retained:1", Ct))!.ToJsonString();
        mutations = fixture.Records.MutationAttempts;
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => runtime.AdmitConstructionAsync(
            request, 32000, ceilings, Ct));
        Assert.Equal(ErrorCodes.LlmBudgetExceeded, error.Code);
        Assert.Equal("not_started", error.Details!["dispatch_status"]!.ToString());
        Assert.Equal("construction_review_allowance", error.Details!["classification"]!.ToString());
        // A single draft would fit, but required review headroom cannot be omitted by the planner.
        await runtime.AdmitConstructionAsync(request, 32000, [8192], Ct);
        Assert.Equal(mutations, fixture.Records.MutationAttempts);
        Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(retained, (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, "retained:1", Ct))!.ToJsonString());
        Assert.Equal(0, provider.Calls);
        Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct)));
        Assert.Null(await fixture.Campaign.LoadAsync("planning-evaluation-exchange-rates", "USD-EUR", Ct));
        Assert.Empty(await fixture.Records.ListAsync("planning-evaluation-requests", "benchmark", BenchmarkCampaign.Author, Ct));
        Assert.Empty(await fixture.Records.ListAsync("planning-evaluation-receipts", "benchmark", BenchmarkCampaign.Author, Ct));
    }

    [Theory]
    [InlineData(32000, 0, 2048)]
    [InlineData(32000, 5, 0)]
    [InlineData(32000, 5, -1)]
    [InlineData(0, 5, 2048)]
    [InlineData(-1, 5, 2048)]
    public async Task ConstructionFinancialPreflightRejectsInvalidCeilingsWithoutMutation(int input, int calls, int output)
    {
        using var fixture = new Fixture();
        var rates = new Rates(fixture.Time); var provider = new Provider(fixture.Campaign);
        using var model = fixture.Model(rates, provider);
        var measured = new LiveWorkflowEvaluation.PlanningModel(model, new());
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = measured }, (_, _) => Task.CompletedTask);
        var request = new LLMRequest { ClientRequestId = "unissued:1", Prompt = "Exact original" };
        var original = System.Text.Json.JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.AdmitConstructionAsync(
            request, input, Enumerable.Repeat(output, calls).ToArray(), Ct));
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, rates.Calls);
        Assert.Equal(0, fixture.Records.MutationAttempts);
        Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
    }

    [Fact]
    public async Task ExplicitBenchmarkWorkspaceOwnsBothRecordsAndConfigurationPaths()
    {
        using var fixture = new Fixture();
        var expected = Path.Combine(fixture.Root, ".GnOuGo", "data", "gnougo-keyvault.db");
        var records = KeyVaultBenchmarkModel.WorkspaceRecords(fixture.Root);
        await records.UpsertAsync("public-fixture", "benchmark", "entry", "{}", BenchmarkCampaign.Author, Ct);
        Assert.True(File.Exists(expected));
        Assert.NotNull(await KeyVaultBenchmarkModel.WorkspaceRecords(fixture.Root).GetAsync("public-fixture", "benchmark", "entry", BenchmarkCampaign.Author, Ct));
        Assert.Equal(Path.Combine(fixture.Root, ".GnOuGo", "data", "gnougo-agent.db"),
            KeyVaultBenchmarkModel.WorkspaceDatabasePath(fixture.Root, "", ".GnOuGo/data/gnougo-agent.db"));
        var explicitConfig = Path.Combine(fixture.Root, "declared-agent.db");
        Assert.Equal(explicitConfig, KeyVaultBenchmarkModel.WorkspaceDatabasePath(fixture.Root, explicitConfig, "ignored"));
    }

    [Theory]
    [InlineData("execute", false)]
    [InlineData("revise", true)]
    public async Task ExplicitJudgeMismatchStopsBeforeReadinessOrTransport(string phase, bool enabled)
    {
        using var fixture = new Fixture();
        var rates = new Rates(fixture.Time) { Defect = "network" };
        var provider = new Provider(fixture.Campaign);
        using var model = fixture.Model(rates, provider);
        var retained = new JsonObject { ["session"] = new JsonObject { ["request"] = new JsonObject
            { ["options"] = enabled ? new JsonObject { ["business_judge"] = true } : new JsonObject() } } };
        await fixture.Campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:frozen", retained, Ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LiveWorkflowEvaluation.RunAsync(
            ["--case", "amazon", "--run", "frozen", enabled ? "--no-business-judge" : "--business-judge"],
            phase, fixture.Campaign, model, fixture.Root));
        Assert.Contains("judge", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rates.Calls);
        Assert.Equal(0, provider.Calls);
        Assert.True(JsonNode.DeepEquals(retained, await fixture.Campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:frozen", Ct)));
        Assert.Equal(0L, (await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct))["calls"]!.GetValue<long>());
    }

    [Fact]
    public async Task ConflictingJudgeSwitchesStopBeforeReadinessOrTransport()
    {
        using var fixture = new Fixture();
        var rates = new Rates(fixture.Time) { Defect = "network" };
        var provider = new Provider(fixture.Campaign);
        using var model = fixture.Model(rates, provider);
        await Assert.ThrowsAsync<ArgumentException>(() => LiveWorkflowEvaluation.RunAsync(
            ["--case", "amazon", "--run", "fresh", "--business-judge", "--no-business-judge"],
            "plan", fixture.Campaign, model, fixture.Root));
        Assert.Equal(0, rates.Calls);
        Assert.Equal(0, provider.Calls);
        Assert.Null(await fixture.Campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:fresh", Ct));
    }

    [Theory]
    [InlineData("missing-price")]
    [InlineData("missing-quote")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("invalid-rate")]
    [InlineData("wrong-currency")]
    [InlineData("network")]
    public async Task ReadinessStopsBeforeProviderOrExternalWork(string defect)
    {
        using var fixture = new Fixture();
        var rates = new Rates(fixture.Time) { Defect = defect };
        var provider = new Provider(fixture.Campaign);
        using var model = new KeyVaultBenchmarkModel(fixture.Campaign, Options(), provider, rates,
            new Price { Missing = defect == "missing-price" }, fixture.Time);
        var readiness = await model.ReadinessAsync(Ct);
        Assert.False(readiness["ready"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, readiness["accounting"]!["error"]!.ToString());
        Assert.Equal("not_started", readiness["accounting"]!["details"]!["dispatch_status"]!.ToString());
        // The real host exits before constructing the MCP transport, starting a proxy or executing any workflow.
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => LiveWorkflowEvaluation.RunAsync(
            ["--case", "amazon", "--run", "unused"], "execute", fixture.Campaign, model, fixture.Root));
        Assert.Equal(0, provider.Calls);
        Assert.Null(await fixture.Campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:unused", Ct));
        Assert.Equal(0L, (await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct))["calls"]!.GetValue<long>());
    }

    [Fact]
    public async Task ReadinessReportsExactDeclaredReasoningSupportWithoutInferringOrChangingAdmission()
    {
        using var fixture = new Fixture();
        var options = Options();
        var provider = new Provider(fixture.Campaign);
        var rates = new Rates(fixture.Time);
        foreach (var capabilities in new ModelCapabilityMetadata?[]
        {
            null, new(), new() { SupportsReasoningEffort = false },
            new() { SupportsReasoningEffort = true },
            new() { SupportsReasoningEffort = true, SupportedReasoningEfforts = ["low", "medium"] }
        })
        {
            options.ModelOverrides.Clear();
            if (capabilities is not null)
                options.ModelOverrides["openai/pinned"] = new() { Capabilities = capabilities };
            using var model = new KeyVaultBenchmarkModel(fixture.Campaign, options, provider, rates, new Price(), fixture.Time);
            var configurationHash = model.ConfigurationFingerprint;
            var readiness = await model.ReadinessAsync(Ct);
            Assert.Equal(capabilities?.SupportsReasoningEffort, readiness["supports_reasoning_effort"]?.GetValue<bool>());
            if (capabilities?.SupportedReasoningEfforts is { } efforts)
                Assert.Equal(efforts, readiness["supported_reasoning_efforts"]!.AsArray().Select(v => v!.GetValue<string>()));
            else Assert.Null(readiness["supported_reasoning_efforts"]);
            Assert.Equal(configurationHash, model.ConfigurationFingerprint);
            Assert.Equal(configurationHash, readiness["configuration_hash"]!.GetValue<string>());
            Assert.True(readiness["ready"]!.GetValue<bool>());
            Assert.Equal(0, readiness["model_calls"]!.GetValue<int>());
        }
        Assert.Equal(0, provider.Calls);
        Assert.Empty(await fixture.Records.ListAsync("planning-evaluation-requests", "benchmark", BenchmarkCampaign.Author, Ct));
    }

    [Fact]
    public async Task FreshQuotesSurviveRestartButExpiredQuotesNeverAuthorizeNewRequests()
    {
        using var fixture = new Fixture();
        var rates = new Rates(fixture.Time);
        using (var model = fixture.Model(rates)) Assert.True((await model.ReadinessAsync(Ct))["ready"]!.GetValue<bool>());
        var original = await fixture.Campaign.LoadAsync("planning-evaluation-exchange-rates", "USD-EUR", Ct);
        Assert.Equal(1, rates.Calls);
        rates.Defect = "network";
        using (var restarted = fixture.Model(rates)) Assert.True((await restarted.ReadinessAsync(Ct))["ready"]!.GetValue<bool>());
        Assert.Equal(1, rates.Calls);
        fixture.Time.Advance(TimeSpan.FromDays(8));
        using (var restarted = fixture.Model(rates)) Assert.False((await restarted.ReadinessAsync(Ct))["ready"]!.GetValue<bool>());
        Assert.Equal(2, rates.Calls);
        Assert.True(JsonNode.DeepEquals(original, await fixture.Campaign.LoadAsync("planning-evaluation-exchange-rates", "USD-EUR", Ct)));
        using var other = new KeyVaultBenchmarkModel(new(fixture.Records, "other"), Options(), new Provider(fixture.Campaign), rates, new Price(), fixture.Time);
        Assert.False((await other.ReadinessAsync(Ct))["ready"]!.GetValue<bool>());
        Assert.Equal(3, rates.Calls);
    }

    [Fact]
    public async Task HistoricalConstructionCannotRetryUncertainTransportButMayConsumeAnExistingReceipt()
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time);
        var provider = new Provider(fixture.Campaign);
        using var model = fixture.Model(rates, provider);
        var settings = LiveWorkflowEvaluation.FreshPlanningRequest([], "fresh", "amazon", "Original request", "fixture", "pinned");
        settings.Options.Remove(PlanningTransportPolicies.OptionName); // Retained historical single-attempt behavior.
        await model.CapturePlanningPolicyAsync(settings, Ct);
        var completed = new LLMRequest
        {
            ClientRequestId = "fresh:1", Prompt = "bounded original draft", MaxTokens = 8192,
            StructuredOutputStrict = true,
            StructuredOutputSchema = new JsonObject
            {
                ["title"] = "construction_draft", ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["script"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("script")
            }
        };
        PlanningConstructionPhasePolicies.Apply(completed, settings, "construction_draft");
        var receipt = await model.CallAsync(completed, Ct);
        Assert.True(provider.LastDisableTransportRetries);
        Assert.Equal(1, provider.Calls);
        rates.Defect = "network";
        Assert.True(JsonNode.DeepEquals(receipt.Json, (await model.CallAsync(completed, Ct)).Json));
        Assert.Equal(1, provider.Calls);
        provider.Failure = new IOException("Unknown completion");
        var pending = new LLMRequest { ClientRequestId = "fresh:2", Prompt = "bounded later draft", MaxTokens = 8192,
            StructuredOutputStrict = true, StructuredOutputSchema = completed.StructuredOutputSchema!.DeepClone() };
        PlanningConstructionPhasePolicies.Apply(pending, settings, "construction_draft");
        await Assert.ThrowsAsync<IOException>(() => model.CallAsync(pending, Ct));
        var before = await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, pending.ClientRequestId, Ct);
        Assert.True(before!["effective_transport_policy"]!["disable_transport_retries"]!.GetValue<bool>());
        Assert.False(before["effective_transport_policy"]!["allow_http_recovery"]!.GetValue<bool>());
        Assert.Equal(1, before["effective_transport_policy"]!["effective_retry_policy"]!["MaxAttempts"]!.GetValue<int>());
        var costs = await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct);
        using var restarted = fixture.Model(rates, provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CallAsync(pending, Ct));
        Assert.Equal(2, provider.Calls);
        Assert.True(JsonNode.DeepEquals(before, await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, pending.ClientRequestId, Ct)));
        Assert.True(JsonNode.DeepEquals(costs, await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct)));
        Assert.Equal(1L, costs["unknown_attempts"]!.GetValue<long>());
    }

    [Fact]
    public async Task HistoricalConstructionServerFailureStaysReservedAndCannotReplayOrAuthorizeAnotherRequestAfterRestart()
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time);
        var provider = new Provider(fixture.Campaign) { Failure = new LLMClientException(LLMClientFailureKind.ServiceUnavailable,
            "Retained server failure.", retryable: true, statusCode: 502, safeProviderCode: null,
            attemptCount: 1, retryExhausted: true, retryAfterMilliseconds: null) };
        using var model = fixture.Model(rates, provider);
        var settings = LiveWorkflowEvaluation.FreshPlanningRequest([], "failed", "amazon", "Original request", "fixture", "pinned");
        settings.Options.Remove(PlanningTransportPolicies.OptionName); // Retained historical single-attempt behavior.
        await model.CapturePlanningPolicyAsync(settings, Ct);
        var request = new LLMRequest { ClientRequestId = "failed:1", Prompt = "bounded original draft", MaxTokens = 8192,
            StructuredOutputStrict = true, StructuredOutputSchema = new JsonObject { ["title"] = "construction_draft",
                ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray(), ["additionalProperties"] = false } };
        PlanningConstructionPhasePolicies.Apply(request, settings, "construction_draft");
        await Assert.ThrowsAsync<LLMClientException>(() => model.CallAsync(request, Ct));
        Assert.Equal(1, provider.Calls);
        Assert.True(provider.LastDisableTransportRetries);
        var original = (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId, Ct))!;
        Assert.Equal(502, original["transport"]!["Attempts"]![0]!["Status"]!.GetValue<int>());
        Assert.False(original["effective_transport_policy"]!["allow_http_recovery"]!.GetValue<bool>());
        var hash = PlanningGraphCompiler.Fingerprint(original.ToJsonString());
        var accounting = await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct);
        Assert.Equal(1L, accounting["unknown_attempts"]!.GetValue<long>());
        Assert.Equal(original["cost_ceiling_eur"]!.GetValue<decimal>(), accounting["reserved_cost_eur"]!.GetValue<decimal>());
        using var restarted = fixture.Model(rates, provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CallAsync(request, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Campaign.CallAsync(new() { ClientRequestId = "another:1" },
            _ => throw new InvalidOperationException("Must not admit."), _ => throw new InvalidOperationException("Must not dispatch."), Ct));
        Assert.Equal(1, provider.Calls);
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct)));
        Assert.Equal(hash, PlanningGraphCompiler.Fingerprint((await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId, Ct))!.ToJsonString()));
        Assert.Null(await fixture.Campaign.LoadAsync("planning-evaluation-receipts", request.ClientRequestId, Ct));
        Assert.Null(await fixture.Campaign.LoadAsync("planning-evaluation-closures", request.ClientRequestId, Ct));
    }

    [Fact]
    public async Task SettlementUsesAdmittedQuoteAndCommittedReceiptReplaysWithoutNetwork()
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time);
        var provider = new Provider(fixture.Campaign)
        {
            AfterDispatch = () => { fixture.Time.Advance(TimeSpan.FromDays(8)); rates.Defect = "network"; }
        };
        var request = new LLMRequest { ClientRequestId = "fresh:1", Prompt = "observed", MaxTokens = 8192 };
        using (var model = fixture.Model(rates, provider))
        {
            var response = await model.CallExecutionAsync(request, Ct);
            Assert.Equal(.09m, response.Usage!["benchmark_cost_eur"]!.GetValue<decimal>());
        }
        Assert.Equal(1, provider.Calls); Assert.Equal(1, rates.Calls);
        var record = await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId, Ct);
        Assert.Equal(.9m, record!["exchange_quote"]!["rate"]!.GetValue<decimal>());
        Assert.Equal("fixture-authority", record["exchange_quote"]!["source"]!.ToString());
        using (var restarted = fixture.Model(rates, provider))
            Assert.Equal(.09m, (await restarted.CallExecutionAsync(request, Ct)).Usage!["benchmark_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(1, provider.Calls); Assert.Equal(1, rates.Calls);
    }

    [Fact]
    public async Task TypedNonDispatchIsDurableAndCannotRedispatchOrChangeItsIdentity()
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time) { Defect = "missing-quote" };
        var provider = new Provider(fixture.Campaign);
        var request = new LLMRequest { ClientRequestId = "denied:1", Prompt = "original", MaxTokens = 8192 };
        using (var model = fixture.Model(rates, provider))
        {
            var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => model.CallExecutionAsync(request, Ct));
            Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, error.Code);
            Assert.Equal("currency_quote_unavailable", error.Details!["reason"]!.ToString());
            Assert.Equal(0L, (await model.PartialUsageAsync(request.ClientRequestId, Ct))!["input_tokens"]!.GetValue<long>());
        }
        rates.Defect = null;
        using (var restarted = fixture.Model(rates, provider))
        {
            await Assert.ThrowsAsync<WorkflowRuntimeException>(() => restarted.CallExecutionAsync(request, Ct));
            request.Prompt = "changed";
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CallExecutionAsync(request, Ct));
        }
        Assert.Equal(0, provider.Calls); Assert.Equal(1, rates.Calls);
        Assert.False(await fixture.Campaign.HasUncertainRequestAsync(Ct));
        Assert.Null(await fixture.Campaign.LoadAsync("planning-evaluation-requests", request.ClientRequestId, Ct));
    }

    [Theory]
    [InlineData("mapping.dynamic")]
    [InlineData("llm.call")]
    public async Task VerifiedFailurePersistsAndCleansUpWithoutRetry(string stepType)
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time) { Defect = "missing-quote" };
        var provider = new Provider(fixture.Campaign);
        using var model = fixture.Model(rates, provider);
        var workflow = Workflow(stepType);
        var client = new IdentifiedClient(model);
        var result = await fixture.Engine(client).ExecuteAsync(workflow, new JsonObject(), Ct);
        Assert.True(result.Error?.Code == ErrorCodes.LlmBudgetUnverifiable, result.Error?.Code + ": " + result.Error?.Message);
        var saved = (await fixture.Store().ReadAsync("tenant", "run", Ct))!;
        Assert.True(saved.FinalizationCompleted);
        Assert.Equal("completed", Assert.Single(saved.Invocations.Values, i => i.IsFinalization && i.StepType == "set").Status);
        Assert.DoesNotContain(saved.Invocations.Values, i => i.Status == "needs_reconciliation");
        Assert.Equal(0, provider.Calls);
        var replay = await fixture.Engine(client).ResumeAsync("tenant", "run", saved.Revision, workflow, Ct);
        Assert.Equal(result.Error?.Code, replay.Error?.Code);
        Assert.Equal(0, provider.Calls); Assert.Equal(1, rates.Calls);
        Assert.Null(await fixture.Store().ReadAsync("other", "run", Ct));
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("post-dispatch-budget")]
    [InlineData("failed-preflight-record")]
    [InlineData("failed-completion-record")]
    public async Task UnverifiedOrUndurableCompletionStillBlocksCleanupAndReplay(string defect)
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time);
        var provider = new Provider(fixture.Campaign);
        if (defect == "failed-preflight-record") { rates.Defect = "missing-quote"; fixture.Records.FailCollection = "planning-evaluation-failures"; }
        if (defect == "failed-completion-record") fixture.Records.FailCollection = "planning-evaluation-receipts";
        if (defect == "transport") provider.Failure = new IOException("Completion unavailable");
        if (defect == "post-dispatch-budget") provider.Failure = new WorkflowRuntimeException(ErrorCodes.LlmBudgetUnverifiable, "Settlement unavailable");
        using var model = fixture.Model(rates, provider);
        var workflow = Workflow("mapping.dynamic");
        var result = await fixture.Engine(model).ExecuteAsync(workflow, new JsonObject(), Ct);
        Assert.Equal("RUN_NEEDS_RECONCILIATION", result.Error?.Code);
        var saved = (await fixture.Store().ReadAsync("tenant", "run", Ct))!;
        Assert.DoesNotContain(saved.Invocations.Values, i => i.IsFinalization && i.Status == "completed");
        var calls = provider.Calls;
        await fixture.Engine(model).ResumeAsync("tenant", "run", saved.Revision, workflow, Ct);
        Assert.Equal(calls, provider.Calls);
    }

    [Fact]
    public async Task HistoricalUnknownAdmissionIsNotReclassifiedOrRepriced()
    {
        using var fixture = new Fixture(); const string id = "historical:1";
        var request = new LLMRequest { ClientRequestId = id, MaxTokens = 8192 };
        var journal = new BenchmarkHttpJournal(fixture.Campaign, id, 100, 20, 1m);
        await journal.SaveAsync(new() { Attempts = [new() { Id = "unknown" }] }, Ct);
        await fixture.Campaign.SaveAsync("planning-evaluation-requests", id,
            System.Text.Json.JsonSerializer.SerializeToNode(request, GnOuGo.Flow.Core.Planning.PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var original = await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct);
        var rates = new Rates(fixture.Time) { Defect = "network" };
        using var model = fixture.Model(rates);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.CallExecutionAsync(request, Ct));
        Assert.Equal(0, rates.Calls);
        Assert.True(JsonNode.DeepEquals(original, await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct)));
        Assert.Equal(1m, (await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Null((await fixture.Campaign.LoadAsync("planning-evaluation-failures", id, Ct))?["dispatch_status"]);
    }

    [Fact]
    public async Task FailedFlowReceiptPublicationRemainsUncertainAfterRestart()
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time) { Defect = "missing-quote" };
        var provider = new Provider(fixture.Campaign);
        fixture.RunRecords.FailContent = "LLM_BUDGET_UNVERIFIABLE";
        using var model = fixture.Model(rates, provider);
        var workflow = Workflow("mapping.dynamic"); RunResult? result = null;
        var failure = await Record.ExceptionAsync(async () => result = await fixture.Engine(model).ExecuteAsync(workflow, new JsonObject(), Ct));
        Assert.True(failure is not null || result?.Success == false);
        fixture.RunRecords.FailContent = null;
        var saved = (await fixture.Store().ReadAsync("tenant", "run", Ct))!;
        Assert.DoesNotContain(saved.Invocations.Values, i => i.IsFinalization && i.Status == "completed");
        Assert.Contains(saved.Invocations.Values, i => i.Recovery == StepRecovery.External && i.DispatchedAt is not null && i.CompletedAt is null);
        var resumed = await fixture.Engine(model).ResumeAsync("tenant", "run", saved.Revision, workflow, Ct);
        Assert.Equal("RUN_NEEDS_RECONCILIATION", resumed.Error?.Code);
        Assert.Equal(0, provider.Calls); Assert.Equal(1, rates.Calls);
    }

    [Fact]
    public async Task CancellationDuringQuoteResolutionIsNotDisguisedAsAccountingRefusal()
    {
        using var fixture = new Fixture(); using var cancel = new CancellationTokenSource();
        var rates = new Rates(fixture.Time) { Cancel = cancel }; var provider = new Provider(fixture.Campaign);
        using var model = fixture.Model(rates, provider);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.CallExecutionAsync(new() { ClientRequestId = "cancelled:1", MaxTokens = 8192 }, cancel.Token));
        Assert.Equal(0, provider.Calls);
        Assert.Null((await fixture.Campaign.LoadAsync("planning-evaluation-failures", "cancelled:1", Ct))?["dispatch_status"]);
        Assert.False(await fixture.Campaign.HasUncertainRequestAsync(Ct));
    }

    [Theory]
    [InlineData(false, 429)]
    [InlineData(false, 500)]
    [InlineData(false, 0)]
    [InlineData(true, 429)]
    [InlineData(true, 500)]
    [InlineData(true, 0)]
    public async Task FreshRuntimeReservesEveryFailureAndNeverRetries(bool proxy, int status)
    {
        using var fixture = new Fixture(); var options = RuntimeOptions();
        using var handler = new RuntimeHandler(status); using var http = new HttpClient(handler);
        using var model = RuntimeModel(fixture, options, http);
        var id = RuntimeId(proxy);
        handler.BeforeSend = async () =>
        {
            var row = (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
            var attempt = Assert.Single(row["transport"]!["Attempts"]!.AsArray());
            Assert.Null(attempt!["Status"]);
            Assert.Equal(.09m, (await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        };
        var error = await Record.ExceptionAsync(() => InvokeRuntime(model, proxy));
        Assert.Equal(proxy && status != 0, error is null);
        Assert.Equal(1, handler.Calls);
        var journal = (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
        var policy = journal["effective_transport_policy"]!;
        Assert.Equal(2, policy["version"]!.GetValue<int>());
        Assert.True(policy["disable_transport_retries"]!.GetValue<bool>());
        Assert.Equal(1, policy["effective_retry_policy"]!["MaxAttempts"]!.GetValue<int>());
        Assert.Equal(0, policy["effective_retry_policy"]!["MaxUncertainRetries"]!.GetValue<int>());
        Assert.Null(journal["session_attempt_limit"]); // Runtime does not acquire a new planning cap.
        var before = journal.ToJsonString();
        var totals = await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct);
        Assert.Equal(1L, totals["calls"]!.GetValue<long>());
        Assert.Equal(1L, totals["unknown_attempts"]!.GetValue<long>());
        Assert.Equal(.09m, totals["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(0m, totals["known_cost_eur"]!.GetValue<decimal>());
        _ = await Record.ExceptionAsync(() => InvokeRuntime(model, proxy));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(before, (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(totals, await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct)));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RuntimeExactCompletedResponseRecoversWithoutSendOrHistoricalPolicyRewrite(bool proxy, bool historical, bool receipt)
    {
        using var fixture = new Fixture(); var options = RuntimeOptions();
        using var handler = new RuntimeHandler(200); using var http = new HttpClient(handler);
        using var model = RuntimeModel(fixture, options, http);
        await InvokeRuntime(model, proxy);
        var id = RuntimeId(proxy); var campaign = fixture.Campaign;
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
        if (historical)
        {
            var policy = journal["effective_transport_policy"]!.AsObject();
            policy.Remove("request_hash"); policy.Remove("wire_fingerprint"); policy["version"] = 1;
            if (proxy)
            {
                policy.Remove("effective_retry_policy"); policy["allow_http_recovery"] = false; policy["max_attempts"] = 1;
                // Historical SDK evidence retained only the body, not its upstream URI.
                journal["transport"]!["Fingerprint"] = PlanningGraphCompiler.Fingerprint(System.Text.Json.JsonSerializer.Deserialize((await campaign.LoadAsync("planning-evaluation-requests", id, Ct))!, PlanningJsonContext.Default.LLMRequest)!.Prompt);
            }
            else
            {
                policy.Remove("mode"); policy["disable_transport_retries"] = false;
                policy["effective_retry_policy"]!["MaxAttempts"] = 4;
                policy["effective_retry_policy"]!["MaxUncertainRetries"] = 1;
            }
            journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(policy.ToJsonString());
        }
        if (!receipt)
        {
            journal.Remove("usage");
            await fixture.Records.DeleteAsync("planning-evaluation-receipts", "benchmark", "fixture:" + id, BenchmarkCampaign.Author, Ct);
        }
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, id, journal, Ct);
        var policyBefore = journal["effective_transport_policy"]!.ToJsonString();
        var transportBefore = journal["transport"]!.ToJsonString();
        var originalBefore = (await campaign.LoadAsync("planning-evaluation-requests", id, Ct))!.ToJsonString();
        var receiptBefore = (await campaign.LoadAsync("planning-evaluation-receipts", id, Ct))?.ToJsonString();
        fixture.Time.Advance(TimeSpan.FromDays(20));
        if (proxy && historical)
        {
            var before = journal.ToJsonString();
            await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeRuntime(model, proxy));
            Assert.Equal(1, handler.Calls);
            Assert.Equal(before, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
            Assert.Equal(receiptBefore, (await campaign.LoadAsync("planning-evaluation-receipts", id, Ct))?.ToJsonString());
            Assert.Equal(originalBefore, (await campaign.LoadAsync("planning-evaluation-requests", id, Ct))!.ToJsonString());
            return;
        }
        if (proxy)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeRuntime(model, proxy,
                upstream: "https://example.invalid/v1/other/chat/completions"));
            Assert.Equal(1, handler.Calls);
        }
        await InvokeRuntime(model, proxy);
        Assert.Equal(1, handler.Calls);
        var after = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
        Assert.Equal(policyBefore, after["effective_transport_policy"]!.ToJsonString());
        Assert.Equal(transportBefore, after["transport"]!.ToJsonString());
        Assert.Equal(originalBefore, (await campaign.LoadAsync("planning-evaluation-requests", id, Ct))!.ToJsonString());
        if (receipt) Assert.Equal(receiptBefore, (await campaign.LoadAsync("planning-evaluation-receipts", id, Ct))!.ToJsonString());
        var totals = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(.09m, totals["known_cost_eur"]!.GetValue<decimal>()); Assert.Equal(0m, totals["reserved_cost_eur"]!.GetValue<decimal>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeRuntime(model, proxy, "altered input"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeRuntime(model, proxy, output: 32768));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RuntimePhysicalReservationNeverReplaysButEmptyFreshJournalMayStart(bool proxy, bool reservedAttempt)
    {
        using var fixture = new Fixture(); var options = RuntimeOptions();
        using var handler = new RuntimeHandler(200); using var http = new HttpClient(handler);
        using var model = RuntimeModel(fixture, options, http);
        // Interrupt after durable admission; no transport has run.
        fixture.Records.FailCollection = "planning-evaluation-requests";
        await Assert.ThrowsAsync<IOException>(() => InvokeRuntime(model, proxy));
        fixture.Records.FailCollection = null;
        Assert.Equal(0, handler.Calls);
        var id = RuntimeId(proxy);
        var row = (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
        Assert.Empty(row["transport"]!["Attempts"]!.AsArray());
        var unchanged = row.ToJsonString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeRuntime(model, proxy, "changed after admission"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeRuntime(model, proxy, output: 32768));
        if (proxy) await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeRuntime(model, proxy,
            upstream: "https://example.invalid/v1/other/chat/completions"));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(unchanged, (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
        if (reservedAttempt)
        {
            var journal = new BenchmarkHttpJournal(fixture.Campaign, id, row["input_ceiling"]!.GetValue<long>(),
                row["output_ceiling"]!.GetValue<long>(), row["cost_ceiling_eur"]!.GetValue<decimal>(), null,
                effectiveTransportPolicy: row["effective_transport_policy"]!.AsObject(), time: fixture.Time);
            await journal.SaveAsync(new() { Attempts = [new() { Id = "reserved-before-crash" }] }, Ct);
            var before = (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString();
            await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeRuntime(model, proxy));
            Assert.Equal(0, handler.Calls);
            Assert.Equal(before, (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
            Assert.Equal(.09m, (await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        }
        else
        {
            await InvokeRuntime(model, proxy);
            Assert.Equal(1, handler.Calls);
        }
    }

    private static LLMOptions RuntimeOptions()
    {
        var options = Options(); options.Models["fixture"].Url = "https://example.invalid/v1";
        options.Models["fixture"].RetryPolicy = new() { MaxAttempts = 4, MaxUncertainRetries = 1, BaseDelayMilliseconds = 1 };
        return options;
    }
    private static string RuntimeId(bool proxy) => proxy ? "runtime-copilot:" + PlanningGraphCompiler.Fingerprint("sdk-one") : "runtime-execution:one";
    private static KeyVaultBenchmarkModel RuntimeModel(Fixture fixture, LLMOptions options, HttpClient http) => new(fixture.Campaign,
        options, new RoutingLLMClientAdapter(new RoutingLLMClient(http, options)), new Rates(fixture.Time), new Price(), fixture.Time, http);
    private static async Task InvokeRuntime(KeyVaultBenchmarkModel model, bool proxy, string prompt = "exact input",
        int output = 8192, string upstream = "https://example.invalid/v1/chat/completions")
    {
        if (proxy)
            await model.ProxyInferenceAsync("runtime", new()
            {
                ["X-GnOuGo-Inference-Upstream"] = upstream,
                ["X-GnOuGo-Inference-Request"] = "sdk-one"
            }, new JsonObject { ["model"] = "pinned", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }), ["max_completion_tokens"] = output }.ToJsonString(), Ct);
        else await model.CallExecutionAsync(new() { ClientRequestId = RuntimeId(false), Prompt = prompt, MaxTokens = output }, Ct);
    }
    private sealed class RuntimeHandler(int status) : HttpMessageHandler
    {
        internal int Calls;
        internal Func<Task>? BeforeSend;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (BeforeSend is not null) await BeforeSend();
            if (status == 0) throw new HttpRequestException("Offline fixture transport outcome unavailable");
            return new((System.Net.HttpStatusCode)status) { Content = new StringContent(status == 200
                ? """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"done"}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}"""
                : """{"error":{"code":"rate_limit_exceeded","message":"temporary fixture failure"}}""", System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private static CompiledWorkflow Workflow(string stepType)
    {
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: model
                    type: mapping.dynamic
                    input:
                      sources: {raw: {name: observed}}
                      objective: Extract the observed name.
                      binding: fixture
                      producer_contract: fixture
                    output_schema:
                      type: object
                      properties:
                        value: {type: object, properties: {name: {type: string}}, required: [name], additionalProperties: false}
                      required: [value]
                      additionalProperties: false
                finally:
                  - {id: cleanup, type: set, input: {value: cleaned}}
            """);
        if (stepType == "llm.call")
        {
            var step = document.Workflows["main"].Steps[0]; step.Type = stepType; step.OutputSchema = null;
            step.Input = new JsonObject { ["model"] = "pinned", ["prompt"] = "observed", ["max_tokens"] = 8192 };
        }
        return new WorkflowCompiler().Compile(document).Workflows["main"];
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan time) => _now += time;
    }
    private sealed class SuccessHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{\"script\":\"source.raw\"}"}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}""") });
        }
    }
    private sealed class RecoveringClient(ILLMClient inner) : ILLMClient, ILLMCapabilityResolver
    {
        internal bool InterruptAfterResponse, Unavailable; internal int CapabilityCalls;
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var response = await inner.CallAsync(request, ct);
            if (InterruptAfterResponse) { InterruptAfterResponse = false; throw new IOException("Interrupted after persisted HTTP success"); }
            return response;
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct)
        { CapabilityCalls++; return Unavailable ? Task.FromResult<int?>(null) : ((ILLMCapabilityResolver)inner).InputTokenAllowanceAsync(provider, model, outputTokens, ct); }
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct)
        { CapabilityCalls++; return Unavailable ? Task.FromResult<bool?>(false) : ((ILLMCapabilityResolver)inner).SupportsStructuredOutputAsync(provider, model, ct); }
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct)
        { CapabilityCalls++; return Unavailable ? Task.FromResult<IReadOnlyList<string>?>([]) : ((ILLMCapabilityResolver)inner).SupportedReasoningLevelsAsync(provider, model, ct); }
    }
    private sealed class Rates(Clock time) : IExchangeRateProvider
    {
        internal int Calls; internal string? Defect; internal CancellationTokenSource? Cancel;
        public ValueTask<CurrencyExchangeQuote?> GetQuoteAsync(string sourceCurrency, string targetCurrency, CancellationToken ct)
        {
            Calls++;
            Cancel?.Cancel(); ct.ThrowIfCancellationRequested();
            if (Defect == "network") throw new HttpRequestException("Fixture unavailable");
            return ValueTask.FromResult<CurrencyExchangeQuote?>(Defect == "missing-quote" ? null : new(
                Defect == "wrong-currency" ? "GBP" : sourceCurrency, targetCurrency, Defect == "invalid-rate" ? 0m : .9m,
                time.GetUtcNow() + (Defect == "stale" ? TimeSpan.FromDays(-8) : Defect == "future" ? TimeSpan.FromDays(1) : TimeSpan.Zero), "fixture-authority"));
        }
    }
    private sealed class Price : IModelUsageCostEstimator
    {
        internal bool Missing;
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => Missing ? null : .1m;
    }
    private sealed class IdentifiedClient(KeyVaultBenchmarkModel model) : ILLMClient, ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string name, int outputTokens, CancellationToken ct) => model.InputTokenAllowanceAsync(provider, name, outputTokens, ct);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string name, CancellationToken ct) => model.SupportsStructuredOutputAsync(provider, name, ct);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string name, CancellationToken ct) => model.SupportedReasoningLevelsAsync(provider, name, ct);

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            request.ClientRequestId ??= "runtime:1";
            return model.CallExecutionAsync(request, ct);
        }
    }
    private sealed class Provider(BenchmarkCampaign campaign) : ILLMClient, ILLMCapabilityResolver
    {
        internal int Calls; internal Action? AfterDispatch; internal Exception? Failure;
        internal bool LastDisableTransportRetries;
        internal TimeProvider? Time;
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(96000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            LastDisableTransportRetries = request.DisableTransportRetries;
            var record = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId!, ct))!;
            var journal = new BenchmarkHttpJournal(campaign, request.ClientRequestId!, record["input_ceiling"]!.GetValue<long>(),
                record["output_ceiling"]!.GetValue<long>(), record["cost_ceiling_eur"]!.GetValue<decimal>(), record["session_attempt_limit"]?.GetValue<int>(),
                record["construction_review_cost_headroom_eur"]?.GetValue<decimal>() ?? 0,
                record["construction_review_attempt_headroom"]?.GetValue<int>() ?? 0,
                record["effective_transport_policy"]?.AsObject(), Time);
            var state = (await journal.LoadAsync(ct))!;
            state.Attempts.Add(new() { Id = "attempt" }); await journal.SaveAsync(state, ct);
            Calls++; AfterDispatch?.Invoke();
            if (Failure is not null)
            {
                if (Failure is LLMClientException { StatusCode: { } status })
                { state.Attempts[^1].Status = status; await journal.SaveAsync(state, CancellationToken.None); }
                throw Failure;
            }
            state.Attempts[^1].Status = 200; await journal.SaveAsync(state, CancellationToken.None);
            return new() { Text = "{\"script\":\"source.raw\"}", Json = new JsonObject { ["script"] = "source.raw" },
                Usage = new JsonObject { ["input_tokens"] = 10L, ["output_tokens"] = 2L } };
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("admission-fixture-").FullName;
        internal Clock Time { get; } = new();
        internal Records Records { get; }
        internal Records RunRecords { get; }
        internal BenchmarkCampaign Campaign => new(Records, "fixture");
        internal Fixture()
        {
            Records = new(new KeyVaultRecordStore(Path.Combine(Root, "vault.db")));
            RunRecords = new(new KeyVaultRecordStore(Path.Combine(Root, "runs.db")));
        }
        internal KeyVaultBenchmarkModel Model(Rates rates, Provider? provider = null)
        {
            provider ??= new Provider(Campaign); provider.Time = Time;
            return new(Campaign, Options(), provider, rates, new Price(), Time);
        }
        internal EncryptedWorkflowRunStore Store() => new(RunRecords, Path.Combine(Root, "index.db"), Path.Combine(Root, "owners"));
        internal WorkflowEngine Engine(ILLMClient client) => new() { LLMClient = client, RunStore = Store(), LlmDefaults = new() { Model = "pinned" }, Limits = new() { TenantId = "tenant", RunId = "run" } };
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Records(IKeyVaultRecordStore inner) : IKeyVaultRecordStore
    {
        internal string? FailCollection;
        internal string? FailContent;
        internal int MutationAttempts;
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default) => inner.GetAsync(c, t, k, a, ct);
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default)
        {
            MutationAttempts++;
            return c == FailCollection || FailContent is not null && v.Contains(FailContent, StringComparison.Ordinal)
                ? throw new IOException("Injected persistence failure") : inner.UpsertAsync(c, t, k, v, a, ct);
        }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default) => inner.ListAsync(c, t, a, ct);
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default)
        { MutationAttempts++; return inner.DeleteAsync(c, t, k, a, ct); }
    }
}
