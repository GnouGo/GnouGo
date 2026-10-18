using System.Text.Json.Nodes;
using Bunit;
using GnOuGo.Agent.Server.Components.Pages;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class PlanningTraceUiTests
{
    [Fact]
    public async Task ReviewShowsOriginalRequestAndAssertionsWithoutAcknowledgingOrExecutingContent()
    {
        var state = await ApprovalSession();
        state.Request.Prompt = "Return the requested result. <script>untrusted()</script>\nRequested revision: retain exact text.";
        state.Plan!.Root.Tasks.Add(new() { Id = "check", Kind = "value", Objective = "Validate before publishing", Requires = new() { Kind = "boolean", Boolean = true } });
        var hash = state.ComputeArtifactHash();
        var cut = Render<GnOuGo.Agent.Server.Components.Planning.PlannerStageDetails>(p => p
            .Add(c => c.Session, GnOuGo.Agent.Server.Planning.PlanningEndpoints.ToDto(state))
            .Add(c => c.ReviewEnabled, true));
        Assert.Equal(state.Request.Prompt, cut.Find("[aria-label='Original request and revisions'] pre").TextContent);
        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("Required assertion", cut.Markup);
        Assert.Contains("omitted actions", cut.Markup);
        Assert.All(cut.FindAll("input[type=checkbox]"), check => Assert.False(check.HasAttribute("checked")));
        Assert.Equal(hash, state.ComputeArtifactHash());
    }

    [Fact]
    public async Task ApprovalExplainsMissingAcknowledgmentsAndPreservesSameArtifactPolling()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var planner = new RecordingRevisionPlanner(); Configure(fixture, planner);
        var state = await ApprovalSession(); Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var cut = OpenApprovalPage();
        cut.WaitForAssertion(() => Assert.Contains("0/2 reviewed", cut.Find("#approval-review-status").TextContent));
        Assert.Equal(state.Requirements!.Outcomes.Select(r => r.Description), cut.FindAll("#approval-missing-requirements li").Select(e => e.TextContent));
        Assert.Equal("http://localhost/planning/approval-ui#business-requirements", cut.Find("a[href$='#business-requirements']").GetAttribute("href"));
        Assert.Equal("-1", cut.Find("#business-requirements").GetAttribute("tabindex"));
        Assert.Contains(state.ComputeArtifactHash()!, cut.Find("#approval-artifact-help").TextContent);
        Assert.Contains("Saving does not execute", cut.Find("#approval-artifact-help").TextContent);
        cut.Find("#approve-and-save").Click(); Assert.Empty(planner.Commands);
        cut.FindAll("input[type=checkbox]")[0].Change(true);
        Assert.Contains("1/2 reviewed", cut.Find("#approval-review-status").TextContent);
        Assert.Single(cut.FindAll("#approval-missing-requirements li"));
        Assert.True(cut.Find("#approve-and-save").HasAttribute("disabled"));
        // Exercise the real polling refresh without manufacturing a saved revision.
        await cut.InvokeAsync(async () => await (Task)typeof(PlanningPage).GetMethod("RefreshAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(cut.Instance, null)!);
        await cut.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(cut.Instance, null));
        Assert.Contains("1/2 reviewed", cut.Find("#approval-review-status").TextContent);
        cut.FindAll("input[type=checkbox]")[1].Change(true);
        Assert.Contains("2/2 reviewed", cut.Find("#approval-review-status").TextContent);
        Assert.Empty(cut.FindAll("#approval-missing-requirements"));
        Assert.False(cut.Find("#approve-and-save").HasAttribute("disabled"));
        cut.FindAll("input[type=checkbox]")[0].Change(false);
        Assert.Contains("1/2 reviewed", cut.Find("#approval-review-status").TextContent);
        Assert.True(cut.Find("#approve-and-save").HasAttribute("disabled"));
        Assert.Empty(planner.Commands);
        await DisposeComponentsAsync();
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("artifact")]
    [InlineData("session")]
    public async Task ApprovalAcknowledgmentsClearWhenDisplayedAuthorityChanges(string change)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(); Configure(fixture);
        var state = await ApprovalSession(); Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var cut = OpenApprovalPage();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("input[type=checkbox]").Count));
        for (var index = 0; index < 2; index++) cut.FindAll("input[type=checkbox]")[index].Change(true);
        var previous = state.Revision;
        if (change == "session")
        {
            state.Request.SessionId = "approval-other"; Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
            cut.Render(p => p.Add(c => c.SessionId, state.Request.SessionId));
        }
        else
        {
            if (change == "revision") state.Revision++;
            else { state.Revision++; state.Yaml += "\n# Different artifact requires fresh review\n"; }
            Assert.True(await fixture.Store.TrySaveAsync(state, previous, Ct));
        }
        cut.WaitForAssertion(() => Assert.Contains("0/2 reviewed", cut.Find("#approval-review-status").TextContent), TimeSpan.FromSeconds(5));
        Assert.All(cut.FindAll("input[type=checkbox]"), check => Assert.False(check.HasAttribute("checked")));
        Assert.True(cut.Find("#approve-and-save").HasAttribute("disabled"));
        await DisposeComponentsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalBindsExactAcknowledgmentsAndDoubleClickCannotSaveTwice(bool stale)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = await ApprovalSession(); var writes = 0; string? stored = null;
        var agents = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", (_, _) => Task.FromResult(new McpCallResult { Content = stored is null
                ? new JsonObject { ["success"] = false, ["error_code"] = "NOT_FOUND" }
                : new JsonObject { ["success"] = true, ["agent"] = new JsonObject { ["id"] = "saved", ["workflow"] = stored } } }))
            .OnTool("agent_add", (args, _) => { writes++; stored = args!["workflow"]!.GetValue<string>();
                return Task.FromResult(new McpCallResult { Content = new JsonObject { ["success"] = true, ["agent"] = new JsonObject { ["id"] = "saved" } } }); });
        var planner = new RecordingRevisionPlanner { BeforeAdvance = async () => { entered.TrySetResult(); await release.Task; } };
        Configure(fixture, planner, agents); Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var cut = OpenApprovalPage();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("input[type=checkbox]").Count));
        // Reverse the review order; the exact explicitly acknowledged IDs must be sent.
        cut.FindAll("input[type=checkbox]")[1].Change(true); cut.FindAll("input[type=checkbox]")[0].Change(true);
        var submit = cut.Find("#approve-and-save").ClickAsync(new());
        try
        {
            await entered.Task.WaitAsync(Ct);
            cut.Find("#approve-and-save").Click(); Assert.Single(planner.Commands);
            Assert.True(cut.Find("#approve-and-save").HasAttribute("disabled"));
            if (stale)
            {
                var changed = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
                changed.Revision++; Assert.True(await fixture.Store.TrySaveAsync(changed, state.Revision, Ct));
            }
        }
        finally { release.TrySetResult(); await submit; }
        var approval = planner.Commands[0];
        Assert.Equal("approve", approval.Kind); Assert.Equal(state.Revision, approval.ExpectedRevision);
        Assert.Equal(state.ComputeArtifactHash(), approval.ArtifactHash);
        Assert.Equal(new[] { "retained", "result" }, approval.ReviewedRequirementIds);
        var saved = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
        Assert.Equal(stale ? PlanningStatus.FinalReview : PlanningStatus.Saved, saved.Status);
        Assert.Equal(stale ? 0 : 1, writes);
        if (stale) { Assert.Single(planner.Commands); Assert.Contains("newer planning revision", cut.Find("[role=alert]").TextContent); }
        else
        {
            Assert.Single(planner.Commands); // Saving is handled by the host, outside the planner.
            Assert.Equal(state.ComputeArtifactHash(), saved.ApprovedHash);
            Assert.Equal(state.Yaml, stored);
        }
        await DisposeComponentsAsync();
    }

    private IRenderedComponent<PlanningPage> OpenApprovalPage()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/planning/approval-ui");
        return Render<PlanningPage>(p => p.Add(c => c.SessionId, "approval-ui"));
    }

    private static async Task<PlanningSession> ApprovalSession()
    {
        var state = Session("approval-ui", "Reviewed result", PlanningStatus.FinalReview);
        state.Request.Policy = GnOuGo.Agent.Server.Planning.AgentPlanningPolicy.Create();
        state.Requirements = GnOuGo.Planning.Examples.PlanningCorpus.Requirements("local");
        state.Requirements.Outcomes = [new("result", "Return the exact requested value"), new("retained", "Preserve the requested output contract")];
        state.Plan = GnOuGo.Planning.Examples.PlanningCorpus.LiteralResult();
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.Graph = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Graph!;
        state.Yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Catalog, state.Request.Name);
        return state;
    }
}
