using System.Diagnostics;
using System.Text.Json;
using Bunit;
using GnOuGo.Agent.Server.Components.Pages;
using GnOuGo.Agent.Server.Components.Tracing;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningTraceUiTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task PrerequisiteCauseAndUnappliedRepairSurvivePersistenceAndDesignerRendering()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(); Configure(fixture);
        var state = Session("repair", "repair failure", PlanningStatus.Stopped);
        state.Diagnostics = [new("SEMANTIC_BINDING_BLOCKED", "/actions/consumer", "Waiting for original evidence")
        { Prerequisite = new("blocked_dependency", "PRIVATE_PREREQUISITE_CONTEXT", ConsumerCapability: "issued", ContractPath: "/source", RootActionId: "producer") }];
        state.RevisionScope = ["producer", "consumer"];
        state.Plan = GnOuGo.Planning.Examples.PlanningCorpus.Decision(); state.Plan.Choices[0].Selected = "formal";
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var restored = (await fixture.Store.LoadAsync("planning-tests", "repair", Ct))!;
        var dto = PlanningEndpoints.ToDto(restored);
        Assert.Equal("producer", Assert.Single(dto.Diagnostics).Prerequisite!.RootActionId);
        Assert.Equal(new[] { "producer", "consumer" }, dto.RevisionScope); Assert.Single(dto.Choices);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/planning/repair");
        var cut = Render<PlanningPage>(p => p.Add(c => c.SessionId, "repair"));
        cut.WaitForAssertion(() => { Assert.Contains("Tasks open for revision", cut.Markup); Assert.Contains("Blocked by action:", cut.Markup); Assert.Contains("Selected: formal", cut.Markup); });
        foreach (var file in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("PRIVATE_PREREQUISITE_CONTEXT", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
        await DisposeComponentsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompatibleHistoryRemainsVisibleWithoutBlockingHealthySessions(bool workflow)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        Configure(fixture);
        Assert.True(await fixture.Store.TrySaveAsync(Session("healthy-designer", "healthy designer", PlanningStatus.Stopped), null, Ct));
        await StoreWorkflow(fixture, Session("healthy-chat", "healthy chat", PlanningStatus.FinalReview));
        var legacy = await PlanningHistoryTests.StoreLegacyAsync(fixture, workflow);
        var traceId = Capture("legacy", workflow, "historical planning trace");
        var cut = Render<PlanningPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("healthy designer", cut.Markup);
            Assert.Contains("healthy chat", cut.Markup);
            Assert.Contains("legacy session", cut.Markup);
            Assert.Contains("Unavailable", cut.Markup);
            Assert.DoesNotContain("could not be mapped", cut.Markup);
        });
        cut.Find("button[aria-label='Traces for legacy session']").Click();
        cut.WaitForAssertion(() => Assert.Contains(traceId, cut.Markup));
        var source = workflow ? "?source=workflow" : "";
        Services.GetRequiredService<NavigationManager>().NavigateTo("/planning/legacy" + source);
        cut.Render(p => p.Add(c => c.SessionId, "legacy"));
        cut.WaitForAssertion(() => Assert.Contains(PlanningSessionInspection.UnavailableMessage, cut.Markup));
        Assert.DoesNotContain("Approve this revision", cut.Markup);
        Assert.DoesNotContain("Retry with retained usage", cut.Markup);
        Assert.DoesNotContain("Revise workflow", cut.Markup);
        Assert.DoesNotContain("Cancel planning", cut.Markup);
        cut.Find("main button").Click();
        cut.WaitForAssertion(() => Assert.Contains(traceId, cut.Markup));
        // A second load must remain read-only, including the uncertain reservation.
        cut.Render(p => p.Add(c => c.SessionId, "legacy"));
        cut.WaitForAssertion(() => Assert.Contains("incompatible with planning format 10", cut.Markup));
        var after = await fixture.Records.GetAsync(legacy.Collection, legacy.TenantId, legacy.Key, "test", Ct);
        Assert.Equal(legacy, after);
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task ListShowsBothOriginsAndOpensSelectedSessionTraces()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        Configure(fixture);
        var designer = Session("same", "designer", PlanningStatus.Stopped);
        Assert.True(await fixture.Store.TrySaveAsync(designer, null, Ct));
        await StoreWorkflow(fixture, Session("same", "chat", PlanningStatus.Failed));
        var traceId = Capture("same", true, "recorded chat failure");
        var cut = Render<PlanningPage>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("li button").Count));
        Assert.Contains("/planning/same?source=workflow", cut.Markup);
        Assert.Contains("/planning/same\"", cut.Markup);
        cut.Find("button[aria-label='Traces for chat']").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Planning traces", cut.Markup);
            Assert.Contains("Origin: Chat", cut.Markup);
            Assert.Contains(traceId, cut.Markup);
        });
        cut.Find("button[aria-label='Close trace panel']").Click();
        Assert.DoesNotContain("gnougo-trace-panel--open", cut.Markup);
        cut.Find("button[aria-label='Traces for designer']").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain(traceId, cut.Markup));
        await DisposeComponentsAsync();
    }

    [Theory]
    [InlineData(PlanningStatus.FinalReview)]
    [InlineData(PlanningStatus.Stopped)]
    public async Task WorkflowDetailsStayReadOnlyAndNavigationClosesTraces(string status)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        Configure(fixture);
        var state = Session("workflow-session", "chat", status);
        state.PendingCall = new() { Id = "uncertain", Purpose = "intent", Request = new() { Prompt = "private" } };
        await StoreWorkflow(fixture, state);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/planning/workflow-session?source=workflow");
        var traceId = Capture("workflow-session", true, "planning failure");
        var cut = Render<PlanningPage>(p => p.Add(c => c.SessionId, "workflow-session"));
        cut.WaitForAssertion(() => Assert.Contains("This session is read-only", cut.Markup));
        Assert.DoesNotContain("Approve this revision", cut.Markup);
        Assert.DoesNotContain("Retry with retained usage", cut.Markup);
        Assert.DoesNotContain("Revise workflow", cut.Markup);
        Assert.DoesNotContain("Cancel planning", cut.Markup);
        cut.Find("main button").Click();
        cut.WaitForAssertion(() => Assert.Contains(traceId, cut.Markup));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/planning/missing?source=workflow");
        cut.Render(p => p.Add(c => c.SessionId, "missing"));
        cut.WaitForAssertion(() => Assert.Contains("Planning session not found", cut.Markup));
        Assert.DoesNotContain("gnougo-trace-panel--open", cut.Markup);
        Assert.DoesNotContain("Create plan", cut.Markup);
        var stored = await Services.GetRequiredService<PlanningSessionService>().GetWorkflowSessionAsync("workflow-session", Ct);
        Assert.Equal(status, stored!.Status);
        Assert.Equal(1, stored.ModelCalls);
        Assert.Equal("uncertain", stored.PendingCall!.Id);
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task SelectorKeepsTracesSeparateAndSessionSwitchClearsPreviousTelemetry()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        Configure(fixture);
        var first = Capture("one", false, "first attempt");
        var second = Capture("one", false, "resumed attempt");
        var other = Capture("two", false, "other session");
        var cut = Render<TraceSidebar>(p => p.Add(c => c.PlanningSessionId, "one").Add(c => c.IsOpen, true));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#planning-trace-select option").Count));
        cut.Find("#planning-trace-select").Change(first);
        cut.WaitForAssertion(() => Assert.Contains(first, cut.Find(".gnougo-trace-panel__meta").TextContent));
        Assert.DoesNotContain(other, cut.Markup);
        cut.Find("#planning-trace-select").Change(second);
        cut.WaitForAssertion(() => Assert.Contains(second, cut.Find(".gnougo-trace-panel__meta").TextContent));
        cut.Render(p => p.Add(c => c.PlanningSessionId, "two").Add(c => c.IsOpen, true));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(other, cut.Markup);
            Assert.DoesNotContain(first, cut.Markup);
            Assert.DoesNotContain(second, cut.Markup);
        });
        cut.Render(p => p.Add(c => c.PlanningSessionId, "none").Add(c => c.IsOpen, true));
        cut.WaitForAssertion(() => Assert.Contains("Stored telemetry is unavailable", cut.Markup));
        Assert.DoesNotContain(other, cut.Markup);
        await DisposeComponentsAsync();
    }

    private void Configure(PlanningPersistenceTests.StoreFixture fixture)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(),
            settings: new() { BackgroundProcessingEnabled = false }));
        Services.AddLogging();
        Services.AddHttpClient();
        Services.Configure<OpenTelemetrySettings>(s => s.TenantId = "planning-tests");
        Services.Configure<TraceDebugSettings>(_ => { });
        Services.AddSingleton<LocalTraceDebugStore>();
        Services.AddSingleton<TraceDebugService>();
    }

    private string Capture(string session, bool workflow, string name)
    {
        using var activity = new Activity(name).SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded).Start();
        activity.SetTag("tenant.id", "planning-tests");
        activity.SetTag(workflow ? "gnougo-flow.plan.session_id" : "gnougo.planning.session_id", session);
        Services.GetRequiredService<LocalTraceDebugStore>().Track(activity);
        return activity.TraceId.ToHexString();
    }

    private static PlanningSession Session(string id, string name, string status) => new()
    {
        Request = new() { TenantId = "planning-tests", SessionId = id, Name = name, Prompt = "Return a value" },
        Status = status, ModelCalls = 1, Revision = 3
    };

    private static Task StoreWorkflow(PlanningPersistenceTests.StoreFixture fixture, PlanningSession state)
        => fixture.Records.UpsertAsync("flow-planning-sessions-v10", "planning-tests", state.Request.SessionId,
            JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), "test", Ct);
}
