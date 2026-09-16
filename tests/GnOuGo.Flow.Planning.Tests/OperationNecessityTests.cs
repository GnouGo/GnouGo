using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic necessity evidence and identity responses, never replacements for historical receipts.</summary>
public sealed class OperationNecessityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningRuntimeEvidence Add(PlanningSnapshot state, int index, PlanningOperationNecessity necessity,
        string role = "action", string kind = "local_processing")
    {
        var reference = PlanningOperations.SourceScopes(state)[index].Clause;
        var initial = PlanningFixtures.Runtime(state, reference, kind, evidenceRole: role);
        var value = PlanningOperations.SealRuntime(state, initial with { Necessity = necessity,
            NecessityReference = necessity == PlanningOperationNecessity.Unspecified ? null : reference.Id });
        state.RuntimeEvidence.Remove(initial); state.RuntimeEvidence.Add(value); PlanningFixtures.EmptyRuntime(state);
        return value;
    }

    private static TypedPlannerTests.FakeRuntime Identity(PlanningSnapshot state, string status = "same_as")
    {
        var first = PlanningOperations.Scopes(state).First(s => s.Evidence!.EvidenceRole == "action");
        var target = PlanningOperations.EffectDomain(state, first.Evidence!).First(p => p.Value.BoundaryReference == first.Evidence!.ActionReference).Key;
        OperationEffectFixtures.Seed(state, scope => OperationEffectFixtures.Answer(state, scope,
            status == "distinct" && scope.Evidence!.EvidenceRole == "action" ? null : [target]));
        return NoModel();
    }
    private static TypedPlannerTests.FakeRuntime NoModel() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("Unexpected dispatch.") };

    [Theory]
    [InlineData(PlanningOperationNecessity.Required, true)]
    [InlineData(PlanningOperationNecessity.Optional, false)]
    [InlineData(PlanningOperationNecessity.Unspecified, true)]
    public async Task IdentityReuseAggregatesNecessityWithoutFilteringOutTheRoot(PlanningOperationNecessity second, bool required)
    {
        var state = OperationAdmissionTests.State("Transform the supplied value. The transformation is explicitly requested with its necessity stated here.");
        var first = Add(state, 0, PlanningOperationNecessity.Unspecified); Add(state, 1, second);
        var runtime = Identity(state); await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal(required, operation.Required); Assert.Equal(2, operation.OperationAdmission!.Assignments.Count);
        Assert.Equal(PlanningOperations.EffectDomain(state, first).Single(p => p.Value.BoundaryReference == first.ActionReference).Key, operation.Id);
        Assert.Empty(runtime.Requests); Assert.Empty(state.RepairAllowances);
        PlanningOperations.RequireCurrent(state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitConflictStopsAfterSameOccurrenceIsChosenWithoutCommitting(bool reversed)
    {
        var state = OperationAdmissionTests.State("This transformation is mandatory. The same transformation is optional.");
        Add(state, 0, reversed ? PlanningOperationNecessity.Optional : PlanningOperationNecessity.Required);
        Add(state, 1, reversed ? PlanningOperationNecessity.Required : PlanningOperationNecessity.Optional);
        var runtime = Identity(state);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", error.Code); Assert.NotNull(error.Details?["location"]);
        Assert.Empty(runtime.Requests); Assert.Null(state.OperationAdmissionFingerprint);
        Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Empty(state.RepairAllowances);
    }

    [Fact]
    public async Task IndependentOccurrencesMayKeepDifferentNecessity()
    {
        var state = OperationAdmissionTests.State("The first transformation is mandatory. The second transformation is optional.");
        Add(state, 0, PlanningOperationNecessity.Required); Add(state, 1, PlanningOperationNecessity.Optional);
        await PlanningOperations.ResolveAsync(state, Identity(state, "distinct"), Ct);
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray();
        Assert.Equal(2, operations.Length); Assert.Single(operations, o => o.Required); Assert.Single(operations, o => !o.Required);
    }

    [Theory]
    [InlineData(PlanningOperationNecessity.Unspecified, true)]
    [InlineData(PlanningOperationNecessity.Required, true)]
    [InlineData(PlanningOperationNecessity.Optional, false)]
    public async Task ExternalReadUsesTheSameNecessityRules(PlanningOperationNecessity necessity, bool required)
    {
        var state = OperationAdmissionTests.State("Read the external value with the declared necessity."); Add(state, 0, necessity, kind: "external_read");
        await PlanningOperations.ResolveAsync(state, Identity(state), Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal("external_read", operation.Kind); Assert.Equal(required, operation.Required);
    }

    [Fact]
    public async Task RuntimeConditionsDoNotMakeRequiredCapabilitiesOptional()
    {
        var state = OperationAdmissionTests.State("Reading is required to implement this workflow. Perform the read only when enabled.");
        Add(state, 0, PlanningOperationNecessity.Required, kind: "external_read");
        Add(state, 1, PlanningOperationNecessity.Unspecified, "governing", "external_read");
        await PlanningOperations.ResolveAsync(state, Identity(state), Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.True(operation.Required); Assert.Equal("attach", operation.OperationAdmission!.Assignments[1].Disposition);
    }

    [Theory]
    [InlineData(PlanningOperationNecessity.Unspecified, true)]
    [InlineData(PlanningOperationNecessity.Optional, false)]
    public async Task GoverningEvidenceOnlyChangesNecessityWhenExplicit(PlanningOperationNecessity governing, bool required)
    {
        var state = OperationAdmissionTests.State("Transform the value. This rule governs the transformation.");
        Add(state, 0, PlanningOperationNecessity.Unspecified); Add(state, 1, governing, "governing");
        await PlanningOperations.ResolveAsync(state, Identity(state), Ct);
        Assert.Equal(required, Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation).Required);
    }

    [Fact]
    public async Task UnspecifiedGoverningEvidenceCannotOverwriteExplicitOptionality()
    {
        var state = OperationAdmissionTests.State("The transformation is optional. When performed use these rules.");
        Add(state, 0, PlanningOperationNecessity.Optional); Add(state, 1, PlanningOperationNecessity.Unspecified, "governing");
        await PlanningOperations.ResolveAsync(state, Identity(state), Ct);
        Assert.False(Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation).Required);
    }

    [Fact]
    public async Task CompletedSyntheticEffectPagesRestoreNecessityProofWithoutDispatch()
    {
        var state = OperationAdmissionTests.State("Transform the value. That transformation is optional. Apply the declared rules.");
        Add(state, 0, PlanningOperationNecessity.Unspecified); Add(state, 1, PlanningOperationNecessity.Optional); Add(state, 2, PlanningOperationNecessity.Unspecified, "governing");
        PlanningSnapshot? checkpoint = null; var runtime = Identity(state);
        runtime.OnCheckpoint = snapshot =>
        {
            if (checkpoint is null && snapshot.DecisionPages.Any(p => p.Status == "completed"))
            { checkpoint = PlanningContext.Clone(snapshot); throw new OperationCanceledException("Synthetic crash after completed fixture pages."); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.NotNull(checkpoint); var pages = checkpoint.DecisionPages.Select(p => p.Id).ToArray();
        Assert.Equal(2, pages.Length);
        await PlanningOperations.ResolveAsync(checkpoint, NoModel(), Ct);
        Assert.False(Assert.Single(checkpoint.Obligations, PlanningSourceDecisions.IsOperation).Required);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(checkpoint, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        await PlanningOperations.ResolveAsync(restored, NoModel(), Ct);
        Assert.Equal(checkpoint.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
        Assert.Equal(pages, restored.DecisionPages.Select(p => p.Id)); Assert.Empty(restored.RequestAccounting); Assert.Empty(restored.RepairAllowances);
        var operation = Assert.Single(restored.Obligations, PlanningSourceDecisions.IsOperation);
        restored.Obligations.Remove(operation); restored.Obligations.Add(operation with { Required = true });
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(restored));
    }

    [Fact]
    public void ResponseDomainRequiresTypedNecessityAndOwnedEvidence()
    {
        var state = OperationAdmissionTests.State("Transform the supplied value.");
        var scope = PlanningOperations.SourceScopes(state)[0];
        var schema = PlanningOperations.RuntimeSchema(state, PlanningSourceAuthority.RequestedBehavior, scope.Boundaries);
        var span = new JsonObject { ["start"] = "b0", ["end"] = "b4" };
        var value = new JsonObject { ["role"] = "local_behavior", ["kind"] = "local_processing", ["action"] = span.DeepClone(),
            ["execution"] = "generated_workflow", ["evidence"] = "action", ["baseline"] = null };
        IReadOnlyList<string> Validate() => PlanningContractValidation.ValidateInstance(new JsonArray(value.DeepClone()), schema);
        value["required"] = false; Assert.NotEmpty(Validate()); value.Remove("required");
        foreach (var kind in new[] { "required", "optional" })
        {
            value["necessity"] = new JsonObject { ["state"] = kind, ["evidence"] = null }; Assert.NotEmpty(Validate());
            value["necessity"]!["evidence"] = span.DeepClone(); Assert.Empty(Validate());
        }
        value["necessity"] = new JsonObject { ["state"] = "unspecified", ["evidence"] = span.DeepClone() }; Assert.NotEmpty(Validate());
        value["necessity"]!["evidence"] = null; Assert.Empty(Validate());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("unknown")]
    public async Task UnprovenNecessityFailsClosed(string defect)
    {
        var state = OperationAdmissionTests.State("Transform the value. A different source statement.");
        var value = Add(state, 0, PlanningOperationNecessity.Required);
        var invalid = PlanningOperations.SealRuntime(state, value with
        {
            Necessity = defect == "unknown" ? PlanningOperationNecessity.Unknown : value.Necessity,
            NecessityReference = defect == "missing" ? null : PlanningOperations.SourceScopes(state)[1].Clause.Id
        });
        state.RuntimeEvidence.Remove(value); state.RuntimeEvidence.Add(invalid); PlanningFixtures.EmptyRuntime(state);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
        Assert.Null(state.OperationAdmissionFingerprint);
    }

    [Fact]
    public async Task ExistingConditionalNodeKeepsBaselineAuthorityWithoutModelOptionality()
    {
        var state = OperationAdmissionTests.State("Keep the existing behavior."); state.Request.Baseline = TypedPlannerTests.Graph();
        state.Request.Baseline.Workflows[0].Steps[0].If = new() { Kind = "boolean", Boolean = false };
        var scope = PlanningOperations.SourceScopes(state).First(s => s.Source.Baseline is { OwnerKind: "node", Field: null });
        var baseline = PlanningSourceGroundingRules.BaselineNodes(state).Single().Key;
        var evidence = PlanningFixtures.Runtime(state, scope.Clause, baseline: baseline);
        Assert.Throws<InvalidOperationException>(() => PlanningOperations.RuntimeSchema(state, scope.Source.Authority, scope.Boundaries));
        Assert.Equal(PlanningOperationNecessity.Unspecified, evidence.Necessity);
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.True(operation.Required); Assert.Equal(baseline, operation.OperationAdmission!.BaselineReference);
        var forged = PlanningOperations.SealRuntime(state, evidence with { Necessity = PlanningOperationNecessity.Optional, NecessityReference = scope.Clause.Id });
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ValidateRuntime(state, forged));
    }

    [Fact]
    public async Task CapturedLocalMismatchConvergesWithExplicitlySyntheticNecessityResponses()
    {
        var state = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "operation-admission-stage1.json"), Ct), PlanningJsonContext.Default.PlanningSnapshot)!;
        PlanningFixtures.ReassessSyntheticSources(state); // Explicit synthetic current-proof fixture, not receipt replay.
        PlanningFixtures.EmptyRuntime(state);
        foreach (var (fragment, necessity, role) in new[]
        {
            ("classifying a single record.", PlanningOperationNecessity.Unspecified, "action"),
            ("Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.", PlanningOperationNecessity.Required, "action"),
            ("when approved is false,", PlanningOperationNecessity.Unspecified, "governing"),
            ("when approved is true and amount>=threshold,", PlanningOperationNecessity.Unspecified, "governing"),
            ("standard otherwise.", PlanningOperationNecessity.Unspecified, "governing"),
            ("Preserve the original id and amount.", PlanningOperationNecessity.Unspecified, "action")
        })
        {
            var start = state.Request.Prompt.IndexOf(fragment, StringComparison.Ordinal); Assert.True(start >= 0);
            var scope = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request" && s.Clause.Start <= start && s.Clause.Start + s.Clause.Length >= start + fragment.Length);
            var reference = scope.Clause with { Id = "synthetic_necessity_" + start, Start = start, Length = fragment.Length };
            state.References.Add(reference);
            var initial = PlanningFixtures.Runtime(state, reference, evidenceRole: role);
            var evidence = PlanningOperations.SealRuntime(state, initial with { Necessity = necessity,
                NecessityReference = necessity == PlanningOperationNecessity.Unspecified ? null : reference.Id });
            state.RuntimeEvidence.Remove(initial); state.RuntimeEvidence.Add(evidence);
        }
        PlanningFixtures.EmptyRuntime(state);
        PlanningDeclarations.Commit(state, state.DeclarationAssignments, PlanningDeclarations.EvidenceFingerprint(state));
        var declarations = state.DeclarationFingerprint; var runtime = Identity(state);
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.True(operation.Required); Assert.Equal("local_processing", operation.Kind);
        Assert.Equal(3, operation.OperationAdmission!.Assignments.Count(a => a.Disposition == "attach")); Assert.Empty(runtime.Requests);
        Assert.Single(PlanningOperations.DeclarationExclusions(state)); Assert.Equal(declarations, state.DeclarationFingerprint);
        Assert.Equal(["record", "threshold"], state.Declarations.Where(d => d.Direction == "input").Select(d => PlanningDeclarations.Name(state, d)).Order(StringComparer.Ordinal));
        Assert.Equal("classifiedResult", PlanningDeclarations.Name(state, Assert.Single(state.Declarations, d => d.Direction == "output")));
        var threshold = state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold");
        Assert.False(threshold.Required); Assert.Equal(100m, PlanningDeclarations.Default(state, threshold)!.Number);
    }
}
