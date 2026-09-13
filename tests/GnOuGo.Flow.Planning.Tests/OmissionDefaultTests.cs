using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.DeclarationGroundingTests;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic semantic corrections of retained Stage-1 evidence; not historical receipts.</summary>
public sealed class OmissionDefaultTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (PlanningSnapshot State, List<PlanningDeclarationAssignment> Assignments) Fixture()
    {
        var state = State(Classifier);
        Add(state, "Required input record", "record");
        Add(state, "Optional input threshold", "threshold");
        Add(state, "defaulting to 100 when omitted.", "omission", "omission_default");
        Add(state, "Return classifiedResult", "output", "business_output");
        Add(state, "standard otherwise.", "fallback", "runtime_fallback");
        Add(state, "approved is false", "condition", "runtime_condition");
        Add(state, "Preserve the original id and amount.", "preservation", "business_output");
        Add(state, "classifying a single record.", "operation", "local_processing");
        state.Preparation!.Capabilities.Add(new() { Id = "local", StepType = "set", Resolution = "local", Required = true,
            Description = "Classify the original record under the declared rule.", OperationIds = ["operation"] });
        return (state, [Distinct(state, "record", "record"), Distinct(state, "threshold", "threshold", "optional"),
            Link("omission", "threshold", "modifier_of", "optional", Token(state, "omission", "100")),
            Distinct(state, "output", "classifiedResult"), Link("preservation", "output", "modifier_of")]);
    }

    private static JsonObject Schema(PlanningSnapshot state, string candidate)
        => PlanningDeclarations.Decisions(state).Single(p => p.Schema["properties"]?[candidate] is not null).Schema["properties"]![candidate]!.AsObject();

    [Fact]
    public async Task OmissionAndRuntimeFallbackRemainSeparateThroughBehaviorAndSkeleton()
    {
        var (state, assignments) = Fixture();
        Assert.DoesNotContain(PlanningDeclarations.Candidates(state), o => o.Id is "fallback" or "condition");
        Assert.Equal(PlanningSourceSemanticRole.RuntimeCondition, state.Obligations.Single(o => o.Id == "fallback").Grounding!.Role);
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("intent_declarations", phase);
            var result = Response(request, assignments);
            Assert.Empty(PlanningContractValidation.ValidateInstance(result, request.StructuredOutputSchema!));
            return Task.FromResult(new LLMResponse { Json = result });
        } };
        await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        state.BehaviorPlan = PlanningBehaviorDecisions.Assemble(state, new());
        var workflow = Assert.Single(state.BehaviorPlan.Workflows);
        Assert.Equal(["record", "threshold"], workflow.Inputs.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("classifiedResult", Assert.Single(workflow.Outputs).Name);
        Assert.Contains("standard otherwise", state.BehaviorPlan.Summary);
        Assert.Contains("Preserve the original id and amount", workflow.Outputs[0].Description);
        PlanningGraphSkeleton.Create(state);
        var input = state.Graph!.Workflows[0].Inputs.Single(p => p.Name == "threshold");
        Assert.False(input.Required); Assert.Equal(100m, input.Default!.Number);
        Assert.Null(state.Declarations.Single(d => d.Direction == "output").DefaultReference);
        var restored = PlanningContext.Clone(state); var requests = runtime.Requests.Count; var events = restored.Events.Count;
        await PlanningDeclarations.ResolveAsync(restored, runtime, Ct);
        Assert.Equal(requests, runtime.Requests.Count); Assert.Equal(events, restored.Events.Count);
    }

    [Theory]
    [InlineData("distinct")]
    [InlineData("same_as")]
    [InlineData("modifier_of")]
    public void OutputDefaultAssignmentsFailTheResponseSchema(string disposition)
    {
        var (state, _) = Fixture();
        // Retained failure selected false from the classification clause as an output default.
        var conditionalLiteral = Token(state, "condition", "false");
        var assignment = disposition == "distinct" ? Distinct(state, "output", "classifiedResult")
            : Link("preservation", "output", disposition);
        assignment = assignment with { DefaultReference = conditionalLiteral };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(assignment), Schema(state, assignment.CandidateId)));
    }

    [Theory]
    [InlineData("same_as", "record", "output")]
    [InlineData("modifier_of", "record", "output")]
    [InlineData("same_as", "output", "record")]
    [InlineData("modifier_of", "output", "record")]
    [InlineData("modifier_of", "omission", "output")]
    [InlineData("same_as", "omission", "threshold")]
    public void IncompatibleLinksAreAbsentFromTheResponseSchema(string disposition, string candidate, string target)
    {
        var (state, _) = Fixture();
        var value = Link(candidate, target, disposition, candidate == "omission" ? "optional" : "unspecified",
            candidate == "omission" ? Token(state, "omission", "100") : null);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(value), Schema(state, candidate)));
    }

    [Theory]
    [InlineData("required")]
    [InlineData("unspecified")]
    public void OmissionDefaultRequiresOptionalPresenceInTheSchema(string presence)
    {
        var (state, _) = Fixture();
        var value = Link("omission", "threshold", "modifier_of", presence, Token(state, "omission", "100"));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(value), Schema(state, "omission")));
    }

    [Fact]
    public void DefaultLiteralMustBeInsideItsOmissionEvidenceNotJustTheSameClause()
    {
        var state = State("Optional input limit defaults to 100 when omitted, and false is a branch condition.");
        Add(state, "Optional input limit", "input"); Add(state, "defaults to 100 when omitted", "omission", "omission_default");
        var schema = Schema(state, "omission");
        foreach (var (token, valid) in new[] { ("100", true), ("false", false) })
        {
            var value = Link("omission", "input", "modifier_of", "optional", Token(state, "omission", token));
            Assert.Equal(valid, PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(value), schema).Count == 0);
        }
        var direct = Distinct(state, "input", "limit", "optional", "100");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(direct), Schema(state, "input")));
    }

    [Fact]
    public void MissingLiteralHasNoModifierEscapeHatch()
    {
        var state = State("Optional input limit defaults when omitted.");
        Add(state, "Optional input limit", "input"); Add(state, "defaults when omitted", "omission", "omission_default");
        var schema = Schema(state, "omission");
        Assert.DoesNotContain("modifier_of", schema.ToJsonString());
        Assert.Contains("unresolved", schema.ToJsonString());
    }

    [Fact]
    public void CapturedFallbackCannotAuthorizeTheHistoricalOutputDefaultEvenIfMisclassified()
    {
        var (state, _) = Fixture();
        // b8c6201e revision 18: this span was historically default_value. This
        // deliberately wrong new-kind selection is synthetic, never a replacement receipt.
        Add(state, "standard otherwise.", "misclassified", "omission_default");
        var proposed = Link("misclassified", "output", "modifier_of", "optional", Token(state, "condition", "false"));
        var schema = Schema(state, "misclassified");
        Assert.DoesNotContain("modifier_of", schema.ToJsonString());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(proposed), schema));
    }

    [Fact]
    public void BaselineOutputAndRequiredInputCannotReceiveOmissionDefaults()
    {
        var (state, _) = Fixture();
        state.Request.Baseline = TypedPlannerTests.Graph();
        state.Request.Baseline.Workflows[0].Inputs.Add(new() { Name = "required", Required = true, Schema = new() { Type = "number" } });
        foreach (var baseline in PlanningDeclarations.Baselines(state))
        {
            var value = Link("omission", baseline.Key, "modifier_of", "optional", Token(state, "omission", "100"));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(value), Schema(state, "omission")));
        }
    }

    [Fact]
    public void OldAmbiguousKindHasNoAdmissionOrSchemaAlternative()
    {
        var state = State("Optional input limit defaults to 100 when omitted.");
        Assert.DoesNotContain("default_value", PlanningSourceGroundingRules.Kinds(PlanningSourceAuthority.RequestedBehavior));
        Assert.Throws<WorkflowRuntimeException>(() => Add(state, "defaults to 100 when omitted.", "historical", "default_value"));
    }
}
