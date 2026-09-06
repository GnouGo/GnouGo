using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using GnOuGo.Agent.Server.Components.Pages;
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
    public async Task RepairedQuestions_RenderWithoutDefaultAnswers_SubmitAndResume_RejectDuplicateSubmission()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new IntentClient(IntentClarificationFixture.QuotedQuestions(), IntentClarificationFixture.Questions(), IntentClarificationFixture.Ready());
        // Intent uses the actual planner and encrypted model journal. Stop at the next
        // phase boundary: catalog/generation correctness is covered by planner tests.
        var planner = new PlanningSessionLifecycleTests.DelegatePlanner(async (state, command, runtime, ct) =>
        {
            if (state.IntentChecked && command.Kind == "advance")
            {
                state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
                state.Revision++; state.Status = PlanningStatus.BehaviorReview; state.CurrentPhase = PlanningPhase.Behavior;
                return state;
            }
            return await new TypedWorkflowPlanner().AdvanceAsync(state, command, runtime, ct);
        });
        using var service = PlanningSessionLifecycleTests.Create(fixture, planner, PlanningSessionLifecycleTests.AgentCatalog(), client);
        await service.StartAsync(Ct);
        try
        {
            var started = await service.StartAsync("clarification-ui", IntentClarificationFixture.Prompt, false, Ct);
            await PlanningSessionLifecycleTests.WaitForStatus(service, started.Request.SessionId, PlanningStatus.Clarification);
            var pending = (await service.GetAsync(started.Request.SessionId, Ct))!;
            await using (var context = Context(service))
            {
                var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, started.Request.SessionId));
                page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("fieldset").Count));
                Assert.Contains("Planner v2", page.Find("[aria-label='Planner version']").TextContent);
                Assert.Contains("Understanding your request", page.Find("[aria-label='Current planning phase']").TextContent);
                Assert.Empty(page.FindAll("input[type=radio][checked]"));
                Assert.True(Button(page, "Submit answers").HasAttribute("disabled"));
                Assert.Empty((await service.GetAsync(started.Request.SessionId, Ct))!.Answers);
            }
            // Reconnection creates a fresh component; pending questions stay unanswered.
            await using (var context = Context(service))
            {
                var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, started.Request.SessionId));
                page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("fieldset").Count));
                Assert.True(Button(page, "Submit answers").HasAttribute("disabled"));
                for (var index = 0; index < 3; index++) page.Find($"input[name='choice_{index}'][value=first]").Change("first");
                Assert.False(Button(page, "Submit answers").HasAttribute("disabled"));
                Button(page, "Submit answers").Click();
                await PlanningSessionLifecycleTests.WaitForStatus(service, started.Request.SessionId, PlanningStatus.BehaviorReview);
            }
            var resumed = (await service.GetAsync(started.Request.SessionId, Ct))!;
            Assert.Equal(3, Assert.Single(resumed.Answers).Answers.Count);
            Assert.Equal(1, resumed.ClarificationForms);
            Assert.Equal(3, resumed.ClarificationQuestions);
            Assert.Equal(3, client.Calls);
            Assert.Equal(3, resumed.Usage!.Calls);
            await Assert.ThrowsAsync<PlanningConflictException>(() => service.SubmitAsync(started.Request.SessionId,
                new() { Kind = "answer", ExpectedRevision = pending.Revision, Answers = new JsonObject { ["choice_0"] = "second" } }, Ct));
        }
        finally { await service.StopAsync(Ct); }
    }

    [Fact]
    public async Task RecoverySurvivesRestart_EditAndRetryPreserveHistoryUsageAndTenantIsolation()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new IntentClient(new JsonObject());
        var state = new PlanningSnapshot { Request = new() { TenantId = "planning-tests", Prompt = "PRIVATE_RECOVERY_CONTENT_45687", Name = "recovery" } };
        state.Request.Options["generator"] = new JsonObject { ["provider"] = "openai", ["model"] = "gpt-4o-mini" };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        using (var first = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client))
        {
            await first.StartAsync(Ct);
            try { await PlanningSessionLifecycleTests.WaitForStatus(first, state.Request.SessionId, PlanningStatus.Recovery); }
            finally { await first.StopAsync(Ct); }
        }
        var recovery = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
        Assert.Equal(2, recovery.Usage!.Calls);
        Assert.Null(recovery.Outcome);
        Assert.Null(await fixture.Store.LoadAsync("different-tenant", state.Request.SessionId, Ct));
        var restartClient = new IntentClient(IntentClarificationFixture.Questions());
        using var restarted = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), restartClient);
        await restarted.StartAsync(Ct);
        try
        {
            await using var context = Context(restarted);
            var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
            page.WaitForAssertion(() => Assert.Contains("Waiting for your recovery choice", page.Markup));
            Assert.Equal(0, restartClient.Calls);
            Assert.Equal(recovery.Revision, (await restarted.GetAsync(state.Request.SessionId, Ct))!.Revision);
            Assert.NotNull(Button(page, "Retry from the retained plan"));
            Assert.NotNull(Button(page, "Cancel planning"));
            page.Find("#plan-intent-edit").Change(IntentClarificationFixture.Prompt);
            Button(page, "Save request and retry").Click();
            await PlanningSessionLifecycleTests.WaitForStatus(restarted, state.Request.SessionId, PlanningStatus.Clarification);
            var edited = (await restarted.GetAsync(state.Request.SessionId, Ct))!;
            Assert.Equal(state.Request.SessionId, edited.Request.SessionId);
            Assert.Equal(IntentClarificationFixture.Prompt, edited.Request.Prompt);
            Assert.Equal(3, edited.Question!.Fields!.Count);
            Assert.Equal(3, edited.Usage!.Calls);
            Assert.NotEmpty(Assert.Single(edited.IntentHistory).Diagnostics);
            Assert.Empty(edited.Diagnostics);
            Assert.Equal(1, edited.ClarificationForms);
            Assert.Equal(3, edited.ClarificationQuestions);
            await Assert.ThrowsAsync<PlanningConflictException>(() => restarted.SubmitAsync(state.Request.SessionId,
                new() { Kind = "retry", ExpectedRevision = recovery.Revision }, Ct));
        }
        finally { await restarted.StopAsync(Ct); }
        foreach (var file in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(state.Request.Prompt, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
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
            await PlanningSessionLifecycleTests.WaitForStatus(service, started.Request.SessionId, PlanningStatus.Recovery);
            Activity[] activities;
            lock (stopped) activities = stopped.Where(a => a.GetTagItem("gnougo.planning.session_id") as string == started.Request.SessionId).ToArray();
            var span = Assert.Single(activities);
            Assert.Equal(ActivityStatusCode.Ok, span.Status);
            Assert.Equal("exhausted", span.GetTagItem("gnougo.planning.repair.outcome"));
            Assert.Equal(2, span.GetTagItem("gnougo.planning.version"));
            Assert.Equal("intent", span.GetTagItem("gnougo.planning.phase"));
            Assert.Null(span.GetTagItem("gnougo.planning.outcome"));
            Assert.All(span.Tags, tag => Assert.DoesNotContain(started.Request.Prompt, tag.Value ?? ""));
        }
        finally { await service.StopAsync(Ct); }
    }

    [Fact]
    public async Task RetainedBehaviorFailure_RestartAndUiRetryReachReviewWithoutApproving()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = BehaviorFailure();
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var repaired = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
        repaired.Workflows[0].Outputs[0].Schema.SchemaPointer = "/output/properties/message";
        var client = new IntentClient(JsonSerializer.SerializeToNode(repaired, PlanningJsonContext.Default.PlanningGraph)!);
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client);
        await service.StartAsync(Ct);
        try
        {
            await using var context = Context(service);
            var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
            page.WaitForAssertion(() => Assert.Contains("Unvalidated behavior candidate", page.Markup));
            Assert.Equal(0, client.Calls);
            Assert.NotNull(page.Find("#plan-intent-edit"));
            Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent == "Accept behavior and generate");
            Button(page, "Retry from the retained plan").Click();
            await PlanningSessionLifecycleTests.WaitForStatus(service, state.Request.SessionId, PlanningStatus.BehaviorReview);
            var reviewed = (await service.GetAsync(state.Request.SessionId, Ct))!;
            page.WaitForAssertion(() => Assert.NotNull(Button(page, "Accept behavior and generate")), TimeSpan.FromSeconds(5));
            Assert.Null(reviewed.ReviewedGraph); Assert.Null(reviewed.ApprovedHash); Assert.Null(reviewed.Yaml);
            Assert.Equal(1, client.Calls); Assert.Equal(1, reviewed.Usage!.Calls);
            Assert.Equal(2, reviewed.ClarificationForms); Assert.Equal(7, reviewed.ClarificationQuestions);
            Assert.Equal(PlanningPhase.Behavior, reviewed.CurrentPhase);
            Assert.Empty(reviewed.Diagnostics);
            await Assert.ThrowsAsync<PlanningConflictException>(() => service.SubmitAsync(state.Request.SessionId,
                new() { Kind = "accept_behavior", ExpectedRevision = state.Revision, ArtifactHash = reviewed.ArtifactHash }, Ct));
            Assert.Null(await fixture.Store.LoadAsync("different-tenant", state.Request.SessionId, Ct));
        }
        finally { await service.StopAsync(Ct); }
    }

    [Fact]
    public async Task BehaviorRecovery_RetainsCandidateAndRepairBudgetAcrossRestart_AndAllowsUiEdit()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = BehaviorFailure(); state.Status = PlanningStatus.Recovery; state.BehaviorAssessmentCalls = 2;
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var client = new IntentClient(IntentClarificationFixture.Questions());
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), client);
        await service.StartAsync(Ct);
        try
        {
            await using var context = Context(service);
            var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
            page.WaitForAssertion(() => Assert.Contains("Automatic repair stopped before your behavior review", page.Markup));
            Assert.Equal(0, client.Calls);
            Assert.Equal(2, (await service.GetAsync(state.Request.SessionId, Ct))!.BehaviorAssessmentCalls);
            page.Find("#plan-intent-edit").Change(IntentClarificationFixture.Prompt);
            Button(page, "Save request and retry").Click();
            await PlanningSessionLifecycleTests.WaitForStatus(service, state.Request.SessionId, PlanningStatus.Clarification);
            var edited = (await service.GetAsync(state.Request.SessionId, Ct))!;
            Assert.Null(edited.Graph); Assert.Null(edited.Preparation);
            Assert.Equal(3, edited.ClarificationForms); Assert.Equal(10, edited.ClarificationQuestions);
            Assert.Single(edited.IntentHistory); Assert.Empty(edited.Diagnostics);
            Assert.Equal(1, client.Calls);
        }
        finally { await service.StopAsync(Ct); }
    }

    private static PlanningSnapshot BehaviorFailure() => new()
    {
        Request = new() { TenantId = "planning-tests", Prompt = "Return a message", Name = "behavior-recovery",
            Options = new JsonObject { ["generator"] = new JsonObject { ["provider"] = "openai", ["model"] = "gpt-4o-mini" } } },
        Status = PlanningStatus.Failed, CurrentPhase = PlanningPhase.Behavior, IntentChecked = true,
        ClarificationForms = 2, ClarificationQuestions = 7,
        Diagnostics = [new("PLANNING_FAILED", "$", "The authoritative schema reference is unresolved.")],
        Preparation = new() { AllowedStepTypes = ["set"], Capabilities = [new() { Id = "declared", StepType = "set",
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false}""")!.AsObject() }] },
        Graph = new() { Summary = "Return a message", Workflows = [new() { Key = "main",
            Steps = [new() { Key = "value", Type = "set", Input = new() { Kind = "object", Members = [new("message", new() { Kind = "string", Text = "Hello" })] } }],
            Outputs = [new() { Name = "message", Schema = new() { CapabilityId = "declared", SchemaPointer = "/message" }, Value = new() { Kind = "output", Source = "value", Path = ["message"] } }] }] }
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
            return Task.FromResult(new LLMResponse { Json = json, Text = json.ToJsonString(), Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
}
