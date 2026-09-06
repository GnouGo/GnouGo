using Bunit;
using GnOuGo.Agent.Server.Components.Pages;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningPageTests
{
    [Fact]
    public async Task EarlyBehaviorReview_IsVisibleBeforeCode_ApprovalAndAnswersSurviveReopen()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = new PlanningSnapshot
        {
            Request = new() { TenantId = "planning-tests", Prompt = "Return a message", Name = "early-review" },
            Status = PlanningStatus.BehaviorReview, CurrentPhase = PlanningPhase.Behavior,
            Preparation = new() { AllowedStepTypes = ["set"] },
            BehaviorPlan = new() { Summary = "Return a greeting", Workflows = [new() { Key = "main", Purpose = "Return the requested greeting", Steps = [new() { Key = "greeting", Purpose = "Return a message" }], Outputs = [new("message", "A readable greeting", true)] }] },
            Answers = [new("Which greeting?", new() { ["greeting"] = "Hello" })], ClarificationForms = 1, ClarificationQuestions = 1
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
        Assert.Equal(PlanningStatus.Generating, restored.Status); Assert.Single(restored.Answers); Assert.Equal(1, restored.ClarificationQuestions);
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
        page.WaitForAssertion(() => Assert.Equal("first YAML", page.Find("textarea[aria-label='Workflow YAML']").GetAttribute("value")));
        page.Render(parameters => parameters.Add(p => p.SessionId, second.Request.SessionId));
        page.WaitForAssertion(() =>
        {
            Assert.Equal("second YAML", page.Find("textarea[aria-label='Workflow YAML']").GetAttribute("value"));
            Assert.DoesNotContain("first YAML", page.Markup);
            Assert.Equal("Second", page.Find("h2").TextContent);
        });
        Assert.True(context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "GnOuGo.Agent.markdown.enhance") >= 2);
    }

    private static PlanningSnapshot Session(string name, string yaml) => new()
    {
        Request = new() { Name = name, TenantId = "planning-tests", Prompt = "Return a greeting" },
        Status = PlanningStatus.FinalReview, Revision = 4, Yaml = yaml, ArtifactHash = PlanningGraphCompiler.Fingerprint(yaml),
        Graph = new() { Summary = "Behavior for " + name, Workflows = [new() { Key = "main", Purpose = name, Steps = [new() { Key = "step", Type = "set", Purpose = "Return greeting" }] }] }
    };
}
