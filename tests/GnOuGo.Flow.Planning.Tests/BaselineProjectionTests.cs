using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Explicitly synthetic structural fixtures; no responses substitute for historical receipts.</summary>
public sealed class BaselineProjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime NoModel() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("No model decision is authorized.") };
    private static PlanningSnapshot State() => new() { Request = new() { TenantId = "tenant", SessionId = "structural", Prompt = "",
        Baseline = new() { Workflows = [new() { Key = "main", Inputs = [new() { Name = "record", Schema = new() { Type = "object" } },
            new() { Name = "threshold", Required = false, Default = new() { Kind = "number", Number = 100 }, Schema = new() { Type = "number" } }],
            Outputs = [new() { Name = "classifiedResult", Schema = new() { Type = "object" } }] }] } } };
    private static TypedPlannerTests.FakeRuntime AnnotationsOnly() => new() { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
    {
        Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
        {
            Assert.Null(p.Value!["properties"]!["runtime"]);
            return new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject { ["obligations"] = new JsonArray() });
        }))
    }) };
    private static async Task Prepare(PlanningSnapshot state, TypedPlannerTests.FakeRuntime runtime)
    {
        await PlanningSourceDecisions.InterpretAsync(state, runtime, Ct);
        await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
    }

    [Fact]
    public async Task PortsAndLargeSerializedContractsHaveNoInterpretationOrRuntimeChoices()
    {
        var state = State(); var schema = state.Request.Baseline!.Workflows[0].Inputs[0].Schema;
        for (var i = 0; i < 90; i++) schema.Properties.Add(new() { Name = "member_" + i, Schema = new() { Type = "string", Enum = [".;{}\\\"", "[]"] } });
        var before = PlanningGraphCompiler.Fingerprint(state.Request.Baseline);
        Assert.Empty(PlanningSourceDecisions.InterpretationDecisions(state));
        var runtime = NoModel(); await Prepare(state, runtime);
        Assert.Empty(runtime.Requests); Assert.Empty(state.Obligations); Assert.Equal(3, state.Declarations.Count);
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(state.Request.Baseline));
        var threshold = state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold");
        Assert.False(threshold.Required); Assert.Equal(100m, PlanningDeclarations.Default(state, threshold)!.Number);
        Assert.All(state.RuntimeEvidence, e => { Assert.Equal("contract", e.Role); Assert.Equal(PlanningRuntimeEvidenceOrigin.EngineBaseline, e.Origin); });
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireExecutableIntent(state));
    }

    [Fact]
    public async Task NativeNodesRetainExactIdentitiesNestingConditionsFinalizersAndBindings()
    {
        var state = State(); var workflow = state.Request.Baseline!.Workflows[0];
        workflow.Steps = [new() { Key = "container", Type = "sequence", Steps = [new() { Key = "first", Type = "set", Output = "value",
            If = new() { Kind = "boolean", Boolean = false }, Input = new() { Kind = "object" } }] },
            new() { Key = "second", Type = "set", Input = new() { Kind = "output", Source = "first" } }];
        workflow.Finally = [new() { Key = "final", Type = "emit" }];
        var before = PlanningGraphCompiler.Fingerprint(state.Request.Baseline);
        var runtime = NoModel(); await PlanningSourceDecisions.InterpretAsync(state, runtime, Ct);
        await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        OperationEffectFixtures.Seed(state); // Explicit synthetic non-data pair answers; exact baseline edge remains deterministic.
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray();
        Assert.Equal(4, operations.Length); Assert.All(operations, o => Assert.True(o.Required)); Assert.Empty(runtime.Requests);
        foreach (var (id, node) in PlanningSourceGroundingRules.BaselineNodes(state))
        {
            var operation = Assert.Single(operations, o => o.OperationAdmission!.BaselineReference == id);
            Assert.Equal("operation_" + PlanningGraphCompiler.Fingerprint(new JsonArray("operation-v1", "existing", node.Workflow, node.Node.Key).ToJsonString())[..24], operation.Id);
        }
        var edge = Assert.Single(operations.SelectMany(o => o.OperationAdmission!.Dependencies!.Assignments), a => a.Disposition == "data");
        Assert.Equal(PlanningDependencyOrigin.DeterministicBaseline, edge.Origin);
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(state.Request.Baseline));
        var restored = PlanningContext.Clone(state); PlanningOperations.RequireCurrent(restored);
        Assert.Equal(state.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
    }

    [Theory]
    [InlineData("mcp.call", "Read a value")]
    [InlineData("mcp.call", "Delete an owned resource")]
    [InlineData("opaque", "Create a workflow resource")]
    public async Task OpaqueNodePurposeCannotInventEffectsOrResourceOwnership(string type, string purpose)
    {
        var state = State(); state.Request.Baseline!.Workflows[0].Steps = [new() { Key = "opaque", Type = type, Purpose = purpose }];
        var runtime = AnnotationsOnly();
        await PlanningSourceDecisions.InterpretAsync(state, runtime, Ct); await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        var calls = runtime.Requests.Count;
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", error.Code); Assert.Contains("baseline_", error.Details!["location"]!.ToString());
        Assert.Empty(state.Obligations); Assert.Equal(calls, runtime.Requests.Count);
    }

    [Fact]
    public async Task NodeAnnotationsGovernOnlyTheirExactNodeAndCannotCreateRoots()
    {
        var state = State(); state.Request.Baseline!.Workflows[0].Steps = [new() { Key = "a", Type = "set", Purpose = "Identical description." },
            new() { Key = "b", Type = "set", Purpose = "Identical description." }];
        var runtime = AnnotationsOnly();
        await PlanningSourceDecisions.InterpretAsync(state, runtime, Ct); await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        OperationEffectFixtures.Seed(state); await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray(); Assert.Equal(2, operations.Length);
        Assert.NotEqual(operations[0].Id, operations[1].Id);
        Assert.All(operations, o => { Assert.Equal(2, o.OperationAdmission!.Assignments.Count); Assert.Single(o.OperationAdmission.Assignments, a => a.Disposition == "attach"); });
        Assert.All(PlanningSourceDecisions.InterpretationDecisions(state), d =>
        { Assert.Null(d.Schema["properties"]!["runtime"]); Assert.DoesNotContain("declaration_candidate", d.Schema.ToJsonString()); Assert.DoesNotContain("local_processing", d.Schema.ToJsonString()); });
    }

    [Fact]
    public void PortAnnotationAttachmentIsLockedToItsExactOwner()
    {
        var state = State(); state.Request.Baseline!.Workflows[0].Inputs[1].Schema.Description = "Finite numeric value.";
        var source = PlanningIntentAssessment.IntentSources(state).Single(s => s.Baseline is { Field: not null });
        var reference = PlanningReferences.Register(state, source.Id, source.Kind, source.Text).Single();
        var obligation = new PlanningObligation("constraint", [reference.Id], "business_decision", "declaration_constraint", true);
        state.Obligations.Add(obligation with { Grounding = PlanningSourceGroundingRules.Create(state, obligation) });
        var target = PlanningDeclarations.CanonicalId("threshold", "main", "input");
        var schema = PlanningDeclarations.AttachmentDecisions(state, []).Single().Schema["properties"]!["constraint"]!.AsObject();
        var answer = new JsonObject { ["disposition"] = "modifier_of", ["target"] = target, ["presence"] = "unspecified", ["default"] = null };
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, schema));
        answer["target"] = PlanningDeclarations.CanonicalId("record", "main", "input"); Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, schema));
        PlanningDeclarations.Commit(state, [new("constraint", "modifier_of", target, null, null, "unspecified", null)], PlanningDeclarations.EvidenceFingerprint(state));
        Assert.Single(state.Declarations.Single(d => d.Id == target).ModifierReferences);
        Assert.Throws<WorkflowRuntimeException>(() => PlanningSourceGroundingRules.Create(state, obligation with { Kind = "declaration_candidate" }));
    }

    [Fact]
    public void CodeExpressionsAndLiteralMetadataAreNeverDesignatedProse()
    {
        var state = State(); var workflow = state.Request.Baseline!.Workflows[0]; workflow.Functions = "Serialized prose? No. Function code.";
        workflow.Inputs[0].Default = new() { Kind = "string", Text = "Delete the record." };
        workflow.Steps = [new() { Key = "x", Expr = new() { Kind = "expression", Text = "return 'Read a secret.';" },
            Input = new() { Kind = "string", Text = "Create an external resource." } }];
        Assert.Empty(PlanningSourceDecisions.InterpretationDecisions(state));
        Assert.All(PlanningIntentAssessment.IntentSources(state).Where(s => s.Baseline is not null), s => Assert.True(s.Structural));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("contract")]
    [InlineData("node")]
    [InlineData("owner")]
    public async Task ForeignChangedAndRemovedStructuralProofFailsClosed(string change)
    {
        var state = State(); state.Request.Baseline!.Workflows[0].Steps = [new() { Key = "x" }]; await Prepare(state, NoModel());
        var saved = PlanningContext.Clone(state); var evidence = saved.RuntimeEvidence.Single(e => e.EvidenceRole == "action");
        if (change == "tenant") saved.Request.TenantId = "foreign";
        if (change == "contract") saved.Request.Baseline!.Workflows[0].Inputs[1].Default!.Number = 200;
        if (change == "node") saved.Request.Baseline!.Workflows[0].Steps.Clear();
        if (change == "owner")
        { var index = saved.References.FindIndex(r => r.Id == evidence.SourceReference); saved.References[index] = saved.References[index] with { Baseline = saved.References[index].Baseline! with { Node = "foreign" } }; }
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(saved));
    }

    [Fact]
    public async Task ReviewedBehaviorNeverReplacesStructuralAuthorityAndReceiptReplayDoesNotSpendAgain()
    {
        var state = State(); state.Request.Baseline!.Workflows[0].Steps = [new() { Key = "saved", Purpose = "Apply the existing rule." }];
        var runtime = AnnotationsOnly(); await Prepare(state, runtime);
        var restored = PlanningContext.Clone(state); restored.BehaviorRevision = new() { Text = "", ReviewedBaselineBehavior = "unrelated review context with invented node" };
        var before = JsonSerializer.Serialize(restored.RequestAccounting, PlanningJsonContext.Default.ListPlanningRequestAccounting);
        await PlanningSourceDecisions.InterpretAsync(restored, NoModel(), Ct);
        Assert.Equal(before, JsonSerializer.Serialize(restored.RequestAccounting, PlanningJsonContext.Default.ListPlanningRequestAccounting));
        Assert.Equal(state.RuntimeEvidenceFingerprint, restored.RuntimeEvidenceFingerprint);
        Assert.Equal(PlanningIntentAssessment.IntentSources(state), PlanningIntentAssessment.IntentSources(restored));
        Assert.Single(PlanningSourceGroundingRules.BaselineNodes(restored));
    }
    [Fact]
    public async Task BaselineRevisionRetiresAnActionWithoutReintroducingItOnRestart()
    {
        var state = State(); state.Request.Baseline!.Workflows[0].Steps = [new() { Key = "retired" }];
        state.BehaviorRevision = new() { Text = "Remove the existing action." };
        await PlanningSourceDecisions.InterpretAsync(state, NoModel(), Ct); await PlanningDeclarations.ResolveAsync(state, NoModel(), Ct);
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_revision_obligations", phase);
            return Task.FromResult(new LLMResponse { Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, p.Value!["enum"]![1]!.DeepClone()))) });
        } };
        await PlanningOperations.ResolveAsync(state, runtime, Ct); Assert.Empty(state.Obligations);
        var restarted = PlanningContext.Clone(state); await PlanningOperations.ResolveAsync(restarted, NoModel(), Ct);
        Assert.Empty(restarted.Obligations); Assert.Equal(state.RequestAccounting.Count, restarted.RequestAccounting.Count);
    }

    [Fact]
    public async Task OwnerBoundPolicyCannotReachAnotherBaselineOperation()
    {
        var state = State(); state.Request.Baseline!.Workflows[0].Steps = [new() { Key = "owned", Type = "llm.call", Purpose = "Require confirmation for this action." },
            new() { Key = "other", Type = "llm.call" }];
        var source = PlanningIntentAssessment.IntentSources(state).Single(s => s.Baseline is { Field: not null });
        var reference = PlanningReferences.Register(state, source.Id, source.Kind, source.Text).Single();
        var policy = new PlanningObligation("permission", [reference.Id], "workflow", "confirmation_required", true);
        policy = policy with { Grounding = PlanningSourceGroundingRules.Create(state, policy) }; state.Obligations.Add(policy);
        PlanningFixtures.EmptyRuntime(state); PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        OperationEffectFixtures.Seed(state); await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray();
        var node = PlanningBaselineProjection.NodeReference(state, reference.Baseline!);
        var own = operations.Single(o => o.OperationAdmission!.BaselineReference == node);
        var foreign = operations.Single(o => o.Id != own.Id);
        var decision = PlanningPolicyClauseDecisions.Build(state, policy.Grounding!.ClauseReference, [policy], [policy], operations);
        var answer = new JsonObject { [policy.Id] = PolicyGroundingTests.Permission("require_confirmation", "operation", own.Id) };
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, decision.Schema));
        answer[policy.Id]!["scope"]!["target"] = foreign.Id;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, decision.Schema));
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { Json = PolicyGroundingTests.Reply(request, new() { [policy.Id] = PolicyGroundingTests.Permission("require_confirmation", "effect", "execute") }) }) };
        await PlanningConfirmationPolicies.ResolveAsync(state, runtime, Ct);
        var scoped = Assert.Single(state.ScopedPolicies); Assert.Equal([own.Id], scoped.TargetOperationIds);
        scoped.TargetOperationIds.Add(foreign.Id);
        Assert.Throws<WorkflowRuntimeException>(() => PlanningConfirmationPolicies.Validate(state, scoped));
    }

}
