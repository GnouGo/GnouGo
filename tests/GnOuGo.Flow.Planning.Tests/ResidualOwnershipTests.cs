using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

// Source meanings here are explicit synthetic answers, never inferred fixture authority.
public sealed class ResidualOwnershipTests
{
    private static (PlanningSnapshot State, PlanningOperations.Scope Scope) Setup(string text = "Choose the standard result otherwise.", int supportWords = 4)
    {
        var state = OperationAdmissionTests.State(text);
        state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        var scope = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        var action = scope.Select("b0", "b" + supportWords); state.References.Add(action);
        PlanningFixtures.Runtime(state, action, independentBoundary: false);
        OperationEffectFixtures.SeedDefaultRequests(state);
        return (state, Assert.Single(PlanningOperations.Scopes(state)));
    }

    private static JsonObject Answer(PlanningSnapshot state, PlanningOperations.Scope scope, string kind = "runtime_fallback", string group = "g0") => new()
    {
        ["status"] = "qualified", ["units"] = new JsonArray(),
        ["residual"] = new JsonObject(PlanningOperations.ResidualPortions(state, scope).Select(p =>
            new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject { ["kind"] = kind, ["group"] = group })))
    };

    [Fact]
    public void RetainedOmissionIsImpossibleInQualifiedSchemaAndSupportRemainsFixed()
    {
        var (state, scope) = Setup("Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.", 16);
        var portion = Assert.Single(PlanningOperations.ResidualPortions(state, scope));
        Assert.Equal("otherwise.", scope.Source.Text.Substring(portion.Reference.Start, portion.Reference.Length));
        var before = JsonSerializer.Serialize(PlanningOperations.ReadExecutionRequests(state).ToList(), PlanningJsonContext.Default.ListPlanningExecutionRequestProof);
        var answer = Answer(state, scope);
        var decision = PlanningOperations.ContributionDecision(state, scope);
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, decision.Schema));
        var proof = PlanningOperations.ParseContributions(state, scope, answer);
        Assert.Equal(8, proof.Version);
        var property = Assert.Single(proof.Contributions, c => c.Role == "governing_property");
        Assert.Empty(property.RuntimeEvidenceIds); Assert.Null(property.EffectId);
        Assert.Equal("otherwise.", PlanningChoiceEvidence.Text(state, property.EvidenceReference));
        Assert.Equal(before, JsonSerializer.Serialize(PlanningOperations.ReadExecutionRequests(state).ToList(), PlanningJsonContext.Default.ListPlanningExecutionRequestProof));
        answer["residual"]!.AsObject().Remove(portion.Key);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, decision.Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, answer));
        Assert.Null(state.OperationAdmissionFingerprint);
    }

    [Theory]
    [InlineData("excluded")]
    [InlineData("runtime_condition")]
    [InlineData("descriptive_property")]
    public void EstablishedFallbackRemovesConflictingChoicesBeforeDispatch(string kind)
    {
        var (state, scope) = Setup();
        PolicyGroundingTests.Add(state, "request", "otherwise.", "fallback", "runtime_fallback");
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        OperationEffectFixtures.SeedDefaultRequests(state);
        var answer = Answer(state, scope, kind);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.ContributionDecision(state, scope).Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, answer));
    }

    [Fact]
    public void AdjacentGroupingIsExplicitAndGroupNamesNeverBecomeProofIdentity()
    {
        var (state, scope) = Setup("Transform the value with a stable result.", 3);
        var answer = Answer(state, scope, "descriptive_property");
        var first = PlanningOperations.ParseContributions(state, scope, answer);
        Assert.Single(first.Contributions, c => c.Role == "governing_property");
        foreach (var item in answer["residual"]!.AsObject()) item.Value!["group"] = "g5";
        state.References.Reverse(); state.RuntimeEvidence.Reverse(); state.Obligations.Reverse();
        var second = PlanningOperations.ParseContributions(state, scope, answer);
        Assert.Equal(first.ProofFingerprint, second.ProofFingerprint);
        answer["residual"]!.AsObject().Last().Value!["group"] = "g2";
        Assert.Equal(2, PlanningOperations.ParseContributions(state, scope, answer).Contributions.Count(c => c.Role == "governing_property"));
    }

    [Fact]
    public void DistinctGroundingCannotBeCoalescedByTheSameResponseGroup()
    {
        var (state, scope) = Setup("Transform the value with care and accuracy.", 3);
        PolicyGroundingTests.Add(state, "request", "with care", "condition-a", "runtime_condition");
        PolicyGroundingTests.Add(state, "request", "and accuracy.", "condition-b", "runtime_condition");
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        OperationEffectFixtures.SeedDefaultRequests(state);
        var proof = PlanningOperations.ParseContributions(state, scope, Answer(state, scope, "runtime_condition"));
        var properties = proof.Contributions.Where(c => c.Role == "governing_property").ToArray();
        Assert.Equal(2, properties.Length);
        Assert.Equal(new[] { "condition-a", "condition-b" }, properties.SelectMany(c => c.SourceBindings).Select(b => b.SemanticObligationId).Order());
    }

    [Fact]
    public void DifferentMeaningsWithinOneGapRemainSeparateEvenWithTheSameGroup()
    {
        var (state, scope) = Setup("Transform otherwise under strict constraints.", 1);
        var answer = Answer(state, scope, "runtime_rule");
        answer["residual"]!["p0"]!["kind"] = "runtime_fallback";
        var proof = PlanningOperations.ParseContributions(state, scope, answer);
        var properties = proof.Contributions.Where(c => c.Role == "governing_property").ToArray();
        Assert.Equal(2, properties.Length);
        Assert.Equal("otherwise", PlanningChoiceEvidence.Text(state, Assert.Single(properties, c => c.GoverningKind == "runtime_fallback").EvidenceReference));
        Assert.Equal("under strict constraints.", PlanningChoiceEvidence.Text(state, Assert.Single(properties, c => c.GoverningKind == "runtime_rule").EvidenceReference));
    }

    [Fact]
    public void ConflictingEstablishedKindsFailBeforeAPropertyRequestCanBeIssued()
    {
        var (state, scope) = Setup();
        PolicyGroundingTests.Add(state, "request", "otherwise.", "fallback", "runtime_fallback");
        PolicyGroundingTests.Add(state, "request", "otherwise.", "condition", "runtime_condition");
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        OperationEffectFixtures.SeedDefaultRequests(state);
        var error = Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ContributionDecision(state, scope));
        Assert.Contains("conflicting established semantic kinds", error.Message);
        Assert.Empty(state.RequestAccounting);
    }

    [Theory]
    [InlineData("Transform β otherwise!", 2, "otherwise!")]
    [InlineData("Transform 😀 with précision, reliably.", 2, "with précision, reliably.")]
    public void CoordinatesPreserveUnicodeAndPunctuation(string text, int words, string expected)
    {
        var (state, scope) = Setup(text, words);
        var proof = PlanningOperations.ParseContributions(state, scope, Answer(state, scope, "descriptive_property"));
        var property = Assert.Single(proof.Contributions, c => c.Role == "governing_property");
        Assert.Equal(expected, PlanningChoiceEvidence.Text(state, property.EvidenceReference));
    }

    [Fact]
    public void MissingForeignAndLegacyAnswersCannotAuthorizeCurrentProofs()
    {
        var (state, scope) = Setup(); var decision = PlanningOperations.ContributionDecision(state, scope);
        var answer = Answer(state, scope); answer["residual"]!["foreign"] = new JsonObject { ["kind"] = "runtime_fallback", ["group"] = "g0" };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, decision.Schema));
        var legacy = new JsonObject { ["status"] = "qualified", ["units"] = new JsonArray() };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(legacy, decision.Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, legacy));
        OperationEffectFixtures.SeedPages(state, [decision], new() { [decision.Id] = Answer(state, scope) });
        state.Request.Prompt += " Additional evidence.";
        Assert.ThrowsAny<Exception>(() => PlanningOperations.ReadContributions(state));
    }

    [Fact]
    public void FragmentationCannotIncreaseTheSixUnitAllowance()
    {
        var (state, scope) = Setup("Transform value with stable precise repeatable complete local rules.", 2);
        var answer = Answer(state, scope, "descriptive_property");
        var index = 0;
        foreach (var item in answer["residual"]!.AsObject()) item.Value!["group"] = "g" + index++ % 2;
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.ContributionDecision(state, scope).Schema));
        var error = Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, answer));
        Assert.Contains("semantic-unit allowance", error.Message);
    }

    [Fact]
    public void DeclarationIslandCannotBeReclassifiedOrBridgedByGrouping()
    {
        var state = DeclarationGroundingTests.State("Required output result. Transform value under policy preserve original members with care.");
        state.OperationAdmissionFingerprint = null;
        DeclarationGroundingTests.Add(state, "Required output result.", "result");
        var contract = DeclarationGroundingTests.Add(state, "preserve original members", "preservation", "declaration_constraint");
        PlanningDeclarations.Commit(state, DeclarationGroundingTests.Canonicalize(state,
            [DeclarationGroundingTests.Distinct(state, "result", "result", direction: "output"),
             DeclarationGroundingTests.Link("preservation", "result", "modifier_of")]), PlanningDeclarations.EvidenceFingerprint(state));
        var source = PlanningOperations.SourceScopes(state).Single(s => s.Source.Text.Substring(s.Clause.Start, s.Clause.Length).Contains("Transform", StringComparison.Ordinal));
        var action = source.Select("b0", "b2"); state.References.Add(action);
        PlanningFixtures.Runtime(state, action, independentBoundary: false);
        var scope = Assert.Single(PlanningOperations.Scopes(state));
        OperationEffectFixtures.SeedDefaultRequests(state);
        var proof = PlanningOperations.ParseContributions(state, scope, Answer(state, scope, "descriptive_property"));
        var properties = proof.Contributions.Where(c => c.Role == "governing_property").ToArray();
        Assert.Equal(2, properties.Length);
        Assert.Equal(new[] { "under policy", "with care." }, properties.Select(c => PlanningChoiceEvidence.Text(state, c.EvidenceReference)).Order());
        var owned = state.References.Single(r => r.Id == contract.EvidenceReferences[0]);
        Assert.All(PlanningOperations.ResidualPortions(state, scope), p => Assert.True(p.Reference.Start >= owned.Start + owned.Length || p.Reference.Start + p.Reference.Length <= owned.Start));
    }

    [Fact]
    public void NonRequestDescriptionRetainsRuntimeAndSourceOnlyOwnershipSeparately()
    {
        var (state, scope) = Setup("This is deterministic local processing.", 4);
        var joint = OperationEffectFixtures.ContributionAnswer(state, scope, OperationEffectFixtures.Defer(scope));
        joint["units"]![0]!["evidence"] = scope.Clause.Id;
        var proof = OperationEffectFixtures.ParseQualification(state, scope, joint);
        Assert.DoesNotContain(proof.Contributions, c => c.Role == "supports");
        Assert.Equal(2, proof.Contributions.Count);
        Assert.Contains(proof.Contributions, c => c.RuntimeEvidenceIds.Count == 1);
        Assert.Contains(proof.Contributions, c => c.RuntimeEvidenceIds.Count == 0);
        Assert.All(proof.Contributions, c => Assert.Equal("descriptive_property", c.GoverningKind));
    }

    [Fact]
    public void OwnershipEdgesMaySplitWordsWithoutLosingAnySourcePosition()
    {
        var (state, scope) = Setup("Transform item otherwise.", 2);
        PolicyGroundingTests.Add(state, "request", "other", "partial", "runtime_condition");
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        OperationEffectFixtures.SeedDefaultRequests(state);
        var portions = PlanningOperations.ResidualPortions(state, scope);
        Assert.Equal(new[] { "other", "wise." }, portions.Select(p => scope.Source.Text.Substring(p.Reference.Start, p.Reference.Length)));
        var proof = PlanningOperations.ParseContributions(state, scope, Answer(state, scope, "runtime_condition"));
        Assert.Equal(2, proof.Contributions.Count(c => c.Role == "governing_property"));
        Assert.Contains(proof.SourceBindings, b => b.SemanticObligationId == "partial");
    }
}
