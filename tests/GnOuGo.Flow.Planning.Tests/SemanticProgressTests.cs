using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SemanticProgressTests
{
    private static PlanningGraph Fixture()
    {
        var graph = Graph(); graph.Workflows[0].Outputs.Clear();
        graph.Workflows[0].Steps = Enumerable.Range(0, 4).Select(i => new PlanningNode { Key = "operation" + i, Input = Obj(("value", Str("original"))) }).ToList();
        return graph;
    }
    private static PlanningGraph Copy(PlanningGraph graph) => JsonSerializer.Deserialize(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
    private static PlanningDiagnostic Finding(string code, int index) => new(code, "/workflows/0/steps/" + index + "/input", "Required behavior remains unresolved.");

    [Theory]
    [InlineData("original_code", "renamed_code")]
    [InlineData("code_initial", "code_renomme")]
    public async Task NewFindingsInUnchangedCodeDoNotUndoRepairOrEraseOutstandingRequirements(string oldCode, string newCode)
    {
        var state = Session(PlanningStatus.Validating); state.Preparation = Preparation(); state.Request.MaxRepairs = 0;
        state.BestGraph = Fixture(); state.Graph = Copy(state.BestGraph); state.Graph.Workflows[0].Steps[0].Input = Obj(("value", Str("corrected")));
        state.BestDiagnostics = [Finding("fixed", 0), Finding(oldCode, 1), Finding("omitted_by_later_review", 2)];
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.BestGraph), PlanningStatus.Validating, 9, true, state.BestDiagnostics.ToList()));
        var expected = PlanningGraphCompiler.Fingerprint(state.Graph);
        // Preserve the comparison baseline across an encrypted-store serialization boundary.
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        var fallback = new FakeRuntime(); var reported = new[] { Finding(newCode, 1), Finding("newly_discovered", 3) };
        var runtime = new FakeRuntime { OnCall = (phase, request, ct) => phase == "semantic_review"
            ? Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(reported.Select(d => (JsonNode)new JsonObject
                { ["code"] = d.Code, ["workflow"] = "main", ["location"] = d.Location, ["message"] = d.Message, ["evidence"] = "Return a greeting", ["blocking"] = true }).ToArray()) } })
            : fallback.CallAsync(request, phase, ct) };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Equal(expected, PlanningGraphCompiler.Fingerprint(state.Graph!)); Assert.True(state.Attempts[^1].Retained);
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "fixed");
        Assert.Contains(state.Diagnostics, d => d.Code == "omitted_by_later_review");
        Assert.Contains(state.Diagnostics, d => d.Code == oldCode);
        Assert.Contains(state.Diagnostics, d => d.Code == "newly_discovered");
        Assert.Null(state.ApprovedHash); Assert.Null(state.Yaml);
    }

    [Fact]
    public void EmptyReviewCannotClearAnUnchangedRequiredFinding()
    {
        var before = Fixture(); var after = Copy(before); var findings = new List<PlanningDiagnostic>();
        Assert.False(PlanningSemanticProgress.Preserve(before, after, [Finding("required", 1)], findings));
        Assert.Equal("required", Assert.Single(findings).Code);
    }

    [Fact]
    public void RetryRestoresOnlyAnExactBaselineValidatedUnderCurrentConstructionContracts()
    {
        var state = Session(PlanningStatus.Recovery); state.Graph = Fixture();
        state.ConstructionUnits = [new() { Key = "unit", ContractVersion = PlanningDataflow.ContractVersion, Status = "validated" }];
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.Graph), PlanningStatus.Validating, 9, true, [Finding("required", 1)]));
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        PlanningSemanticProgress.RestoreBaseline(state);
        Assert.NotSame(state.Graph, state.BestGraph); Assert.Equal("required", Assert.Single(state.BestDiagnostics).Code);
        state.BestGraph = null; state.BestDiagnostics.Clear(); state.ConstructionUnits[0].ContractVersion--;
        PlanningSemanticProgress.RestoreBaseline(state); Assert.Null(state.BestGraph);
        state.ConstructionUnits[0].ContractVersion++; state.Graph!.Workflows[0].Steps[1].Input = Obj(("value", Str("edited")));
        PlanningSemanticProgress.RestoreBaseline(state); Assert.Null(state.BestGraph);
    }

    [Fact]
    public async Task RevalidatingTheSameCandidateCannotReplaceCurrentFindingsWithOlderFindings()
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Fixture(); state.Preparation = Preparation(); state.Request.MaxRepairs = 0;
        state.BestGraph = Copy(state.Graph); state.BestDiagnostics = [Finding("previous_review", 1)];
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.BestGraph), PlanningStatus.Validating, 9, true, state.BestDiagnostics.ToList()));
        var runtime = new FakeRuntime { ValidationResult = _ => [Finding("current_contract", 1)] };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.True(state.Attempts[^1].Retained);
        Assert.Contains(state.Diagnostics, d => d.Code == "current_contract");
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "previous_review");
    }

    [Fact]
    public void FindingsOnChangedInputsOrChangedProducersRemainPotentialRegressions()
    {
        var before = Fixture(); before.Workflows[0].Steps[1].Input = Obj(("value", new() { Kind = "output", Source = "operation0", Path = ["value"] }));
        var after = Copy(before); after.Workflows[0].Steps[0].Input = Obj(("value", Str("changed")));
        Assert.False(PlanningSemanticProgress.UnchangedInput(Finding("dependent", 1).Location, before, after));
        var findings = new List<PlanningDiagnostic> { Finding("new_dependent_failure", 1) };
        Assert.False(PlanningSemanticProgress.Preserve(before, after, [Finding("fixed", 0)], findings));
        Assert.False(PlanningSemanticProgress.Preserve(before, after, [Finding("old", 0)], [Finding("new", 0)]));
        after = Copy(before); after.Functions = "/** @returns {string} A value. */ function helper() { return 'changed'; }";
        Assert.False(PlanningSemanticProgress.UnchangedInput(Finding("dependent", 1).Location, before, after));
    }

    [Fact]
    public void ChangedChildDoesNotEraseAContainerInputFindingButChangedControlDoes()
    {
        var before = Fixture(); var child = before.Workflows[0].Steps[0];
        before.Workflows[0].Steps = [new() { Key = "pages", Type = "loop.sequential", Input = Obj(("times", new() { Kind = "number", Number = 1 })), Steps = [child] }];
        var after = Copy(before); after.Workflows[0].Steps[0].Steps[0].Input = Obj(("value", Str("changed")));
        Assert.True(PlanningSemanticProgress.UnchangedInput("/workflows/0/steps/0/input", before, after));
        after = Copy(before); after.Workflows[0].Steps[0].If = new() { Kind = "boolean", Boolean = false };
        Assert.False(PlanningSemanticProgress.UnchangedInput("/workflows/0/steps/0/steps/0/input", before, after));
    }
}
