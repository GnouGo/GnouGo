using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.DeclarationGroundingTests;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Bounded synthetic decisions over retained evidence, never substitutes for live receipts.</summary>
public sealed class NeutralDeclarationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static void Commit(PlanningSnapshot state, List<PlanningDeclarationAssignment> values)
        => PlanningDeclarations.Commit(state, values, PlanningDeclarations.EvidenceFingerprint(state));

    [Fact]
    public async Task CapturedStageOneMemberEvidencePreservesTheTwoInputOneOutputContract()
    {
        var (state, values) = OmissionDefaultTests.Fixture();
        Add(state, "category has exactly the values rejected, high, standard.", "category");
        values.Add(Link("category", values.Single(a => a.CandidateId == "preservation").TargetId!, "modifier_of"));
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse { Json = Response(request, values) }) };
        await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        var behavior = PlanningBehaviorDecisions.Assemble(state, new());
        var workflow = Assert.Single(behavior.Workflows);
        Assert.Equal(["record", "threshold"], workflow.Inputs.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(workflow.Inputs.Single(p => p.Name == "record").Required);
        Assert.False(workflow.Inputs.Single(p => p.Name == "threshold").Required);
        var output = Assert.Single(workflow.Outputs);
        Assert.Equal("classifiedResult", output.Name); Assert.True(output.Required);
        Assert.Contains("category has exactly the values rejected, high, standard.", output.Description);
        Assert.Contains("Preserve the original id and amount.", output.Description);
        Assert.Equal(100m, PlanningDeclarations.Default(state, state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold"))!.Number);
        Assert.Empty(PlanningDeclarations.ValidateBehavior(state, behavior));
    }

    [Fact]
    public void RootPresenceCanUseOwnedOmissionEvidenceFromAnotherClause()
    {
        var state = State("Input limit is supplied. It defaults to 100 when omitted.");
        Add(state, "Input limit is supplied.", "input");
        var omission = Add(state, "defaults to 100 when omitted.", "omission", "omission_default");
        var root = Distinct(state, "input", "limit", "optional") with { PresenceReference = omission.Grounding!.ClauseReference };
        var values = Canonicalize(state, [root, Link("omission", "input", "modifier_of", "optional", Token(state, "omission", "100"))]);
        Commit(state, values);
        var declaration = Assert.Single(state.Declarations);
        Assert.False(declaration.Required); Assert.Equal(100m, PlanningDeclarations.Default(state, declaration)!.Number);
        Assert.Contains(omission.Grounding.ClauseReference, declaration.ClauseReferences);
    }

    [Theory]
    [InlineData("record", "classifiedResult", "category")]
    [InlineData("parcel", "inspection", "classification")]
    public async Task MemberEvidenceCanAttachToAnOutputWithoutInventingAnInput(string input, string output, string member)
    {
        var state = State($"Required input {input} is an object. Required output {output} contains {member}. {member} has exactly the values rejected, high, standard.");
        Add(state, $"Required input {input}", "input"); Add(state, $"Required output {output}", "output");
        Add(state, $"{member} has exactly the values rejected, high, standard.", "member");
        // 4d45ca3d: this member clause was labelled business_input. This fixture
        // deliberately supplies no direction; canonical targets make the output eligible.
        var values = Canonicalize(state, [Distinct(state, "input", input), Distinct(state, "output", output, direction: "output"), Link("member", "output", "modifier_of")]);
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse { Json = Response(request, values) }) };
        await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        Assert.Equal(2, runtime.Requests.Count);
        Assert.Equal(input, PlanningDeclarations.Name(state, Assert.Single(state.Declarations, d => d.Direction == "input")));
        var canonical = Assert.Single(state.Declarations, d => d.Direction == "output");
        Assert.Equal(output, PlanningDeclarations.Name(state, canonical)); Assert.NotEmpty(canonical.ModifierReferences);
        Assert.DoesNotContain(state.Declarations, d => PlanningDeclarations.Name(state, d) == member);
        Assert.Contains($"{member} has exactly", PlanningDeclarations.Port(state, canonical).Description);
        Assert.All(state.Obligations, o => Assert.Equal("declaration_candidate", o.Kind));
        Assert.Empty(state.DecisionCorrections); Assert.Empty(state.RepairAllowances);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    public void DirectionAndExplicitPresenceAreEstablishedOnlyByCanonicalAdjudication(string direction)
    {
        var state = State($"Required {direction} parcel is an object."); Add(state, "parcel", "candidate");
        var value = Distinct(state, "candidate", "parcel", direction: direction);
        var schema = OmissionDefaultTests.Schema(state, "candidate", [value], false);
        Assert.Empty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(value), schema));
        foreach (var invalid in new[] { value with { Presence = "unspecified" }, value with { DeclarationReference = null },
            value with { PresenceReference = null }, value with { PresenceReference = "foreign" } })
        {
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(invalid), schema));
            Assert.Throws<WorkflowRuntimeException>(() => Commit(state, [invalid]));
            Assert.Empty(state.Declarations);
        }
        Commit(state, [value]); Assert.Equal(direction, Assert.Single(state.Declarations).Direction);
    }

    [Fact]
    public void HistoricalAssignmentIsRejectedBeforeReviewRatherThanInventingRequiredness()
    {
        var (state, values) = OmissionDefaultTests.Fixture();
        Add(state, "category has exactly the values rejected, high, standard.", "member");
        var historical = new JsonObject { ["disposition"] = "distinct", ["name"] = Token(state, "member", "category"),
            ["scope"] = "main", ["presence"] = "unspecified", ["default"] = null };
        var schema = OmissionDefaultTests.Schema(state, "member", values, false);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(historical, schema));
        historical["disposition"] = "distinct_input";
        historical["declaration"] = state.Obligations.Single(o => o.Id == "member").Grounding!.ClauseReference;
        historical["presenceEvidence"] = historical["declaration"]!.DeepClone();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(historical, schema));
        Assert.Null(state.BehaviorPlan); Assert.Empty(state.Declarations);
    }

    [Fact]
    public void ModifiersCannotRewritePortPresenceOrSelectCandidateChains()
    {
        var (state, values) = OmissionDefaultTests.Fixture();
        var modifier = values.Single(a => a.CandidateId == "preservation");
        var schema = OmissionDefaultTests.Schema(state, modifier.CandidateId, values);
        foreach (var invalid in new[] { modifier with { Presence = "required" }, modifier with { Presence = "optional" },
            modifier with { TargetId = "output" }, modifier with { TargetId = "preservation" }, modifier with { TargetId = "foreign" } })
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(invalid), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(modifier), schema));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartReusesCompletedRootsAndAttachmentsWithoutDuplicateAccounting(bool attachmentsCompleted)
    {
        var (state, values) = OmissionDefaultTests.Fixture();
        state.Request.MaxRepairsPerWorkflowGate = 0;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse { Json = Response(request, values) }) };
        await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_declarations", "$plan", PlanningDeclarations.Decisions(state), Ct);
        var attachments = PlanningDeclarations.AttachmentDecisions(state, Roots(state, values));
        if (attachmentsCompleted) await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_declarations", "$plan", attachments, Ct);
        Assert.Empty(state.Declarations); Assert.Null(state.DeclarationFingerprint);
        var pages = state.DecisionPages.Select(p => (p.Id, p.RequestId)).ToArray();
        var requestIds = state.RequestAccounting.Select(r => r.Id).ToArray();
        var restored = PlanningContext.Clone(state);
        Assert.Equal(attachments.Select(p => p.EvidenceFingerprint), PlanningDeclarations.AttachmentDecisions(restored, Roots(restored, values)).Select(p => p.EvidenceFingerprint));
        var resumed = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            Assert.All(request.StructuredOutputSchema!["properties"]!.AsObject(), p => Assert.StartsWith("declarations_attachments_", p.Key));
            return Task.FromResult(new LLMResponse { Json = Response(request, values) });
        } };
        await PlanningDeclarations.ResolveAsync(restored, resumed, Ct);
        Assert.Equal(attachmentsCompleted ? 0 : 1, resumed.Requests.Count);
        Assert.All(pages, p => Assert.Contains(restored.DecisionPages, r => (r.Id, r.RequestId) == p));
        Assert.All(requestIds, id => Assert.Single(restored.RequestAccounting, r => r.Id == id));
        Assert.Empty(restored.DecisionCorrections); Assert.Empty(restored.RepairAllowances);
        Assert.Equal(3, restored.Declarations.Count);
        var count = restored.RequestAccounting.Count;
        await PlanningDeclarations.ResolveAsync(restored, resumed, Ct);
        Assert.Equal(count, restored.RequestAccounting.Count);
    }

    [Theory]
    [InlineData("direction")]
    [InlineData("scope")]
    [InlineData("presence")]
    public void AttachmentRequestsAreBoundToCurrentCanonicalContracts(string change)
    {
        var (state, values) = OmissionDefaultTests.Fixture();
        var roots = Roots(state, values);
        var before = PlanningDeclarations.AttachmentDecisions(state, roots).Select(p => p.EvidenceFingerprint).ToArray();
        var index = roots.FindIndex(a => a.CandidateId == "output");
        if (change == "direction") roots[index] = roots[index] with { Disposition = "distinct_input" };
        if (change == "presence") roots[index] = roots[index] with { Presence = "optional" };
        if (change == "scope")
        {
            Add(state, "one reusable workflow", "boundary", "workflow_boundary");
            roots[index] = roots[index] with { WorkflowScope = "boundary" };
        }
        var after = PlanningDeclarations.AttachmentDecisions(state, roots);
        Assert.NotEqual(before, after.Select(p => p.EvidenceFingerprint));
        if (change != "presence")
        {
            var old = values.Single(a => a.CandidateId == "preservation");
            var schema = after.Single(p => p.Schema["properties"]?[old.CandidateId] is not null).Schema["properties"]![old.CandidateId]!.AsObject();
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(old), schema));
        }
    }

    [Fact]
    public void HistoricalDirectionalProofCannotBypassAdmissionAfterRestore()
    {
        var state = State("Required input parcel."); var current = Add(state, "parcel", "candidate");
        state.Obligations[0] = current with { Kind = "business_input" };
        Assert.Throws<WorkflowRuntimeException>(() => PlanningDeclarations.RequireCurrent(PlanningContext.Clone(state)));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningSourceGroundingRules.ValidateAll(state));
    }

    [Fact]
    public void ADeferredAttachmentCannotBeCommittedAsADeclaration()
    {
        var state = State("An object has a status member."); Add(state, "status", "candidate");
        Assert.Throws<WorkflowRuntimeException>(() => Commit(state, [Retire("candidate") with { Disposition = "deferred_attachment" }]));
        Assert.Empty(state.Declarations); Assert.Null(state.DeclarationFingerprint);
    }
}
