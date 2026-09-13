using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic adjudications of sanitized retained evidence; never historical model receipts.</summary>
public sealed class DeclarationGroundingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal const string Classifier = "Create one reusable workflow classifying a single record.\nRequired input record is an object with required id:string, amount:number and approved:boolean.\nOptional input threshold is a non-nullable number defaulting to 100 when omitted.\nReturn classifiedResult:{id:string,amount:number,category:string}, all members required.\nClassify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.\ncategory has exactly the values rejected, high, standard. Preserve the original id and amount.\nThis is deterministic, local, in-memory business processing.";
    internal static PlanningSnapshot State(string prompt)
    { var state = TypedPlannerTests.Session(); state.Request.Prompt = prompt; state.Preparation = TypedPlannerTests.Preparation(); return state; }
    internal static PlanningObligation Add(PlanningSnapshot state, string fragment, string id, string kind = "business_input")
        => PolicyGroundingTests.Add(state, "request", fragment, id, kind);
    internal static void UseBaselinePorts(PlanningSnapshot state, PlanningBehaviorPlan plan)
    {
        // Isolate downstream behavior tests with already established public
        // contracts, rather than granting authority to ungrounded fixture ports.
        state.Request.Baseline = new() { Entrypoint = plan.Entrypoint, Workflows = plan.Workflows.Select(w => new PlanningWorkflow
        {
            Key = w.Key,
            Inputs = w.Inputs.Select(p => new PlanningPort { Name = p.Name, Required = p.Required, Schema = new() { Type = "string" } }).ToList(),
            Outputs = w.Outputs.Select(p => new PlanningOutput { Name = p.Name, Schema = new() { Type = "string" }, Value = new() { Kind = "string", Text = "fixture" } }).ToList()
        }).ToList() };
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        foreach (var workflow in plan.Workflows)
        {
            var scope = workflow.Key == plan.Entrypoint ? "main" : workflow.Key;
            workflow.Inputs = state.Declarations.Where(d => d.WorkflowScope == scope && d.Direction == "input").Select(d => PlanningDeclarations.Port(state, d)).ToList();
            workflow.Outputs = state.Declarations.Where(d => d.WorkflowScope == scope && d.Direction == "output").Select(d => PlanningDeclarations.Port(state, d)).ToList();
        }
    }

    internal static string Token(PlanningSnapshot state, string candidate, string text)
    {
        var obligation = state.Obligations.Single(o => o.Id == candidate);
        var clause = state.References.Single(r => r.Id == obligation.Grounding!.ClauseReference);
        return PlanningReferences.Lexical(state, clause, PlanningSourceDecisions.Sources(state)[clause.SourceId])
            .First(r => PlanningChoiceEvidence.Text(state, r.Id) == text).Id;
    }
    internal static PlanningDeclarationAssignment Distinct(PlanningSnapshot state, string id, string name, string presence = "required", string? defaultText = null)
        => new(id, "distinct", null, Token(state, id, name), "main", presence, defaultText is null ? null : Token(state, id, defaultText));
    internal static PlanningDeclarationAssignment Link(string id, string target, string disposition = "same_as", string presence = "unspecified", string? defaultReference = null)
        => new(id, disposition, target, null, null, presence, defaultReference);
    internal static PlanningDeclarationAssignment Retire(string id) => new(id, "not_a_declaration", null, null, null, "unspecified", null);
    internal static JsonObject Response(LLMRequest request, IEnumerable<PlanningDeclarationAssignment> assignments)
    {
        var fields = assignments.ToDictionary(a => a.CandidateId, PlanningDeclarations.Assignment, StringComparer.Ordinal);
        return new(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
            new JsonObject(p.Value!["properties"]!.AsObject().Select(f => new KeyValuePair<string, JsonNode?>(f.Key, fields[f.Key].DeepClone()))))));
    }
    internal static (PlanningSnapshot State, List<PlanningDeclarationAssignment> Assignments) Captured()
    {
        var state = State(Classifier);
        // The seven declaration fragments and kinds below were retained from
        // session 3f11a9cf, revision 27. Requiredness is intentionally preliminary.
        Add(state, "Required input record is an object with required id:string, amount:number and approved:boolean.", "record");
        Add(state, "Optional input threshold is a non-nullable number defaulting to 100", "threshold");
        Add(state, "threshold is a non-nullable", "overlap");
        Add(state, "Create one reusable workflow classifying a single", "description", "business_output");
        Add(state, "Return classifiedResult:{id:string,amount:number,category:string}, all members", "result", "business_output");
        Add(state, "Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.", "classification", "business_output");
        Add(state, "Preserve the original id and amount.", "preservation", "business_output");
        Add(state, "defaulting to 100", "default", "default_value");
        Add(state, "classifying a single", "operation", "local_processing");
        state.Preparation!.Capabilities.Add(new() { Id = "local", StepType = "set", Resolution = "local", Required = true,
            Description = "Classify the original record under the declared rule.", OperationIds = ["operation"] });
        return (state, [Distinct(state, "record", "record"), Distinct(state, "threshold", "threshold", "optional", "100"),
            Link("overlap", "threshold", presence: "optional"), Retire("description"), Distinct(state, "result", "classifiedResult"),
            Link("classification", "result", "modifier_of"), Link("preservation", "result", "modifier_of"),
            Link("default", "threshold", "modifier_of", defaultReference: Token(state, "default", "100"))]);
    }
    private static void Commit(PlanningSnapshot state, List<PlanningDeclarationAssignment> assignments) => PlanningDeclarations.Commit(state, assignments, PlanningDeclarations.EvidenceFingerprint(state));

    [Fact]
    public async Task CapturedFragmentsProduceTwoNamedInputsOneOutputAndAnOmissionDefault()
    {
        var (state, assignments) = Captured();
        Assert.Equal(3, state.Obligations.Count(o => o.Kind == "business_input")); Assert.Equal(4, state.Obligations.Count(o => o.Kind == "business_output"));
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("intent_declarations", phase); Assert.Equal("low", request.Reasoning);
            var response = Response(request, assignments); Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
            return Task.FromResult(new LLMResponse { Json = response });
        } };
        await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        state.ObligationRelations = [new("overlap", "operation", "data"), new("record", "operation", "data")];
        var plan = PlanningBehaviorDecisions.Assemble(state, new());
        var workflow = Assert.Single(plan.Workflows);
        Assert.Equal(["record", "threshold"], workflow.Inputs.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(workflow.Inputs.Single(p => p.Name == "record").Required); Assert.False(workflow.Inputs.Single(p => p.Name == "threshold").Required);
        var output = Assert.Single(workflow.Outputs); Assert.Equal("classifiedResult", output.Name);
        Assert.Contains("Preserve the original id and amount.", output.Description); Assert.Contains("Classify as rejected", output.Description);
        Assert.Empty(PlanningDeclarations.ValidateBehavior(state, plan));
        Assert.Equal(["record", "threshold"], Assert.Single(workflow.Steps).InputDependencies!.Order(StringComparer.Ordinal));
        state.BehaviorPlan = plan;
        var accepted = await new TypedWorkflowPlanner().AdvanceAsync(Review(state), new() { Kind = "accept_behavior", ExpectedRevision = state.Revision, ArtifactHash = state.ArtifactHash }, runtime, Ct);
        Assert.NotNull(accepted.ApprovedBehaviorHash); Assert.NotNull(accepted.Graph); Assert.Null(accepted.TechnicalStop);
        var input = accepted.Graph.Workflows[0].Inputs.Single(p => p.Name == "threshold");
        Assert.Equal(100m, input.Default!.Number); Assert.False(input.Required);
        Assert.DoesNotContain(accepted.Construction.Holes, h => h.Path.EndsWith("/default", StringComparison.Ordinal));
        Assert.All(runtime.Phases, phase => Assert.Equal("intent_declarations", phase));
        var restored = PlanningContext.Clone(state); var events = restored.Events.Count; var calls = runtime.Requests.Count;
        await PlanningDeclarations.ResolveAsync(restored, runtime, Ct);
        Assert.Equal(calls, runtime.Requests.Count); Assert.Equal(events, restored.Events.Count); Assert.Equal(state.DeclarationFingerprint, restored.DeclarationFingerprint);
        Assert.NotEmpty(state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold").ModifierReferences);
    }

    private static PlanningSnapshot Review(PlanningSnapshot state)
    { state.Status = PlanningStatus.BehaviorReview; state.ArtifactHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan!); return state; }

    [Theory]
    [InlineData("left", "right")]
    [InlineData("alpha", "omega")]
    public void TwoDistinctSubjectsInOneClauseAreNotMerged(string first, string second)
    {
        var state = State($"Required inputs {first} and {second} are strings.");
        Add(state, first, "a"); Add(state, second, "b");
        Commit(state, [Distinct(state, "a", first), Distinct(state, "b", second)]);
        Assert.Equal(2, state.Declarations.Count); Assert.Single(PlanningDeclarations.Decisions(state));
    }

    [Fact]
    public async Task ModifierBeforeDeclarationAndCrossPageForwardAliasesCommitTogether()
    {
        var state = State("Default to 100 when omitted. Optional input limit is a number. Input limit is used in processing.");
        Add(state, "Default to 100 when omitted.", "a", "default_value"); Add(state, "Optional input limit is a number.", "z"); Add(state, "Input limit is used in processing.", "b");
        var assignments = new List<PlanningDeclarationAssignment> { Link("a", "b", "modifier_of", defaultReference: Token(state, "a", "100")), Link("b", "z"), Distinct(state, "z", "limit", "optional") };
        var pages = PlanningDeclarations.Decisions(state);
        // Three separately persisted pages, including forward links. They grant
        // no port authority until the entire assignment graph is validated.
        foreach (var page in pages)
        {
            var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse { Json = Response(request, assignments) }) };
            await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_declarations", "$plan", [page], Ct);
            Assert.Empty(state.Declarations);
        }
        Commit(state, assignments); var declaration = Assert.Single(state.Declarations);
        Assert.Equal("limit", PlanningDeclarations.Name(state, declaration)); Assert.Equal(100m, PlanningDeclarations.Default(state, declaration)!.Number);
        Assert.Equal("b", Assert.Single(declaration.Aliases));
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing")]
    [InlineData("name")]
    [InlineData("duplicate")]
    [InlineData("incomplete")]
    [InlineData("unresolved")]
    [InlineData("presence")]
    [InlineData("default")]
    public void InvalidOrAmbiguousAdjudicationCannotGrantPortAuthority(string defect)
    {
        var state = State("Optional input limit defaults to 100 or 200. Input limit is optional."); Add(state, "Optional input limit", "a"); Add(state, "Input limit is optional.", "b");
        var values = new List<PlanningDeclarationAssignment> { Distinct(state, "a", "limit", "optional", "100"), Link("b", "a") };
        if (defect == "cycle") values[0] = Link("a", "b");
        if (defect == "missing") values[1] = Link("b", "foreign");
        if (defect == "name") values[0] = values[0] with { NameReference = "invented" };
        if (defect == "duplicate") values.Add(values[0]);
        if (defect == "incomplete") values.RemoveAt(1);
        if (defect == "unresolved") values[0] = Retire("a") with { Disposition = "unresolved" };
        if (defect == "presence") values[1] = values[1] with { Presence = "required" };
        if (defect == "default") { Add(state, "200", "c", "default_value"); values.Add(Link("c", "a", "modifier_of", defaultReference: Token(state, "c", "200"))); }
        var error = Assert.Throws<WorkflowRuntimeException>(() => Commit(state, values));
        Assert.Equal("DECLARATION_GROUNDING_UNRESOLVED", error.Code); Assert.StartsWith("/declarations/", error.Details!["location"]!.ToString());
        Assert.Empty(state.Declarations); Assert.Null(state.DeclarationFingerprint); Assert.Null(state.Outcome); Assert.Null(state.Intent.Question);
    }

    [Fact]
    public void DefaultsDistinguishNoDefaultAndExplicitNull()
    {
        var state = State("Optional inputs first and second may be null, second defaults to null."); Add(state, "first", "a"); Add(state, "second", "b");
        Commit(state, [Distinct(state, "a", "first", "optional"), Distinct(state, "b", "second", "optional", "null")]);
        Assert.Null(PlanningDeclarations.Default(state, state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "first")));
        Assert.Equal("null", PlanningDeclarations.Default(state, state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "second"))!.Kind);
    }

    [Fact]
    public async Task BaselinePortsRetainContractsAndCannotBeInventedFromBaselineText()
    {
        var state = State("Preserve the saved public contract."); state.Request.Baseline = TypedPlannerTests.Graph();
        state.Request.Baseline.Workflows[0].Inputs.Add(new() { Name = "limit", Required = false, Default = new() { Kind = "number", Number = 100 }, Schema = new() { Type = "number" } });
        var runtime = new TypedPlannerTests.FakeRuntime(); await PlanningDeclarations.ResolveAsync(state, runtime, Ct);
        Assert.Empty(runtime.Requests); Assert.Equal(2, state.Declarations.Count);
        var declaration = state.Declarations.Single(d => d.Direction == "input"); Assert.Equal("limit", PlanningDeclarations.Name(state, declaration));
        Assert.Equal(100m, PlanningDeclarations.Default(state, declaration)!.Number);
        var stagedDefault = PlanningDeclarations.Default(state, declaration)!; stagedDefault.Number = 200;
        Assert.Equal(100m, state.Request.Baseline.Workflows[0].Inputs[0].Default!.Number);
        var restored = PlanningContext.Clone(state); PlanningDeclarations.RequireCurrent(restored);
        restored.Request.Baseline!.Workflows[0].Inputs[0].Name = "changed";
        Assert.Throws<WorkflowRuntimeException>(() => PlanningDeclarations.RequireCurrent(restored));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("required")]
    [InlineData("description")]
    [InlineData("proof")]
    public async Task RestoredAndPatchedBehaviorCannotBypassCanonicalDeclarations(string defect)
    {
        var (state, assignments) = Captured(); Commit(state, assignments);
        state.BehaviorPlan = PlanningBehaviorDecisions.Assemble(state, new());
        var workflow = state.BehaviorPlan.Workflows[0]; var port = workflow.Inputs[0];
        if (defect == "name") workflow.Inputs[0] = port with { Name = "invented" };
        if (defect == "missing") workflow.Inputs.RemoveAt(0);
        if (defect == "duplicate") workflow.Inputs.Add(port);
        if (defect == "required") workflow.Inputs[0] = port with { Required = !port.Required };
        if (defect == "description") workflow.Inputs[0] = port with { Description = "Lost governing evidence" };
        if (defect == "proof") state.DeclarationFingerprint = "stale";
        var result = await new TypedWorkflowPlanner().AdvanceAsync(Review(state), new() { Kind = "accept_behavior", ExpectedRevision = state.Revision, ArtifactHash = state.ArtifactHash }, new TypedPlannerTests.FakeRuntime(), Ct);
        Assert.Equal(PlanningStatus.Stopped, result.Status); Assert.Null(result.ApprovedBehaviorHash); Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, d => d.Code is "DECLARATION_GROUNDING_UNRESOLVED" or "BEHAVIOR_DECLARATION_MISMATCH");
    }

    [Fact]
    public async Task RelationshipsExposeCanonicalInputsOnly()
    {
        var (state, assignments) = Captured(); Commit(state, assignments);
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("intent_relations", phase);
            Assert.Equal(2, request.StructuredOutputSchema!["properties"]!.AsObject().Count);
            return Task.FromResult(new LLMResponse { Json = new JsonObject(request.StructuredOutputSchema["properties"]!.AsObject()
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("data")))) });
        } };
        await PlanningSourceDecisions.RelateAsync(state, runtime, Ct);
        Assert.Equal(2, state.ObligationRelations.Count);
        Assert.All(state.ObligationRelations, r => Assert.Contains(state.Declarations, d => d.Id == r.Producer));
        Assert.Single(runtime.Requests);
    }

    [Fact]
    public void ExecutableRepairCannotReplaceDeclaredDefaultWithNullOrAnotherValue()
    {
        var (state, assignments) = Captured(); Commit(state, assignments);
        state.BehaviorPlan = PlanningBehaviorDecisions.Assemble(state, new()); PlanningGraphSkeleton.Create(state);
        Assert.Empty(PlanningDeclarations.ValidateDefaults(state, state.Graph!));
        var threshold = state.Graph!.Workflows[0].Inputs.Single(p => p.Name == "threshold");
        foreach (var value in new PlanningValue?[] { null, new() { Kind = "null" }, new() { Kind = "number", Number = 200 } })
        { threshold.Default = value; Assert.Equal("DECLARATION_DEFAULT_CHANGED", Assert.Single(PlanningDeclarations.ValidateDefaults(state, state.Graph)).Code); }
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("source")]
    [InlineData("candidate")]
    [InlineData("proof")]
    public void StaleOrForeignCanonicalProofFailsClosed(string defect)
    {
        var (state, assignments) = Captured(); Commit(state, assignments); state = PlanningContext.Clone(state);
        if (defect == "tenant") state.Request.TenantId = "foreign";
        if (defect == "source") state.Request.Prompt += " Changed governing text.";
        if (defect == "candidate") state.DeclarationAssignments[0] = Retire(state.DeclarationAssignments[0].CandidateId);
        if (defect == "proof") state.Declarations[0] = state.Declarations[0] with { ProofFingerprint = "forged" };
        Assert.ThrowsAny<Exception>(() => PlanningDeclarations.RequireCurrent(state));
    }

    [Fact]
    public void ExistingSourceCanOnlyAliasAnIssuedBaselinePort()
    {
        var state = State("Preserve the existing result."); state.Request.Baseline = TypedPlannerTests.Graph();
        var source = PlanningIntentAssessment.IntentSources(state).Single(s => s.Id == "existing");
        var reference = PlanningReferences.Register(state, source.Id, source.Kind, source.Text)[0];
        var obligation = new PlanningObligation("existing", [reference.Id], "business_decision", "business_output", true);
        state.Obligations.Add(obligation with { Grounding = PlanningSourceGroundingRules.Create(state, obligation) });
        var baseline = PlanningDeclarations.Baselines(state).Single().Key;
        var schema = Assert.Single(PlanningDeclarations.Decisions(state)).Schema;
        var invalid = new JsonObject { ["existing"] = new JsonObject { ["disposition"] = "distinct", ["name"] = baseline, ["scope"] = "main", ["presence"] = "required", ["default"] = null } };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, schema));
        Commit(state, [Link("existing", baseline)]);
        Assert.Equal("message", PlanningDeclarations.Name(state, Assert.Single(state.Declarations)));
        Assert.Equal(baseline, state.Declarations[0].BaselineReference);
    }

    [Fact]
    public void QuotedPublicNamesAreDecodedExactlyWithoutRenaming()
    {
        var state = State("Required input \"customer record\" is supplied."); Add(state, "\"customer record\"", "name");
        Commit(state, [Distinct(state, "name", "\"customer record\"")]);
        Assert.Equal("customer record", PlanningDeclarations.Name(state, Assert.Single(state.Declarations)));
    }

    [Fact]
    public async Task HistoricalPortsWithoutAnyDeclarationEvidenceCannotBeAccepted()
    {
        var state = State("Retained historical intent."); state.BehaviorPlan = TypedPlannerTests.BehaviorPlan();
        var runtime = new TypedPlannerTests.FakeRuntime();
        var result = await new TypedWorkflowPlanner().AdvanceAsync(Review(state), new() { Kind = "accept_behavior", ExpectedRevision = state.Revision, ArtifactHash = state.ArtifactHash }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, result.Status); Assert.Null(result.ApprovedBehaviorHash); Assert.Null(result.Graph); Assert.Empty(runtime.Requests);
        Assert.Contains(result.Diagnostics, d => d.Rule == "missing_declaration_proof");
    }

    [Fact]
    public void ResponseSchemasExcludeCopiedTextAndUnsupportedNames()
    {
        var (state, assignments) = Captured();
        foreach (var page in PlanningDeclarations.Decisions(state))
        {
            var response = new JsonObject(page.Schema["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, PlanningDeclarations.Assignment(assignments.Single(a => a.CandidateId == p.Key)))));
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, page.Schema));
            var field = response.First().Value!.AsObject(); field["text"] = "Copied clause";
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, page.Schema));
        }
    }
}
