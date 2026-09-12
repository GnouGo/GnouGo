using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using GnOuGo.Agent.Server.Components.Pages;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningRecoveryTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task RecoveryHostOnlyAdvancesTheExplicitlySubmittedSession()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var calls = 0;
        var planner = new PlanningSessionLifecycleTests.DelegatePlanner((state, _, _, _) =>
        { calls++; state.Revision++; state.Status = PlanningStatus.Stopped; return Task.FromResult(state); });
        using var service = PlanningSessionLifecycleTests.Create(fixture, planner, PlanningSessionLifecycleTests.AgentCatalog(), settings: new() { BackgroundProcessingEnabled = false });
        var target = await service.StartAsync("target", "Plan target", false, Ct);
        var other = await service.StartAsync("other", "Plan other", false, Ct);
        await service.StartAsync(Ct); await service.ExecuteTask!;
        Assert.Equal(0, calls);
        await service.SubmitAsync(target.Request.SessionId, new() { Kind = "advance", ExpectedRevision = target.Revision }, Ct);
        Assert.Equal(1, calls); Assert.Equal(PlanningStatus.Created, (await service.GetAsync(other.Request.SessionId, Ct))!.Status);
        await service.StopAsync(Ct);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(101, 101)]
    public async Task ConfiguredModelLimitRetainsHistoricalUsage(int limit, int expectedCalls)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = new PlanningSnapshot { Request = new() { TenantId = "planning-tests", Prompt = "Return a greeting", Name = "retained" }, Usage = new() { StartedAtUtc = DateTimeOffset.UtcNow, Calls = 100, EstimatedCostCurrency = "EUR" } };
        state.Request.Options["generator"] = new JsonObject { ["provider"] = "openai", ["model"] = "gpt-4o-mini" };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(),
            new IntentClient(IntentClarificationFixture.Ready()), new() { BackgroundProcessingEnabled = false, MaxModelCalls = limit });
        var resumed = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "advance", ExpectedRevision = state.Revision }, Ct);
        Assert.True(expectedCalls == resumed.Usage!.Calls, string.Join("; ", resumed.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(limit > 100, resumed.Intent.Checked);
        Assert.Equal(expectedCalls, (await service.GetAsync(state.Request.SessionId, Ct))!.Usage!.Calls);
    }

    [Fact]
    public async Task TypedQuestionRequiresAnAnswerAndRejectsDuplicateSubmissionAfterRestart()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = new PlanningSnapshot { Status = PlanningStatus.Clarification, Request = new() { TenantId = "planning-tests", Prompt = "Choose the requested business outcome", Name = "clarification" } };
        var schema = JsonNode.Parse("""{"type":"object","properties":{"choice":{"type":"string","enum":["first","second"]}},"required":["choice"],"additionalProperties":false}""")!.AsObject();
        var reference = new PlanningReference("owned", state.Request.TenantId + ":" + state.Request.SessionId, state.Revision,
            "request", PlanningGraphCompiler.Fingerprint(state.Request.Prompt), "user_request", 0, state.Request.Prompt.Length);
        state.References.Add(reference);
        state.Preparation = new() { Fingerprint = "catalog" };
        var fingerprint = PlanningGraphCompiler.Fingerprint(state.Request.Prompt + ":" +
            JsonSerializer.Serialize(state.Intent.Answers, PlanningJsonContext.Default.ListPlanningAnswer) + ":" +
            JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.PlanningGraph) + ":" + state.Request.Options["policy"]?.ToJsonString() + ":" + state.Preparation.Fingerprint);
        state.BusinessDecisions.Add(new() { Id = "choice", SubjectReference = reference.Id, EvidenceReferences = [reference.Id], Status = "eligible",
            DependencyFingerprint = fingerprint, Alternatives = [new() { Id = "first", Label = "First business outcome", EvidenceReference = reference.Id },
                new() { Id = "second", Label = "Second business outcome", EvidenceReference = reference.Id }] });
        state.Outcome = new PlanningNeedUserClarification(new("choice", [reference.Id], schema, ["requested_outcome"])
        { Question = "Choose the business outcome", DependencyFingerprint = fingerprint,
            Choices = [new("first", "First business outcome", false, null, [reference.Id]),
                new("second", "Second business outcome", true, "Matches the declared preference.", [reference.Id])] });
        state.Intent.Forms = state.Intent.Questions = 1;
        state.Intent.Question = new() { Prompt = "Choose the business outcome", Mode = "form", Fields = [new() { Name = "choice", Type = "radio", Required = true, Description = "Desired outcome",
            Options = ["first", "second"] }] };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), settings: new() { BackgroundProcessingEnabled = false });
        await using var context = Context(service);
        var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
        Assert.Empty(page.FindAll("input[type=radio][checked]")); Assert.True(Button(page, "Submit answers").HasAttribute("disabled"));
        Assert.Equal(2, page.FindAll("input[type=radio]").Count);
        Assert.Contains("First business outcome", page.Markup); Assert.Contains("Matches the declared preference.", page.Markup);
        Assert.Equal(2, PlanningEndpoints.ToDto(state).Outcome!.Decision!.Choices.Count);
        page.Find("input[type=radio][value=second]").Change(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "second" });
        Assert.Equal("", page.Find("input.form-control").GetAttribute("value") ?? "");
        var result = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = new JsonObject { ["choice"] = "first" } }, Ct);
        Assert.Single(result.Intent.Answers); Assert.Equal(1, result.Intent.Forms); Assert.Null(result.ApprovedHash);
        using var reopened = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), settings: new() { BackgroundProcessingEnabled = false });
        Assert.Single((await reopened.GetAsync(state.Request.SessionId, Ct))!.Intent.Answers);
        await Assert.ThrowsAsync<PlanningConflictException>(() => reopened.SubmitAsync(state.Request.SessionId, new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = new JsonObject { ["choice"] = "second" } }, Ct));
    }


    [Fact]
    public async Task TechnicalStopSurvivesRestartAndRevisionKeepsHistoryBudgetAndTenantIsolation()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new IntentClient(new JsonObject());
        var state = new PlanningSnapshot { Request = new() { TenantId = "planning-tests", Prompt = "PRIVATE_RECOVERY_CONTENT_45687", Name = "recovery" } };
        state.Request.Options["generator"] = new JsonObject { ["provider"] = "openai", ["model"] = "gpt-4o-mini" };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        using (var first = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client, new() { BackgroundProcessingEnabled = false }))
            state = await first.SubmitAsync(state.Request.SessionId, new() { Kind = "advance", ExpectedRevision = state.Revision }, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(2, state.Usage!.Calls); Assert.Null(state.Outcome); Assert.NotNull(state.TechnicalStop);
        using var restarted = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client, new() { BackgroundProcessingEnabled = false });
        await using var context = Context(restarted);
        var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
        Assert.Contains("Planning stopped", page.Markup); Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Retry"));
        var edited = await restarted.SubmitAsync(state.Request.SessionId, new() { Kind = "edit_intent", Text = "Return a revised value", ExpectedRevision = state.Revision }, Ct);
        Assert.Equal(2, edited.Usage!.Calls); Assert.Single(edited.Intent.History); Assert.Empty(edited.Intent.Answers);
        Assert.Equal(2, client.Calls); Assert.Null(await fixture.Store.LoadAsync("different-tenant", state.Request.SessionId, Ct));
        foreach (var file in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(state.Request.Prompt, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
    }


    [Fact]
    public async Task CapabilityRecovery_RestoresConcreteFindingsAboveDiagramWithoutSubmittingQuestions()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = new PlanningSnapshot
        {
            Request = new() { TenantId = "planning-tests", Prompt = "Inspect a resource before running available checks.", Name = "capability-recovery" },
            Status = PlanningStatus.Stopped,
            CurrentPhase = PlanningPhase.Capabilities,
            WaitingSinceUtc = DateTimeOffset.UtcNow.AddHours(-1),
            Diagnostics = [new("conditional_decision_source_unavailable", "/preparation/matching_issues/0",
                "The directory result does not establish its contents. Declare a runtime observation.", ValidationStage: PlanningPhase.Capabilities)]
,
            Intent = new() { Checked = true }
        };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var client = new IntentClient(new JsonObject());
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client);
        await service.StartAsync(Ct);
        try
        {
            await using var context = Context(service);
            var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
            page.WaitForAssertion(() => Assert.Contains("Planning stopped", page.Markup));
            Assert.Contains("Declare a runtime observation", page.Markup);
            Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Retry"));
            Assert.NotNull(Button(page, "Cancel planning"));
            Assert.NotNull(page.Find("#plan-intent-edit"));
            Assert.Empty(page.FindAll("fieldset"));
            Assert.Equal(0, client.Calls);
            var restored = (await service.GetAsync(state.Request.SessionId, Ct))!;
            Assert.Null(restored.Outcome);
            Assert.Null(restored.Intent.Question);
            Assert.Null(await fixture.Store.LoadAsync("another-tenant", state.Request.SessionId, Ct));
        }
        finally { await service.StopAsync(Ct); }
    }

    [Fact]
    public async Task RecoveryTelemetryReportsRepairOutcome_WithoutPrivateContentOrFinalFailure()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "GnOuGo.Agent.Planning",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (stopped) stopped.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), new IntentClient(new JsonObject()));
        await service.StartAsync(Ct);
        try
        {
            var started = await service.StartAsync("telemetry", "PRIVATE_TELEMETRY_REQUEST_78231", false, Ct);
            await PlanningSessionLifecycleTests.WaitForStatus(service, started.Request.SessionId, PlanningStatus.Stopped);
            Activity[] activities;
            lock (stopped) activities = stopped.Where(a => a.GetTagItem("gnougo.planning.session_id") as string == started.Request.SessionId).ToArray();
            var span = Assert.Single(activities);
            Assert.Equal(ActivityStatusCode.Ok, span.Status);
            Assert.Null(span.GetTagItem("gnougo.planning.outcome"));
            Assert.Equal(2, span.GetTagItem("gnougo.planning.version"));
            Assert.Equal("intent", span.GetTagItem("gnougo.planning.phase"));
            Assert.Null(span.GetTagItem("gnougo.planning.outcome"));
            Assert.All(span.Tags, tag => Assert.DoesNotContain(started.Request.Prompt, tag.Value ?? ""));
        }
        finally { await service.StopAsync(Ct); }
    }

    [Fact]
    public async Task RetainedBehaviorFailureCannotRetryOrApproveAnUnvalidatedCandidate()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = BehaviorFailure(); Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var client = new IntentClient(new JsonObject());
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client);
        await service.StartAsync(Ct);
        try
        {
            await using var context = Context(service);
            var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
            page.WaitForAssertion(() => Assert.Contains("Unvalidated behavior candidate", page.Markup));
            Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Retry") || b.TextContent == "Accept behavior and generate");
            var retained = (await service.GetAsync(state.Request.SessionId, Ct))!;
            Assert.Equal(state.Revision, retained.Revision); Assert.Null(retained.ApprovedHash); Assert.Equal(0, client.Calls);
            await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync(state.Request.SessionId, new() { Kind = "retry", ExpectedRevision = state.Revision }, Ct));
            Assert.Null(await fixture.Store.LoadAsync("different-tenant", state.Request.SessionId, Ct));
        }
        finally { await service.StopAsync(Ct); }
    }


    [Fact]
    public async Task BehaviorStopRevisionPreservesTheConsumedAllowance()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = BehaviorFailure(); state.Status = PlanningStatus.Stopped; state.BehaviorAssessmentCalls = 2;
        state.RepairAllowances.Add(new() { WorkflowKey = "main", Gate = "behavior", Attempts = 1 });
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), settings: new() { BackgroundProcessingEnabled = false });
        await using var context = Context(service);
        var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
        Assert.Contains("Planning stopped", page.Markup); Assert.NotNull(page.Find("#plan-intent-edit"));
        var edited = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "edit_intent", Text = "Return a revised value", ExpectedRevision = state.Revision }, Ct);
        Assert.Null(edited.Graph); Assert.Null(edited.Preparation); Assert.Equal(1, Assert.Single(edited.RepairAllowances).Attempts);
        Assert.Equal(2, edited.Intent.Forms); Assert.Equal(7, edited.Intent.Questions); Assert.Single(edited.Intent.History);
    }

    private static PlanningSnapshot BehaviorFailure() => new()
    {
        Request = new()
        {
            TenantId = "planning-tests",
            Prompt = "Return a message",
            Name = "behavior-recovery",
            Options = new JsonObject { ["generator"] = new JsonObject { ["provider"] = "openai", ["model"] = "gpt-4o-mini" } }
        },
        Status = PlanningStatus.Failed,
        CurrentPhase = PlanningPhase.Behavior,
        Diagnostics = [new("PLANNING_FAILED", "$", "The authoritative schema reference is unresolved.")],
        Preparation = new()
        {
            AllowedStepTypes = ["set"],
            RuntimeState = JsonNode.Parse("""
            {"discoveredServers":[],"constraints":[],"capabilities":[
              {"id":"declared","description":"Return a message","required":false,"resolution":"local","server":null,"kind":null,"method":null,"requestBindings":[],"operationIds":[]}
            ]}
            """)!.AsObject(),
            Capabilities = [new() { Id = "declared", StepType = "set", OperationIds = ["declared"],
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false}""")!.AsObject() }]
        },
        BehaviorPlan = new()
        {
            Summary = "Return a message",
            Workflows = [new() { Key = "main", Purpose = "Return a message",
            Steps = [new() { Key = "value", Purpose = "Return a message", InputDependencies = [] }], Outputs = [new("message", "The returned message", true)] }]
        }

,
        Intent = new() { Checked = true, Forms = 2, Questions = 7 }
    };

    private static BunitContext Context(GnOuGo.Agent.Server.Planning.PlanningSessionService service)
    {
        var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(service);
        return context;
    }
    private static AngleSharp.Dom.IElement Button(IRenderedComponent<PlanningPage> page, string text)
        => Assert.Single(page.FindAll("button"), button => button.TextContent == text);
    private sealed class IntentClient(params JsonNode[] responses) : ILLMClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var json = responses[Math.Min(Interlocked.Increment(ref _calls) - 1, responses.Length - 1)].DeepClone();
            if (json["outcome"]?.ToString() == "ready")
                json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                    new JsonArray(new JsonObject { ["start"] = p.Value!["items"]!["properties"]!["start"]!["enum"]![0]!.DeepClone(),
                        ["end"] = p.Value!["items"]!["properties"]!["end"]!["enum"]!.AsArray()[^1]!.DeepClone(), ["kind"] = "local_processing", ["required"] = true }))));
            return Task.FromResult(new LLMResponse { Json = json, Text = json.ToJsonString(), Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
}
