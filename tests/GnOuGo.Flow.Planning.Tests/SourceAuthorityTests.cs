using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SourceAuthorityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonObject Selection(string kind) => new() { ["start"] = "b0", ["end"] = "b2", ["kind"] = kind, ["required"] = true };
    private static JsonObject Schema(PlanningSnapshot state, PlanningSourceAuthority authority)
    {
        const string text = "Perform action.";
        var reference = Assert.Single(PlanningReferences.Register(state, "request", "user_request", text));
        return PlanningSourceDecisions.InterpretationSchema(state, authority, PlanningReferences.Boundaries(reference, text).Schema);
    }

    [Theory]
    [InlineData("external_write")]
    [InlineData("human_interaction")]
    [InlineData("local_processing")]
    [InlineData("workflow_boundary")]
    [InlineData("iteration")]
    [InlineData("business_input")]
    [InlineData("business_output")]
    public void PolicySourcesCannotDeclareOperationsOrWorkflowStructureInTheResponseSchema(string kind)
    {
        var state = TypedPlannerTests.Session();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Selection(kind), Schema(state, PlanningSourceAuthority.ConstraintsOnly)));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Selection(kind), Schema(state, PlanningSourceAuthority.RequestedBehavior)));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Selection("workflow_policy"), Schema(state, PlanningSourceAuthority.ConstraintsOnly)));
        var forged = Selection("workflow_policy"); forged["authority"] = "RequestedBehavior";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(forged, Schema(state, PlanningSourceAuthority.ConstraintsOnly)));
    }

    [Theory]
    [InlineData("external write")]
    [InlineData("persistent mutation")]
    public async Task CapturedPolicySubjectCannotBecomeAnOperationEvenWhenRestored(string subject)
    {
        // Sanitized failure class from 78c92e2d. No synthetic scoped response is used to replay its receipt.
        var state = TypedPlannerTests.Session();
        var policy = "Unless explicitly requested otherwise, obtain runtime human confirmation before the first " + subject + ", with zero writes after rejection.";
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = policy };
        var clause = Assert.Single(PlanningReferences.Register(state, "host", "host_constraint", policy));
        state.Obligations.Add(new("invented", [clause.Id], "capability_contract", "external_write", true));
        var runtime = new TypedPlannerTests.FakeRuntime();
        foreach (var candidate in new[] { state, PlanningContext.Clone(state) })
        {
            var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => CapabilityInventoryDecisions.BuildAsync(candidate, runtime, Ct));
            Assert.Equal("INTENT_SOURCE_AUTHORITY_UNPROVEN", error.Code);
            Assert.Equal("/obligations/@invented", Assert.Single(PlanningPreparationDiagnostics.FromException(error)).Location);
            Assert.Empty(candidate.ScopedPolicies); Assert.Empty(runtime.Requests);
            Assert.False(PlanningSourceDecisions.IsOperation(candidate.Obligations[0]));
        }
        Assert.Throws<WorkflowRuntimeException>(() => PlanningSourceGroundingRules.Create(state, state.Obligations[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserRequestOrAnswerCanAdmitAnOperationWithDefaultPermission(bool answer)
    {
        var state = TypedPlannerTests.Session(); state.Request.Prompt = "Perform the declared effect.";
        var source = "request";
        if (answer)
        {
            state.Intent.Answers.Add(new("What should occur?", new JsonObject { ["action"] = state.Request.Prompt }));
            source = "answer_0_0";
        }
        var obligation = PolicyGroundingTests.Add(state, source, state.Request.Prompt, "action", "external_write");
        var inventory = await CapabilityInventoryDecisions.BuildAsync(state, new TypedPlannerTests.FakeRuntime(), Ct);
        Assert.Equal(PlanningSourceAuthority.RequestedBehavior, obligation.Grounding!.Authority);
        Assert.Equal(PlanningSourceSemanticRole.RequestedAction, obligation.Grounding.Role);
        Assert.Equal("action", Assert.Single(inventory.Operations).Id);
        var protectedInventory = CapabilityConfirmationPolicies.Apply(inventory);
        var permission = Assert.Single(protectedInventory.Operations, o => o.ExecutionKind == "human_interaction");
        Assert.Contains(permission.Id, protectedInventory.Operations.Single(o => o.Id == "action").InputOperationIds);
    }

    [Fact]
    public void BaselineOperationsRequireCurrentIssuedNodeReferences()
    {
        var state = TypedPlannerTests.Session(); state.Request.Baseline = TypedPlannerTests.Graph();
        var schema = Schema(state, PlanningSourceAuthority.ExistingBehavior);
        var reference = Assert.Single(PlanningSourceGroundingRules.BaselineNodes(state)).Key;
        var selected = Selection("local_processing");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(selected, schema));
        selected["baseline"] = "foreign"; Assert.NotEmpty(PlanningContractValidation.ValidateInstance(selected, schema));
        selected["baseline"] = reference; Assert.Empty(PlanningContractValidation.ValidateInstance(selected, schema));
        var source = PlanningIntentAssessment.IntentSources(state).Single(s => s.Id == "existing");
        var clause = PlanningReferences.Register(state, source.Id, source.Kind, source.Text)[0];
        var obligation = new PlanningObligation("existing_action", [clause.Id], "workflow", "local_processing", true);
        Assert.Throws<WorkflowRuntimeException>(() => PlanningSourceGroundingRules.Create(state, obligation));
        obligation = obligation with { Grounding = PlanningSourceGroundingRules.Create(state, obligation, reference), Disposition = "admitted" };
        PlanningSourceGroundingRules.Validate(state, obligation);
        Assert.Equal(PlanningSourceSemanticRole.ExistingAction, obligation.Grounding.Role);
        state.Request.Baseline.Workflows[0].Steps.Clear();
        Assert.Throws<WorkflowRuntimeException>(() => PlanningSourceGroundingRules.Validate(state, obligation));
    }

    [Theory]
    [InlineData("runtime_condition", PlanningSourceSemanticRole.RuntimeCondition)]
    [InlineData("runtime_fallback", PlanningSourceSemanticRole.RuntimeCondition)]
    [InlineData("workflow_policy", PlanningSourceSemanticRole.PolicyConstraint)]
    public void SubjectsAndConditionsCarryNoActionAuthority(string kind, PlanningSourceSemanticRole role)
    {
        var state = TypedPlannerTests.Session(); state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Govern the declared action." };
        var obligation = PolicyGroundingTests.Add(state, "host", "Govern the declared action.", "constraint", kind);
        Assert.Equal(role, obligation.Grounding!.Role); Assert.False(PlanningSourceDecisions.IsOperation(obligation));
        var forged = obligation with { Kind = "external_write", Disposition = "admitted", Grounding = obligation.Grounding with { Role = PlanningSourceSemanticRole.RequestedAction } };
        Assert.Throws<WorkflowRuntimeException>(() => PlanningSourceGroundingRules.Validate(state, forged));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("source")]
    [InlineData("fingerprint")]
    [InlineData("reference")]
    public void ForeignOrStaleRestoredGroundingFailsClosed(string corruption)
    {
        var state = TypedPlannerTests.Session();
        var obligation = PolicyGroundingTests.Add(state, "request", state.Request.Prompt, "action", "local_processing");
        state = PlanningContext.Clone(state); obligation = state.Obligations[0];
        PlanningSourceGroundingRules.Validate(state, obligation);
        if (corruption == "tenant") state.Request.TenantId = "foreign";
        if (corruption == "source") state.Request.Prompt += " Changed.";
        if (corruption == "fingerprint") obligation = obligation with { Grounding = obligation.Grounding! with { Fingerprint = "forged" } };
        if (corruption == "reference") obligation = obligation with { EvidenceReferences = ["foreign"] };
        var error = Assert.Throws<WorkflowRuntimeException>(() => PlanningSourceGroundingRules.Validate(state, obligation));
        Assert.Equal("INTENT_SOURCE_AUTHORITY_UNPROVEN", error.Code);
    }
}
