using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.DeclarationGroundingTests;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Corrected classifications and assignments here are synthetic, never retained model receipts.</summary>
public sealed class DeclarationConstraintTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static void Commit(PlanningSnapshot state, List<PlanningDeclarationAssignment> values)
        => PlanningDeclarations.Commit(state, values, PlanningDeclarations.EvidenceFingerprint(state));
    private static TypedPlannerTests.FakeRuntime Runtime(List<PlanningDeclarationAssignment> values) => new()
    {
        OnCall = (phase, request, _) =>
        {
            Assert.Equal("intent_declarations", phase); Assert.Equal("low", request.Reasoning);
            var response = Response(request, values);
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
            return Task.FromResult(new LLMResponse { Json = response });
        }
    };

    [Fact]
    public async Task CapturedCorrectRootsGainEnumAndPreservationAttachmentsBeforeBehaviorReview()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "declaration-constraint-stage1.json"), Ct))!;
        var state = JsonSerializer.Deserialize(fixture["snapshot"]!.ToJsonString(), PlanningJsonContext.Default.PlanningSnapshot)!;
        var source = fixture.ToJsonString();
        var output = Assert.Single(state.Declarations, d => d.Direction == "output");
        var enumEvidence = Assert.Single(state.Obligations, o => o.Kind == "explicit_value");
        Assert.Empty(output.ModifierReferences);
        Assert.DoesNotContain(enumEvidence.Grounding!.ClauseReference, output.ClauseReferences);
        Assert.Throws<WorkflowRuntimeException>(() => PlanningSourceGroundingRules.ValidateAll(state)); // Historical v3 proof is audit-only.
        var roots = Roots(state, state.DeclarationAssignments);
        var values = state.DeclarationAssignments.ToList();
        var preservation = Assert.Single(state.Obligations, o => PlanningSourceDecisions.Text(state, o) == "Preserve the original id and amount.");
        for (var i = 0; i < state.Obligations.Count; i++)
        {
            var obligation = state.Obligations[i];
            if (obligation.Id == enumEvidence.Id || obligation.Id == preservation.Id)
                obligation = obligation with { Kind = "declaration_constraint", Disposition = "preliminary", AdjudicationFingerprint = null, PolicyIds = [] };
            state.Obligations[i] = obligation with { Grounding = PlanningSourceGroundingRules.Create(state, obligation) };
        }
        values.Add(Link(enumEvidence.Id, output.Id, "modifier_of"));
        values.Add(Link(preservation.Id, output.Id, "modifier_of"));
        // The source/root assignments are unchanged; synthetic attachment targets
        // use current public identity rather than historical candidate-derived IDs.
        var identities = state.Declarations.ToDictionary(d => d.Id, d => PlanningDeclarations.CanonicalId(PlanningDeclarations.Name(state, d), d.WorkflowScope, d.Direction));
        values = values.Select(a => a.TargetId is { } target && identities.TryGetValue(target, out var id) ? a with { TargetId = id } : a).ToList();
        state.Declarations.Clear(); state.DeclarationAssignments.Clear(); state.DeclarationFingerprint = null;
        state.Preparation = TypedPlannerTests.Preparation();
        // Revalidate the same captured root assignments; only the new attachment answers are synthetic additions.
        var runtime = Runtime(values);
        await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        Assert.Equal(roots, Roots(state, state.DeclarationAssignments));
        var canonical = Assert.Single(state.Declarations, d => d.Id == identities[output.Id]);
        Assert.Equal(output.Direction, canonical.Direction); Assert.Equal(output.Required, canonical.Required);
        Assert.Equal(output.WorkflowScope, canonical.WorkflowScope); Assert.Equal(output.NameReference, canonical.NameReference);
        Assert.Contains(enumEvidence.EvidenceReferences[0], canonical.ModifierReferences);
        Assert.Contains(enumEvidence.Grounding.ClauseReference, canonical.ClauseReferences);
        var plan = PlanningBehaviorDecisions.Assemble(state, new());
        var workflow = Assert.Single(plan.Workflows);
        Assert.Equal(["record", "threshold"], workflow.Inputs.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(workflow.Inputs.Single(p => p.Name == "record").Required);
        Assert.False(workflow.Inputs.Single(p => p.Name == "threshold").Required);
        var port = Assert.Single(workflow.Outputs);
        Assert.Equal("classifiedResult", port.Name); Assert.True(port.Required);
        Assert.Contains("category has exactly the values rejected, high, standard.", port.Description);
        Assert.Contains("Preserve the original id and amount.", port.Description);
        Assert.Equal(100m, PlanningDeclarations.Default(state, state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold"))!.Number);
        Assert.Empty(PlanningDeclarations.ValidateBehavior(state, plan));
        Assert.Empty(state.DecisionCorrections); Assert.Empty(state.RepairAllowances);
        Assert.Equal(source, fixture.ToJsonString());
    }

    [Theory]
    [InlineData("record", "classifiedResult", "category")]
    [InlineData("parcel", "inspection", "classification")]
    public async Task ConstraintsEnterAttachmentsDirectlyAndPreserveCanonicalPorts(string input, string output, string member)
    {
        var state = State($"{member} has exactly the values first, second. Required input {input} is supplied. Required output {output} contains {member}. {input} must be a non-nullable object. Preserve the original members in {output}.");
        var constraint = Add(state, $"{member} has exactly the values first, second.", "enum", "declaration_constraint");
        Add(state, $"Required input {input}", "input"); Add(state, $"Required output {output}", "output");
        Add(state, $"{input} must be a non-nullable object.", "type", "declaration_constraint");
        Add(state, $"Preserve the original members in {output}.", "preserve", "declaration_constraint");
        var values = Canonicalize(state, [Distinct(state, "input", input), Distinct(state, "output", output, direction: "output"),
            Link("enum", "output", "modifier_of"), Link("type", "input", "modifier_of"), Link("preserve", "output", "modifier_of")]);
        var rootPages = PlanningDeclarations.Decisions(state);
        Assert.DoesNotContain(rootPages, p => p.Schema["properties"]?["enum"] is not null);
        Assert.All(rootPages, p => Assert.Null(p.Context["presenceEvidence"]?[constraint.Grounding!.ClauseReference]));
        var runtime = Runtime(values);
        await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        Assert.Equal(2, state.Declarations.Count);
        var inputDeclaration = Assert.Single(state.Declarations, d => d.Direction == "input");
        var outputDeclaration = Assert.Single(state.Declarations, d => d.Direction == "output");
        Assert.Single(inputDeclaration.ModifierReferences); Assert.Equal(2, outputDeclaration.ModifierReferences.Count);
        Assert.Contains("non-nullable object", PlanningDeclarations.Port(state, inputDeclaration).Description);
        Assert.Contains("exactly the values", PlanningDeclarations.Port(state, outputDeclaration).Description);
        Assert.Contains("Preserve the original", PlanningDeclarations.Port(state, outputDeclaration).Description);
        Assert.All(state.Declarations, d => { Assert.True(d.Required); Assert.Null(d.DefaultReference); });
        Assert.All(values.Where(a => a.CandidateId is "enum" or "type" or "preserve"), a => Assert.Equal("modifier_of", a.Disposition));
    }

    [Theory]
    [InlineData("distinct_input")]
    [InlineData("distinct_output")]
    [InlineData("same_as")]
    [InlineData("deferred_attachment")]
    [InlineData("not_a_declaration")]
    [InlineData("required")]
    [InlineData("optional")]
    [InlineData("default")]
    [InlineData("scope")]
    [InlineData("name")]
    [InlineData("direction")]
    [InlineData("schema")]
    [InlineData("foreign")]
    public void ConstraintResponseSchemaRejectsEveryPortOrContractMutation(string defect)
    {
        var (state, values) = Fixture();
        var constraint = values.Single(a => a.CandidateId == "constraint");
        var json = PlanningDeclarations.Assignment(constraint);
        if (defect is "distinct_input" or "distinct_output" or "same_as" or "deferred_attachment" or "not_a_declaration") json["disposition"] = defect;
        else if (defect is "required" or "optional") json["presence"] = defect;
        else if (defect == "foreign") json["target"] = "foreign";
        else json[defect] = "unexpected";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(json, OmissionDefaultTests.Schema(state, "constraint", values)));
        Assert.Empty(PlanningContractValidation.ValidateInstance(PlanningDeclarations.Assignment(constraint), OmissionDefaultTests.Schema(state, "constraint", values)));
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("stale")]
    [InlineData("retired")]
    public void InvalidConstraintProofPreventsAnyCanonicalCommit(string defect)
    {
        var (state, values) = Fixture(); var index = values.FindIndex(a => a.CandidateId == "constraint");
        if (defect == "unresolved") values[index] = new("constraint", "unresolved", null, null, null, "unspecified", null);
        if (defect == "missing") values.RemoveAt(index);
        if (defect == "foreign") values[index] = values[index] with { TargetId = "foreign" };
        if (defect == "retired") values[index] = Retire("constraint");
        if (defect == "stale") state.Request.Prompt += " Changed.";
        Assert.Throws<WorkflowRuntimeException>(() => Commit(state, values));
        Assert.Empty(state.Declarations); Assert.Null(state.DeclarationFingerprint);
    }

    [Fact]
    public void ConstraintWithoutARootCanOnlyRemainUnresolved()
    {
        var state = State("A member is not nullable."); Add(state, state.Request.Prompt, "constraint", "declaration_constraint");
        Assert.Empty(PlanningDeclarations.Decisions(state));
        var schema = OmissionDefaultTests.Schema(state, "constraint", []);
        Assert.DoesNotContain("modifier_of", schema.ToJsonString()); Assert.DoesNotContain("not_a_declaration", schema.ToJsonString());
        var error = Assert.Throws<WorkflowRuntimeException>(() => Commit(state, [new("constraint", "unresolved", null, null, null, "unspecified", null)]));
        Assert.Equal("DECLARATION_GROUNDING_UNRESOLVED", error.Code);
    }

    [Fact]
    public async Task ActualExplicitValuesAndRuntimeConditionsDoNotBecomeModifiers()
    {
        var state = State("Use the supplied color blue. The decision returns false otherwise.");
        Add(state, "Use the supplied color blue.", "value", "explicit_value");
        Add(state, "The decision returns false otherwise.", "fallback", "runtime_fallback");
        var runtime = new TypedPlannerTests.FakeRuntime();
        await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        Assert.Empty(runtime.Requests); Assert.Empty(state.Declarations); Assert.Empty(state.DeclarationAssignments);
        Assert.Equal("explicit_value", state.Obligations[0].Kind);
    }

    [Fact]
    public async Task PolicyConstraintCannotCreatePortsOperationsOrConfirmationRequests()
    {
        var state = State("Required output report contains items.");
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = "The items preserve their original ordering." };
        Add(state, "Required output report", "output");
        var policy = PolicyGroundingTests.Add(state, "host", "The items preserve their original ordering.", "constraint", "declaration_constraint");
        Assert.Equal(PlanningSourceAuthority.ConstraintsOnly, policy.Grounding!.Authority);
        Assert.DoesNotContain("declaration_constraint", PlanningSourceGroundingRules.OperationKinds);
        Assert.DoesNotContain("declaration_constraint", PlanningSourceGroundingRules.PolicyKinds);
        var runtime = new TypedPlannerTests.FakeRuntime();
        await PlanningConfirmationPolicies.ResolveAsync(state, runtime, Ct);
        Assert.Empty(runtime.Requests); Assert.Empty(state.ScopedPolicies);
        var values = Canonicalize(state, [Distinct(state, "output", "report", direction: "output"), Link("constraint", "output", "modifier_of")]);
        Commit(state, values);
        Assert.Single(state.Declarations); Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Contains("original ordering", PlanningDeclarations.Port(state, state.Declarations[0]).Description);
    }

    [Fact]
    public void ExistingConstraintOnlyTargetsEstablishedBaselinePortsAndKeepsLockedContracts()
    {
        var state = State("Required input supplied is provided."); state.Request.Baseline = TypedPlannerTests.Graph();
        Add(state, "Required input supplied", "input");
        var source = PlanningIntentAssessment.IntentSources(state).Single(s => s.Id == "existing");
        var reference = PlanningReferences.Register(state, source.Id, source.Kind, source.Text)[0];
        var obligation = new PlanningObligation("existing", [reference.Id], "business_decision", "declaration_constraint", true);
        state.Obligations.Add(obligation with { Grounding = PlanningSourceGroundingRules.Create(state, obligation) });
        var baseline = PlanningDeclarations.Baselines(state).Single().Key;
        var values = Canonicalize(state, [Distinct(state, "input", "supplied"), Link("existing", baseline, "modifier_of")]);
        var schema = OmissionDefaultTests.Schema(state, "existing", values);
        Assert.DoesNotContain(PlanningDeclarations.CanonicalId(PlanningDeclarations.SourceName(state, values[0].NameReference!), "main", "input"), schema.ToJsonString());
        var before = PlanningGraphCompiler.Fingerprint(state.Request.Baseline);
        Commit(state, values);
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(state.Request.Baseline));
        Assert.Single(state.Declarations.Single(d => d.BaselineReference == baseline).ModifierReferences);
    }

    [Theory]
    [InlineData(PlanningSourceAuthority.RequestedBehavior)]
    [InlineData(PlanningSourceAuthority.ExistingBehavior)]
    [InlineData(PlanningSourceAuthority.ConstraintsOnly)]
    public void InterpretationSchemaAllowsConstraintsWithoutConfusingThemWithValues(PlanningSourceAuthority authority)
    {
        var state = State("A field has an allowed value domain.");
        var reference = PlanningReferences.Register(state, "request", "user_request", state.Request.Prompt)[0];
        var boundaries = PlanningReferences.Boundaries(reference, state.Request.Prompt);
        var schema = PlanningSourceDecisions.InterpretationSchema(state, authority, boundaries.Schema);
        foreach (var kind in new[] { "declaration_constraint", "explicit_value" })
            Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["start"] = "b0", ["end"] = "b1", ["kind"] = kind, ["required"] = true }, schema));
        Assert.Contains("declaration_constraint", schema.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstraintAttachmentsReuseRootsAndReceiptsAcrossRestart(bool attachmentCompleted)
    {
        var (state, values) = Fixture(); state.Request.MaxRepairsPerWorkflowGate = 0;
        var runtime = Runtime(values);
        await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_declarations", "$plan", PlanningDeclarations.Decisions(state), Ct);
        if (attachmentCompleted)
            await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_declarations", "$plan", PlanningDeclarations.AttachmentDecisions(state, Roots(state, values)), Ct);
        var pages = state.DecisionPages.Select(p => (p.Id, p.RequestId)).ToArray();
        var restored = PlanningContext.Clone(state); var resumed = Runtime(values);
        await PlanningDeclarations.ResolveAsync(restored, resumed, Ct);
        Assert.Equal(attachmentCompleted ? 0 : 1, resumed.Requests.Count);
        Assert.All(resumed.Requests, r => Assert.All(r.StructuredOutputSchema!["properties"]!.AsObject(), p => Assert.StartsWith("declarations_attachments_", p.Key)));
        Assert.All(pages, p => Assert.Contains(restored.DecisionPages, r => (r.Id, r.RequestId) == p));
        var calls = restored.RequestAccounting.Count; var events = restored.Events.Count;
        await PlanningDeclarations.ResolveAsync(restored, resumed, Ct);
        Assert.Equal(calls, restored.RequestAccounting.Count); Assert.Equal(events, restored.Events.Count);
        Assert.Empty(restored.DecisionCorrections); Assert.Empty(restored.RepairAllowances);
    }

    private static (PlanningSnapshot, List<PlanningDeclarationAssignment>) Fixture()
    {
        var state = State("Required output report contains status. The status domain contains one, two.");
        Add(state, "Required output report", "output"); Add(state, "The status domain contains one, two.", "constraint", "declaration_constraint");
        return (state, Canonicalize(state, [Distinct(state, "output", "report", direction: "output"), Link("constraint", "output", "modifier_of")]));
    }
}
