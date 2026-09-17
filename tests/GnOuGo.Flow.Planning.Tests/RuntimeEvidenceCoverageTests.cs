using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.DeclarationGroundingTests;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic decisions exercise retained failure classes without substituting live receipts.</summary>
public sealed class RuntimeEvidenceCoverageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Task ResolveGrounded(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (state.OperationAdmissionFingerprint is null) OperationEffectFixtures.Seed(state);
        return PlanningOperations.ResolveAsync(state, runtime, ct);
    }
    private static TypedPlannerTests.FakeRuntime NoModel() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("No model ambiguity.") };

    [Fact]
    public async Task ConstraintAuthorityRemovesEveryRuntimeChoiceAndReplaysEnginePolicy()
    {
        var state = OperationAdmissionTests.State("Build a workflow.");
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Confirm before external effects. Do not show implementation details." };
        var decisions = PlanningSourceDecisions.InterpretationDecisions(state);
        var policies = decisions.Where(d => d.Context["role"]!.ToString() == "host_constraint").ToArray();
        Assert.NotEmpty(policies);
        foreach (var decision in policies)
        {
            Assert.Null(decision.Schema["properties"]!["runtime"]);
            Assert.Null(decision.Context["runtimeTask"]); Assert.Null(decision.Context["subjects"]);
            Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["obligations"] = new JsonArray() }, decision.Schema));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["obligations"] = new JsonArray(),
                ["runtime"] = new JsonArray(new JsonObject { ["role"] = "unresolved" }) }, decision.Schema));
        }
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        {
            Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
            {
                var value = new JsonObject { ["obligations"] = new JsonArray() };
                if (p.Value!["properties"]!["runtime"] is not null) value["runtime"] = new JsonArray(new JsonObject { ["role"] = "planning_directive" });
                return new KeyValuePair<string, JsonNode?>(p.Key, value);
            }))
        }) };
        await PlanningSourceDecisions.InterpretAsync(state, runtime, Ct);
        Assert.Equal(policies.Length, state.RuntimeEvidence.Count(e => e.Origin == PlanningRuntimeEvidenceOrigin.EngineSourceAuthority));
        Assert.All(state.RuntimeEvidence.Where(e => e.Origin == PlanningRuntimeEvidenceOrigin.EngineSourceAuthority), e =>
        { Assert.Equal("policy", e.Role); Assert.Equal(PlanningRuntimeExecutionScope.Policy, e.ExecutionScope); Assert.Null(e.ActionReference); });
        var restored = PlanningContext.Clone(state);
        await PlanningSourceDecisions.InterpretAsync(restored, NoModel(), Ct);
        Assert.Equal(state.RuntimeEvidenceFingerprint, restored.RuntimeEvidenceFingerprint);
        Assert.Equal(state.RequestAccounting.Count, restored.RequestAccounting.Count);
        await ResolveGrounded(restored, NoModel(), Ct);
        Assert.DoesNotContain(restored.Obligations, PlanningSourceDecisions.IsOperation);
    }

    [Theory]
    [InlineData("unresolved", PlanningRuntimeEvidenceOrigin.SourceInterpretation)]
    [InlineData("policy", PlanningRuntimeEvidenceOrigin.SourceInterpretation)]
    [InlineData("contract", PlanningRuntimeEvidenceOrigin.EngineSourceAuthority)]
    public void HistoricalOrForgedHostRuntimeProofCannotAuthorizeContinuation(string role, PlanningRuntimeEvidenceOrigin origin)
    {
        var state = OperationAdmissionTests.State("Transform a value.");
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Only permitted effects are allowed." };
        PlanningFixtures.EmptyRuntime(state);
        var policy = state.RuntimeEvidence.Single(e => e.Origin == PlanningRuntimeEvidenceOrigin.EngineSourceAuthority);
        var invalid = PlanningOperations.SealRuntime(state, policy with { Role = role, Origin = origin });
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ValidateRuntime(state, invalid));
    }

    [Theory]
    [InlineData("Preserve the original members.")]
    [InlineData("Keep the original payload unchanged.")]
    [InlineData("The domain contains exactly first and second.")]
    [InlineData("The value is a non-nullable object.")]
    public async Task AttachedContractNeverCreatesOrReusesALocalOccurrence(string constraint)
    {
        var state = State("Required output result. Transform the supplied value. " + constraint);
        Add(state, "Required output result.", "result"); Add(state, constraint, "constraint", "declaration_constraint");
        var action = PlanningFixtures.Runtime(state, Span(state, "Transform the supplied value."));
        var covered = PlanningFixtures.Runtime(state, Span(state, constraint));
        var assignments = Canonicalize(state, [Distinct(state, "result", "result", direction: "output"), Link("constraint", "result", "modifier_of")]);
        PlanningDeclarations.Commit(state, assignments, PlanningDeclarations.EvidenceFingerprint(state)); state.OperationAdmissionFingerprint = null;
        var before = state.DeclarationFingerprint;
        await ResolveGrounded(state, NoModel(), Ct);
        Assert.Equal(action.Id, Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation).OperationAdmission!.Assignments.Single().RuntimeEvidenceId);
        Assert.Contains(covered, state.RuntimeEvidence); Assert.Single(PlanningOperations.DeclarationExclusions(state));
        Assert.Contains(Assert.Single(state.Declarations).ModifierReferences, id => PlanningChoiceEvidence.Text(state, id) == constraint);
        Assert.Equal(before, state.DeclarationFingerprint);
        var restored = PlanningContext.Clone(state); var events = restored.Events.Count;
        await ResolveGrounded(restored, NoModel(), Ct);
        Assert.Equal(events, restored.Events.Count); Assert.Equal(state.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
        var uncommitted = PlanningContext.Clone(state); uncommitted.OperationAdmissionFingerprint = null;
        uncommitted.Obligations.RemoveAll(o => o.OperationAdmission is not null);
        var governing = PlanningOperations.SealRuntime(uncommitted, covered with { EvidenceRole = "governing", OccurrenceBoundary = null });
        uncommitted.RuntimeEvidence[uncommitted.RuntimeEvidence.FindIndex(e => e.Id == covered.Id)] = governing;
        uncommitted.RuntimeEvidenceFingerprint = PlanningOperations.RuntimeFingerprint(uncommitted);
        await ResolveGrounded(uncommitted, NoModel(), Ct);
        Assert.Single(Assert.Single(uncommitted.Obligations, PlanningSourceDecisions.IsOperation).OperationAdmission!.Assignments);
    }

    [Theory]
    [InlineData("Return result:string", "transform the supplied value.", false)]
    [InlineData("Return result:string", "result:string and transform the supplied value.", true)]
    [InlineData("Return result:string", "Return result:string", false)]
    public async Task ExactCoveragePreservesSeparateActionsAndStopsPartialEvidence(string contract, string actionText, bool partial)
    {
        var state = State("Required output result. Return result:string and transform the supplied value.");
        Add(state, "Required output result.", "result"); Add(state, contract, "contract", "declaration_constraint");
        var action = PlanningFixtures.Runtime(state, Span(state, actionText));
        PlanningDeclarations.Commit(state, Canonicalize(state, [Distinct(state, "result", "result", direction: "output"), Link("contract", "result", "modifier_of")]), PlanningDeclarations.EvidenceFingerprint(state));
        state.OperationAdmissionFingerprint = null;
        if (partial)
        {
            var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => ResolveGrounded(state, NoModel(), Ct));
            Assert.Contains(action.Id, error.Details!["location"]!.ToString()); return;
        }
        await ResolveGrounded(state, NoModel(), Ct);
        Assert.Equal(actionText == contract ? 0 : 1, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
    }

    [Fact]
    public async Task UnionOfAttachedSpansCoversOnlyItsOwnEvidence()
    {
        var state = State("Required output result. Keep originals unchanged transform another value.");
        Add(state, "Required output result.", "result"); Add(state, "Keep originals", "first", "declaration_constraint"); Add(state, "unchanged", "second", "declaration_constraint");
        PlanningFixtures.Runtime(state, Span(state, "Keep originals unchanged"));
        PlanningFixtures.Runtime(state, Span(state, "transform another value."));
        PlanningDeclarations.Commit(state, Canonicalize(state, [Distinct(state, "result", "result", direction: "output"),
            Link("first", "result", "modifier_of"), Link("second", "result", "modifier_of")]), PlanningDeclarations.EvidenceFingerprint(state));
        state.OperationAdmissionFingerprint = null; await ResolveGrounded(state, NoModel(), Ct);
        Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation); Assert.Single(PlanningOperations.DeclarationExclusions(state));
    }

    [Fact]
    public async Task CapturedClassifierExcludesPreliminaryPreservationAndKeepsItsThreePorts()
    {
        var state = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "operation-admission-stage1.json"), Ct), PlanningJsonContext.Default.PlanningSnapshot)!;
        PlanningFixtures.ReassessSyntheticSources(state); // Explicit synthetic current-proof fixture, not receipt replay.
        PlanningFixtures.EmptyRuntime(state);
        var root = PlanningFixtures.Runtime(state, Span(state, "classifying a single record."));
        PlanningFixtures.Runtime(state, Span(state, "Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise."), evidenceRole: "governing");
        var preservation = PlanningFixtures.Runtime(state, Span(state, "Preserve the original id and amount."));
        PlanningDeclarations.Commit(state, state.DeclarationAssignments, PlanningDeclarations.EvidenceFingerprint(state));
        OperationEffectFixtures.Seed(state, qualification: members =>
        {
            var scope = Assert.Single(members);
            var answer = OperationEffectFixtures.ContributionAnswer(state, scope);
            // Explicit synthetic rule qualification preserves the retained owned
            // condition semantics; it is not a descriptive-property relabeling.
            if (scope.Evidence!.EvidenceRole == "governing") answer["units"]![0]!["governingKind"] = "runtime_condition";
            return OperationEffectFixtures.CompleteQualification(state, scope, answer);
        });
        await ResolveGrounded(state, NoModel(), Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal("local_processing", operation.Kind);
        // One execution plus five projected rule portions: the two established
        // condition groundings keep their exact boundaries within the clause.
        Assert.Equal(6, operation.OperationAdmission!.Assignments.Count);
        Assert.Equal(root.Id, Assert.Single(operation.OperationAdmission.Assignments, a => a.Disposition == "supports").RuntimeEvidenceId);
        Assert.All(operation.OperationAdmission.ExecutionContributions.SelectMany(p => p.Contributions).Where(c => c.Role == "governing_property"),
            c => Assert.Equal("runtime_condition", c.GoverningKind));
        Assert.Contains(preservation.Id, PlanningOperations.DeclarationExclusions(state).Keys);
        Assert.Equal(["record", "threshold"], state.Declarations.Where(d => d.Direction == "input").Select(d => PlanningDeclarations.Name(state, d)).Order(StringComparer.Ordinal));
        Assert.Equal("classifiedResult", PlanningDeclarations.Name(state, Assert.Single(state.Declarations, d => d.Direction == "output")));
        var threshold = state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold"); Assert.False(threshold.Required); Assert.Equal(100m, PlanningDeclarations.Default(state, threshold)!.Number);
        var canonical = operation.Id; var proof = state.DeclarationFingerprint;
        var restored = PlanningContext.Clone(state); await ResolveGrounded(restored, NoModel(), Ct);
        Assert.Equal(canonical, Assert.Single(restored.Obligations, PlanningSourceDecisions.IsOperation).Id); Assert.Equal(proof, restored.DeclarationFingerprint);
        restored.Declarations[0].ModifierReferences.Add("foreign");
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(restored));
    }

    private static PlanningReference Span(PlanningSnapshot state, string fragment)
    {
        foreach (var scope in PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request"))
        foreach (var start in scope.Boundaries["properties"]!["start"]!["enum"]!.AsArray())
        foreach (var end in scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray())
        {
            if (int.Parse(end!.ToString()[1..], System.Globalization.CultureInfo.InvariantCulture) <= int.Parse(start!.ToString()[1..], System.Globalization.CultureInfo.InvariantCulture)) continue;
            var reference = scope.Select(start.ToString(), end.ToString());
            if (scope.Source.Text.Substring(reference.Start, reference.Length) != fragment) continue;
            if (!state.References.Contains(reference)) state.References.Add(reference); return reference;
        }
        throw new InvalidOperationException("Missing exact synthetic evidence span: " + fragment);
    }
}
