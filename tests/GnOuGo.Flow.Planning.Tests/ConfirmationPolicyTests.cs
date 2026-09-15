using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConfirmationPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static PlanningScopedPolicy Policy(string id, string rule, params string[] targets) => new()
    {
        Id = id, ObligationId = id, Rule = rule, ScopeKind = "operation", Target = targets.FirstOrDefault() ?? "absent",
        TargetOperationIds = targets.ToList(), ClauseReference = "evidence_" + id, EvidenceFingerprint = "fingerprint_" + id,
        Origin = "intent", Applicability = "always", Status = "resolved"
    };
    private static CapabilityInventory Inventory(IEnumerable<PlanningScopedPolicy> policies, params CapabilityInventoryOperation[] operations)
        => new(true, operations, [], []) { PolicyScopeVersion = 2, ScopedPolicies = policies.ToArray() };
    private static CapabilityInventoryOperation Write(string id) => new(id, "Perform declared effect", true, "external_effect", "write");

    [Fact]
    public void DifferentSemanticActionsDoNotConflictOrDisableWritePermission()
    {
        var required = Policy("write_rule", "require_confirmation", "publish");
        var unrelated = Policy("other_rule", "forbid_confirmation", "display");
        var prohibition = Policy("review_rule", "forbid_interaction"); prohibition.TargetOperationIds.Clear();
        var result = CapabilityConfirmationPolicies.Apply(Inventory([required, unrelated, prohibition], Write("publish")));
        var permission = Assert.Single(result.Operations, o => o.ExecutionKind == "human_interaction");
        Assert.Contains(permission.Id, result.Operations.Single(o => o.Id == "publish").InputOperationIds);
        Assert.Equal(permission.Id, result.ScopedPolicies.Single(p => p.Id == required.Id).PermissionOperationId);
        Assert.DoesNotContain(result.Operations, o => o.Id is "display" or "review_rule");
    }

    [Fact]
    public void SameActionHasLocatedConflictWithBothGoverningReferences()
    {
        var rules = new[] { Policy("first", "require_confirmation", "effect"), Policy("second", "forbid_confirmation", "effect") };
        var error = Assert.Throws<WorkflowRuntimeException>(() => PlanningConfirmationPolicies.Effective(rules, "effect"));
        Assert.Equal("CONFIRMATION_POLICY_CONFLICT", error.Code);
        var finding = Assert.Single(PlanningPreparationDiagnostics.FromException(error));
        Assert.Equal("/preparation/policies/@effect", finding.Location);
        Assert.Contains("evidence_first", finding.Message); Assert.Contains("evidence_second", finding.Message);
    }

    [Fact]
    public void BroadRuleIntersectsSpecificActionButDoesNotDisableIndependentWrite()
    {
        var broad = Policy("broad", "require_confirmation", "first", "second"); broad.ScopeKind = "effect"; broad.Target = "write";
        var specific = Policy("specific", "forbid_confirmation", "first");
        Assert.Throws<WorkflowRuntimeException>(() => CapabilityConfirmationPolicies.Apply(Inventory([broad, specific], Write("first"), Write("second"))));
        broad.Applicability = "unless_explicit"; broad.Origin = "policy";
        var result = CapabilityConfirmationPolicies.Apply(Inventory([broad, specific], Write("first"), Write("second")));
        Assert.Empty(result.Operations.Single(o => o.Id == "first").InputOperationIds);
        Assert.Single(result.Operations.Single(o => o.Id == "second").InputOperationIds);
        Assert.Equal(["second"], result.ScopedPolicies.Single(p => p.Id == "broad").TargetOperationIds);
        Assert.Equal(["first", "second"], broad.TargetOperationIds); // No mutable input-policy narrowing.
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("answer")]
    public void OnlyDeclaredDefaultExceptionsPermitAnExplicitOverride(string origin)
    {
        var declared = Policy("default", "require_confirmation", "action"); declared.Origin = "policy"; declared.Applicability = "unless_explicit";
        var explicitRule = Policy("override", "forbid_confirmation", "action"); explicitRule.Origin = origin;
        Assert.Same(explicitRule, PlanningConfirmationPolicies.Effective([declared, explicitRule], "action"));
        declared.Applicability = "always";
        Assert.Throws<WorkflowRuntimeException>(() => PlanningConfirmationPolicies.Effective([declared, explicitRule], "action"));
    }

    [Fact]
    public void DefaultPermissionDoesNotReuseAnUnrelatedHumanInteraction()
    {
        var result = CapabilityConfirmationPolicies.Apply(Inventory([], Write("effect"), new("survey", "Ask an unrelated business question", true, "human_interaction", "none")));
        Assert.Equal(2, result.Operations.Count(o => o.ExecutionKind == "human_interaction"));
        Assert.NotEqual("survey", Assert.Single(result.ScopedPolicies).PermissionOperationId);
        var declared = Write("effect") with { DecisionSourceOperationId = "permission", InputOperationIds = ["permission"] };
        result = CapabilityConfirmationPolicies.Apply(Inventory([], declared, new("permission", "Approve this effect", true, "human_interaction", "none")));
        Assert.Single(result.Operations, o => o.ExecutionKind == "human_interaction");
        Assert.Equal("permission", Assert.Single(result.ScopedPolicies).PermissionOperationId);
    }

    [Fact]
    public void ScopedPermissionCanProtectLocalProcessingWithoutInventingAnExternalWrite()
    {
        var result = CapabilityConfirmationPolicies.Apply(Inventory([Policy("local_rule", "require_confirmation", "calculate")],
            new CapabilityInventoryOperation("calculate", "Calculate the declared result", true, "local_processing", "none")));
        var permission = Assert.Single(result.Operations, o => o.ExecutionKind == "human_interaction");
        Assert.Equal("local_rule", permission.PermissionPolicyId);
        Assert.DoesNotContain(result.Operations, o => o.ExternalEffectKind == "write");
        Assert.DoesNotContain("write", permission.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void HistoricalInventoryCannotConvertItsMissingScopeToPermission(int proofVersion)
    {
        var old = new CapabilityInventory(true, [Write("effect")], [], []) { PolicyScopeVersion = proofVersion };
        var error = Assert.Throws<WorkflowRuntimeException>(() => CapabilityConfirmationPolicies.Apply(old));
        Assert.Equal("CONFIRMATION_SCOPE_UNRESOLVED", error.Code);
    }

    [Theory]
    [InlineData("accept_behavior", "write")]
    [InlineData("approve", "write")]
    [InlineData("accept_behavior", "read")]
    [InlineData("approve", "read")]
    public async Task HistoricalPreparationCannotApproveWithoutCurrentScopeProof(string command, string effect)
    {
        var state = TypedPlannerTests.Session(command == "approve" ? PlanningStatus.FinalReview : PlanningStatus.BehaviorReview);
        state.Preparation = new() { Capabilities = [new() { Id = "action", EffectKind = effect }] };
        var runtime = new TypedPlannerTests.FakeRuntime();
        var stopped = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = command }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, stopped.Status); Assert.Null(stopped.ApprovedHash); Assert.Empty(runtime.Requests);
        Assert.Equal("CONFIRMATION_SCOPE_UNRESOLVED", Assert.Single(stopped.Diagnostics).Code);
    }

    [Theory]
    [InlineData("workflow's own YAML", "confirmation_forbidden")]
    [InlineData("internal calculation trace", "confirmation_forbidden")]
    [InlineData("internal calculation trace", "exact_denial")]
    [InlineData("internal calculation trace", "workflow_policy")]
    public async Task CapturedClassificationScopesUseCompleteClausesAndReplayWithoutNewRequests(string prohibitedSubject, string classification)
    {
        // The two classifications reproduce the retained Stage-1 receipt. Scope
        // assignments below are synthetic regression responses, not historical receipts.
        var state = TypedPlannerTests.Session(); state.Request.Prompt = "Classify one record locally.";
        var host = "Unless explicitly requested otherwise, obtain runtime human confirmation before the first external write, with zero writes after rejection. Do not request review of the " + prohibitedSubject + " during execution.";
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = host };
        var spans = PlanningReferences.Register(state, "host", "host_constraint", host);
        var first = PlanningReferences.Boundaries(spans[0], host).Select("b4", "b12"); state.References.Add(first);
        var last = PlanningReferences.Boundaries(spans[^1], host).Select("b0", "b10"); state.References.Add(last);
        var operation = Assert.Single(PlanningReferences.Register(state, "request", "user_request", state.Request.Prompt));
        state.Obligations = [new("require", [first.Id], "workflow", "confirmation_required", true),
            new("forbid", [last.Id], "workflow", classification, true), new("local", [operation.Id], "workflow", "local_processing", true)];
        state.Obligations = state.Obligations.Select(o => o with { Grounding = PlanningSourceGroundingRules.Create(state, o),
            Disposition = "preliminary" }).ToList();
        PlanningFixtures.AdmitHints(state);
        var calls = 0;
        PlanningSnapshot? receivedBeforeScopeCommit = null;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCheckpoint = snapshot =>
        {
            if (snapshot.DecisionPages.Count > 0 && snapshot.DecisionPages.All(p => p.Status == "completed") && snapshot.ScopedPolicies.Any(p => p.Status == "pending"))
                receivedBeforeScopeCommit = PlanningContext.Clone(snapshot);
            return Task.CompletedTask;
        }, OnCall = (phase, request, _) =>
        {
            calls++; Assert.Equal("confirmation_scope", phase); Assert.Equal("low", request.Reasoning);
            Assert.Contains("write, with zero writes after rejection.", request.Prompt);
            Assert.Contains(prohibitedSubject + " during execution.", request.Prompt);
            var response = PolicyGroundingTests.Reply(request, new()
            {
                ["require"] = PolicyGroundingTests.Permission("require_confirmation", "effect", "write", "unless_explicit"),
                ["forbid"] = new JsonObject
                {
                    ["rule"] = "forbid_interaction", ["scope"] = new JsonObject { ["kind"] = "interaction", ["target"] = PlanningChoiceEvidence.Parent(state, last.Id).Id },
                    ["interactionTargets"] = new JsonArray(), ["applicability"] = "always"
                }
            });
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
            var invalid = response.DeepClone().AsObject();
            var forbidden = invalid.SelectMany(p => p.Value!.AsObject()).Single(p => p.Key == "forbid").Value!;
            forbidden["scope"] = new JsonObject { ["kind"] = "effect", ["target"] = "write" };
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, request.StructuredOutputSchema!));
            forbidden["scope"] = new JsonObject { ["kind"] = "operation", ["target"] = "foreign_action" };
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, request.StructuredOutputSchema!));
            return Task.FromResult(new LLMResponse { Json = response });
        } };
        await PlanningConfirmationPolicies.ResolveAsync(state, runtime, Ct);
        Assert.Equal(2, state.ScopedPolicies.Count); Assert.All(state.ScopedPolicies, p => Assert.Empty(p.TargetOperationIds));
        Assert.Empty(state.Intent.Answers); Assert.Equal(0, state.Intent.Questions);
        var restored = PlanningContext.Clone(state); var fingerprint = PlanningConfirmationPolicies.Fingerprint(restored.ScopedPolicies);
        var previousCalls = calls; var previousEvents = restored.Events.Count;
        await PlanningConfirmationPolicies.ResolveAsync(restored, runtime, Ct);
        Assert.Equal(previousCalls, calls); Assert.Equal(previousEvents, restored.Events.Count);
        Assert.Equal(fingerprint, PlanningConfirmationPolicies.Fingerprint(restored.ScopedPolicies));
        Assert.Equal(state.RequestAccounting.Count, restored.RequestAccounting.Count);
        Assert.NotNull(receivedBeforeScopeCommit);
        var restarted = receivedBeforeScopeCommit;
        await PlanningConfirmationPolicies.ResolveAsync(restarted, runtime, Ct);
        Assert.Equal(previousCalls, calls);
        Assert.Equal(fingerprint, PlanningConfirmationPolicies.Fingerprint(restarted.ScopedPolicies));
        Assert.Equal(state.RequestAccounting.Count, restarted.RequestAccounting.Count);
        var bad = state.ScopedPolicies[0]; bad.Applicability = "unknown";
        Assert.Equal("CONFIRMATION_SCOPE_UNRESOLVED", Assert.Throws<WorkflowRuntimeException>(() => PlanningConfirmationPolicies.Validate(state, bad)).Code);
        bad.Applicability = "always"; state.Request.TenantId = "foreign";
        Assert.Equal("CONFIRMATION_SCOPE_STALE", Assert.Throws<WorkflowRuntimeException>(() => PlanningConfirmationPolicies.Validate(state, bad)).Code);
    }

    [Fact]
    public void UnknownScopeIsNeitherConflictNorClarification()
    {
        var rule = Policy("unknown", "require_confirmation", "effect"); rule.Status = "pending";
        Assert.Equal("CONFIRMATION_SCOPE_UNRESOLVED", Assert.Throws<WorkflowRuntimeException>(() => PlanningConfirmationPolicies.Effective([rule], "effect")).Code);
    }
}
