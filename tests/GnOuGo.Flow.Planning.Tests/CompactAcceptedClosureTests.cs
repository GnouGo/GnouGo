using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactAcceptedClosureTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(8)]
    public async Task TechnicallyCompleteRootRetainsEveryObligationAndReceivesOneGlobalReview(int requirements)
    {
        var state = await State(requirements);
        var original = PlanningConstruction.Hash(state.Plan);
        var prepared = Assert.IsType<PlanningSession>(PlanningCompactConstruction.PrepareReviewedClosure(state));
        Assert.Equal(state.Requirements!.Outcomes.Select(item => item.Id), prepared.Construction!.Candidate!.Extension.RequirementIds);
        Assert.Equal(original, prepared.Construction.Candidate.Hash);
        Assert.False(prepared.Construction.Candidate.BusinessReviewed);
        var runtime = new TestRuntime { Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.GlobalPurpose, purpose);
            var response = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "ok", ["reason"] = "The complete numeric result fulfills all original obligations." } };
            return new() { Json = response, Text = response.ToJsonString() };
        } };
        for (var tick = 0; tick < 12 && state.Status != PlanningStatus.FinalReview; tick++)
            state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Single(runtime.Calls);
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        Assert.True(HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task NestedTechnicalClosureDoesNotClaimGlobalBusinessCompletion()
    {
        var state = await State(5, nested: true);
        var prepared = Assert.IsType<PlanningSession>(PlanningCompactConstruction.PrepareReviewedClosure(state));
        var candidate = prepared.Construction!.Candidate!;
        Assert.Equal("/tasks/outer/body", candidate.Extension.Scope);
        Assert.Equal(state.Requirements!.Outcomes.Select(item => item.Id), candidate.Extension.RequirementIds);
        Assert.False(candidate.Extension.Complete);
        Assert.Equal(["/root"], candidate.OpenScopes);
        Assert.False(candidate.BusinessReviewed);
        Assert.Null(state.ApprovedHash);
        Assert.False(HybridWorkflowPlanner.ConstructionArtifactReviewValid(prepared));
    }

    [Theory]
    [InlineData("remaining")]
    [InlineData("source")]
    [InlineData("missing_public_output")]
    [InlineData("rebuild")]
    public async Task UnresolvedObligationsStillBlockAutomaticFinalization(string missing)
    {
        var state = await State();
        switch (missing)
        {
            case "remaining": state.Construction!.Requirements[0].Status = "remaining"; break;
            case "source": state.Construction!.Compact!.Sources.First(source => source.Kind == "page").Indexed = false; break;
            case "missing_public_output": state.Plan!.Root.Outputs.Clear(); break;
            case "rebuild": state.Construction!.RebuildTasks.Add(new() { Id = "unrebuilt", Kind = "value", Objective = "Preserved required work." }); break;
        }
        Assert.False(PlanningCompactConstruction.CompletionOnly(state));
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
    }

    [Fact]
    public async Task LocalScalarExportCannotSatisfyARequiredPublicArray()
    {
        var state = await State(nested: true);
        state.Requirements!.Outputs![0].Type = new() { Kind = "array", Items = new() { Kind = "number" } };
        state.Plan!.Root.Outputs.Clear();
        Assert.Single(state.Plan.Root.Tasks[0].Body!.Outputs);
        Assert.False(PlanningCompactConstruction.CompletionOnly(state));
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
    }

    [Fact]
    public async Task DependencyInvalidationReopensClosedRequirements()
    {
        var state = await State();
        PlanningConstruction.Rollback(state, "producer", "The exact accepted producer has a demonstrated defect.");
        Assert.Equal("remaining", state.Construction!.Requirements[0].Status);
        Assert.False(PlanningCompactConstruction.CompletionOnly(state));
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
        Assert.Single(state.Construction.Invalidations);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("dependency")]
    [InlineData("new_work")]
    [InlineData("repair")]
    [InlineData("stale")]
    public async Task SelectedClosureCannotInventBindingsOrBypassRepair(string reason)
    {
        var state = await State();
        var step = new PlanningConstructionStep { Id = "close", Kind = "close", Scope = "/root", BaseHash = PlanningConstruction.Hash(state.Plan),
            DiscoveryComplete = true, RequirementIds = ["req_0"], CloseScopes = ["/root"] };
        state.Construction!.Compact!.CurrentStep = step;
        switch (reason)
        {
            case "input": step.Inputs = [new() { Kind = "number", Number = 7 }]; break;
            case "dependency": step.DependencyTaskIds = ["producer"]; break;
            case "new_work": step.ExpectedOutputs = ["A still missing business result."]; break;
            case "repair": state.EditablePaths = ["/tasks/producer/outputs/number"]; break;
            case "stale": step.BaseHash = new string('0', 64); break;
        }
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Null(PlanningCompactConstruction.PrepareSelectedClosure(state));
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task AParentCannotCloseWhileItsChildRemainsOpen()
    {
        var state = await State(nested: true);
        state.Construction!.Compact!.CurrentStep = new()
        {
            Id = "close", Scope = "/root", Kind = "close", RequirementIds = ["req_0"],
            BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true, CloseScopes = ["/root"]
        };
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelectedCloseScopes(state, state.Construction.Compact.CurrentStep));
    }

    private static async Task<PlanningSession> State(int requirements = 1, bool nested = false)
    {
        var outcomes = Enumerable.Range(0, requirements).Select(index => new PlanningRequirement("req_" + index,
            "Preserve the public numeric result and original obligation " + index + "." + (index + 1 < requirements ? " " : ""))).ToList();
        var prompt = string.Concat(outcomes.Select(outcome => outcome.Description));
        var state = new PlanningSession { Request = new() { TenantId = "accepted-closure", Prompt = prompt, MaxModelCalls = 64 } };
        PlanningSessionCreation.Capture(state);
        state.Revision = 1; state.Status = PlanningStatus.Generating;
        state.Requirements = new() { Inputs = [], Outputs = [new() { Name = "number", Required = true, Type = new() { Kind = "number" } }], Outcomes = outcomes };
        var producer = new PlanTask { Id = "producer", Kind = "value", Objective = "Produce the required numeric seven.",
            Outputs = [new("number", new() { Kind = "number", Number = 7 })] };
        var root = new TaskScope();
        if (nested)
        {
            root.Tasks = [new() { Id = "outer", Kind = "sequence", Objective = "Contain the required local computation.",
                Body = new() { Tasks = [producer], Outputs = [new("number", Output("producer"))] } }];
            root.Outputs = [new("number", new() { Kind = "number", Number = 7 })];
        }
        else { root.Tasks = [producer]; root.Outputs = [new("number", Output("producer"))]; }
        state.Plan = new() { Root = root };
        state.Construction!.OpenScopes = nested ? ["/tasks/outer/body", "/root"] : ["/root"];
        state.Construction.Requirements = outcomes.Select(outcome => new PlanningConstructionRequirement
            { Id = outcome.Id, Status = "closed", Checkpoints = [1] }).ToList();
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var source in state.Construction.Compact!.Sources) { source.Indexed = true; source.InterfaceKind = "none"; }
        state.Construction.Compact.InterfaceExtractions = [];
        var page = Assert.Single(state.Construction.Compact.Sources);
        foreach (var (outcome, index) in outcomes.Select((outcome, index) => (outcome, index)))
        {
            var id = page.Id + ":clause:" + index;
            state.Construction.Compact.Sources.Add(new() { Id = id, Kind = "requirement", Text = outcome.Description,
                Version = PlanningGraphCompiler.Fingerprint(outcome.Description), Indexed = true, InterfaceKind = "none" });
            state.Construction.Compact.RequirementSources[outcome.Id] = [id];
        }
        state.Construction.Checkpoints = [new() { Sequence = 1, BaseHash = PlanningConstruction.Hash(new TaskPlan()), PlanHash = PlanningConstruction.Hash(state.Plan),
            TaskIds = nested ? ["outer", "producer"] : ["producer"], RequirementIds = outcomes.Select(outcome => outcome.Id).ToList(),
            ClosedRequirementIds = outcomes.Select(outcome => outcome.Id).ToList(),
            Step = new() { Scope = nested ? "/tasks/outer/body" : "/root", Kind = "value", RequirementIds = outcomes.Select(outcome => outcome.Id).ToList() },
            Fragment = new JsonObject { ["tasks"] = new JsonArray(root.Tasks.Select(task => PlanningConstruction.Node(task, PlanningJsonContext.Default.PlanTask)).ToArray()) } }];
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        return state;
    }
    private static TaskValue Output(string source) => new() { Kind = "output", Source = source, Port = "number" };
}
