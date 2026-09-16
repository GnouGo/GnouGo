using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class OperationCoverageDurabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime NoCalls() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("Unexpected dispatch") };
    private static PlanningSnapshot Independent(bool third = false)
    {
        var state = OperationAdmissionTests.State("Perform the first transformation. Independently perform the second transformation." + (third ? " Independently perform the third transformation." : ""));
        foreach (var scope in PlanningOperations.SourceScopes(state)) PlanningFixtures.Runtime(state, scope.Clause);
        return state;
    }

    [Fact]
    public async Task PackingAndEnumerationDoNotChangeCanonicalCoverageOrDependencyProof()
    {
        var state = Independent(true); OperationEffectFixtures.SeedContributions(state); var other = PlanningContext.Clone(state);
        var decisions = OperationEffectFixtures.Groups(state).Select(g => g.Decision).ToArray();
        Assert.Equal(1, PlanningDecisionPages.PackedPageCount(state, decisions));
        // Find a supported smaller target that changes packing, not semantics.
        for (var limit = 500; limit < 12000; limit += 100)
        {
            other.Request.Generation.MaxInputTokensPerRequest = limit;
            try { _ = PlanningDecisionPages.PackedPageCount(other, PlanningOperations.ContributionDecisions(other)); if (PlanningDecisionPages.PackedPageCount(other, decisions) == 2) break; }
            catch (WorkflowRuntimeException e) when (e.Code == "DECISION_SIZE_UNSUPPORTED") { }
        }
        Assert.Equal(2, PlanningDecisionPages.PackedPageCount(other, decisions));
        other.RuntimeEvidence.Reverse(); other.References.Reverse();
        OperationEffectFixtures.Seed(state); OperationEffectFixtures.Seed(other);
        await PlanningOperations.ResolveAsync(state, NoCalls(), Ct); await PlanningOperations.ResolveAsync(other, NoCalls(), Ct);
        Assert.Equal(state.OperationAdmissionFingerprint, other.OperationAdmissionFingerprint);
        Assert.Equal(PlanningOperations.ReadCoverage(state).Select(p => p.ProofFingerprint), PlanningOperations.ReadCoverage(other).Select(p => p.ProofFingerprint));
    }

    [Fact]
    public async Task PartialCoverageCheckpointDoesNotCommitAndResumesExactRemainingPages()
    {
        var state = Independent(); state.Request.Generation.MaxInputTokensPerRequest = 2500;
        var decisions = OperationEffectFixtures.Groups(state).Select(g => g.Decision).ToArray();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
            { Json = OperationEffectFixtures.Response(state, request), CompletionStatus = "completed" }) };
        PlanningSnapshot? saved = null;
        runtime.OnCheckpoint = s =>
        {
            if (saved is null && s.DecisionPages.Any(p => p.Status == "completed"))
            { saved = PlanningContext.Clone(s); throw new OperationCanceledException(); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.NotNull(saved); Assert.Null(saved.OperationAdmissionFingerprint); Assert.Empty(saved.Obligations);
        var completed = saved.DecisionPages.Where(p => p.Status == "completed").Select(p => p.Id).ToArray();
        var second = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
            { Json = OperationEffectFixtures.Response(saved, request), CompletionStatus = "completed" }) };
        await PlanningOperations.ResolveAsync(saved, second, Ct);
        Assert.All(completed, id => Assert.Single(saved.DecisionPages, p => p.Id == id && p.Status == "completed"));
        Assert.Equal(runtime.Requests.Count + second.Requests.Count, saved.RequestAccounting.Count);
        var copy = PlanningContext.Clone(saved); await PlanningOperations.ResolveAsync(copy, NoCalls(), Ct);
        Assert.Equal(saved.OperationAdmissionFingerprint, copy.OperationAdmissionFingerprint);
    }

    [Fact]
    public void CoupledCoverageCannotBeSplitIntoIndependentContributionAnswers()
    {
        var state = OperationAdmissionTests.State("Transform the value. Apply the detailed transformation rules.");
        var scopes = PlanningOperations.SourceScopes(state);
        PlanningFixtures.Runtime(state, scopes[0].Clause);
        PlanningFixtures.Runtime(state, scopes[1].Clause, independentBoundary: false);
        var group = Assert.Single(OperationEffectFixtures.Groups(state));
        Assert.Equal(2, group.Decision.SourceDecisionIds!.Count);
        state.Request.Generation.MaxInputTokensPerRequest = 100;
        var failure = Assert.Throws<WorkflowRuntimeException>(() => OperationEffectFixtures.Groups(state));
        Assert.Equal("DECISION_SIZE_UNSUPPORTED", failure.Code); Assert.Empty(state.RequestAccounting);
    }

    [Fact]
    public async Task ForcedNecessityConflictHasNoCompletableMappingAndDispatchesNothing()
    {
        var state = OperationAdmissionTests.State("The transformation is required. The same transformation is optional.");
        var scopes = PlanningOperations.SourceScopes(state);
        var initial = PlanningFixtures.Runtime(state, scopes[0].Clause);
        state.RuntimeEvidence.Remove(initial);
        state.RuntimeEvidence.Add(PlanningOperations.SealRuntime(state, initial with
        { Necessity = PlanningOperationNecessity.Required, NecessityReference = initial.ActionReference }));
        PlanningFixtures.Runtime(state, scopes[1].Clause, required: false, independentBoundary: false);
        Assert.Empty(Assert.Single(OperationEffectFixtures.Groups(state)).Plans);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoCalls(), Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", error.Code); Assert.Empty(state.RequestAccounting); Assert.Empty(state.Obligations);
    }

    [Fact]
    public async Task RequiredCoverageCannotBeRemovedFromCommittedSetOrRevivedAfterRevision()
    {
        var state = Independent(); OperationEffectFixtures.Seed(state);
        await PlanningOperations.ResolveAsync(state, NoCalls(), Ct);
        state.Obligations.RemoveAt(0);
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(state));
        // Existing revision assessment remains the only retirement authority.
        var revision = Independent(); revision.BehaviorRevision = new() { Text = "Remove the first transformation." };
        OperationEffectFixtures.Seed(revision, seedDependencies: false);
        var operations = PlanningOperations.MaterializeCoverage(revision);
        var retired = operations.First().Id;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_revision_obligations", phase);
            return Task.FromResult(new LLMResponse { CompletionStatus = "completed", Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, p.Value!["enum"]!.AsArray()[p.Key.Contains(retired, StringComparison.Ordinal) ? 1 : 0]!.DeepClone()))) });
        } };
        await PlanningOperations.ResolveAsync(revision, runtime, Ct);
        Assert.Single(revision.Obligations); Assert.DoesNotContain(revision.Obligations, o => o.Id == retired);
        var restored = PlanningContext.Clone(revision); await PlanningOperations.ResolveAsync(restored, NoCalls(), Ct);
        Assert.Equal(revision.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
    }
}
