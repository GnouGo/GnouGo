using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactReviewContextTests
{
    [Fact]
    public async Task EvidenceFollowupKeepsExactEvidenceAndConstraintsWithoutDuplicatingItsGroup()
    {
        var state = await State();
        var originalPlan = PlanningConstruction.Hash(state.Plan);
        var first = PlanningCompactConstruction.Context(state, review: true);
        var originalGroup = first["scopeGroup"]!.DeepClone();
        var originalStep = JsonSerializer.SerializeToNode(state.Construction!.Compact!.CurrentStep,
            PlanningJsonContext.Default.PlanningConstructionStep)!;
        var runtime = new TestRuntime { Respond = (_, _) => Review(new JsonArray("task:producer", "scope:/groups/reusable/body")) };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var evidence = state.Construction!.Evidence!.ToJsonString();
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            Assert.Equal(evidence, context["evidence"]!.ToJsonString());
            Assert.Null(context["scopeGroup"]);
            Assert.True(JsonNode.DeepEquals(originalGroup, context["evidence"]!["scope:/groups/reusable/body"]!["group"]));
            foreach (var field in originalStep.AsObject().Where(p => p.Key is not ("baseHash" or "discoveryComplete" or "inputs" or "inputTaskIds" or "dependencyTaskIds")))
                Assert.True(JsonNode.DeepEquals(field.Value, context["step"]![field.Key]), field.Key);
            var imported = context["interfaces"]![0]!;
            Assert.Equal(1, imported["schema"]!["minItems"]!.GetValue<int>());
            Assert.Equal(8, imported["schema"]!["maxItems"]!.GetValue<int>());
            Assert.Equal(JsonSerializer.Serialize(state.Construction.Compact!.CurrentStep!.Inputs[0], PlanningJsonContext.Default.TaskValue),
                JsonSerializer.Serialize(imported["binding"]!.Deserialize(PlanningJsonContext.Default.TaskValue), PlanningJsonContext.Default.TaskValue));
            Assert.Equal(first["requirements"]!.ToJsonString(), context["requirements"]!.ToJsonString());
            Assert.Equal(first["globalConstraints"]!.ToJsonString(), context["globalConstraints"]!.ToJsonString());
            Assert.Equal(first["draft"]!.ToJsonString(), context["draft"]!.ToJsonString());
            Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 4000).EstimatedInputTokens, 1, 4000);
            return Review();
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.DoesNotContain(state.Diagnostics, d => d.Required);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(originalPlan, state.Construction!.Checkpoints[^1].BaseHash);
    }

    [Fact]
    public async Task DifferentOrPartialGroupEvidenceDoesNotSuppressTheCompleteSelectedGroup()
    {
        var state = await State();
        PlanningConstruction.LoadEvidence(state, ["scope:/groups/reusable/body"]);
        var original = state.Construction!.Evidence!.DeepClone().AsObject();
        foreach (var change in new[] { "hash", "inputs", "missing" })
        {
            state.Construction.Evidence = original.DeepClone().AsObject();
            var group = state.Construction.Evidence["scope:/groups/reusable/body"]!["group"]!.AsObject();
            if (change == "missing") group.Remove("hash");
            else group[change] = change == "hash" ? JsonValue.Create("different") : new JsonArray();
            var context = PlanningCompactConstruction.Context(state, review: true);
            Assert.NotNull(context["scopeGroup"]);
            Assert.Equal(state.Construction.Evidence.ToJsonString(), context["evidence"]!.ToJsonString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosureJudgeKeepsScopeSemanticsWithoutDraftActions(bool intermediate)
    {
        var state = await State();
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate);
        var guidance = request.Prompt[..request.Prompt.LastIndexOf('\n')];
        Assert.Equal(new[] { "evidenceRequests", "findings", "uncertainty" },
            request.StructuredOutputSchema!["properties"]!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("reselectStep", guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("Add needed final exports", guidance, StringComparison.Ordinal);
        Assert.Contains("complete children and compatible exports", guidance, StringComparison.Ordinal);
        Assert.Contains("Only a closed group is callable", guidance, StringComparison.Ordinal);
        Assert.True(context["constructionStage"]!["closedScopeCount"]!.GetValue<int>() > 0);
        Assert.False(context["constructionStage"]!["requirementCompletionClaimed"]!.GetValue<bool>());
        Assert.False(context["constructionStage"]!["planCompletionClaimed"]!.GetValue<bool>());
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Review().Json!, request.StructuredOutputSchema));
        var unauthorized = Review().Json!.DeepClone().AsObject();
        unauthorized["reselectStep"] = "Withdraw the claim.";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(unauthorized, request.StructuredOutputSchema));
        var draft = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Contains("Withdraw premature selected closeRequirementIds via reselectStep", draft.StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
        Assert.Equal(PlanningCompactConstruction.DraftSemantics(state, state.Construction!.Compact!.CurrentStep!),
            draft.StructuredOutputSchema["description"]!.ToString());
    }

    [Theory]
    [InlineData(true, false, 3)]
    [InlineData(true, true, 3)]
    [InlineData(false, true, 3)]
    [InlineData(true, false, 4)]
    [InlineData(true, true, 4)]
    [InlineData(false, true, 4)]
    public async Task JudgeDistinguishesPreservedPublicAndNestedExports(bool root, bool closing, int presentation)
    {
        var state = await State(presentation);
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        var group = state.Plan!.Groups[0];
        group.Body.Outputs = [new("sequence", new() { Kind = "output", Source = "producer", Port = "sequence" })];
        if (root)
        {
            state.Plan = new() { Inputs = group.Inputs, Root = group.Body };
            state.Requirements!.Inputs = group.Inputs;
            state.Requirements.Outputs = [new() { Name = "sequence", Type = group.Inputs[0].Type }];
            construction.OpenScopes = ["/root"];
            step.Scope = "/root"; step.OutputNames = ["sequence"];
        }
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        step.Kind = closing ? "close" : "value";
        construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = step.Scope, RequirementIds = ["result"],
            CloseScopes = closing ? [step.Scope] : [], Complete = root && closing,
            Tasks = closing ? [] : [new() { Id = "consumer", Kind = "value", Objective = "Use the available sequence.",
                Outputs = [new("copy", new() { Kind = "output", Source = "producer", Port = "sequence" })] }]
        });
        var accepted = PlanningConstruction.Hash(state.Plan);
        foreach (var intermediate in new[] { false, true })
        {
            var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate);
            Assert.Empty(context["draft"]!["outputs"]!.AsArray());
            var view = context["scopeInterface"]!;
            Assert.Equal(step.Scope, view["scope"]!.ToString());
            Assert.Equal(closing ? "tentative_candidate" : "accepted_declarations", view["basis"]!.ToString());
            Assert.Equal(1, view["outputCount"]!.GetValue<int>());
            Assert.Equal("sequence", Assert.Single(view["outputs"]!.AsArray())!["name"]!.ToString());
            Assert.Equal("producer", view["outputs"]![0]!["value"]!["source"]!.ToString());
            if (root) Assert.Equal("sequence", Assert.Single(context["outputs"]!.AsArray())!["name"]!.ToString());
            else Assert.Null(context["outputs"]);
            var guidance = request.Prompt[..request.Prompt.LastIndexOf('\n')];
            if (presentation == 3)
            {
                Assert.Contains("Exports at /root are public; nested-scope exports are local", guidance, StringComparison.Ordinal);
                Assert.Contains("basis:tentative_candidate also includes draft additions", guidance, StringComparison.Ordinal);
                Assert.Contains("draft.outputs lists additions only; [] preserves accepted exports", guidance, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Root outputs are public; nested exports are local", guidance, StringComparison.Ordinal);
                Assert.Contains("empty draft.outputs preserves accepted exports", guidance, StringComparison.Ordinal);
            }
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Review(presentation: presentation).Json!, request.StructuredOutputSchema!));
        }
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(construction.Checkpoints);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public async Task MissingOrIncorrectCandidateExportsRemainSubjectToGroundedReview(bool missing, int presentation)
    {
        var state = await State(presentation);
        state.Construction!.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/groups/reusable/body", RequirementIds = ["result"], CloseScopes = ["/groups/reusable/body"],
            Outputs = missing ? [] : [new("sequence", new() { Kind = "array", Items = [new() { Kind = "string", Text = "second" }] })]
        });
        var accepted = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            Assert.Equal(missing ? 0 : 1, context["scopeInterface"]!["outputCount"]!.GetValue<int>());
            var response = Review(presentation: presentation);
            response.Json!["findings"] = new JsonArray(new JsonObject
            {
                ["kind"] = missing ? "omission" : "inconsistency", ["location"] = "/requirements/0",
                ["evidence"] = new JsonArray("/requirements/0", "/scopeInterface"),
                ["explanation"] = missing ? "The closed boundary has no required sequence export." : "The exported constant does not preserve the supplied sequence.",
                ["uncertainty"] = "Execution remains unverified."
            });
            response.Text = response.Json.ToJsonString();
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response.Json, request.StructuredOutputSchema!));
            return response;
        } };
        state = await Advance(state, runtime);
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.False(state.Construction.Candidate!.LocalReviewed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalReviewAndGenerationKeepTheirOriginalContextRepresentation(bool version2WithPhasePolicy)
    {
        var state = await State();
        PlanningConstruction.LoadEvidence(state, ["scope:/groups/reusable/body"]);
        var generation = PlanningCompactConstruction.Context(state);
        Assert.NotNull(generation["scopeGroup"]);
        Assert.NotNull(generation["step"]!["baseHash"]);
        Assert.NotNull(generation["step"]!["discoveryComplete"]);
        if (version2WithPhasePolicy)
        {
            state.Construction!.Compact!.PresentationVersion = 2;
            state.Request.Options["construction_presentation_version"] = 2;
        }
        else
        {
            state.Request.Options.Remove("construction_presentation_version");
            state.Construction!.Compact!.PresentationVersion = null;
            state.Construction.Compact.InterfaceExtractions = null;
            state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
            state.Construction.Limits.PhasePolicy = null;
        }
        var retained = PlanningCompactConstruction.Context(state, review: true);
        if (!version2WithPhasePolicy)
        {
            Assert.Equal(JsonSerializer.SerializeToNode(state.Construction!.Compact!.CurrentStep,
                PlanningJsonContext.Default.PlanningConstructionStep)!.ToJsonString(), retained["step"]!.ToJsonString());
            Assert.NotNull(retained["scopeGroup"]);
            Assert.Equal(PlanningCompactConstruction.ImportedInterfaces(state, state.Construction.Compact.CurrentStep!).ToJsonString(),
                retained["interfaces"]!.ToJsonString());
        }
        var runtime = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.Status == PlanningStatus.Generating && s.PendingCall is not null));
        var exact = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var contextHash = saved.Construction!.PendingReviewHash;
        var calls = saved.ModelCalls;
        if (version2WithPhasePolicy)
        {
            Assert.Contains("outputs are public /root results; scopeInterface includes preserved local exports.", saved.PendingCall.Request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("draft.outputs lists additions only", saved.PendingCall.Request.Prompt, StringComparison.Ordinal);
            Assert.Contains("Withdraw premature selected closeRequirementIds via reselectStep", saved.PendingCall.Request.Prompt, StringComparison.Ordinal);
            saved.Construction.Compact!.PresentationVersion = 3;
            saved.Request.Options["construction_presentation_version"] = 3;
            Assert.DoesNotContain("reselectStep", HybridWorkflowPlanner.CreateConstructionReviewRequest(saved, intermediate: false).Request.Prompt, StringComparison.Ordinal);
        }
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(contextHash, saved.Construction.PendingReviewHash);
            return Review();
        } };
        var resumed = await Advance(saved, recovery);
        Assert.Equal(calls, resumed.ModelCalls);
        Assert.Single(recovery.Calls);
        Assert.DoesNotContain(resumed.Diagnostics, d => d.Required);
    }

    private static async Task<PlanningSession> State(int presentation = 3)
    {
        const string requirement = "Expose the complete supplied sequence through the reusable boundary, preserving order and repetitions.";
        var state = new PlanningSession { Request = new() { TenantId = "compact-review-context", Prompt = requirement } };
        PlanningSessionCreation.Capture(state);
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        var construction = state.Construction!;
        var compact = construction.Compact!;
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var source in compact.Sources) source.Indexed = true;
        compact.RequirementSources["result"] = [compact.Sources[0].Id];
        state.Requirements = new() { Outcomes = [new("result", requirement)], Inputs = [], Outputs = [] };
        construction.Requirements = [new() { Id = "result" }];
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Groups = [new() { Id = "reusable",
            Inputs = [new() { Name = "sequence", Type = new() { Kind = "array", MinItems = 1, MaxItems = 8,
                Items = new() { Kind = "string", Nullable = true, Enum = ["first", "second"] } } }],
            Body = new() { Tasks = [new() { Id = "producer", Kind = "value", Objective = "Preserve each supplied value unchanged.",
                Outputs = [new("sequence", new() { Kind = "input", Source = "sequence" })] }] } }] };
        construction.OpenScopes = ["/root", "/groups/reusable/body"];
        compact.CurrentStep = new() { Id = "close_boundary", Scope = "/groups/reusable/body", Kind = "close",
            BaseHash = PlanningConstruction.Hash(state.Plan), Objective = "Expose the exact sequence and close this reusable boundary.",
            RequirementIds = ["result"], InputTaskIds = ["producer"], Inputs = [new() { Kind = "output", Source = "producer", Port = "sequence" }],
            ExpectedOutputs = ["The complete sequence with original order, repetitions and explicit nulls."], DiscoveryComplete = true };
        construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/groups/reusable/body",
            Outputs = [new("sequence", new() { Kind = "output", Source = "producer", Port = "sequence" })],
            RequirementIds = ["result"], CloseScopes = ["/groups/reusable/body"] });
        return state;
    }

    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Review(JsonArray? evidence = null, int presentation = 3)
    {
        var response = new JsonObject { ["findings"] = evidence is null ? new JsonArray() : null,
            ["evidenceRequests"] = evidence, ["uncertainty"] = "Execution and business outcomes remain unverified." };
        if (presentation == 4) response["inconclusive"] = null;
        return new() { Json = response, Text = response.ToJsonString() };
    }
}
