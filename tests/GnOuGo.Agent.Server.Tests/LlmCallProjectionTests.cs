using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LlmCallProjectionTests
{
    [Fact]
    public void ExactOwnerAndRequestJoinOnceAcrossTracesWithoutInventingJournalUsageOrAcceptance()
    {
        var now = DateTimeOffset.UtcNow;
        var journal = new LlmCallProjection("tenant", "session", false, "session:1:hash", "session:session:1:hash", now,
            "provider", "model", "construction_draft", "block", "Response received", "invalid", "Unavailable", null, null);
        TraceGroupDto Trace(string trace, string tenant = "tenant", string session = "session") => new(trace, now, now,
            [new("span", null, "gen_ai.generate", 3, now, now, 0, 1, null, new()
            { ["tenant.id"] = tenant, ["gnougo.planning.session_id"] = session, ["gnougo.llm.request_id"] = "session:1:hash",
                ["gnougo.llm.call_id"] = "call", ["gen_ai.usage.input_tokens"] = 999 }, [], [], [])]);
        var joined = Assert.Single(LlmCallProjection.Join([journal], [Trace("one"), Trace("two"), Trace("foreign", "other")], "tenant", "session", false));
        Assert.NotNull(joined.Span); Assert.Null(joined.InputTokens); Assert.Equal("invalid", joined.TechnicalValidation);
        Assert.Equal("Unavailable", joined.BusinessReview);
        var fallback = Assert.Single(LlmCallProjection.Join([journal], [], "tenant", "session", false));
        Assert.Null(fallback.Span); Assert.Null(fallback.TraceId); Assert.Contains("telemetry absent", fallback.Source);
        Assert.Empty(LlmCallProjection.Join([], [Trace("foreign", "tenant", "other")], "tenant", "session", false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarningAuthorityPreservesConfiguredAllocationsIncludingLargeExplicitOverrides(bool large)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var settings = new TypedWorkflowPlanningSettings { BackgroundProcessingEnabled = false };
        Assert.False(settings.DiagnosticModelMaximum);
        if (large) settings.PhasePolicy = new()
        {
            Navigation = new() { TargetInputTokens = 3000, MaxInputTokens = 6000, OutputTokens = 70000 },
            Draft = new() { TargetInputTokens = 8000, MaxInputTokens = 16000, OutputTokens = 90000 },
            Review = new() { TargetInputTokens = 4000, MaxInputTokens = 8000, OutputTokens = 80000 }
        };
        var policy = new PlanningHostPolicy(fixture.Records, Options.Create(settings));
        using var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(),
            settings: settings, hostPolicy: policy, runtimeOptions: DiagnosticPlanningTests.ModelOptions());
        var state = await service.StartAsync("configured", "Return the required public result", false, ct, thresholdMode: "diagnostic", diagnosticGrant: new("operator"));
        var phases = PlanningConstructionPhasePolicies.Read(state.Request)!;
        Assert.Equal(large ? 70000 : 8192, phases.Navigation.OutputTokens);
        Assert.Equal(large ? 90000 : 8192, phases.Draft.OutputTokens);
        Assert.Equal(large ? 80000 : 2048, phases.Review.OutputTokens);
        var before = state.Request.Options.ToJsonString();
        settings.DiagnosticModelMaximum = true; // Later defaults never recapture a historical session.
        var restored = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, ct))!;
        await policy.VerifyAsync(restored, ct);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(before), restored.Request.Options));
    }
}
