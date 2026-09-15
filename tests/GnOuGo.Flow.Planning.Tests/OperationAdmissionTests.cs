using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic runtime facets, never substitutes for retained provider receipts.</summary>
public sealed class OperationAdmissionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static PlanningSnapshot State(string prompt)
    {
        var state = new PlanningSnapshot { Request = new() { TenantId = "tenant", SessionId = "operation-fixture", Prompt = prompt } };
        PlanningFixtures.EmptyRuntime(state); return state;
    }
    private static PlanningRuntimeEvidence Add(PlanningSnapshot state, int clause, string kind = "local_processing", string evidenceRole = "action",
        string? resource = null, string? baseline = null, string? resourceAction = null)
        => PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state)[clause].Clause, kind, evidenceRole, resource, baseline, resourceAction);
    private static PlanningRuntimeEvidence Exclude(PlanningSnapshot state, int clause, string role)
    {
        var source = PlanningOperations.SourceScopes(state)[clause].Clause;
        var value = PlanningOperations.SealRuntime(state, new("", source.Id, source.Id, role, null, null, null, null, null, null, null, false, ""));
        state.RuntimeEvidence.Add(value); PlanningFixtures.EmptyRuntime(state); return value;
    }
    private static TypedPlannerTests.FakeRuntime NoModel() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("No semantic decision is unresolved.") };
    private static void Replace(PlanningSnapshot state, PlanningRuntimeEvidence before, PlanningRuntimeEvidence after)
    { state.RuntimeEvidence.Remove(before); state.RuntimeEvidence.Add(PlanningOperations.SealRuntime(state, after)); PlanningFixtures.EmptyRuntime(state); }

    [Theory]
    [InlineData("Classify a record according to rules.")]
    [InlineData("Transform an entry according to instructions.")]
    [InlineData("Aggregate the supplied values.")]
    public async Task LocalRuntimeEvidenceResolvesWithoutAnOperationOrCapabilityRequest(string text)
    {
        var state = State(text);
        var hint = PolicyGroundingTests.Add(state, "request", text, "hint", "workflow_policy");
        Add(state, 0); var runtime = NoModel();
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal("local_processing", operation.Kind); Assert.Contains(hint, state.Obligations);
        PlanningOperations.RequireExecutableIntent(state); Assert.Empty(runtime.Requests);
        var inventory = new CapabilityContracts.CapabilityInventory(true,
            [new(operation.Id, text, true, "local_processing", "none", "", "requested_effect", "", false, "")], [], []);
        var matches = await CapabilityDecisionPages.MatchAsync(state, runtime, inventory, new CapabilityContracts.CapabilityCatalog([], ""), Ct);
        Assert.Equal("local", matches["operation_matches"]![operation.Id]!["status"]!.ToString()); Assert.Empty(runtime.Requests);
        state.Preparation = TypedPlannerTests.Preparation();
        state.Preparation.Capabilities.Add(new() { Id = "native", Resolution = "local", StepType = "set", Required = true, OperationIds = [operation.Id] });
        state.BehaviorPlan = PlanningBehaviorDecisions.Assemble(state, new());
        Assert.Null(Assert.Single(state.BehaviorPlan.Workflows[0].Steps).CapabilityId);
        PlanningGraphSkeleton.Create(state); Assert.Equal("set", Assert.Single(state.Graph!.Workflows[0].Steps).Type);
    }

    [Theory]
    [InlineData("Create a reusable workflow.", "planning_directive")]
    [InlineData("Build a reusable transformation.", "planning_directive")]
    [InlineData("Return value:{category:string}.", "contract")]
    [InlineData("Return classifiedResult:{id:string,amount:number,category:string}, all members required.", "contract")]
    [InlineData("Optional limit defaults to 10 when omitted.", "contract")]
    [InlineData("Confirm before an external write.", "policy")]
    public async Task NonExecutableEvidenceIsNeverAnOperationQuestion(string text, string role)
    {
        var state = State(text); Exclude(state, 0, role); var runtime = NoModel();
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        Assert.Empty(PlanningOperations.Scopes(state)); Assert.Empty(runtime.Requests);
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireExecutableIntent(state)).Code);
    }

    [Fact]
    public async Task ConditionsReuseOneLocalOccurrenceAndCannotExposeExternalAlternatives()
    {
        var state = State("Transform an entry. When accepted choose the upper category. Otherwise choose the lower category.");
        var root = Add(state, 0); Add(state, 1, evidenceRole: "governing"); Add(state, 2, evidenceRole: "governing");
        var condition = PolicyGroundingTests.Add(state, "request", "When accepted choose the upper category.", "condition", "runtime_condition");
        var fallback = PolicyGroundingTests.Add(state, "request", "Otherwise choose the lower category.", "fallback", "runtime_fallback");
        var runtime = NoModel(); await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal(3, operation.OperationAdmission!.Assignments.Count); Assert.Contains(condition, state.Obligations); Assert.Contains(fallback, state.Obligations); Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task MixedLocalReadAndResourceActionsKeepSeparateIdentitiesAndDependencies()
    {
        var state = State("Read the requested item. Classify the loaded item. Create a temporary resource. Delete the owned temporary resource.");
        Add(state, 0, "external_read"); Add(state, 1); Add(state, 2, "resource_lifecycle", resourceAction: "create"); Add(state, 3, "cleanup", resourceAction: "delete");
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var ops = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray(); Assert.Equal(4, ops.Length);
        var read = ops.Single(o => o.Kind == "external_read"); var local = ops.Single(o => o.Kind == "local_processing");
        state.ObligationRelations = [new(read.Id, local.Id, "data")];
        state.Preparation = TypedPlannerTests.Preparation();
        state.Preparation.Capabilities = [new() { Id = "read", StepType = "mcp.call", Resolution = "mcp", Required = true, OperationIds = [read.Id] },
            new() { Id = "local", StepType = "set", Resolution = "local", Required = true, OperationIds = [local.Id], InputOperationIds = [read.Id] }];
        var behavior = PlanningBehaviorDecisions.Assemble(state, new());
        Assert.Equal([read.Id, local.Id], behavior.Workflows[0].Steps.SelectMany(n => n.OperationIds));
        Assert.Null(behavior.Workflows[0].Steps[1].CapabilityId);
    }

    [Fact]
    public void HostPolicyCannotReturnActionFacets()
    {
        var state = State("Process the supplied value."); state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Create an external resource only with permission." };
        var source = PlanningIntentAssessment.IntentSources(state).Single(s => s.Authority == PlanningSourceAuthority.ConstraintsOnly);
        var reference = PlanningReferences.Register(state, source.Id, source.Kind, source.Text)[0];
        Assert.Throws<InvalidOperationException>(() => PlanningOperations.RuntimeSchema(state, source.Authority, PlanningReferences.Boundaries(reference, source.Text).Schema));
        var pages = PlanningSourceDecisions.InterpretationDecisions(state).Where(d => d.Context["role"]!.ToString() == "host_constraint");
        Assert.NotEmpty(pages); Assert.All(pages, p => Assert.Null(p.Schema["properties"]!["runtime"]));
    }

    [Theory]
    [InlineData("resource_lifecycle", null)]
    [InlineData("cleanup", "create")]
    [InlineData("local_processing", "delete")]
    public async Task IncompatibleLifecycleProofFailsClosed(string kind, string? action)
    {
        var state = State("Perform the requested action."); Add(state, 0, kind, resourceAction: action);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
        Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation);
    }

    [Fact]
    public async Task PlanningArtifactCannotSatisfyResourceOwnership()
    {
        var state = State("Create one reusable workflow."); Exclude(state, 0, "planning_directive"); Add(state, 0, "resource_lifecycle", resourceAction: "create");
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
    }

    [Fact]
    public async Task BaselineNeedsItsExactCompatibleNode()
    {
        var state = State("Keep the existing behavior."); state.Request.Baseline = TypedPlannerTests.Graph();
        var scope = PlanningOperations.SourceScopes(state).First(s => s.Source.Authority == PlanningSourceAuthority.ExistingBehavior);
        var id = PlanningSourceGroundingRules.BaselineNodes(state).Single().Key;
        var evidence = PlanningFixtures.Runtime(state, scope.Clause, baseline: id);
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Equal(id, Assert.Single(state.Obligations).OperationAdmission!.BaselineReference);
        Replace(state, evidence, evidence with { BaselineReference = "foreign" }); state.OperationAdmissionFingerprint = null; state.Obligations.Clear();
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("execution_scope")]
    [InlineData("origin")]
    public async Task MissingOrForeignProofCannotBecomeAQuestion(string defect)
    {
        var state = State("Transform a value."); var evidence = Add(state, 0);
        if (defect == "foreign") state.RuntimeEvidence[0] = evidence with { ResourceReference = "foreign" };
        if (defect == "missing") state.RuntimeEvidenceFingerprint = null;
        if (defect == "stale") state.Request.Prompt = "Do something else.";
        if (defect == "execution_scope") state.RuntimeEvidence[0] = evidence with { ExecutionScope = PlanningRuntimeExecutionScope.PlanningArtifact };
        if (defect == "origin") state.RuntimeEvidence[0] = evidence with { Origin = PlanningRuntimeEvidenceOrigin.Unknown };
        if (defect is "execution_scope" or "origin") state.RuntimeEvidenceFingerprint = PlanningOperations.RuntimeFingerprint(state);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
    }

    [Fact]
    public async Task RuntimeKindIsIndependentOfPreliminaryHint()
    {
        var state = State("Transform the provided record."); PolicyGroundingTests.Add(state, "request", state.Request.Prompt, "wrong", "resource_lifecycle"); Add(state, 0);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Equal("local_processing", Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation).Kind);
    }

    [Fact]
    public async Task CapturedDeclarationFixtureKeepsPortsAndAddsOnlyTheLocalRule()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "operation-admission-stage1.json"), Ct))!;
        var state = JsonSerializer.Deserialize(fixture.ToJsonString(), PlanningJsonContext.Default.PlanningSnapshot)!;
        state.RuntimeEvidence.Clear(); PlanningFixtures.EmptyRuntime(state);
        PlanningDeclarations.Commit(state, state.DeclarationAssignments, PlanningDeclarations.EvidenceFingerprint(state));
        var declarationProof = state.DeclarationFingerprint;
        var rule = state.Obligations.Single(o => o.Kind == "workflow_policy" && PlanningSourceDecisions.Text(state, o).StartsWith("Classify as", StringComparison.Ordinal));
        PlanningFixtures.Runtime(state, state.References.Single(r => r.Id == rule.Grounding!.ClauseReference));
        var runtime = NoModel(); await PlanningOperations.ResolveAsync(state, runtime, Ct);
        PlanningDeclarations.RequireCurrent(state); Assert.Equal(declarationProof, state.DeclarationFingerprint);
        var op = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        state.Preparation = TypedPlannerTests.Preparation();
        state.Preparation.Capabilities.Add(new() { Id = "local", StepType = "set", Resolution = "local", Required = true, OperationIds = [op.Id] });
        var behavior = PlanningBehaviorDecisions.Assemble(state, new()); var workflow = Assert.Single(behavior.Workflows);
        Assert.Equal(["record", "threshold"], workflow.Inputs.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("classifiedResult", Assert.Single(workflow.Outputs).Name);
        Assert.Contains("category has exactly", workflow.Outputs[0].Description); Assert.Contains("Preserve the original", workflow.Outputs[0].Description);
        var threshold = state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold");
        Assert.False(threshold.Required); Assert.Equal(100m, PlanningDeclarations.Default(state, threshold)!.Number);
        Assert.Empty(PlanningDeclarations.ValidateBehavior(state, behavior)); Assert.Empty(runtime.Requests);
        var restarted = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        await PlanningOperations.ResolveAsync(restarted, NoModel(), Ct);
        Assert.Equal(state.OperationAdmissionFingerprint, restarted.OperationAdmissionFingerprint); Assert.Equal(state.RequestAccounting.Count, restarted.RequestAccounting.Count);
    }

    [Fact]
    public async Task GoverningAmbiguityUsesOnlyIssuedSameKindTargetsAndReplaysReceipt()
    {
        var state = State("Transform the first value. Transform the second value. Apply this rule to the requested transformation.");
        var first = Add(state, 0); Add(state, 1); Add(state, 2, evidenceRole: "governing");
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("intent_operations", phase); var field = request.StructuredOutputSchema!["properties"]!.AsObject().Single();
            Assert.DoesNotContain("external_write", field.Value!.ToJsonString()); Assert.DoesNotContain("resource_lifecycle", field.Value.ToJsonString());
            var attachment = field.Value["anyOf"]!.AsArray().FirstOrDefault(v => v?["properties"]?["targets"] is not null);
            var answer = attachment is null ? new JsonObject { ["status"] = "distinct" }
                : new JsonObject { ["status"] = "attach", ["targets"] = new JsonArray(attachment["properties"]!["targets"]!["items"]!["enum"]![0]!.DeepClone()) };
            return Task.FromResult(new LLMResponse { Json = new JsonObject { [field.Key] = answer }, CompletionStatus = "completed" });
        } };
        await PlanningOperations.ResolveAsync(state, runtime, Ct); Assert.Equal(2, runtime.Requests.Count);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        await PlanningOperations.ResolveAsync(restored, NoModel(), Ct); Assert.Equal(state.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
    }
    [Fact]
    public async Task MultipleActionsInOneClauseRemainDistinct()
    {
        var state = State("Read a value and classify that value.");
        var scope = PlanningOperations.SourceScopes(state)[0];
        var read = scope.Select("b0", "b3"); var local = scope.Select("b4", "b7");
        state.References.AddRange([read, local]);
        PlanningFixtures.Runtime(state, read, "external_read"); PlanningFixtures.Runtime(state, local);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Equal(2, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
    }

    [Fact]
    public async Task DeclarationCoverageDoesNotSuppressSeparateActionEvidenceInTheSameClause()
    {
        var state = State("Return result:string after transforming the provided value.");
        state.Request.Baseline = TypedPlannerTests.Graph();
        PlanningFixtures.EmptyRuntime(state);
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        var declarationProof = state.DeclarationFingerprint;
        var scope = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        var action = scope.Select("b3", "b7"); state.References.Add(action);
        PlanningFixtures.Runtime(state, action);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Equal("local_processing", Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation).Kind);
        Assert.Contains(state.RuntimeEvidence, e => e.ClauseReference == scope.Clause.Id && e.Role == "contract");
        Assert.Equal(declarationProof, state.DeclarationFingerprint); PlanningDeclarations.RequireCurrent(state);
    }

    [Fact]
    public async Task IncompleteCoverageStopsWithoutInventingAnAction()
    {
        var state = State("Transform a value. Preserve its source."); Add(state, 0);
        state.RuntimeEvidence.RemoveAll(e => e.ClauseReference == PlanningOperations.SourceScopes(state)[1].Clause.Id);
        state.RuntimeEvidenceFingerprint = PlanningOperations.RuntimeFingerprint(state);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
        Assert.Empty(state.Obligations);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("tenant")]
    [InlineData("source")]
    [InlineData("historical_version")]
    public async Task AdmissionCannotBeTransferredOrReusedWithStaleProof(string defect)
    {
        var state = State("Transform a record."); Add(state, 0); await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        if (defect == "fingerprint") state.OperationAdmissionFingerprint = null;
        if (defect == "tenant") state.Request.TenantId = "other";
        if (defect == "source") state.Request.Prompt += " Revised.";
        if (defect == "historical_version") state.Obligations[0] = state.Obligations[0] with { OperationAdmission = state.Obligations[0].OperationAdmission! with { Version = 1 } };
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(state));
    }

    [Fact]
    public async Task RevisionRetiresRecognizedActionAndRestartCannotResurrectIt()
    {
        var state = State("Read a value."); Add(state, 0, "external_read"); state.BehaviorRevision = new() { Text = "Remove that read operation." };
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_revision_obligations", phase);
            return Task.FromResult(new LLMResponse { Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, p.Value!["enum"]![1]!.DeepClone()))) });
        } };
        await PlanningOperations.ResolveAsync(state, runtime, Ct); Assert.Empty(state.Obligations);
        var calls = runtime.Requests.Count; await PlanningOperations.ResolveAsync(PlanningContext.Clone(state), runtime, Ct); Assert.Equal(calls, runtime.Requests.Count);
    }

    [Fact]
    public async Task CancellationCannotCommitPartialAuthority()
    {
        var state = State("Read a value. Transform it."); Add(state, 0, "external_read"); Add(state, 1);
        using var cancellation = new CancellationTokenSource(); await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, NoModel(), cancellation.Token)); Assert.Empty(state.Obligations);
    }

    [Fact]
    public async Task UnverifiableIdentityDecisionCannotRedispatchOrCommitPartialActions()
    {
        var state = State("Transform one value. Transform another value. Apply the shared rule.");
        var root = Add(state, 0); Add(state, 1); Add(state, 2, evidenceRole: "governing");
        state.Intent.Checked = true;
        var runtime = new TypedPlannerTests.FakeRuntime();
        runtime.OnPrepareSnapshot = async snapshot => { await PlanningOperations.ResolveAsync(snapshot, runtime, Ct); return TypedPlannerTests.Preparation(); };
        runtime.OnCall = (_, _, _) => throw new LLMClientException(LLMClientFailureKind.Transport, "Synthetic unavailable receipt.", true);
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.True(state.TechnicalStop!.Unverifiable); Assert.Empty(state.Obligations); Assert.Single(runtime.Requests);
        await planner.AdvanceAsync(PlanningContext.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, Ct); Assert.Single(runtime.Requests); Assert.Empty(state.RepairAllowances);
    }

    [Fact]
    public void ResourceResponseRequiresOwnershipAndNeverAcceptsPlanningExecutionScope()
    {
        var state = State("Create an owned temporary resource."); var scope = PlanningOperations.SourceScopes(state)[0];
        var schema = PlanningOperations.RuntimeSchema(state, PlanningSourceAuthority.RequestedBehavior, scope.Boundaries);
        var action = new JsonObject { ["role"] = "runtime_action", ["kind"] = "resource_lifecycle", ["action"] = new JsonObject { ["start"] = "b0", ["end"] = "b5" },
            ["execution"] = "generated_workflow", ["resource"] = new JsonObject { ["start"] = "b3", ["end"] = "b5" }, ["evidence"] = "action", ["required"] = true,
            ["baseline"] = null, ["ownership"] = "workflow_runtime_resource", ["resourceAction"] = "create" };
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonArray(action.DeepClone()), schema));
        action.Remove("ownership"); Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonArray(action.DeepClone()), schema));
        action["ownership"] = "workflow_runtime_resource"; action["execution"] = "planning_artifact";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonArray(action.DeepClone()), schema));
    }

}
