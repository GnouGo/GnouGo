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
    internal static (PlanningSnapshot State, List<PlanningDeclarationAssignment> Assignments) Fixture()
    {
        var state = State(Classifier);
        Add(state, "Required input record", "record");
        Add(state, "Optional input threshold", "threshold");
        Add(state, "defaulting to 100 when omitted.", "omission", "omission_default");
        Add(state, "Return classifiedResult", "output");
        Add(state, "standard otherwise.", "fallback", "runtime_fallback");
        Add(state, "approved is false", "condition", "runtime_condition");
        Add(state, "Preserve the original id and amount.", "preservation");
        Add(state, "classifying a single record.", "operation", "local_processing");
        state.Preparation!.Capabilities.Add(new() { Id = "local", StepType = "set", Resolution = "local", Required = true,
            Description = "Classify the original record under the declared rule.", OperationIds = ["operation"] });
        return (state, Canonicalize(state, [Distinct(state, "record", "record"), Distinct(state, "threshold", "threshold", "optional"),
            Link("omission", "threshold", "modifier_of", "optional", Token(state, "omission", "100")),
            Distinct(state, "output", "classifiedResult", direction: "output"), Link("preservation", "output", "modifier_of")]));
    }
    internal static JsonObject Schema(PlanningSnapshot state, string candidate, List<PlanningDeclarationAssignment> assignments, bool attachment = true)
        => (attachment ? PlanningDeclarations.AttachmentDecisions(state, Roots(state, assignments)) : PlanningDeclarations.Decisions(state))
            .Single(p => p.Schema["properties"]?[candidate] is not null).Schema["properties"]![candidate]!.AsObject();

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
    [InlineData("distinct_output")]
    [InlineData("same_as")]
    [InlineData("modifier_of")]
    public void OutputDefaultAssignmentsFailTheResponseSchema(string disposition)
    {
        var (state, assignments) = Fixture();
        var assignment = disposition == "distinct_output" ? assignments.Single(a => a.CandidateId == "output")
            : assignments.Single(a => a.CandidateId == "preservation") with { Disposition = disposition };
        assignment = assignment with { DefaultReference = Token(state, "condition", "false") };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(assignment),
            Schema(state, assignment.CandidateId, assignments, disposition != "distinct_output")));
    }

    [Theory]
    [InlineData("same_as", "record", "output")]
    [InlineData("modifier_of", "record", "output")]
    [InlineData("same_as", "output", "record")]
    [InlineData("modifier_of", "output", "record")]
    public void EstablishedRootsCannotBecomeCrossDirectionLinks(string disposition, string candidate, string target)
    {
        var (state, assignments) = Fixture();
        var value = Canonicalize(state, assignments.Append(Link("attempt", target, disposition))).Last() with { CandidateId = candidate };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(value), Schema(state, candidate, assignments, false)));
        Assert.DoesNotContain(PlanningDeclarations.AttachmentDecisions(state, Roots(state, assignments)), p => p.Schema["properties"]?[candidate] is not null);
    }

    [Theory]
    [InlineData("modifier_of", "output")]
    [InlineData("modifier_of", "record")]
    [InlineData("same_as", "threshold")]
    public void OmissionTargetsOnlyOptionalInputModifiers(string disposition, string target)
    {
        var (state, assignments) = Fixture();
        var value = Canonicalize(state, assignments.Append(Link("attempt", target, disposition, "optional", Token(state, "omission", "100")))).Last() with { CandidateId = "omission" };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(value), Schema(state, "omission", assignments)));
    }

    [Theory]
    [InlineData("required")]
    [InlineData("unspecified")]
    public void OmissionDefaultRequiresOptionalPresenceInTheSchema(string presence)
    {
        var (state, assignments) = Fixture();
        var value = assignments.Single(a => a.CandidateId == "omission") with { Presence = presence };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(value), Schema(state, "omission", assignments)));
    }

    [Fact]
    public void DefaultLiteralMustBeInsideItsOmissionEvidenceNotJustTheSameClause()
    {
        var state = State("Optional input limit defaults to 100 when omitted, and false is a branch condition.");
        Add(state, "Optional input limit", "input"); Add(state, "defaults to 100 when omitted", "omission", "omission_default");
        var assignments = Canonicalize(state, [Distinct(state, "input", "limit", "optional"), Link("omission", "input", "modifier_of", "optional", Token(state, "omission", "100"))]);
        var schema = Schema(state, "omission", assignments);
        foreach (var (token, valid) in new[] { ("100", true), ("false", false) })
        {
            var value = assignments[1] with { DefaultReference = Token(state, "omission", token) };
            Assert.Equal(valid, PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(value), schema).Count == 0);
        }
        var direct = Distinct(state, "input", "limit", "optional", "100");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(direct), Schema(state, "input", assignments, false)));
    }

    [Fact]
    public void MissingLiteralHasNoModifierEscapeHatch()
    {
        var state = State("Optional input limit defaults when omitted.");
        Add(state, "Optional input limit", "input"); Add(state, "defaults when omitted", "omission", "omission_default");
        var schema = Schema(state, "omission", [Distinct(state, "input", "limit", "optional")]);
        Assert.DoesNotContain("modifier_of", schema.ToJsonString()); Assert.Contains("unresolved", schema.ToJsonString());
    }

    [Fact]
    public void CapturedFallbackCannotAuthorizeTheHistoricalOutputDefaultEvenIfMisclassified()
    {
        var (state, assignments) = Fixture();
        // Historical b8c6201e default_value is intentionally reclassified incorrectly
        // in this synthetic fixture. No historical receipt is substituted.
        Add(state, "standard otherwise.", "misclassified", "omission_default");
        var proposed = Link("misclassified", assignments.Single(a => a.CandidateId == "preservation").TargetId!, "modifier_of", "optional", Token(state, "condition", "false"));
        var schema = Schema(state, "misclassified", assignments);
        Assert.DoesNotContain("modifier_of", schema.ToJsonString());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(proposed), schema));
    }

    [Fact]
    public void BaselineOutputAndRequiredInputCannotReceiveOmissionDefaults()
    {
        var (state, assignments) = Fixture();
        state.Request.Baseline = TypedPlannerTests.Graph();
        state.Request.Baseline.Workflows[0].Inputs.Add(new() { Name = "required", Required = true, Schema = new() { Type = "number" } });
        foreach (var (id, port) in PlanningDeclarations.Baselines(state))
        {
            var value = Link("omission", PlanningDeclarations.CanonicalId(id, id, port.Scope, port.Direction), "modifier_of", "optional", Token(state, "omission", "100"));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(value), Schema(state, "omission", assignments)));
        }
    }

    [Fact]
    public void OldAmbiguousKindsHaveNoAdmissionOrSchemaAlternative()
    {
        var state = State("Optional input limit defaults to 100 when omitted.");
        foreach (var kind in new[] { "default_value", "business_input", "business_output" })
        {
            Assert.DoesNotContain(kind, PlanningSourceGroundingRules.Kinds(PlanningSourceAuthority.RequestedBehavior));
            Assert.Throws<WorkflowRuntimeException>(() => Add(state, "defaults to 100 when omitted.", "historical_" + kind, kind));
        }
    }
}
