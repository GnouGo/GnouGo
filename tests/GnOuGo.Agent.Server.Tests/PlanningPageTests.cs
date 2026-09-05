using Bunit;
using GnOuGo.Agent.Server.Components.Pages;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningPageTests
{
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
