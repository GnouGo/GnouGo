using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic complete-clause responses; never replacements for retained live receipts.</summary>
public sealed class PolicyGroundingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static PlanningObligation Add(PlanningSnapshot state, string sourceId, string fragment, string id, string kind)
    {
        var source = PlanningIntentAssessment.IntentSources(state).Single(s => s.Id == sourceId);
        var parent = PlanningReferences.Register(state, source.Id, source.Kind, source.Text)
            .Single(r => source.Text.Substring(r.Start, r.Length).Contains(fragment, StringComparison.Ordinal));
        var start = source.Text.IndexOf(fragment, parent.Start, StringComparison.Ordinal);
        var reference = parent with { Id = parent.Id + "_" + id, Kind = parent.Kind + ":selection", Start = start, Length = fragment.Length };
        state.References.Add(reference);
        var obligation = new PlanningObligation(id, [reference.Id], sourceId == "host" ? "workflow" : "capability_contract", kind, true);
        obligation = obligation with { Grounding = PlanningSourceGroundingRules.Create(state, obligation),
            Disposition = "preliminary" };
        state.Obligations.Add(obligation); return obligation;
    }
    internal static JsonObject Permission(string rule, string scope, string target, string applicability = "always") => new()
    { ["rule"] = rule, ["scope"] = new JsonObject { ["kind"] = scope, ["target"] = target }, ["applicability"] = applicability };
    internal static JsonObject Reply(LLMRequest request, Dictionary<string, JsonObject> fields) => new(request.StructuredOutputSchema!["properties"]!.AsObject()
        .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject(p.Value!["properties"]!.AsObject().Select(f =>
            new KeyValuePair<string, JsonNode?>(f.Key, fields[f.Key].DeepClone()))))));
    private static TypedPlannerTests.FakeRuntime Runtime(Dictionary<string, JsonObject> fields) => new() { OnCall = (phase, request, _) =>
    {
        Assert.Equal("confirmation_scope", phase);
        var response = Reply(request, fields);
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
        Assert.DoesNotContain("rejectionAction", request.StructuredOutputSchema!.ToJsonString());
        return Task.FromResult(new LLMResponse { Json = response });
    } };
    private static PlanningSnapshot State(string policy, string prompt = "Perform the requested action.")
    {
        var state = TypedPlannerTests.Session(); state.Request.Prompt = prompt;
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = policy }; return state;
    }

    [Theory]
    [InlineData("Unless explicitly requested otherwise, obtain runtime human confirmation before the first external write, with zero writes after rejection.", "with zero writes after rejection.")]
    [InlineData("Permission is required to perform the action; a declined response permits no action.", "a declined response permits no action.")]
    [InlineData("Permission is required to perform the action. A declined response permits no action.", "A declined response permits no action.")]
    public async Task RequiredPermissionAndRejectionBecomeOnePolicyWithoutInventingAnOperation(string clause, string rejection)
    {
        var state = State(clause); var firstClause = clause.Split(['.', ';'])[0];
        var required = Add(state, "host", firstClause, "permission", "confirmation_required");
        var preliminary = Add(state, "host", rejection, "rejection", "confirmation_forbidden");
        var runtime = Runtime(new() { ["permission"] = Permission("require_confirmation", "effect", "write"),
            ["rejection"] = new() { ["rule"] = "rejection_condition", ["permissionRule"] = "permission" } });
        await PlanningConfirmationPolicies.ResolveAsync(state, runtime, Ct);
        var policy = Assert.Single(state.ScopedPolicies);
        Assert.Equal("require_confirmation", policy.Rule); Assert.Empty(policy.TargetOperationIds);
        Assert.Equal(["permission", "rejection"], policy.GoverningObligationIds);
        Assert.Contains(required.EvidenceReferences[0], policy.GoverningReferences);
        Assert.Contains(preliminary.EvidenceReferences[0], policy.GoverningReferences);
        Assert.Equal("rejection_condition", preliminary.Disposition); Assert.Equal("confirmation_forbidden", preliminary.Kind); // Audit label retained, not authority.
        Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Contains(state.Events, e => e.Kind == "policy_classification_retired");
        if (required.Grounding!.ClauseReference == preliminary.Grounding!.ClauseReference)
            Assert.Single(Assert.Single(state.DecisionPages).Decisions); // Indivisible clause, not fragment pages.
        var restored = PlanningContext.Clone(state); var events = restored.Events.Count;
        await PlanningConfirmationPolicies.ResolveAsync(restored, runtime, Ct);
        Assert.Equal(events, restored.Events.Count); Assert.Single(runtime.Requests);
        Assert.Equal(PlanningConfirmationPolicies.Fingerprint(state.ScopedPolicies), PlanningConfirmationPolicies.Fingerprint(restored.ScopedPolicies));
    }

    [Theory]
    [InlineData("confirmation_required")]
    [InlineData("confirmation_forbidden")]
    public async Task UnsupportedPreliminaryPolarityCanRetireWithoutDroppingItsWorkflowConstraint(string preliminary)
    {
        var state = State("Preserve the original record on failure.");
        Add(state, "request", state.Request.Prompt, "action", "local_processing");
        var obligation = Add(state, "host", "Preserve the original record on failure.", "constraint", preliminary);
        var runtime = Runtime(new() { ["constraint"] = new() { ["rule"] = "not_confirmation_policy", ["reason"] = "other_workflow_constraint" } });
        PlanningFixtures.AdmitHints(state);
        await PlanningDeclarations.ResolveAsync(state, new TypedPlannerTests.FakeRuntime(), Ct);
        PlanningFixtures.RefreshAdmission(state);
        var inventory = await CapabilityInventoryDecisions.BuildAsync(state, runtime, Ct);
        Assert.Empty(inventory.ScopedPolicies); Assert.Single(inventory.Operations);
        Assert.Equal("not_confirmation_policy", obligation.Disposition);
        Assert.Equal("Preserve the original record on failure.", Assert.Single(inventory.Constraints).Description);
        Assert.NotNull(obligation.AdjudicationFingerprint); Assert.Empty(obligation.PolicyIds);
    }

    [Fact]
    public async Task ExplicitAskingProhibitionStillConflictsWithRequiredPermissionOnSameAction()
    {
        var state = State("Require permission for this action. Do not ask for confirmation of this action.");
        Add(state, "request", state.Request.Prompt, "action", "external_write");
        Add(state, "host", "Require permission for this action.", "require", "confirmation_required");
        Add(state, "host", "Do not ask for confirmation of this action.", "forbid", "confirmation_forbidden");
        PlanningFixtures.AdmitHints(state);
        var runtime = Runtime(new() { ["require"] = Permission("require_confirmation", "operation", PlanningFixtures.OperationId(state, "action")),
            ["forbid"] = Permission("forbid_confirmation", "operation", PlanningFixtures.OperationId(state, "action")) });
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningConfirmationPolicies.ResolveAsync(state, runtime, Ct));
        Assert.Equal("CONFIRMATION_POLICY_CONFLICT", error.Code);
        Assert.All(state.ScopedPolicies, p => Assert.Equal("pending", p.Status));
    }

    [Fact]
    public async Task DuplicateRequirementsNormalizeOnlyMatchingPermissionSemantics()
    {
        var state = State("Require permission to perform the action, and obtain consent for that action.");
        Add(state, "request", state.Request.Prompt, "action", "external_write");
        Add(state, "host", "Require permission to perform the action,", "first", "confirmation_required");
        Add(state, "host", "obtain consent for that action.", "second", "confirmation_required");
        PlanningFixtures.AdmitHints(state);
        await PlanningConfirmationPolicies.ResolveAsync(state, Runtime(new() { ["first"] = Permission("require_confirmation", "operation", PlanningFixtures.OperationId(state, "action")),
            ["second"] = Permission("require_confirmation", "operation", PlanningFixtures.OperationId(state, "action")) }), Ct);
        var policy = Assert.Single(state.ScopedPolicies); Assert.Equal(2, policy.GoverningObligationIds.Count);
        Assert.Equal([policy.Id], state.Obligations.Single(o => o.Id == "second").PolicyIds);
    }

    [Theory]
    [InlineData("not_confirmation_policy")]
    [InlineData("forbid_confirmation")]
    public async Task RejectionCannotClaimARetiredOrProhibitedPermission(string governingRule)
    {
        var state = State("Constrain the action. Prevent the action after rejection.");
        Add(state, "host", "Constrain the action.", "owner", "confirmation_required");
        Add(state, "host", "Prevent the action after rejection.", "rejection", "confirmation_forbidden");
        var runtime = Runtime(new() { ["owner"] = governingRule == "not_confirmation_policy" ?
            new() { ["rule"] = governingRule, ["reason"] = "other_workflow_constraint" } : Permission(governingRule, "effect", "write"),
            ["rejection"] = new() { ["rule"] = "rejection_condition", ["permissionRule"] = "owner" } });
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningConfirmationPolicies.ResolveAsync(state, runtime, Ct));
        Assert.Equal("CONFIRMATION_SCOPE_UNRESOLVED", error.Code); Assert.Null(state.Outcome); Assert.Null(state.Intent.Question);
    }
}
