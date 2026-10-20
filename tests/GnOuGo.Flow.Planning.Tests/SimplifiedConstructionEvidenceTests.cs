using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SimplifiedConstructionEvidenceTests
{
    [Fact]
    public async Task FreshBatchExpandsTheIssuedGrammarThenAcceptsOneReviewedDraft()
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var source = Context(request)["sources"]![0]!;
            return Wire(request, new JsonObject { ["sourceId"] = source["id"]!.DeepClone(),
                ["clauses"] = new JsonArray(new JsonObject { ["text"] = source["text"]!.DeepClone(),
                    ["kind"] = "requirement", ["interfaces"] = "none" }) });
        } };
        var state = await Advance(new() { Request = new() { TenantId = "batch-flow", Prompt = "Read the exact authorized record." } }, runtime);
        Assert.Empty(state.Diagnostics);
        var fixture = State();
        state.Catalog = fixture.Catalog;
        state.Discovery = fixture.Discovery;
        var step = fixture.Construction!.Compact!.CurrentStep!;
        step.RequirementIds = ["req_0"]; step.DiscoveryComplete = true;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction!.Compact!.CurrentStep = step;
        var accepted = PlanningConstruction.Hash(state.Plan);
        var calls = state.ModelCalls; var drafts = 0; var judges = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose == PlanningConstruction.LocalPurpose)
            {
                judges++;
                return Wire(request, new() { ["findings"] = new JsonArray(), ["uncertainty"] = "Execution remains unverified.", ["evidenceRequests"] = null });
            }
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            var context = Context(request);
            drafts++;
            if (drafts == 1)
                return Wire(request, new() { ["baseHash"] = context["baseHash"]!.DeepClone(),
                    ["evidenceRequests"] = new JsonArray(Address("first"), Address("second")) });
            Assert.Equal(2, context["evidence"]!.AsObject().Count);
            Assert.Equal(3, context["operations"]![0]!["editableInputs"]!.AsArray().Count);
            var extension = new PlanningConstructionExtension
            {
                RequirementIds = ["req_0"],
                Tasks = [new() { Id = "read_exact", Kind = "operation", Operation = "read", Objective = "Read the exact authorized record.",
                    Inputs = [new("required", new() { Kind = "string", Text = "A" }),
                        new("first", new() { Kind = "string", Text = "B" }), new("second", new() { Kind = "string", Text = "x" })] }]
            };
            return Wire(request, new() { ["baseHash"] = context["baseHash"]!.DeepClone(),
                ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension) });
        };
        state = await Advance(state, runtime);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(2, state.Construction!.Evidence!.Count);
        Assert.Equal(0, state.ReplanAttempts);
        for (var index = 0; index < 3 && state.Construction!.Checkpoints.Count == 0; index++)
            state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(2, drafts);
        Assert.Equal(1, judges);
        Assert.Equal(calls + 3, state.ModelCalls);
        Assert.Equal(3, Assert.Single(state.Plan!.Root.Tasks).Inputs.Count);
    }

    [Fact]
    public void CoherentBatchPreservesExactConstraintsAndAdmitsTheCompleteCumulativeRequest()
    {
        var state = State();
        var contract = JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var calls = 0;
        PlanningConstruction.LoadEvidence(state, [Address("first"), Address("second")], followup: probe =>
        {
            calls++;
            Assert.Equal(2, probe.Construction!.Compact!.CurrentStep!.SchemaPointers.Count);
            Assert.Empty(state.Construction!.Compact!.CurrentStep!.SchemaPointers);
            var view = Assert.Single(PlanningCompactConstruction.ContractViews(probe))!;
            Assert.Equal("^[A-Z]+$", view["inputSchema"]!["properties"]!["required"]!["pattern"]!.ToString());
            Assert.Equal(17, view["inputSchema"]!["properties"]!["first"]!["maxLength"]!.GetValue<int>());
            Assert.Equal("x", view["inputSchema"]!["properties"]!["second"]!["enum"]![0]!.ToString());
            return Followup(probe);
        }, purpose: PlanningConstruction.DraftPurpose);
        Assert.Equal(1, calls);
        Assert.Equal(2, state.Construction!.Evidence!.Count);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(contract, JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability));
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        var request = Followup(state);
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningConstruction.DraftPurpose, 16000).EstimatedInputTokens, 1, 16000);
    }

    [Fact]
    public void OversizedFollowupRejectsEverySelectionCursorAndFingerprintAtomically()
    {
        var state = State();
        state.Discovery.Resolved[0].InputSchema["properties"]!["second"]!["description"] = new string('x', 80000);
        var before = Snapshot(state);
        var exception = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state,
            [Address("first"), Address("second")], followup: Followup, purpose: PlanningConstruction.DraftPurpose));
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_LIMIT");
        Assert.Equal(before, Snapshot(state));
        var rejected = Assert.Single(state.RequestMeasurements!);
        Assert.Equal("rejected_before_dispatch", rejected.Admission);
        Assert.True(rejected.EstimatedInputTokens > rejected.InputLimit);
        PlanningConstruction.LoadEvidence(state, [Address("first")], followup: Followup, purpose: PlanningConstruction.DraftPurpose);
        Assert.Single(state.Construction!.Compact!.CurrentStep!.SchemaPointers);
        Assert.DoesNotContain(Address("second"), state.Construction.Evidence!.Select(item => item.Key));
    }

    [Fact]
    public void SmallerNavigationPreviewClearsOnlyResolvedEvidenceDiagnostics()
    {
        var state = State();
        state.Diagnostics = [new("CONSTRUCTION_EVIDENCE_LIMIT", "/evidenceRequests", "Previous batch was too large."),
            new("CONSTRUCTION_STEP_INVALID", "/currentStep", "Retain this independent finding.")];
        PlanningConstruction.LoadEvidence(state, [Address("first")], followup: probe =>
        {
            Assert.DoesNotContain(probe.Diagnostics, finding => finding.Code == "CONSTRUCTION_EVIDENCE_LIMIT");
            Assert.Contains(probe.Diagnostics, finding => finding.Code == "CONSTRUCTION_STEP_INVALID");
            var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(probe),
                PlanningCompactConstruction.DiscoveryContext(probe), PlanningCompactConstruction.DiscoverySchema(probe),
                PlanningCompactConstruction.DiscoveryPurpose);
            return PlanningModelCalls.PrepareRequest(probe, PlanningCompactConstruction.DiscoveryPurpose,
                request.Prompt, request.StructuredOutputSchema!.AsObject(), request.MaxTokens);
        }, purpose: PlanningCompactConstruction.DiscoveryPurpose);
        // The coordinator removes the resolved finding only after this transaction succeeds.
        Assert.Equal(2, state.Diagnostics.Count);
        Assert.Contains(state.Diagnostics, finding => finding.Code == "CONSTRUCTION_STEP_INVALID");
        Assert.Equal("prepared", Assert.Single(state.RequestMeasurements!).Admission);
    }

    [Fact]
    public void FirstDescriptionInABatchRetainsItsExactIssuedKnowledgeIdentity()
    {
        var state = State();
        const string description = "description:read@v1:0";
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        var expected = PlanningCompactConstruction.DescriptionReadKey(state, schema,
            new JsonObject { ["evidenceRequests"] = new JsonArray(description) });
        Assert.NotNull(expected);
        PlanningConstruction.LoadEvidence(state, [description, Address("first")], followup: Followup,
            purpose: PlanningConstruction.DraftPurpose, issuedDiscoverySchema: schema);
        Assert.Contains(expected, state.Construction!.EvidenceRequests);
        var restored = PlannerFixture.Clone(state);
        Assert.Contains(expected, restored.Construction!.EvidenceRequests);
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(restored,
            [description], expected, Followup, PlanningConstruction.DraftPurpose));
    }

    [Fact]
    public void UnknownSecondAddressDoesNotRetainTheFirstSuccessfulView()
    {
        var state = State();
        var before = Snapshot(state);
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state,
            [Address("first"), "schema:read@stale:/input/properties/second"], followup: Followup, purpose: PlanningConstruction.DraftPurpose));
        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void SingletonFingerprintsSurviveRecoveryAndPreventRegrouping()
    {
        var state = State();
        PlanningConstruction.LoadEvidence(state, [Address("first")], followup: Followup, purpose: PlanningConstruction.DraftPurpose);
        state = PlannerFixture.Clone(state);
        PlanningConstruction.LoadEvidence(state, [Address("second"), Address("first")], followup: Followup, purpose: PlanningConstruction.DraftPurpose);
        var before = Snapshot(state);
        var exception = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state,
            [Address("first"), Address("second")], followup: Followup, purpose: PlanningConstruction.DraftPurpose));
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(before, Snapshot(state));
        foreach (var address in new[] { Address("first"), Address("second") })
            Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [address]), state.Construction!.EvidenceRequests);
    }

    [Fact]
    public void MixingANewViewCannotBypassTheExactDescriptionRereadGuard()
    {
        var state = State();
        const string description = "description:read@v1:0";
        PlanningConstruction.LoadEvidence(state, [description], followup: Followup, purpose: PlanningConstruction.DraftPurpose);
        var before = Snapshot(state);
        var exception = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state,
            [description, Address("first")], followup: Followup, purpose: PlanningConstruction.DraftPurpose));
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void HistoricalBatchSemanticsAndAdmissionRemainUnchanged()
    {
        var state = State();
        state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        PlanningConstruction.LoadEvidence(state, [Address("first")]);
        PlanningConstruction.LoadEvidence(state, [Address("second")]);
        PlanningConstruction.LoadEvidence(state, [Address("first"), Address("second")],
            followup: _ => throw new InvalidOperationException("Historical evidence does not acquire fresh admission."), purpose: PlanningConstruction.DraftPurpose);
        Assert.Equal(2, state.Construction!.Evidence!.Count);
        var schema = PlanningCompactConstruction.DraftSchema(state);
        Assert.Equal(1, schema["properties"]!["evidenceRequests"]!["anyOf"]![0]!["maxItems"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("value")]
    [InlineData("transform")]
    [InlineData("sequence")]
    [InlineData("conditional")]
    [InlineData("parallel")]
    [InlineData("foreach")]
    [InlineData("call")]
    [InlineData("group")]
    [InlineData("choice")]
    [InlineData("close")]
    public void SelectedKindUsesTheSameSemanticShapesWithoutConstructingOtherTaskAlternatives(string kind)
    {
        var state = State();
        var shared = PlanningSchemas.ConstructionDefinitions(state, kind);
        var historical = PlanningSchemas.FullProposal(state, compact: false, admitted: new HashSet<string>(), scopeGuidance: false)["$defs"]!;
        var expected = historical["task"]!["anyOf"]!.AsArray().Where(task =>
            task!["properties"]!["kind"]!["enum"]![0]!.ToString() == kind &&
            (kind != "transform" || task["properties"]!["mode"] is not null)).ToArray();
        var actual = shared["task"]!["anyOf"]!.AsArray();
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++) Assert.True(JsonNode.DeepEquals(expected[i], actual[i]));
        Assert.Null(shared["plan"]);
        Assert.Null(shared["requirements"]);
        state.Construction!.Compact!.CurrentStep!.Kind = kind;
        var schema = PlanningCompactConstruction.DraftSchema(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Equal(8, schema["properties"]!["evidenceRequests"]!["anyOf"]![0]!["maxItems"]!.GetValue<int>());
        Assert.DoesNotContain(Walk(schema), node => node.ContainsKey("$ref") && node.Count != 1);
    }

    [Fact]
    public void RequiredReferencesAndCoupledConstraintsAreIndivisibleAcrossBatchedViews()
    {
        var state = State();
        var original = state.Discovery.Resolved[0].InputSchema;
        original["$defs"] = new JsonObject { ["code"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Z]+$" } };
        original["properties"]!["required"] = new JsonObject { ["$ref"] = "#/$defs/code" };
        original["allOf"] = new JsonArray(new JsonObject { ["required"] = new JsonArray("first", "second") });
        var retained = original.DeepClone();
        PlanningConstruction.LoadEvidence(state, [Address("first"), Address("second")], followup: Followup, purpose: PlanningConstruction.DraftPurpose);
        var input = Assert.Single(PlanningCompactConstruction.ContractViews(state))!["inputSchema"];
        Assert.True(JsonNode.DeepEquals(retained, input));
        Assert.True(JsonNode.DeepEquals(retained, original));
    }

    [Fact]
    public void FreshSchemaOffersRollbackOnlyForAcceptedTasksOutsideCorrection()
    {
        var state = State();
        Assert.False(AllowsRollback());
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "accepted", Kind = "value",
            Outputs = [new("value", new() { Kind = "string", Text = "unchanged" })] }] } };
        Assert.True(AllowsRollback());
        state.Phase = PlanningPhase.Replanning;
        Assert.False(AllowsRollback());
        state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        Assert.True(AllowsRollback());

        bool AllowsRollback()
        {
            var source = PlanningCompactConstruction.DraftSchema(state);
            var schema = PlanningSchemas.Object(("rollback", source["properties"]!["rollbackTask"]!.DeepClone().AsObject()));
            schema["$defs"] = source["$defs"]!.DeepClone();
            return PlanningContractValidation.ValidateInstance(new JsonObject { ["rollback"] = "accepted" }, schema).Count == 0;
        }
    }

    private static LLMRequest Followup(PlanningSession state)
    {
        var presentation = PlanningConstruction.DraftRequest(state, repair: false);
        return PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose, presentation.Prompt,
            presentation.StructuredOutputSchema!.AsObject(), presentation.MaxTokens);
    }

    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response)
    {
        var transported = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject());
        Assert.NotNull(transported);
        return new() { Json = transported, Text = transported.ToJsonString() };
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);

    private static string Address(string name) => "schema:read@v1:/input/properties/" + name;
    private static string Snapshot(PlanningSession state)
    {
        var node = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!.AsObject();
        node.Remove("requestMeasurements"); // Safe rejected/prepared metadata is deliberately retained.
        return node.ToJsonString();
    }

    private static IEnumerable<JsonObject> Walk(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var (_, child) in obj) foreach (var nested in Walk(child)) yield return nested;
        }
        else if (node is JsonArray array) foreach (var child in array) foreach (var nested in Walk(child)) yield return nested;
    }

    private static PlanningSession State()
    {
        var state = new PlanningSession
        {
            IntentVersion = 3,
            Request = new() { TenantId = "batch", SessionId = "batch", Prompt = "Read exact supplied values.", Generation = new("medium", 32000, 8192) },
            Requirements = new() { Summary = "Read exact supplied values.", Inputs = [], Outputs = [], Outcomes = [new("read", "Read exact supplied values.")] },
            Catalog = new() { AllowedStepTypes = ["mcp.call"] },
            Construction = new() { Version = 2, Compact = new() { PresentationVersion = 2,
                CurrentStep = new() { Id = "step", Kind = "operation", Scope = "/root", OperationIds = ["read"],
                    RequirementIds = ["read"], Objective = "Read exact supplied values." } } }
        };
        state.Request.Options["construction_version"] = 2;
        state.Request.Options["construction_limits"] = new JsonObject
        {
            ["phasePolicy"] = JsonSerializer.SerializeToNode(new PlanningConstructionPhasePolicy(), PlanningJsonContext.Default.PlanningConstructionPhasePolicy)
        };
        state.Construction.Compact.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Requirements.Add(new() { Id = "read" });
        state.Discovery.Resolved.Add(new()
        {
            Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = "read",
            Description = "Read the exact authorized record.", EffectKind = "read",
            InputSchema = JsonNode.Parse("""
                {"type":"object","properties":{"required":{"type":"string","pattern":"^[A-Z]+$"},
                "first":{"type":"string","maxLength":17},"second":{"type":"string","enum":["x","y"]}},
                "required":["required"],"additionalProperties":false}
                """)!.AsObject(),
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
        });
        return state;
    }
}
