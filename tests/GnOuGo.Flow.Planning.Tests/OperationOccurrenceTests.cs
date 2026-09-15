using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Explicit synthetic identity answers; never historical or live receipts.</summary>
public sealed class OperationOccurrenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime NoModel() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("Unexpected semantic decision.") };
    private static PlanningRuntimeEvidence Add(PlanningSnapshot state, int index, string evidence = "action", string kind = "local_processing", bool required = true)
        => PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state)[index].Clause, kind, evidenceRole: evidence, required: required);
    private static TypedPlannerTests.FakeRuntime Answers(string identity = "same_as", bool shared = false) => new() { OnCall = (phase, request, _) =>
    {
        Assert.Equal("intent_operations", phase);
        var field = request.StructuredOutputSchema!["properties"]!.AsObject().Single();
        var variants = field.Value!["anyOf"]!.AsArray();
        var attachment = variants.FirstOrDefault(v => v?["properties"]?["targets"] is not null);
        JsonObject value;
        if (attachment is not null)
        {
            var choices = attachment["properties"]!["targets"]!["items"]!["enum"]!.AsArray();
            value = new() { ["status"] = "attach", ["targets"] = new JsonArray(choices.Take(shared ? choices.Count : 1).Select(v => v!.DeepClone()).ToArray()) };
        }
        else
        {
            value = new() { ["status"] = identity };
            if (identity == "same_as") value["target"] = variants.Single(v => v?["properties"]?["target"] is not null)!["properties"]!["target"]!["enum"]![0]!.DeepClone();
        }
        return Task.FromResult(new LLMResponse { Json = new JsonObject { [field.Key] = value }, CompletionStatus = "completed" });
    } };

    [Fact]
    public void InterpretationCannotSelectOccurrenceIdentityOrForeignClauseSubject()
    {
        var state = OperationAdmissionTests.State("Transform an entry. Apply its rules.");
        var scope = PlanningOperations.SourceScopes(state)[0];
        var schema = PlanningOperations.RuntimeSchema(state, PlanningSourceAuthority.RequestedBehavior, scope.Boundaries);
        var value = new JsonObject { ["role"] = "local_behavior", ["kind"] = "local_processing", ["action"] = new JsonObject { ["start"] = "b0", ["end"] = "b3" },
            ["execution"] = "generated_workflow", ["evidence"] = "action", ["necessity"] = new JsonObject { ["state"] = "unspecified", ["evidence"] = null }, ["baseline"] = null };
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonArray(value.DeepClone()), schema));
        value["subject"] = PlanningOperations.SourceScopes(state)[1].Clause.Id;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonArray(value.DeepClone()), schema));
        value.Remove("subject"); value["occurrence"] = "distinct";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonArray(value.DeepClone()), schema));
        Assert.All(PlanningSourceDecisions.InterpretationDecisions(state), d => Assert.Null(d.Context["subjects"]));
    }

    [Theory]
    [InlineData("same_as", 1)]
    [InlineData("distinct", 2)]
    [InlineData("not_an_operation", 1)]
    public async Task CompatibleActionsReceiveOnlyIdentityChoices(string answer, int count)
    {
        var state = OperationAdmissionTests.State("Transform a value. Transform according to the supplied rules.");
        var first = Add(state, 0); var second = Add(state, 1);
        var runtime = Answers(answer); await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray(); Assert.Equal(count, operations.Length);
        Assert.Single(runtime.Requests); Assert.Empty(state.RepairAllowances);
        var firstId = PlanningOperations.CanonicalId(state, state.References.Single(r => r.Id == first.ActionReference), null);
        Assert.Contains(operations, o => o.Id == firstId);
        if (answer == "same_as") Assert.Contains(Assert.Single(operations).OperationAdmission!.Assignments, a => a.RuntimeEvidenceId == second.Id && a.TargetId == firstId);
        var schema = runtime.Requests[0].StructuredOutputSchema!.ToJsonString();
        Assert.DoesNotContain("requiredness", schema); Assert.DoesNotContain("external_write", schema); Assert.DoesNotContain("resource_lifecycle", schema);
        Assert.DoesNotContain(first.ClauseReference, schema); Assert.DoesNotContain(second.ClauseReference, schema);
    }

    [Fact]
    public async Task SingleCanonicalRootReceivesRulesDeterministicallyAfterDuplicateResolution()
    {
        var state = OperationAdmissionTests.State("Classify the supplied object. Classify using the declared conditions. This computation is deterministic.");
        Add(state, 0); Add(state, 1); var rule = Add(state, 2, "governing");
        var runtime = Answers(); await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal(3, operation.OperationAdmission!.Assignments.Count); Assert.Single(runtime.Requests);
        Assert.Contains(operation.OperationAdmission.Assignments, a => a.RuntimeEvidenceId == rule.Id && a.Disposition == "attach" && a.ResolutionOrigin == "deterministic");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task MultipleRootsUseABoundedValidatedGoverningTargetSet(bool shared, int targetCount)
    {
        var state = OperationAdmissionTests.State("Transform the first value. Transform the second value. Apply this rule to the transformations.");
        Add(state, 0); Add(state, 1); var rule = Add(state, 2, "governing");
        var runtime = Answers("distinct", shared); await PlanningOperations.ResolveAsync(state, runtime, Ct);
        Assert.Equal(2, runtime.Requests.Count);
        Assert.Equal(targetCount, state.Obligations.Where(PlanningSourceDecisions.IsOperation).Count(o => o.OperationAdmission!.Assignments.Any(a => a.RuntimeEvidenceId == rule.Id)));
        var schema = runtime.Requests[1].StructuredOutputSchema!;
        Assert.DoesNotContain(rule.ClauseReference, schema.ToJsonString()); Assert.DoesNotContain("distinct", schema.ToJsonString());
        var restored = PlanningContext.Clone(state); await PlanningOperations.ResolveAsync(restored, NoModel(), Ct);
        Assert.Equal(state.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
        Assert.Equal(state.RequestAccounting.Count, restored.RequestAccounting.Count);
    }

    [Fact]
    public async Task GoverningEvidenceCannotCreateItsOwnRoot()
    {
        var state = OperationAdmissionTests.State("This computation is deterministic."); Add(state, 0, "governing");
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", failure.Code); Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation);
    }

    [Fact]
    public async Task SameAnchorWithContradictoryKindStopsBeforeAnIdentityRequest()
    {
        var state = OperationAdmissionTests.State("Perform the requested action.");
        Add(state, 0); Add(state, 0, kind: "external_read");
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", failure.Code); Assert.Empty(state.RequestAccounting);
    }

    [Fact]
    public async Task RestoredModelAssignmentRequiresItsCompletedPage()
    {
        var state = OperationAdmissionTests.State("Transform an entry. Transform by the specified rules."); Add(state, 0); Add(state, 1);
        await PlanningOperations.ResolveAsync(state, Answers(), Ct);
        state.DecisionPages.Clear();
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(state));
    }

    [Theory]
    [InlineData("external_read", true)]
    public async Task IncompatibleFactsCannotBecomeReuseAlternatives(string kind, bool required)
    {
        var state = OperationAdmissionTests.State("Transform the first value. Perform the second action.");
        Add(state, 0); Add(state, 1, kind: kind, required: required);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct); Assert.Equal(2, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
    }

    [Fact]
    public async Task RestartAfterCompletedRootPageReusesIdentityBeforeAttachment()
    {
        var state = OperationAdmissionTests.State("Transform the first value. Transform the second value. Apply the common rule.");
        Add(state, 0); Add(state, 1); Add(state, 2, "governing");
        PlanningSnapshot? checkpoint = null;
        var runtime = Answers("distinct", true);
        runtime.OnCheckpoint = snapshot =>
        {
            if (checkpoint is null && snapshot.DecisionPages.Any(p => p.Status == "completed"))
            { checkpoint = PlanningContext.Clone(snapshot); throw new OperationCanceledException("Synthetic crash after durable receipt."); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.NotNull(checkpoint); Assert.Single(checkpoint.DecisionPages);
        var firstPage = checkpoint.DecisionPages.Single().Id;
        var resumed = Answers("distinct", true); await PlanningOperations.ResolveAsync(checkpoint, resumed, Ct);
        Assert.Single(resumed.Requests); Assert.Equal(2, checkpoint.RequestAccounting.Count); Assert.Equal(2, checkpoint.DecisionPages.Count);
        Assert.Contains(checkpoint.DecisionPages, p => p.Id == firstPage && p.Status == "completed");
        var uninterrupted = OperationAdmissionTests.State(state.Request.Prompt); Add(uninterrupted, 0); Add(uninterrupted, 1); Add(uninterrupted, 2, "governing");
        await PlanningOperations.ResolveAsync(uninterrupted, Answers("distinct", true), Ct);
        Assert.Equal(uninterrupted.OperationAdmissionFingerprint, checkpoint.OperationAdmissionFingerprint);
        Assert.Equal(uninterrupted.DecisionPages.Select(p => p.Id), checkpoint.DecisionPages.Select(p => p.Id));
    }

    [Fact]
    public async Task CapturedClassifierConvergesWithSyntheticIdentityAndUnchangedDeclarations()
    {
        var state = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "operation-admission-stage1.json"), Ct), PlanningJsonContext.Default.PlanningSnapshot)!;
        PlanningFixtures.EmptyRuntime(state);
        var scopes = PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request").ToArray();
        foreach (var (fragment, role) in new[] { ("classifying a single record.", "action"),
            ("Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.", "action"),
            ("Preserve the original id and amount.", "action"), ("This is deterministic, local, in-memory business processing.", "governing") })
        {
            var offset = state.Request.Prompt.IndexOf(fragment, StringComparison.Ordinal);
            var scope = scopes.Single(s => s.Clause.Start <= offset && s.Clause.Start + s.Clause.Length >= offset + fragment.Length);
            var reference = scope.Clause with { Id = "fixture_" + offset, Start = offset, Length = fragment.Length };
            state.References.Add(reference); PlanningFixtures.Runtime(state, reference, evidenceRole: role);
        }
        PlanningDeclarations.Commit(state, state.DeclarationAssignments, PlanningDeclarations.EvidenceFingerprint(state));
        var declarations = state.DeclarationFingerprint;
        var runtime = Answers(); await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal("local_processing", operation.Kind); Assert.Equal(3, operation.OperationAdmission!.Assignments.Count); Assert.Single(runtime.Requests);
        Assert.Single(PlanningOperations.DeclarationExclusions(state)); Assert.Equal(declarations, state.DeclarationFingerprint);
        Assert.Equal(["record", "threshold"], state.Declarations.Where(d => d.Direction == "input").Select(d => PlanningDeclarations.Name(state, d)).Order(StringComparer.Ordinal));
        Assert.Equal("classifiedResult", PlanningDeclarations.Name(state, Assert.Single(state.Declarations, d => d.Direction == "output")));
        var threshold = state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold"); Assert.False(threshold.Required); Assert.Equal(100m, PlanningDeclarations.Default(state, threshold)!.Number);
    }
}
