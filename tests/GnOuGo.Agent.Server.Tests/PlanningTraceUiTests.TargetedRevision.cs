using System.Text.Json;
using Bunit;
using GnOuGo.Agent.Server.Components.Pages;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class PlanningTraceUiTests
{
    private const string GuardPath = "/tasks/consume/requires";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetedRevisionUsesOnlySelectedPathsAndDisplayedAuthority(bool artifact)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var planner = new RecordingRevisionPlanner(); Configure(fixture, planner);
        var state = RevisionSession();
        if (artifact) state.Yaml = "retained artifact for hash comparison";
        state.Diagnostics.Add(state.Diagnostics[^1]);
        state.Diagnostics.Add(new("REVISION_REQUIRED", "/root/outputs/message", "An independent output finding"));
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var cut = OpenRevisionPage();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#targeted-revision input").Count));
        Assert.Contains("Task consume", cut.Markup); Assert.Contains("CONTRACT_UNSATISFIED", cut.Markup);
        cut.Find("#targeted-revision input").Change(true);
        Assert.Empty(planner.Commands);
        Assert.True(cut.Find("#revise-selected-bindings").HasAttribute("disabled"));
        cut.Find("#plan-revision").Change("Correct only the declared field reference; retain both assertions.");
        cut.Find("#revise-selected-bindings").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("id=\"targeted-revision\"", cut.Markup));
        var command = Assert.Single(planner.Commands);
        Assert.Equal("revise", command.Kind); Assert.True(command.PreserveRequirements);
        Assert.Equal(new[] { GuardPath }, command.EditablePaths);
        Assert.Equal(state.Revision, command.ExpectedRevision); Assert.Equal(state.ComputeArtifactHash(), command.ArtifactHash);
        Assert.Null(command.ReviewedRequirementIds);
        var saved = (await fixture.Store.LoadAsync("planning-tests", "targeted-ui", Ct))!;
        Assert.Equal(command.EditablePaths, saved.EditablePaths);
        Assert.Equal(requirements, JsonSerializer.Serialize(saved.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(state.ModelCalls, saved.ModelCalls); Assert.Null(saved.ApprovedHash);
        Assert.Equal(JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(saved.Plan, PlanningJsonContext.Default.TaskPlan));
        await DisposeComponentsAsync();
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("retired")]
    [InlineData("exhausted")]
    [InlineData("requirements")]
    [InlineData("saved")]
    [InlineData("chat")]
    public async Task TargetedRevisionCannotBypassSessionBoundaries(string boundary)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var planner = new RecordingRevisionPlanner(); Configure(fixture, planner);
        var state = RevisionSession();
        if (boundary == "pending") state.PendingCall = new() { Id = "uncertain", Request = new() { Prompt = "Retained request" } };
        if (boundary == "retired") state.IntentVersion = 1;
        if (boundary == "exhausted") state.ModelCalls = state.Request.MaxModelCalls;
        if (boundary == "requirements") state.Requirements = null;
        if (boundary == "saved") state.Status = PlanningStatus.Saved;
        if (boundary == "chat") await StoreWorkflow(fixture, state);
        else Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var cut = OpenRevisionPage(boundary == "chat" ? "workflow" : null);
        cut.WaitForAssertion(() => Assert.Contains("Malformed declared field", cut.Markup));
        if (boundary is "saved" or "chat") Assert.Empty(cut.FindAll("#revise-selected-bindings"));
        else
        {
            Assert.True(cut.Find("#targeted-revision").HasAttribute("disabled"));
            cut.Find("#targeted-revision input").Change(true);
            cut.Find("#plan-revision").Change("Correct the binding");
            cut.Find("#revise-selected-bindings").Click();
            Assert.True(cut.Find("#revise-selected-bindings").HasAttribute("disabled"));
        }
        Assert.Empty(planner.Commands);
        await DisposeComponentsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedScopeKeepsInstructionsAndNeverFallsBack(bool overlapping)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var planner = new RecordingRevisionPlanner(); Configure(fixture, planner);
        var state = RevisionSession();
        state.Diagnostics = [new("REVISION_REQUIRED", GuardPath + "/items/0", "Unsupported precise location")];
        if (overlapping) state.Diagnostics.Add(new("REVISION_REQUIRED", GuardPath, "Parent location"));
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var cut = OpenRevisionPage();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#targeted-revision input")));
        for (var index = 0; index < cut.FindAll("#targeted-revision input").Count; index++)
            cut.FindAll("#targeted-revision input")[index].Change(true);
        cut.Find("#plan-revision").Change("Keep this requested correction after rejection");
        cut.Find("#revise-selected-bindings").Click();
        cut.WaitForAssertion(() => Assert.Contains(overlapping ? "nonoverlapping" : "EditablePaths supports", cut.Find("[role=alert]").TextContent));
        Assert.Equal("Keep this requested correction after rejection", cut.Find("#plan-revision").GetAttribute("value"));
        Assert.Single(planner.Commands);
        var saved = (await fixture.Store.LoadAsync("planning-tests", "targeted-ui", Ct))!;
        Assert.Equal(state.Revision, saved.Revision); Assert.Null(saved.EditablePaths);
        // Let the real polling path observe a newer snapshot; the command error must survive it.
        saved.Revision++; saved.ModelCalls++;
        Assert.True(await fixture.Store.TrySaveAsync(saved, state.Revision, Ct));
        cut.WaitForAssertion(() => Assert.Contains($"Revision {saved.Revision}", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.NotEmpty(cut.FindAll("[role=alert]"));
        Assert.All(cut.FindAll("#targeted-revision input"), input => Assert.False(input.HasAttribute("checked")));
        Assert.Single(planner.Commands);
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task NavigationClearsScopeAndRepeatedSubmissionDispatchesOnce()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var planner = new RecordingRevisionPlanner { BeforeAdvance = () => gate.Task }; Configure(fixture, planner);
        var state = RevisionSession(); Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var other = RevisionSession(); other.Request.SessionId = "other-ui";
        Assert.True(await fixture.Store.TrySaveAsync(other, null, Ct));
        var cut = OpenRevisionPage();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#targeted-revision input")));
        cut.Find("#targeted-revision input").Change(true);
        cut.Render(p => p.Add(c => c.SessionId, "other-ui"));
        cut.WaitForAssertion(() => Assert.Contains("other-ui", cut.Markup));
        Assert.False(cut.Find("#targeted-revision input").HasAttribute("checked"));
        cut.Find("#targeted-revision input").Change(true);
        cut.Find("#plan-revision").Change("Repair this session's guard");
        var submit = cut.Find("#revise-selected-bindings").ClickAsync(new());
        try
        {
            cut.WaitForAssertion(() => Assert.Single(planner.Commands));
            cut.Find("#revise-selected-bindings").Click();
            Assert.Single(planner.Commands);
        }
        finally { gate.TrySetResult(); await submit; }
        Assert.Equal(state.Revision, (await fixture.Store.LoadAsync("planning-tests", "targeted-ui", Ct))!.Revision);
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task ConcurrentRevisionRejectsTheDisplayedAuthorityAndClearsSelection()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = RevisionSession(); state.Yaml = "original artifact";
        var planner = new RecordingRevisionPlanner { BeforeAdvance = async () =>
        {
            var changed = (await fixture.Store.LoadAsync("planning-tests", "targeted-ui", Ct))!;
            changed.Revision++; changed.Yaml = "newer artifact";
            Assert.True(await fixture.Store.TrySaveAsync(changed, state.Revision, Ct));
        } };
        Configure(fixture, planner); Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var cut = OpenRevisionPage();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#targeted-revision input")));
        cut.Find("#targeted-revision input").Change(true);
        cut.Find("#plan-revision").Change("Retain these instructions after a conflict");
        cut.Find("#revise-selected-bindings").Click();
        cut.WaitForAssertion(() => Assert.Contains("newer revision", cut.Find("[role=alert]").TextContent));
        var command = Assert.Single(planner.Commands);
        Assert.Equal(state.Revision, command.ExpectedRevision); Assert.Equal(state.ComputeArtifactHash(), command.ArtifactHash);
        Assert.False(cut.Find("#targeted-revision input").HasAttribute("checked"));
        Assert.Equal("Retain these instructions after a conflict", cut.Find("#plan-revision").GetAttribute("value"));
        var retained = (await fixture.Store.LoadAsync("planning-tests", "targeted-ui", Ct))!;
        Assert.Null(retained.EditablePaths); Assert.Equal(state.ModelCalls, retained.ModelCalls);
        Assert.Equal("newer artifact", retained.Yaml);
        await DisposeComponentsAsync();
    }

    private IRenderedComponent<PlanningPage> OpenRevisionPage(string? source = null)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/planning/targeted-ui" + (source is null ? "" : "?source=" + source));
        return Render<PlanningPage>(p => p.Add(c => c.SessionId, "targeted-ui"));
    }

    private static PlanningSession RevisionSession()
    {
        var state = Session("targeted-ui", "Generic field correction", PlanningStatus.Stopped);
        state.Catalog = new();
        state.Requirements = new() { Summary = "Preserve the observed result", Outcomes = [new("result", "Return observed text")] };
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "consume", Kind = "value", Objective = "Retain the asserted result",
            Requires = new() { Kind = "predicate", Predicate = "and", Items = [new() { Kind = "boolean", Boolean = true }, new() { Kind = "boolean", Boolean = true }] },
            Outputs = [new("message", new() { Kind = "string", Text = "exact" })] }],
            Outputs = [new("message", new() { Kind = "output", Source = "consume", Port = "message" })] } };
        state.Diagnostics = [new("CONTRACT_UNSATISFIED", GuardPath, "Malformed declared field"),
            new("REVISION_REQUIRED", GuardPath, "Explicitly revise the protected assertion")];
        return state;
    }

    private sealed class RecordingRevisionPlanner : IWorkflowPlanner
    {
        internal List<PlanningCommand> Commands { get; } = [];
        internal Func<Task>? BeforeAdvance { get; init; }
        public async Task<PlanningSession> AdvanceAsync(PlanningSession session, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
        {
            Commands.Add(command);
            if (BeforeAdvance is not null) await BeforeAdvance();
            return await new HybridWorkflowPlanner().AdvanceAsync(session, command, runtime, ct);
        }
    }
}
