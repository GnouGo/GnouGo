using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.DeclarationGroundingTests;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Correction responses are synthetic. Captured initial decisions are never substituted into historical receipts.</summary>
public sealed class CanonicalRootTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningDeclarationAssignment Deferred(string id) => new(id, "deferred_attachment", null, null, null, "unspecified", null);
    private static (PlanningSnapshot State, List<PlanningDeclarationAssignment> Initial, List<PlanningDeclarationAssignment> Final) Fixture(string name = "parcel", string other = "receipt", bool acrossClauses = false)
    {
        var text = $"Required input {name} is an object. " + (acrossClauses ? $"The required input {name} is supplied. " : "") + $"Required output {other} is returned.";
        var state = State(text); Add(state, $"Required input {name}", "a");
        Add(state, acrossClauses ? $"The required input {name}" : $"{name} is an object.", "b");
        Add(state, $"Required output {other}", "neighbor");
        var initial = new List<PlanningDeclarationAssignment> { Distinct(state, "a", name), Distinct(state, "b", name), Distinct(state, "neighbor", other, direction: "output") };
        var final = Canonicalize(state, [initial[0], Link("b", "a"), initial[2]]);
        return (state, initial, final);
    }
    private static TypedPlannerTests.FakeRuntime Runtime(List<PlanningDeclarationAssignment> initial, List<PlanningDeclarationAssignment> final,
        Func<LLMRequest, JsonObject>? correction = null) => new()
    {
        OnCall = (phase, request, _) =>
        {
            var root = request.StructuredOutputSchema!["properties"]!.AsObject().All(p => p.Key.StartsWith("declarations_roots_", StringComparison.Ordinal));
            var values = phase.EndsWith("_repair", StringComparison.Ordinal)
                ? correction?.Invoke(request) ?? Response(request, final.Select(PlanningDeclarations.RootAssignment))
                : Response(request, root ? initial : final);
            return Task.FromResult(new LLMResponse { Json = values, CompletionStatus = "completed",
                Usage = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 30 } });
        }
    };

    [Theory]
    [InlineData("parcel", "receipt", false)]
    [InlineData("input", "output", false)] // Names are source values, not reserved keywords.
    [InlineData("entry", "assessment", true)]
    public async Task CompatibleDuplicatesGetOneCoupledCorrectionAndPreserveNeighbors(string name, string other, bool acrossClauses)
    {
        var (state, initial, final) = Fixture(name, other, acrossClauses);
        var original = JsonSerializer.Serialize(initial, PlanningJsonContext.Default.ListPlanningDeclarationAssignment);
        var decision = Assert.Single(PlanningDeclarations.RootCorrections(state, initial));
        Assert.Equal(["a", "b"], decision.Schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("neighbor", decision.Schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(acrossClauses ? 2 : 1, decision.SourceDecisionIds!.Count);
        var runtime = Runtime(initial, final);
        await PlanningDeclarations.ResolveAsync(state, runtime, Ct); PlanningFixtures.RefreshAdmission(state);
        Assert.Equal(2, state.Declarations.Count);
        var input = state.Declarations.Single(d => d.Direction == "input");
        Assert.Equal(name, PlanningDeclarations.Name(state, input)); Assert.Equal(["a", "b"], input.Candidates); Assert.Equal(["b"], input.Aliases);
        Assert.Equal(initial[2], state.DeclarationAssignments.Single(a => a.CandidateId == "neighbor"));
        Assert.Equal(original, JsonSerializer.Serialize(initial, PlanningJsonContext.Default.ListPlanningDeclarationAssignment));
        Assert.Single(runtime.Phases, p => p == "intent_declarations_repair");
        Assert.Equal(1, Assert.Single(state.RepairAllowances).Attempts);
        Assert.Equal(acrossClauses ? 2 : 1, state.DecisionCorrections.Count);
        var rootPage = state.DecisionPages.Single(p => p.Decisions.Any(d => d.StartsWith("declarations_roots_", StringComparison.Ordinal)));
        Assert.Contains("distinct_input", rootPage.Candidate!.ToJsonString());
        var snapshot = PlanningContext.Clone(state); var calls = runtime.Requests.Count; var counts = snapshot.GateProgress.Sum(g => g.Failures);
        await PlanningDeclarations.ResolveAsync(snapshot, runtime, Ct);
        Assert.Equal(calls, runtime.Requests.Count); Assert.Equal(counts, snapshot.GateProgress.Sum(g => g.Failures));
        Assert.Equal(state.DeclarationFingerprint, snapshot.DeclarationFingerprint);
    }

    [Fact]
    public async Task CanonicalIdentityDoesNotDependOnTheWinningCandidateOrNameReference()
    {
        var (state, initial, final) = Fixture(acrossClauses: true);
        await PlanningDeclarations.ResolveAsync(state, Runtime(initial, final), Ct);
        var other = Fixture(acrossClauses: true);
        var swapped = Canonicalize(other.State, [Link("a", "b"), other.Initial[1], other.Initial[2]]);
        await PlanningDeclarations.ResolveAsync(other.State, Runtime(other.Initial, swapped), Ct);
        var left = state.Declarations.Single(d => d.Direction == "input"); var right = other.State.Declarations.Single(d => d.Direction == "input");
        Assert.NotEqual(left.NameReference, right.NameReference);
        Assert.Equal(left.Id, right.Id); Assert.NotEqual(left.ProofFingerprint, right.ProofFingerprint);
        Assert.NotEqual(PlanningDeclarations.CanonicalId("value", "main", "input"), PlanningDeclarations.CanonicalId("Value", "main", "input"));
        Assert.NotEqual(PlanningDeclarations.CanonicalId("a:b", "c", "input"), PlanningDeclarations.CanonicalId("a", "b:c", "input"));
    }

    [Fact]
    public void BaselineAndNewDeclarationsUseTheSamePublicIdentity()
    {
        var (state, initial, _) = Fixture();
        PlanningDeclarations.Commit(state, [initial[0], Retire("b"), initial[2]], PlanningDeclarations.EvidenceFingerprint(state));
        var id = state.Declarations.Single(d => d.Direction == "input").Id;
        var baseline = State("Use the existing public contract."); baseline.Request.Baseline = new() { Entrypoint = "flow", Workflows = [new()
            { Key = "flow", Inputs = [new() { Name = "parcel", Required = true, Schema = new() { Type = "object" } }] }] };
        PlanningDeclarations.Commit(baseline, [], PlanningDeclarations.EvidenceFingerprint(baseline));
        Assert.Equal(id, Assert.Single(baseline.Declarations).Id);
        Assert.NotEqual(PlanningDeclarations.CanonicalId("parcel", "other", "input"), id);
        Assert.NotEqual(PlanningDeclarations.CanonicalId("parcel", "main", "output"), id);
    }

    [Fact]
    public async Task AnotherEvidencedNameOrRetirementCanResolveTheDuplicate()
    {
        foreach (var retire in new[] { false, true })
        {
            var state = State("Required inputs alpha and beta are supplied."); Add(state, "Required inputs alpha", "a"); Add(state, "alpha and beta", "b");
            var initial = new List<PlanningDeclarationAssignment> { Distinct(state, "a", "alpha"), Distinct(state, "b", "alpha") };
            var final = new List<PlanningDeclarationAssignment> { initial[0], retire ? Retire("b") : Distinct(state, "b", "beta") };
            await PlanningDeclarations.ResolveAsync(state, Runtime(initial, final), Ct);
            Assert.Equal(retire ? 1 : 2, state.Declarations.Count); Assert.Equal(1, state.RepairAllowances.Sum(a => a.Attempts));
        }
    }

    [Theory]
    [InlineData("presence")]
    [InlineData("default")]
    public void ContradictoryRootContractsDoNotGetACorrection(string defect)
    {
        var (state, initial, _) = Fixture();
        initial[1] = defect == "presence" ? initial[1] with { Presence = "optional" } : initial[1] with { DefaultReference = initial[1].NameReference };
        Assert.Throws<WorkflowRuntimeException>(() => PlanningDeclarations.RootCorrections(state, initial));
        Assert.Empty(state.DecisionCorrections); Assert.Empty(state.RepairAllowances);
    }

    [Theory]
    [InlineData("presence")]
    [InlineData("direction")]
    [InlineData("scope")]
    [InlineData("neighbor")]
    [InlineData("name")]
    [InlineData("default")]
    public void CorrectionSchemaPreventsChangingContractsOrNeighbors(string defect)
    {
        var (state, initial, _) = Fixture(); var d = Assert.Single(PlanningDeclarations.RootCorrections(state, initial));
        var answer = new JsonObject { ["a"] = PlanningDeclarations.Assignment(initial[0]), ["b"] = PlanningDeclarations.Assignment(Deferred("b")) };
        if (defect == "presence") answer["a"]!["presence"] = "optional";
        if (defect == "direction") answer["a"]!["disposition"] = "distinct_output";
        if (defect == "scope") answer["a"]!["scope"] = "foreign";
        if (defect == "neighbor") answer["neighbor"] = PlanningDeclarations.Assignment(initial[2]);
        if (defect == "name") answer["a"]!["name"] = initial[2].NameReference;
        if (defect == "default") answer["a"]!["default"] = initial[0].NameReference;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, d.Schema));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedOrInvalidCorrectionStopsWithoutAnotherRound(bool invalid)
    {
        var (state, initial, _) = Fixture();
        var runtime = Runtime(initial, initial, r =>
        {
            var values = Response(r, initial);
            if (invalid) values.First().Value!["a"]!["presence"] = "optional";
            return values;
        });
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningDeclarations.ResolveAsync(state, runtime, Ct));
        Assert.Single(runtime.Phases, p => p.EndsWith("repair", StringComparison.Ordinal));
        Assert.Equal(1, state.RepairAllowances.Sum(a => a.Attempts)); Assert.Empty(state.Declarations);
        var calls = runtime.Requests.Count;
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningDeclarations.ResolveAsync(PlanningContext.Clone(state), runtime, Ct));
        Assert.Equal(calls, runtime.Requests.Count);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("staged")]
    [InlineData("completed")]
    public async Task RestartReusesTheCorrectionIdentityAndCompletedRoots(string checkpoint)
    {
        var (state, initial, final) = Fixture(); var runtime = Runtime(initial, final); PlanningSnapshot? saved = null;
        runtime.OnCheckpoint = snapshot =>
        {
            if (snapshot.DecisionPages.Any(p => p.SourceDecisionIds is not null && p.Status == checkpoint))
            { saved = PlanningContext.Clone(snapshot); throw new OperationCanceledException("synthetic checkpoint"); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningDeclarations.ResolveAsync(state, runtime, Ct));
        Assert.NotNull(saved); var page = Assert.Single(saved.DecisionPages, p => p.SourceDecisionIds is not null);
        var resumed = Runtime(initial, final);
        await PlanningDeclarations.ResolveAsync(saved, resumed, Ct);
        Assert.Equal(page.Id, Assert.Single(saved.DecisionPages, p => p.SourceDecisionIds is not null).Id);
        Assert.Equal(page.RequestId, Assert.Single(saved.DecisionPages, p => p.SourceDecisionIds is not null).RequestId);
        Assert.DoesNotContain(resumed.Requests, r => r.StructuredOutputSchema!["properties"]!.AsObject().Any(p => p.Key.StartsWith("declarations_roots_", StringComparison.Ordinal)));
        Assert.Equal(checkpoint == "pending" ? 1 : 0, resumed.Phases.Count(p => p.EndsWith("repair", StringComparison.Ordinal)));
        Assert.Equal(1, saved.RepairAllowances.Sum(a => a.Attempts));
    }

    [Fact]
    public async Task ZeroRepairAllowanceStopsBeforeTheCorrectionDispatch()
    {
        var (state, initial, final) = Fixture(); state.Request.MaxRepairsPerWorkflowGate = 0; var runtime = Runtime(initial, final);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningDeclarations.ResolveAsync(state, runtime, Ct));
        Assert.Equal("REPAIR_EXHAUSTED", error.Code); Assert.Single(runtime.Requests); Assert.Empty(state.DecisionCorrections);
    }

    [Fact]
    public async Task CapturedDuplicateRootReceiptHasABoundedSyntheticCorrectionToTheThreePublicPorts()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "duplicate-roots-stage1.json"), Ct))!;
        var state = JsonSerializer.Deserialize(fixture["snapshot"]!.ToJsonString(), PlanningJsonContext.Default.PlanningSnapshot)!;
        state.Preparation = TypedPlannerTests.Preparation();
        PlanningFixtures.AdmitHints(state);
        var initial = fixture["rootCandidate"]!.AsObject().SelectMany(p => p.Value!.AsObject()).Select(p =>
        {
            var v = p.Value!;
            return new PlanningDeclarationAssignment(p.Key, v["disposition"]!.ToString(), null, v["name"]?.ToString(), v["scope"]?.ToString(),
                v["presence"]?.ToString() ?? "unspecified", null) { DeclarationReference = v["declaration"]?.ToString(), PresenceReference = v["presenceEvidence"]?.ToString() };
        }).ToList();
        Assert.Equal(2, initial.Count(a => a.NameReference is not null && PlanningDeclarations.SourceName(state, a.NameReference) == "input"));
        const string winner = "ob_e2e1d85d32959b66", overlap = "ob_44c3a5319cbc8547", threshold = "ob_391eff8e3e36fc8a", output = "ob_3956dfa516982d75";
        var final = initial.Select(a => a.CandidateId switch
        {
            winner => a with { NameReference = Token(state, winner, "record") },
            overlap => Link(overlap, winner),
            "ob_393c11626c8f47af" => Link(a.CandidateId, threshold),
            _ => a
        }).ToList();
        foreach (var (id, target) in new[] { ("ob_31994882d225f429", winner), ("ob_71bacc0dc31855eb", winner), ("ob_b27a5cc904dc25b8", threshold),
            ("ob_576b3dd237abc52e", output), ("ob_2675816419f003ee", output), ("ob_22fe78002892f262", output) })
            final.Add(Link(id, target, "modifier_of"));
        final.Add(Link("ob_0fed767efbdf95bc", threshold, "modifier_of", "optional", Token(state, "ob_0fed767efbdf95bc", "100")));
        final = Canonicalize(state, final);
        var runtime = Runtime(initial, final);
        await PlanningDeclarations.ResolveAsync(state, runtime, Ct); PlanningFixtures.RefreshAdmission(state);
        Assert.Equal(initial.Single(a => a.CandidateId == threshold), state.DeclarationAssignments.Single(a => a.CandidateId == threshold));
        Assert.Equal(initial.Single(a => a.CandidateId == output), state.DeclarationAssignments.Single(a => a.CandidateId == output));
        var plan = PlanningBehaviorDecisions.Assemble(state, new()); var workflow = Assert.Single(plan.Workflows);
        Assert.Equal(["record", "threshold"], workflow.Inputs.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(workflow.Inputs.Single(p => p.Name == "record").Required); Assert.False(workflow.Inputs.Single(p => p.Name == "threshold").Required);
        Assert.Equal(100m, PlanningDeclarations.Default(state, state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold"))!.Number);
        var result = Assert.Single(workflow.Outputs); Assert.Equal("classifiedResult", result.Name); Assert.True(result.Required);
        Assert.Contains("category has exactly the values rejected, high, standard.", result.Description);
        Assert.Contains("Preserve the original id and amount.", result.Description);
        Assert.Empty(PlanningDeclarations.ValidateBehavior(state, plan));
        Assert.Single(state.DecisionPages, p => p.Origin == PlanningDecisionPageOrigin.SemanticCorrection);
        Assert.Equal(1, state.RepairAllowances.Sum(a => a.Attempts));
    }

    [Fact]
    public void DuplicateGroupsSharingAClauseStayInOneCoupledCorrection()
    {
        var state = State("Required inputs first and second are supplied.");
        foreach (var id in new[] { "a", "b", "c", "d" }) Add(state, state.Request.Prompt, id);
        var roots = new List<PlanningDeclarationAssignment> { Distinct(state, "a", "first"), Distinct(state, "b", "first"), Distinct(state, "c", "second"), Distinct(state, "d", "second") };
        var decision = Assert.Single(PlanningDeclarations.RootCorrections(state, roots));
        Assert.Equal(4, decision.Schema["properties"]!.AsObject().Count); Assert.Single(decision.SourceDecisionIds!);
    }

    [Fact]
    public async Task ASchemaCorrectionOfTheOriginalDecisionCannotGainAnotherIdentityCorrection()
    {
        var (state, initial, final) = Fixture(); var decision = Assert.Single(PlanningDeclarations.RootCorrections(state, initial));
        PlanningDecisionPages.RecordCorrections(state, decision.SourceDecisionIds!, decision.EvidenceFingerprint, "$plan", PlanningGates.Response);
        var runtime = Runtime(initial, final);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningDeclarations.ResolveAsync(state, runtime, Ct));
        Assert.Equal("DECISION_CORRECTION_EXHAUSTED", error.Code); Assert.DoesNotContain(runtime.Phases, p => p.EndsWith("repair", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIndivisibleOversizedConflictCannotDispatchACorrection()
    {
        var (state, initial, final) = Fixture();
        await PlanningDecisionPages.ResolveAsync(state, Runtime(initial, final), "intent_declarations", "$plan", PlanningDeclarations.Decisions(state), Ct);
        var correction = Assert.Single(PlanningDeclarations.RootCorrections(state, initial)); correction.Context["largeEvidence"] = new string('x', 60000);
        var runtime = Runtime(initial, final);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningDecisionPages.ResolveCorrectionsAsync(state, runtime,
            "intent_declarations", "$plan", PlanningGates.Response, [correction], Ct));
        Assert.Equal("DECISION_SIZE_UNSUPPORTED", error.Code); Assert.Empty(runtime.Requests); Assert.Empty(state.RepairAllowances);
    }

    [Fact]
    public async Task UnverifiableRootCorrectionStopsTheSessionWithoutRedispatch()
    {
        var (state, initial, final) = Fixture(); state.Intent.Checked = true; state.Preparation = null;
        var runtime = Runtime(initial, final); var respond = runtime.OnCall!;
        runtime.OnCall = (phase, request, ct) => phase.EndsWith("repair", StringComparison.Ordinal)
            ? throw new LLMClientException(LLMClientFailureKind.Transport, "Synthetic unverifiable transport failure.", true)
            : respond(phase, request, ct);
        var planner = new TypedWorkflowPlanner();
        var stopped = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, stopped.Status); Assert.True(stopped.TechnicalStop!.Unverifiable);
        Assert.Equal(1, stopped.RepairAllowances.Sum(a => a.Attempts)); Assert.Equal(2, runtime.Requests.Count);
        var restored = await planner.AdvanceAsync(PlanningContext.Clone(stopped), new() { ExpectedRevision = stopped.Revision }, runtime, Ct);
        Assert.Equal(2, runtime.Requests.Count); Assert.Equal(PlanningStatus.Stopped, restored.Status);
    }

    [Fact]
    public void SameSpellingInDifferentDirectionsIsNotADuplicate()
    {
        var state = State("Required input value is supplied. Required output value is returned.");
        Add(state, "Required input value", "a"); Add(state, "Required output value", "b");
        var roots = new List<PlanningDeclarationAssignment> { Distinct(state, "a", "value"), Distinct(state, "b", "value", direction: "output") };
        Assert.Empty(PlanningDeclarations.RootCorrections(state, roots));
        PlanningDeclarations.Commit(state, roots, PlanningDeclarations.EvidenceFingerprint(state));
        Assert.Equal(2, state.Declarations.Count); Assert.NotEqual(state.Declarations[0].Id, state.Declarations[1].Id);
    }
}
