using Bunit;
using GnOuGo.Agent.Server.Components.Pages;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningPageTests
{
    [Fact]
    public async Task WorkflowProgressSurvivesPersistenceAndIsNeverPresentedAsValidated()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = Session("Partial construction", ""); state.Status = PlanningStatus.Stopped; state.CurrentPhase = PlanningPhase.Construction;
        state.Yaml = null; state.ArtifactHash = null;
        state.Construction.Workflows = [new() { WorkflowKey = "main", Status = "constructed", Calls = 2 }];
        Assert.True(await fixture.Store.TrySaveAsync(state, null, ct));
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        await using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddSingleton(service);
        var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
        page.WaitForAssertion(() => Assert.Contains("Validated workflows: 0 / 1", page.Markup));
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Approve"));
        var restored = (await service.GetAsync(state.Request.SessionId, ct))!;
        var dto = GnOuGo.Agent.Server.Planning.PlanningEndpoints.ToDto(restored);
        Assert.Equal("constructed", dto.Workflows![0].Status); Assert.Equal(2, dto.Workflows[0].Calls);
        Assert.Equal(2, restored.Construction.Workflows[0].Calls); Assert.Null(restored.ApprovedHash);
    }

    [Fact]
    public async Task RecoveryGenerationSettingsAndWorkflowsSurviveRestartWithoutChangingApproval()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = Session("Paused generation", ""); state.Status = PlanningStatus.Stopped; state.CurrentPhase = PlanningPhase.Construction;
        state.ApprovedBehaviorHash = "accepted-behavior"; state.ApprovedHash = "old-artifact";
        state.Intent.Answers = [new("Which outcome?", new() { ["choice"] = "retain" })]; state.Intent.Forms = 1;
        state.Request.Options["generator"] = new System.Text.Json.Nodes.JsonObject { ["model"] = "configured-model", ["reasoning"] = "medium" };
        state.Construction.Workflows = [new() { WorkflowKey = "main", Status = "validated", Calls = 2, RepairCalls = 1, GraphFingerprint = "receipt" }];
        Assert.True(await fixture.Store.TrySaveAsync(state, null, ct));
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        await using (var context = new BunitContext())
        {
            context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddSingleton(service);
            var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
            page.WaitForAssertion(() => Assert.Contains("configured-model", page.Markup));
            page.Find("select[aria-label='Routine reasoning']").Change("low");
            Assert.Single(page.FindAll("button"), b => b.TextContent == "Apply generation settings").Click();
            page.WaitForAssertion(() => Assert.Contains("Reasoning: routine low", page.Markup));
        }
        using var reopened = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        var restored = (await reopened.GetAsync(state.Request.SessionId, ct))!;
        Assert.Equal("low", restored.Request.Generation.ReasoningProfile.Routine); Assert.Equal(state.ApprovedBehaviorHash, restored.ApprovedBehaviorHash);
        Assert.Null(restored.ApprovedHash); Assert.Single(restored.Intent.Answers); Assert.Single(restored.GenerationHistory);
        Assert.Equal(2, restored.Construction.Workflows[0].Calls); Assert.Equal(1, restored.Construction.Workflows[0].RepairCalls);
        Assert.Null(await fixture.Store.LoadAsync("another-tenant", state.Request.SessionId, ct));
        await Assert.ThrowsAsync<PlanningConflictException>(() => reopened.SubmitAsync(state.Request.SessionId,
            new() { Kind = "configure_generation", ExpectedRevision = state.Revision, Generation = new() { ReasoningProfile = new() { Routine = "high" } } }, ct));
    }

    [Fact]
    public async Task EarlyBehaviorReview_IsVisibleBeforeCode_ApprovalAndAnswersSurviveReopen()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = new PlanningSnapshot
        {
            Request = new() { TenantId = "planning-tests", Prompt = "Return a message", Name = "early-review" },
            Status = PlanningStatus.BehaviorReview,
            CurrentPhase = PlanningPhase.Behavior,
            Preparation = new() { AllowedStepTypes = ["set"] },
            BehaviorPlan = new() { Summary = "Return a greeting", Workflows = [new() { Key = "main", Purpose = "Return the requested greeting", Steps = [new() { Key = "greeting", Purpose = "Return a message" }], Outputs = [new("message", "A readable greeting", true)] }] },
            Intent = new() { Answers = [new("Which greeting?", new() { ["greeting"] = "Hello" })], Forms = 1, Questions = 1 }
        };
        state.ArtifactHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        Assert.True(await fixture.Store.TrySaveAsync(state, null, ct));
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        await using (var context = new BunitContext())
        {
            context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddSingleton(service);
            var page = context.Render<PlanningPage>(p => p.Add(x => x.SessionId, state.Request.SessionId));
            page.WaitForAssertion(() => Assert.Contains("A readable greeting", page.Markup));
            Assert.Contains("Which greeting?", page.Markup);
            Assert.Empty(page.FindAll("textarea[aria-label='Workflow YAML']"));
            var accept = Assert.Single(page.FindAll("button"), b => b.TextContent == "Accept behavior and generate");
            accept.Click();
            page.WaitForAssertion(() => Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent == "Accept behavior and generate"));
        }
        using var reopened = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        var restored = (await reopened.GetAsync(state.Request.SessionId, ct))!;
        Assert.Equal(state.ArtifactHash, restored.ApprovedBehaviorHash); Assert.Null(restored.ApprovedHash);
        Assert.Equal(PlanningStatus.Generating, restored.Status); Assert.Single(restored.Intent.Answers); Assert.Equal(1, restored.Intent.Questions);
        Assert.Null(await fixture.Store.LoadAsync("different-tenant", state.Request.SessionId, ct));
        await Assert.ThrowsAsync<PlanningConflictException>(() => reopened.SubmitAsync(state.Request.SessionId, new() { Kind = "accept_behavior", ExpectedRevision = state.Revision, ArtifactHash = state.ArtifactHash }, ct));
    }

    [Fact]
    public async Task NavigationBetweenEqualRevisions_ShowsTheSelectedSessionsYamlAndDiagram()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var first = Session("First", "first YAML");
        var second = Session("Second", "second YAML");
        Assert.True(await fixture.Store.TrySaveAsync(first, null, Xunit.TestContext.Current.CancellationToken));
        Assert.True(await fixture.Store.TrySaveAsync(second, null, Xunit.TestContext.Current.CancellationToken));
        using var service = PlanningSessionLifecycleTests.Create(fixture, new TypedWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(service);
        var page = context.Render<PlanningPage>(parameters => parameters.Add(p => p.SessionId, first.Request.SessionId));
        page.WaitForAssertion(() => Assert.Equal("first YAML", page.Find("textarea[aria-label='Workflow YAML']").TextContent));
        page.Render(parameters => parameters.Add(p => p.SessionId, second.Request.SessionId));
        page.WaitForAssertion(() =>
        {
            Assert.Equal("second YAML", page.Find("textarea[aria-label='Workflow YAML']").TextContent);
            Assert.DoesNotContain("first YAML", page.Markup);
            Assert.Equal("Second", page.Find("h2").TextContent);
        });
        Assert.True(page.Find("textarea[aria-label='Workflow YAML']").HasAttribute("readonly"));
        Assert.True(context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "GnOuGo.Agent.markdown.enhance") >= 2);
    }

    private static PlanningSnapshot Session(string name, string yaml) => new()
    {
        Request = new() { Name = name, TenantId = "planning-tests", Prompt = "Return a greeting" },
        Status = PlanningStatus.FinalReview,
        Revision = 4,
        Yaml = yaml,
        ArtifactHash = PlanningGraphCompiler.Fingerprint(yaml),
        Graph = new() { Summary = "Behavior for " + name, Workflows = [new() { Key = "main", Purpose = name, Steps = [new() { Key = "step", Type = "set", Purpose = "Return greeting" }] }] }
    };
}
