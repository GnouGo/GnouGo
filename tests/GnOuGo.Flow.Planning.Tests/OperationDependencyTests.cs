using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>All semantic responses here are synthetic, not substituted historical receipts.</summary>
public sealed class OperationDependencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime NoModel() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("Unexpected provider request.") };

    private static (PlanningSnapshot State, List<PlanningObligation> Operations) Setup(int count = 2)
    {
        var state = OperationAdmissionTests.State(string.Join(" ", Enumerable.Range(0, count).Select(i => $"Transform value {i}.")));
        foreach (var scope in PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request")) PlanningFixtures.Runtime(state, scope.Clause);
        OperationEffectFixtures.Seed(state, seedDependencies: false);
        return (state, OperationEffectFixtures.Staged(state));
    }

    private static JsonObject Answers(PlanningOperations.DependencyDomain domain, Func<string, string, bool>? data = null) => new(domain.Decisions.Select(d =>
        new KeyValuePair<string, JsonNode?>(d.Id, OperationEffectFixtures.DependencyAnswer(d.Schema,
            data?.Invoke(d.Context["producer"]!.ToString(), d.Context["consumer"]!.ToString()) == true))));

    private static void Install(PlanningSnapshot state, List<PlanningObligation> operations, PlanningOperations.DependencyDomain domain, JsonObject answers)
    {
        OperationEffectFixtures.SeedPages(state, domain.Decisions, answers);
        PlanningOperations.InstallDependencies(state, operations, domain, answers);
    }

    private static string Proofs(IEnumerable<PlanningObligation> operations) => string.Join("|", operations.OrderBy(o => o.Id, StringComparer.Ordinal)
        .Select(o => o.OperationAdmission!.Dependencies!.ProofFingerprint));

    [Fact]
    public void SelfAndPossibleButUnrealizedEffectsAreNeverDependencyEndpoints()
    {
        var (state, ops) = Setup();
        var domain = PlanningOperations.DependencyDecisions(state, ops);
        Assert.Equal(2, domain.Decisions.Length);
        Assert.All(domain.Decisions, d => Assert.NotEqual(d.Context["producer"]!.ToString(), d.Context["consumer"]!.ToString()));
        Assert.Empty(PlanningOperations.DependencyDecisions(state, [ops[0]]).Decisions);
        foreach (var scope in PlanningOperations.Scopes(state))
        {
            var answer = OperationEffectFixtures.Answer(state, scope);
            answer["producers"] = OperationEffectFixtures.Strings([ops[0].Id]);
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.EffectDecision(state, scope).Schema));
        }
    }

    [Fact]
    public async Task SoleOtherOperationDoesNotProveDependencyAndNoneIsEvidenceBound()
    {
        var (state, ops) = Setup(); var domain = PlanningOperations.DependencyDecisions(state, ops);
        Assert.Equal(2, domain.Decisions.Length);
        foreach (var decision in domain.Decisions)
        {
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["relation"] = "none" }, decision.Schema));
            var none = OperationEffectFixtures.DependencyAnswer(decision.Schema); none["consumerEvidence"] = new JsonArray("foreign");
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(none, decision.Schema));
        }
        Install(state, ops, domain, Answers(domain));
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Empty(PlanningOperations.EffectRelations(state.Obligations.Where(PlanningSourceDecisions.IsOperation)));
        Assert.Equal(2, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
        Assert.All(ops, o => Assert.Equal("none", Assert.Single(o.OperationAdmission!.Dependencies!.Assignments).Disposition));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void CyclesStopAsAWholeInEveryPairOrder(int count)
    {
        var (state, ops) = Setup(count); var ids = ops.Select(o => o.Id).ToArray();
        var domain = PlanningOperations.DependencyDecisions(state, ops);
        var answers = Answers(domain, (p, c) => Array.IndexOf(ids, c) == (Array.IndexOf(ids, p) + 1) % count);
        Assert.Throws<WorkflowRuntimeException>(() => Install(state, ops, domain, answers));
        var reverse = domain with { Decisions = domain.Decisions.Reverse().ToArray() };
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.InstallDependencies(state, ops.AsEnumerable().Reverse().ToList(), reverse, answers));
        Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation);
    }

    [Fact]
    public void EvidenceOperationPairAndPageOrderDoNotChangeTheDependencyProof()
    {
        var (state, ops) = Setup(3); var ids = ops.Select(o => o.Id).ToArray();
        var domain = PlanningOperations.DependencyDecisions(state, ops);
        var answers = Answers(domain, (p, c) => p == ids[0] && c == ids[2] || p == ids[1] && c == ids[2]);
        Install(state, ops, domain, answers); var expected = Proofs(ops);
        var clone = PlanningContext.Clone(state); clone.RuntimeEvidence.Reverse(); clone.References.Reverse();
        var reordered = OperationEffectFixtures.Staged(clone).AsEnumerable().Reverse().ToList();
        var reorderedDomain = PlanningOperations.DependencyDecisions(clone, reordered);
        Assert.Equal(domain.Fingerprint, reorderedDomain.Fingerprint);
        var reverse = new JsonObject(answers.Reverse().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value!.DeepClone())));
        PlanningOperations.InstallDependencies(clone, reordered, reorderedDomain with { Decisions = reorderedDomain.Decisions.Reverse().ToArray() }, reverse);
        Assert.Equal(expected, Proofs(reordered));
        Assert.Equal(2, reordered.SelectMany(o => o.OperationAdmission!.Dependencies!.Assignments).Count(a => a.Disposition == "data"));
    }

    [Fact]
    public void StableOwnedSourcesInDifferentSourceOrderHaveTheSameDependencyProof()
    {
        var fingerprints = new List<string>();
        foreach (var reverse in new[] { false, true })
        {
            var state = OperationAdmissionTests.State("Keep the requested transformations.");
            state.BusinessDecisions = [new() { Id = "first", Status = "accepted", CustomAnswer = "Transform the first value." },
                new() { Id = "second", Status = "accepted", CustomAnswer = "Transform the second value." }];
            if (reverse) state.BusinessDecisions.Reverse();
            foreach (var scope in PlanningOperations.SourceScopes(state).Where(s => s.Source.Id.StartsWith("business_answer_", StringComparison.Ordinal)))
                PlanningFixtures.Runtime(state, scope.Clause);
            OperationEffectFixtures.Seed(state, seedDependencies: false);
            var ops = OperationEffectFixtures.Staged(state); var domain = PlanningOperations.DependencyDecisions(state, ops);
            var ids = ops.Select(o => o.Id).Order(StringComparer.Ordinal).ToArray();
            Install(state, ops, domain, Answers(domain, (p, c) => p == ids[0] && c == ids[1]));
            fingerprints.Add(Proofs(ops));
        }
        Assert.Equal(fingerprints[0], fingerprints[1]);
    }

    [Fact]
    public void DependencyAssessmentCannotCreateCrossWorkflowVisibility()
    {
        var state = OperationAdmissionTests.State("Transform the first value. Transform the second value. Create another workflow.");
        PolicyGroundingTests.Add(state, "request", "Create another workflow.", "child_scope", "workflow_boundary");
        foreach (var scope in PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request").Take(2)) PlanningFixtures.Runtime(state, scope.Clause);
        var scopes = PlanningOperations.Scopes(state);
        OperationEffectFixtures.Seed(state, scope => OperationEffectFixtures.Answer(state, scope,
            [PlanningOperations.EffectDomain(state, scope.Evidence!).Single(p => p.Value.BoundaryReference == scope.Evidence!.ActionReference &&
                p.Value.WorkflowScope == (scope.Evidence.Id == scopes[0].Evidence!.Id ? "main" : "child_scope")).Key]), seedDependencies: false);
        var ops = OperationEffectFixtures.Staged(state); var domain = PlanningOperations.DependencyDecisions(state, ops);
        Assert.Empty(domain.Decisions); Assert.Empty(domain.Facts);
        var forged = new JsonObject { [PlanningOperations.DependencyDecisionId(ops[0].Id, ops[1].Id)] = new JsonObject { ["relation"] = "data" } };
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.InstallDependencies(state, ops, domain, forged));
        Assert.Contains(state.Obligations, o => o.Id == "child_scope");
    }

    [Fact]
    public async Task ResourceOwnershipAndPermissionRemainDownstreamAndDataCannotBeReintroduced()
    {
        var state = OperationAdmissionTests.State("Create a resource. Delete that owned resource.");
        var scopes = PlanningOperations.SourceScopes(state);
        PlanningFixtures.Runtime(state, scopes[0].Clause, "resource_lifecycle", resourceAction: "create");
        PlanningFixtures.Runtime(state, scopes[1].Clause, "cleanup", resourceAction: "delete");
        OperationEffectFixtures.Seed(state);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Empty(PlanningOperations.EffectRelations(state.Obligations));
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            var properties = request.StructuredOutputSchema!["properties"]!.AsObject();
            Assert.All(properties, p => Assert.DoesNotContain("data", p.Value!["enum"]!.AsArray().Select(v => v!.ToString())));
            Assert.Contains(properties, p => p.Value!["enum"]!.AsArray().Any(v => v!.ToString() == "owned_resource"));
            return Task.FromResult(new LLMResponse { Json = new JsonObject(properties.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("none")))) });
        } };
        await PlanningSourceDecisions.RelateAsync(state, runtime, Ct);
        Assert.Single(runtime.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentPackingAndPartialCheckpointProduceIdenticalProof(bool interrupt)
    {
        var (state, ops) = Setup(4); var ids = ops.Select(o => o.Id).Order(StringComparer.Ordinal).ToArray();
        var domain = PlanningOperations.DependencyDecisions(state, ops);
        var answers = Answers(domain, (p, c) => p == ids[0] && c == ids[3]);
        var expectedState = PlanningContext.Clone(state); var expectedOps = OperationEffectFixtures.Staged(expectedState);
        Install(expectedState, expectedOps, PlanningOperations.DependencyDecisions(expectedState, expectedOps), answers);
        // Change only test packing, never evidence or production limits.
        state.Request.Generation.MaxInputTokensPerRequest = 3500;
        OperationEffectFixtures.Seed(state, seedDependencies: false);
        Assert.True(PlanningDecisionPages.PackedPageCount(state, domain.Decisions) > 1);
        PlanningSnapshot? checkpoint = null;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { CompletionStatus = "completed", Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
            new KeyValuePair<string, JsonNode?>(p.Key, answers[p.Key]!.DeepClone()))) }) };
        if (interrupt) runtime.OnCheckpoint = snapshot =>
        {
            if (checkpoint is null && snapshot.DecisionPages.Any(p => p.Status == "completed" && p.Decisions.Any(id => id.StartsWith("data_", StringComparison.Ordinal))))
            { checkpoint = PlanningContext.Clone(snapshot); throw new OperationCanceledException("Synthetic crash after a verified dependency page."); }
            return Task.CompletedTask;
        };
        if (interrupt)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
            Assert.NotNull(checkpoint); Assert.DoesNotContain(checkpoint.Obligations, PlanningSourceDecisions.IsOperation);
            state = checkpoint; runtime.OnCheckpoint = null;
        }
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        Assert.Equal(Proofs(expectedOps), Proofs(state.Obligations.Where(PlanningSourceDecisions.IsOperation)));
        var counts = state.RequestAccounting.Count; var proof = state.OperationAdmissionFingerprint;
        var restored = PlanningContext.Clone(state); await PlanningOperations.ResolveAsync(restored, NoModel(), Ct);
        Assert.Equal(counts, restored.RequestAccounting.Count); Assert.Equal(proof, restored.OperationAdmissionFingerprint);
        Assert.Empty(restored.RepairAllowances);
        var requested = runtime.Requests.SelectMany(r => r.StructuredOutputSchema!["properties"]!.AsObject().Select(p => p.Key)).ToArray();
        Assert.Equal(domain.Decisions.Length, requested.Length); Assert.Equal(requested.Length, requested.Distinct().Count());
    }

    private static (PlanningSnapshot State, List<PlanningObligation> Operations) Baseline(bool call = false, bool cycle = false, bool missingInterface = false)
    {
        var state = OperationAdmissionTests.State("Keep both existing operations.");
        var first = new PlanningNode { Key = "producer", Type = "set", Input = new() { Kind = "string", Text = "value" } };
        var second = new PlanningNode { Key = "consumer", Type = "set", Input = new() { Kind = "output", Source = "producer" } };
        var main = new PlanningWorkflow { Key = "main", Steps = [first, second] };
        state.Request.Baseline = new() { Entrypoint = "main", Workflows = [main] };
        if (cycle) first.Input = new() { Kind = "output", Source = "consumer" };
        if (call)
        {
            first.Type = "workflow.call"; first.Input = TypedPlannerTests.Obj(("ref", new() { Kind = "workflow", Source = "child" }), ("args", TypedPlannerTests.Obj()));
            var child = TypedPlannerTests.Graph().Workflows[0]; child.Key = "child";
            if (!missingInterface) state.Request.Baseline.Workflows.Add(child);
        }
        var source = PlanningOperations.SourceScopes(state).First(s => s.Source.Authority == PlanningSourceAuthority.ExistingBehavior);
        foreach (var node in PlanningSourceGroundingRules.BaselineNodes(state).Where(p => p.Value.Workflow == "main"))
        {
            // Distinct owned baseline node authority; each retains the same complete source clause.
            var reference = source.Clause with { Id = "synthetic_baseline_" + node.Value.Node.Key, Start = source.Source.Text.IndexOf("\"" + node.Value.Node.Key + "\"", StringComparison.Ordinal) + 1, Length = node.Value.Node.Key.Length }; state.References.Add(reference);
            PlanningFixtures.Runtime(state, reference, baseline: node.Key);
        }
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        OperationEffectFixtures.Seed(state, seedDependencies: false);
        return (state, OperationEffectFixtures.Staged(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactBaselineAndCallerInterfaceAreRequiredWithoutModelChoices(bool call)
    {
        var (state, ops) = Baseline(call);
        var domain = PlanningOperations.DependencyDecisions(state, ops);
        Assert.Empty(domain.Decisions);
        var edge = Assert.Single(domain.Facts, f => f.Disposition == "data");
        Assert.Equal(call ? PlanningDependencyOrigin.DeterministicInterface : PlanningDependencyOrigin.DeterministicBaseline, edge.Origin);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Contains(new PlanningObligationRelation(edge.Producer, edge.Consumer, "data"), PlanningOperations.EffectRelations(state.Obligations));
        Assert.Empty(state.RequestAccounting);
        // A forged none cannot suppress a required baseline producer.
        var consumer = state.Obligations.Single(o => o.Id == edge.Consumer);
        var proof = consumer.OperationAdmission!.Dependencies!;
        proof.Assignments[0] = proof.Assignments[0] with { Disposition = "none" };
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(state));
    }

    [Fact]
    public void RequiredMissingAndCyclicBaselineProducersStopBeforeDispatch()
    {
        var (state, ops) = Baseline();
        var consumer = ops.Single(o => PlanningOperations.EffectAnchor(state, o).BoundaryReference == PlanningSourceGroundingRules.BaselineNodes(state).Single(p => p.Value.Node.Key == "consumer").Key);
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.DependencyDecisions(state, [consumer]));
        var cycle = Baseline(cycle: true);
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.DependencyDecisions(cycle.State, cycle.Operations));
        Assert.Empty(state.RequestAccounting);
    }

    [Fact]
    public void DependencyResolutionCannotInventAMissingCalleeInterface()
    {
        var (state, ops) = Baseline(call: true, missingInterface: true);
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.DependencyDecisions(state, ops));
        Assert.Empty(state.RequestAccounting);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("origin")]
    [InlineData("fingerprint")]
    [InlineData("receipt")]
    [InlineData("legacy")]
    public async Task RestoredOrForgedDependencyProofFailsClosed(string defect)
    {
        var (state, ops) = Setup(); var domain = PlanningOperations.DependencyDecisions(state, ops);
        Install(state, ops, domain, Answers(domain, (p, c) => p == ops[0].Id && c == ops[1].Id));
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var op = state.Obligations.First(PlanningSourceDecisions.IsOperation); var proof = op.OperationAdmission!.Dependencies!;
        if (defect == "self") proof.Assignments[0] = proof.Assignments[0] with { Producer = op.Id, Disposition = "data" };
        if (defect == "origin") proof.Assignments[0] = proof.Assignments[0] with { Origin = PlanningDependencyOrigin.DeterministicBaseline };
        if (defect == "fingerprint") op = PlanningOperations.Prove(state, op, op.OperationAdmission with { Dependencies = proof with { DomainFingerprint = "stale" } });
        if (defect == "receipt") state.DecisionPages.RemoveAll(p => p.Decisions.Any(id => id.StartsWith("data_", StringComparison.Ordinal)));
        if (defect == "legacy") op.OperationAdmission!.Assignments[0].Effect!.Producers.Add(op.Id);
        state.Obligations[state.Obligations.FindIndex(o => o.Id == op.Id)] = op;
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(state));
    }

    [Fact]
    public void UnresolvedCannotBecomeAnEmptyDependencyProof()
    {
        var (state, ops) = Setup(); var domain = PlanningOperations.DependencyDecisions(state, ops);
        var answers = Answers(domain); answers[domain.Decisions[0].Id] = new JsonObject { ["relation"] = "unresolved" };
        Assert.Throws<WorkflowRuntimeException>(() => Install(state, ops, domain, answers));
        Assert.All(ops, o => Assert.Null(o.OperationAdmission!.Dependencies));
    }
}
