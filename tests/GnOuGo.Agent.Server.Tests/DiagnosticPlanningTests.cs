using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Planning;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class DiagnosticPlanningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("output", "strict")]
    [InlineData("output", "diagnostic")]
    [InlineData("input", "strict")]
    [InlineData("input", "diagnostic")]
    [InlineData("context", "strict")]
    [InlineData("context", "diagnostic")]
    public async Task ContradictoryMeasuredPhysicalUsageIsNeverAcceptedOrReplayed(string kind, string mode)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var settings = new TypedWorkflowPlanningSettings { BackgroundProcessingEnabled = false, DiagnosticEnabled = true, DiagnosticModelMaximum = true };
        var policy = new PlanningHostPolicy(fixture.Records, Options.Create(settings));
        using var model = new JournaledPlanningModel(new PhysicalUsageModel(kind), preserveUsage: true);
        // Zero fixture tariffs isolate physical validation from an earlier monetary
        // reservation overage; independent tests enforce the real financial guard.
        var options = ModelOptions();
        options.ModelOverrides["openai/gpt-6-luna"] = new() { Id = "gpt-6-luna", ProviderType = "openai", Pricing = new()
            { InputPer1MTokens = 0, OutputPer1MTokens = 0, CachedInputPer1MTokens = 0, CacheWriteInputPer1MTokens = 0, CacheWrite12hInputPer1MTokens = 0 } };
        using var server = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), model, settings, policy, options);
        var state = await server.StartAsync("physical-usage", "Return 42", false, Ct, thresholdMode: mode, diagnosticGrant: mode == "diagnostic" ? new("operator") : null);
        state = await server.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = state.Revision }, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Graph); Assert.Null(state.ApprovedHash);
        Assert.Contains(state.Diagnostics, d => d.Code == (mode == "diagnostic" ? "MODEL_PHYSICAL_LIMIT" : "LLM_BUDGET_EXCEEDED")); Assert.Single(model.Requests);
        Assert.NotNull(await fixture.Records.GetAsync(PlanningModelJournal.HttpCollection, "planning-tests", model.Requests[0].ClientRequestId!, EfPlanningSessionStore.Author, Ct));
        var restored = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
        Assert.Equal(state.Diagnostics, restored.Diagnostics); Assert.Single(model.Requests);
    }
    private sealed class PhysicalUsageModel(string kind) : GnOuGo.Flow.Core.Runtime.ILLMClient
    {
        public async Task<GnOuGo.Flow.Core.Runtime.LLMResponse> CallAsync(GnOuGo.Flow.Core.Runtime.LLMRequest request, CancellationToken ct)
        {
            var result = await new GnOuGo.Planning.Examples.CurrentPlanningFixture.LiteralModel().CallAsync(request, ct);
            return new() { Json = result.Json, Usage = new JsonObject { ["input_tokens"] = kind == "input" ? 1050001 : kind == "context" ? 1049000 : 10,
                ["output_tokens"] = kind == "output" ? 128001 : kind == "context" ? 5000 : 5 } };
        }
    }
    public static TheoryData<string, string> AdapterThresholds
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var category in new[] { "calls", "corrections", "tokens", "elapsed", "input", "output", "recursive_tasks", "nodes" })
                foreach (var mode in new[] { "strict", "diagnostic" }) cases.Add(category, mode);
            return cases;
        }
    }
    [Theory]
    [MemberData(nameof(AdapterThresholds))]
    public async Task IsolatedThresholdsTraverseRealServerAdmissionAndEncryptedRestart(string category, string mode)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var settings = new TypedWorkflowPlanningSettings { BackgroundProcessingEnabled = false, DiagnosticEnabled = true, DiagnosticModelMaximum = true };
        switch (category)
        {
            case "calls": settings.MaxModelCalls = 1; break;
            case "corrections": settings.MaxReplanAttempts = 0; break;
            case "tokens": settings.MaxTotalTokens = 1; break;
            case "elapsed": settings.MaxActiveMilliseconds = 1; break;
            case "input": settings.MaxInputTokensPerRequest = 512; break;
            case "output": settings.DraftOutputTokens = settings.ReviewOutputTokens = 1; break;
            case "recursive_tasks": settings.MaxReplanAttempts = 0; break;
            case "nodes": settings.ConstructionPreset = "large"; settings.LargeMaxStepsTotal = settings.LargeMaxStepsPerWorkflow = 1; settings.LargeMaxReplanAttempts = 0; break;
        }
        var policy = new PlanningHostPolicy(fixture.Records, Options.Create(settings));
        using var model = new JournaledPlanningModel(new ThresholdModel(category));
        using var server = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), model,
            settings, policy, ModelOptions());
        var state = await server.StartAsync("threshold-" + category, "Return the required public numeric result named result with value 42.", false, Ct,
            thresholdMode: mode, diagnosticGrant: mode == "diagnostic" ? new("operator") : null);
        var captured = state.Request.Options.ToJsonString();
        // New host defaults cannot replace the authenticated creation capture.
        settings.MaxModelCalls = 1; settings.MaxTotalTokens = 1;
        for (var index = 0; index < 25 && !PlanningStatus.IsTerminal(state.Status) && !PlanningStatus.IsWaiting(state.Status); index++)
        {
            state = await server.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = state.Revision }, Ct);
            state = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
            await policy.VerifyAsync(state, Ct);
        }
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(captured), state.Request.Options));
        Assert.Equal(mode == "diagnostic" ? PlanningStatus.FinalReview : PlanningStatus.Stopped, state.Status);
        Assert.Null(state.ApprovedHash);
        if (mode == "diagnostic")
        {
            Assert.All(model.Requests, request => Assert.Equal(128000, request.MaxTokens));
            if (category != "output") Assert.NotEmpty((state.ThresholdWarnings ?? []).Concat(state.Usage?.Warnings ?? []).Concat(state.RequestMeasurements?.SelectMany(m => m.Warnings ?? []) ?? []));
            if (category is "nodes" or "recursive_tasks") Assert.Equal(category == "nodes" ? 2 : 10, state.Plan!.Root.Tasks.Count);
        }
    }
    private sealed class ThresholdModel(string category) : GnOuGo.Flow.Core.Runtime.ILLMClient
    {
        private readonly GnOuGo.Planning.Examples.CurrentPlanningFixture.LiteralModel _literal = new();
        private bool _invalid;
        public async Task<GnOuGo.Flow.Core.Runtime.LLMResponse> CallAsync(GnOuGo.Flow.Core.Runtime.LLMRequest request, CancellationToken ct)
        {
            var response = await _literal.CallAsync(request, ct);
            var purpose = request.StructuredOutputSchema!["title"]!.ToString();
            if (category == "corrections" && purpose == "construction_select" && !_invalid)
            { _invalid = true; return new() { Json = new JsonObject(), Usage = response.Usage }; }
            if (category is "recursive_tasks" or "nodes" && purpose == "construction_draft")
            {
                var tasks = response.Json!["extension"]!["tasks"]!.AsArray();
                for (var index = 1; index < (category == "nodes" ? 2 : 10); index++)
                { var task = tasks[0]!.DeepClone(); task["id"] = "helper_" + index; tasks.Add(task); }
            }
            return response;
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCreationSnapshotRecoversResolvedAllocationsOnlyWithUntamperedHostEvidence(bool tamper)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var policy = new PlanningHostPolicy(fixture.Records, Options.Create(new TypedWorkflowPlanningSettings { DiagnosticEnabled = true, DiagnosticModelMaximum = true }));
        var factory = new WorkflowPlanningRuntimeFactory(fixture.Records, Path.Combine(fixture.Root, "leases"), new CreationHostPolicy(policy));
        PlanningSession Initial() => new() { Request = new() { TenantId = "planning-tests", Prompt = "Return 42" } };
        var context = new GnOuGo.Flow.Core.Runtime.StepExecutionContext
        {
            Engine = new GnOuGo.Flow.Core.Runtime.WorkflowEngine(), Data = new(),
            Limits = new() { RunId = "creation-recovery", TenantId = "planning-tests" },
            Step = new() { Source = new GnOuGo.Flow.Core.Models.StepDef { Id = "plan", Type = "workflow.plan" } }
        };
        string id;
        await using (var created = await factory.OpenAsync(context, Initial(), Ct)) id = created.Session.Request.SessionId;
        await fixture.Records.DeleteAsync("flow-planning-sessions-v11", "planning-tests", id, EfPlanningSessionStore.Author, Ct);
        if (tamper)
        {
            var definition = (await fixture.Records.GetAsync("flow-planning-definitions-v11", "planning-tests", id, EfPlanningSessionStore.Author, Ct))!;
            var node = JsonNode.Parse(definition.Value)!;
            node["creationCheckpoint"]!["session"]!["request"]!["generation"]!["maxOutputTokens"] = 32000;
            await fixture.Records.UpsertAsync("flow-planning-definitions-v11", "planning-tests", id, node.ToJsonString(), EfPlanningSessionStore.Author, Ct);
            await Assert.ThrowsAsync<PlanningConflictException>(() => factory.OpenAsync(context, Initial(), Ct));
        }
        else
        {
            await using var restored = await factory.OpenAsync(context, Initial(), Ct);
            Assert.Equal(128000, restored.Session.Request.Generation.MaxOutputTokens);
            Assert.Equal(128000, PlanningConstructionPhasePolicies.Read(restored.Session.Request)!.Review.OutputTokens);
            Assert.Equal(0, restored.Session.ModelCalls);
        }
    }
    private sealed class CreationHostPolicy(PlanningHostPolicy policy) : IPlanningHostPolicy
    {
        public Task CaptureAsync(PlanningSession session, CancellationToken ct) => policy.CaptureAsync(session, ModelOptions(), "diagnostic", new("operator"), ct);
        public Task CaptureAsync(PlanningSession session, PlanningSession creation, CancellationToken ct) => policy.CaptureAsync(session, ModelOptions(), "diagnostic", new("operator"), ct, creation);
        public Task VerifyAsync(PlanningSession session, CancellationToken ct) => policy.VerifyAsync(session, ct);
        public Task VerifyCreationAsync(PlanningSession session, CancellationToken ct) => policy.VerifyCreationAsync(session, ct);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticNeverWaivesFinancialAdmissionOrAcceptsInvalidOutput(bool invalidOutput)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var settings = new TypedWorkflowPlanningSettings { BackgroundProcessingEnabled = false, DiagnosticEnabled = true, DiagnosticModelMaximum = true };
        var policy = new PlanningHostPolicy(fixture.Records, Options.Create(settings));
        using var model = new JournaledPlanningModel(new InvalidModel());
        using var server = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), model,
            settings, policy, ModelOptions(), new() { Amount = invalidOutput ? 50 : .000001m });
        var state = await server.StartAsync("mandatory-guards", "Return 42", false, Ct, thresholdMode: "diagnostic", diagnosticGrant: new("operator"));
        state = await server.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = state.Revision }, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.ApprovedHash); Assert.Null(state.Graph);
        if (invalidOutput)
        {
            Assert.Single(model.Requests); Assert.Contains(state.Diagnostics, d => d.Code == "PLANNING_RESPONSE_INVALID");
            Assert.NotNull(await fixture.Records.GetAsync(PlanningModelJournal.Collection, "planning-tests",
                state.Request.SessionId + ":" + model.Requests[0].ClientRequestId, EfPlanningSessionStore.Author, Ct));
        }
        else { Assert.Empty(model.Requests); Assert.Contains(state.Diagnostics, d => d.Code == "LLM_BUDGET_EXCEEDED"); }
    }
    private sealed class InvalidModel : GnOuGo.Flow.Core.Runtime.ILLMClient
    {
        public Task<GnOuGo.Flow.Core.Runtime.LLMResponse> CallAsync(GnOuGo.Flow.Core.Runtime.LLMRequest request, CancellationToken ct)
            => Task.FromResult(new GnOuGo.Flow.Core.Runtime.LLMResponse { Text = "invalid response remains inspectable" });
    }
    internal static LLMOptions ModelOptions() => new()
    {
        DefaultProvider = "openai", DefaultModel = "gpt-6-luna",
        Models = { ["openai"] = new() { Type = "openai", Url = "https://example.invalid/v1" } }
    };

    [Theory]
    [InlineData("strict")]
    [InlineData("diagnostic")]
    public async Task RealServerChainRetainsThresholdPolicyAllocationsAndMoneyAcrossReload(string mode)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var settings = new TypedWorkflowPlanningSettings { BackgroundProcessingEnabled = false, DiagnosticEnabled = true, DiagnosticModelMaximum = true,
            MaxModelCalls = 1, MaxTotalTokens = 1, MaxActiveMilliseconds = 1 };
        var policy = new PlanningHostPolicy(fixture.Records, Options.Create(settings));
        using var model = new JournaledPlanningModel(new GnOuGo.Planning.Examples.CurrentPlanningFixture.LiteralModel());
        using var server = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), model,
            settings, policy, ModelOptions());
        var state = await server.StartAsync("diagnostic-" + mode, "Return the required public numeric result named result with value 42.", false, Ct,
            thresholdMode: mode, diagnosticGrant: mode == "diagnostic" ? new("test-local-operator") : null);
        var capture = state.Request.Options.ToJsonString();
        if (mode == "diagnostic")
        {
            var phases = PlanningConstructionPhasePolicies.Read(state.Request)!;
            Assert.Equal(128000, phases.Navigation.OutputTokens); Assert.Equal(128000, phases.Draft.OutputTokens); Assert.Equal(128000, phases.Review.OutputTokens);
        }
        for (var step = 0; step < 20 && !PlanningStatus.IsTerminal(state.Status) && !PlanningStatus.IsWaiting(state.Status); step++)
        {
            state = await server.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = state.Revision }, Ct);
            state = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
        }
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(capture), state.Request.Options));
        if (mode == "strict") { Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Empty(model.Requests); }
        else
        {
            Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.True(state.ModelCalls > 1);
            Assert.NotEmpty(state.ThresholdWarnings!); Assert.NotEmpty(state.InspectionEvents!); Assert.Null(state.ApprovedHash);
            Assert.All(model.Requests, request => Assert.Equal(128000, request.MaxTokens));
            Assert.Contains(state.Usage!.Warnings!, warning => warning.Kind == "calls");
            Assert.True(state.Usage.EstimatedCost < 50); Assert.Contains(state.InspectionEvents!, e => e.Phase == "ready_for_approval");
            await policy.VerifyAsync(state, Ct);
            state.Request.Options["llm_budget"]!["max_estimated_cost"]!["amount"] = 5000;
            await Assert.ThrowsAsync<PlanningConflictException>(() => policy.VerifyAsync(state, Ct));
        }
    }

    [Fact]
    public async Task GrantEnablementAndUntamperedHostRecordAreRequired()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var enabled = new PlanningHostPolicy(fixture.Records, Options.Create(new TypedWorkflowPlanningSettings { DiagnosticEnabled = true, DiagnosticModelMaximum = true }));
        var state = new PlanningSession { Request = new() { TenantId = "tenant", Prompt = "Return 42" } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => enabled.CaptureAsync(state, ModelOptions(), "diagnostic", null, Ct));
        var disabled = new PlanningHostPolicy(fixture.Records, Options.Create(new TypedWorkflowPlanningSettings { DiagnosticEnabled = false }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disabled.CaptureAsync(state, ModelOptions(), "diagnostic", new("operator"), Ct));
        state.Request.Options["optimization_threshold_policy"] = "diagnostic-v1";
        await Assert.ThrowsAsync<PlanningConflictException>(() => enabled.VerifyAsync(state, Ct));
        state.Request.Options.Clear();
        await enabled.CaptureAsync(state, ModelOptions(), "diagnostic", new("operator"), Ct);
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var restored = JsonSerializer.Deserialize(original, PlanningJsonContext.Default.PlanningSession)!;
        await enabled.VerifyAsync(restored, Ct);
        restored.Request.TenantId = "other-tenant";
        await Assert.ThrowsAsync<PlanningConflictException>(() => enabled.VerifyAsync(restored, Ct));
        restored = JsonSerializer.Deserialize(original, PlanningJsonContext.Default.PlanningSession)!;
        restored.Request.Options["construction_limits"]!["phasePolicy"]!["draft"]!["outputTokens"] = 1024;
        await Assert.ThrowsAsync<PlanningConflictException>(() => enabled.VerifyAsync(restored, Ct));
    }

    [Fact]
    public void NewCaptureFreezesResolvedStreamingAndHistoricalCaptureRemainsNonStreaming()
    {
        var options = ModelOptions();
        var provider = options.ResolveProvider(options.DefaultProvider)!;
        provider.RequestPolicy.StreamingSupport = new() { [LLMTransportResolver.ChatCompletions] = true };
        provider.ModelTransportOverrides = new() { [options.DefaultModel] = new() { StreamingSupport = new() { [LLMTransportResolver.ChatCompletions] = true } } };
        var state = new PlanningRequest();
        CapturedPlanningModel.Capture(state, options);
        var retained = state.Options.ToJsonString();
        provider.RequestPolicy.StreamingMode = LLMStreamingMode.NonStreaming;
        provider.RequestPolicy.StreamingSupport[LLMTransportResolver.ChatCompletions] = false;
        var restored = CapturedPlanningModel.Restore(options, state);
        Assert.Equal(LLMStreamingMode.Streaming, Assert.Single(restored.CapturedTransports!).Value.EffectiveMode);
        Assert.Equal(retained, state.Options.ToJsonString());
        var capture = state.Options[CapturedPlanningModel.OptionName]!.AsObject();
        capture["version"] = 1; capture.Remove("transport"); capture.Remove("modelTransportOverrides");
        capture["requestPolicy"]!.AsObject().Remove("streamingSupport");
        provider.RequestPolicy.StreamingMode = LLMStreamingMode.Streaming;
        provider.RequestPolicy.StreamingSupport[LLMTransportResolver.ChatCompletions] = true;
        retained = state.Options.ToJsonString();
        restored = CapturedPlanningModel.Restore(options, state);
        Assert.Equal(LLMStreamingMode.NonStreaming, Assert.Single(restored.CapturedTransports!).Value.EffectiveMode);
        Assert.Equal(retained, state.Options.ToJsonString());
    }

    [Fact]
    public void CapturedTariffsAndCapabilitiesIgnoreNewCatalogOverridesAndRejectRouteChanges()
    {
        var options = ModelOptions(); var state = new PlanningRequest();
        CapturedPlanningModel.Capture(state, options);
        options.ModelOverrides["openai/gpt-6-luna"] = new() { Id = "gpt-6-luna", MaxOutputTokens = 1,
            Pricing = new() { InputPer1MTokens = 1000, OutputPer1MTokens = 1000 } };
        var restored = CapturedPlanningModel.Restore(options, state);
        var captured = new LLMModelMetadataResolver(restored).Resolve("openai", "gpt-6-luna");
        Assert.Equal(128000, captured.MaxOutputTokens); Assert.Equal(.1m, captured.Pricing!.InputPer1MTokens);
        options.Models["openai"].Url = "https://another.invalid/v1";
        Assert.Throws<PlanningConflictException>(() => CapturedPlanningModel.Restore(options, state));
    }

    [Fact]
    public void OperatorBootstrapIsSingleUseAndRejectsRemoteOrForeignOrigin()
    {
        var bootstrap = new LocalOperatorBootstrap(); var token = bootstrap.Issue();
        Assert.False(bootstrap.Consume("wrong")); Assert.True(bootstrap.Consume(token)); Assert.False(bootstrap.Consume(token));
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Scheme = "http"; context.Request.Host = new("127.0.0.1:5000");
        Assert.True(LocalOperatorAccess.IsLocal(context));
        context.Request.Headers.Origin = "https://foreign.invalid"; Assert.False(LocalOperatorAccess.IsLocal(context));
        context.Request.Headers.Remove("Origin"); context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1"); Assert.False(LocalOperatorAccess.IsLocal(context));
    }

    [Fact]
    public void RedactionPreservesNonsecretEvidence()
    {
        var value = DiagnosticRedaction.Redact("""{"api_key":"secret","cookie":"session=value","authorization":"Bearer hidden","output_tokens":42,"text":"retained"}""");
        Assert.DoesNotContain("secret", value); Assert.DoesNotContain("session=value", value); Assert.DoesNotContain("hidden", value);
        Assert.Contains("42", value); Assert.Contains("retained", value);
        var headers = DiagnosticRedaction.Redact("Authorization: Basic private-credentials\nCookie: private-cookie\nhttps://private-user:private-password@example.invalid/path?sig=private-signature");
        Assert.DoesNotContain("private-", headers); Assert.Contains("example.invalid/path", headers);
    }
}
