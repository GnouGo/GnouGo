using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactRequirementPageTests
{
    [Fact]
    public async Task InitialSelectionShowsAdjacentOriginalConstraintsBeforeChoosingAContainer()
    {
        string[] clauses = ["Accept the original observations. ", "Transform each observation in a reusable group. ",
            "Run the per-observation calls concurrently and preserve their order."];
        var (state, runtime) = await Ready(clauses);
        state.Construction!.Requirements[0].Status = "closed";
        var originals = Originals(state);
        var calls = state.ModelCalls;
        runtime.Respond = (request, _) =>
        {
            var context = Context(request);
            Assert.Equal(["req_0", "req_1", "req_2"], Ids(context["requirements"]!));
            Assert.Equal(clauses, context["requirements"]!.AsArray().Select(requirement => requirement!["original"]!.ToString()));
            Assert.All(context["requirements"]!.AsArray(), requirement => Assert.NotEmpty(requirement!["sources"]!.AsArray()));
            Assert.Null(context["nextRequirements"]);
            var selection = Selection(state, "req_1", "req_2");
            selection["step"]!["kind"] = "foreach";
            Assert.Empty(PlanningContractValidation.ValidateInstance(selection, request.StructuredOutputSchema!));
            var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens;
            Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, limit).EstimatedInputTokens, 1, limit);
            return new() { Json = selection };
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(["req_1", "req_2"], state.Construction!.Compact!.CurrentStep!.RequirementIds);
        var draft = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Equal(["req_0", "req_1", "req_2"], Ids(Context(draft)["requirements"]!));
        Assert.Equal(["closed", "remaining", "remaining"], state.Construction.Requirements.Select(requirement => requirement.Status));
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(originals, Originals(state));
        Assert.Equal(0, state.ReplanAttempts);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparatePagesRemainTogetherUntilStepSelectionIncludingExactRecovery(bool interrupted)
    {
        var clauses = Enumerable.Range(0, 9).Select(index => $"Preserve original value {index}. ").ToArray();
        var (state, runtime) = await Ready(clauses);
        var originals = Originals(state);
        var baseline = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, _) => new() { Json = Evidence(state, "page:requirements:0") };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var first = state.Construction!.Evidence!["page:requirements:0"]!.ToJsonString();
        var next = state.Construction.Evidence["page:requirements:0"]!["next"]!.ToString();
        var calls = state.ModelCalls;
        string? issued = null;
        if (interrupted)
        {
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
            runtime.Respond = (_, _) => { interruption.Cancel(); throw new OperationCanceledException(interruption.Token); };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
                new() { ExpectedRevision = state.Revision }, runtime, interruption.Token));
            state = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
            issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
            Assert.Equal(calls + 1, state.ModelCalls);
        }
        runtime.Respond = (request, _) =>
        {
            if (issued is not null) Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = Evidence(state, next) };
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(first, state.Construction!.Evidence!["page:requirements:0"]!.ToJsonString());
        Assert.Equal(2, state.Construction.Evidence.Count);
        var allIds = state.Requirements!.Outcomes.Select(requirement => requirement.Id).ToArray();
        var ids = new[] { "req_1", "req_8" };
        var beforeRepeat = Snapshot(state);
        var repeated = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [next],
            followup: Followup, purpose: PlanningCompactConstruction.SelectPurpose));
        Assert.Contains(repeated.Diagnostics, finding => finding.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(beforeRepeat, Snapshot(state));
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(2, Context(request)["evidence"]!.AsObject().Count);
            Assert.All(allIds, id => Assert.Empty(PlanningContractValidation.ValidateInstance(Selection(state, id), request.StructuredOutputSchema!)));
            Assert.Empty(PlanningContractValidation.ValidateInstance(Selection(state, ids), request.StructuredOutputSchema!));
            Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, 6000).EstimatedInputTokens, 1, 6000);
            return new() { Json = Selection(state, ids) };
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(ids, state.Construction!.Compact!.CurrentStep!.RequirementIds);
        Assert.Null(state.Construction.Evidence);
        Assert.Empty(state.Construction.EvidenceRequests);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(originals, Originals(state));
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
    }

    [Fact]
    public async Task OneEvidenceReadKeepsSiblingObligationsSelectableTogether()
    {
        var (state, runtime) = await Ready();
        var originals = Originals(state);
        var calls = state.ModelCalls;
        runtime.Respond = (request, _) => new() { Json = Evidence(state, "page:requirements:0") };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var page = state.Construction!.Evidence!["page:requirements:0"]!;
        Assert.Equal(["req_0", "req_1", "req_2"], Ids(page["items"]!));
        Assert.Null(page["next"]);
        Assert.Equal(2, state.Construction.Compact!.RequirementOffset);
        var evidence = page.ToJsonString();
        state = PlannerFixture.Clone(state);
        Assert.Equal(evidence, state.Construction!.Evidence!["page:requirements:0"]!.ToJsonString());
        runtime.Respond = (request, _) =>
        {
            var context = Context(request);
            Assert.Equal(["req_0", "req_1", "req_2"], Ids(context["requirements"]!));
            Assert.Equal(["req_0", "req_1", "req_2"], Ids(context["evidence"]!["page:requirements:0"]!["items"]!));
            Assert.Null(context["nextRequirements"]);
            var response = Selection(state, "req_0", "req_1", "req_2");
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
            var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens;
            Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, limit).EstimatedInputTokens, 1, limit);
            return new() { Json = response };
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(["req_0", "req_1", "req_2"], state.Construction!.Compact!.CurrentStep!.RequirementIds);
        Assert.Equal(calls + 2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.All(state.Construction.Requirements, requirement => Assert.Equal("remaining", requirement.Status));
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(originals, Originals(state));
    }

    [Fact]
    public async Task PendingSelectionRecoversItsExactRequestAndCapturedPageBehavior()
    {
        var (state, runtime) = await Ready();
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
        runtime.Respond = (_, _) =>
        {
            interruption.Cancel();
            throw new OperationCanceledException(interruption.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = state.Revision }, runtime, interruption.Token));
        state = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
        Assert.Empty(state.Diagnostics);
        var issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = state.ModelCalls;
        var corrections = state.ReplanAttempts;
        var activeMilliseconds = state.ActiveMilliseconds;
        var originals = Originals(state);
        var receipts = new Dictionary<string, LLMResponse>(StringComparer.Ordinal)
        { [state.PendingCall!.Id] = new() { Json = Evidence(state, "page:requirements:0") } };
        var physicalCalls = 0;
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            if (receipts.TryGetValue(request.ClientRequestId!, out var receipt)) return receipt;
            physicalCalls++;
            throw new InvalidOperationException("Recovery must consume the exact saved receipt.");
        } };
        state = await Advance(state, recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Single(recovery.Calls);
        Assert.Equal(0, physicalCalls);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(corrections, state.ReplanAttempts);
        Assert.True(state.ActiveMilliseconds >= activeMilliseconds);
        Assert.Null(state.PendingCall);
        Assert.Equal(["req_0", "req_1", "req_2"],
            Ids(state.Construction!.Evidence!["page:requirements:0"]!["items"]!));
        Assert.Equal(originals, Originals(state));
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task CardinalityAndOriginalPageBoundariesStayStableAsUnrelatedRequirementsGrow()
    {
        var clauses = Enumerable.Range(0, 10).Select(index => $"Preserve value {index}, including 界😀. ").ToArray();
        var (state, _) = await Ready(clauses);
        AppendPage(state, "Keep the next independent result.");
        PlanningConstruction.LoadEvidence(state, ["page:requirements:0"], followup: Followup,
            purpose: PlanningCompactConstruction.SelectPurpose);
        var page = state.Construction!.Evidence!["page:requirements:0"]!;
        Assert.Equal(8, page["items"]!.AsArray().Count);
        Assert.Equal("page:requirements:8", page["next"]!.ToString());
        Assert.Equal(7, state.Construction.Compact!.RequirementOffset);
        Assert.Equal(11, PlanningCompactConstruction.SelectionContext(state)["requirements"]!.AsArray().Count);
        PlanningConstruction.LoadEvidence(state, ["page:requirements:8"], followup: Followup,
            purpose: PlanningCompactConstruction.SelectPurpose);
        Assert.Equal(["req_8", "req_9"], Ids(state.Construction.Evidence!["page:requirements:8"]!["items"]!));
        Assert.Equal("page:requirements:10", state.Construction.Evidence!["page:requirements:8"]!["next"]!.ToString());
        Assert.Equal(9, state.Construction.Compact.RequirementOffset);
        var before = Followup(state);
        for (var index = state.Requirements!.Outcomes.Count; index < 500; index++)
            AppendPage(state, $"Preserve independent obligation {index}.");
        var after = Followup(state);
        Assert.Equal(Ids(Context(before)["evidence"]!["page:requirements:0"]!["items"]!),
            Ids(Context(after)["evidence"]!["page:requirements:0"]!["items"]!));
        Assert.True(Context(after)["omittedRequirementCount"]!.GetValue<int>() > 0);
        Assert.InRange(PlanningRequestMeasurements.Measure(after, PlanningCompactConstruction.SelectPurpose, 6000).EstimatedInputTokens, 1, 6000);
        Assert.All(state.Construction.Requirements, requirement => Assert.Equal("remaining", requirement.Status));
        Assert.Contains("界😀", page["items"]![0]!["original"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedIndivisibleRequirementRemainsWholeAndFailsAdmissionAtomically()
    {
        var (state, _) = await Ready();
        var compact = state.Construction!.Compact!;
        var requirement = state.Requirements!.Outcomes[1] with { Description = new string('x', 24000) };
        state.Requirements.Outcomes[1] = requirement;
        compact.Sources.Clear(); compact.RequirementSources.Clear();
        state.Request.Prompt = string.Concat(state.Requirements.Outcomes.Select(value => value.Description));
        var offset = 0;
        foreach (var original in state.Requirements.Outcomes)
        {
            var sourceIds = new List<string>(); string? preceding = null;
            for (var start = 0; start < original.Description.Length;)
            {
                var length = PlanningCompactConstruction.OriginalPageLength(original.Description, start);
                var text = original.Description.Substring(start, length);
                var pageId = "request:" + offset;
                compact.Sources.Add(new() { Id = pageId, Kind = "page", Text = text, Indexed = true,
                    Version = PlanningGraphCompiler.Fingerprint(text), ContinuationOf = preceding });
                var sourceId = pageId + ":clause:0";
                compact.Sources.Add(new() { Id = sourceId, Kind = "requirement", Text = text, Indexed = true,
                    Version = PlanningGraphCompiler.Fingerprint(text), ContinuationOf = preceding is null ? null : preceding + ":clause:0" });
                sourceIds.Add(sourceId); preceding = pageId;
                start += length; offset += length;
            }
            compact.RequirementSources.Add(original.Id, sourceIds);
        }
        state.Construction.Requirements[0].Status = "closed";
        var initial = Followup(state);
        Assert.Equal(requirement.Description, Assert.Single(Context(initial)["requirements"]!.AsArray(), row => row?["id"]?.ToString() == requirement.Id)!["original"]!.ToString());
        var ceiling = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens;
        Assert.True(PlanningRequestMeasurements.Measure(initial, PlanningCompactConstruction.SelectPurpose, ceiling).EstimatedInputTokens > ceiling);
        var preview = PlannerFixture.Clone(state);
        Assert.True(PlanningCompactConstruction.LoadEvidence(preview, "page:requirements:1", out var exact));
        Assert.Equal(requirement.Description, Assert.Single(exact!["items"]!.AsArray())!["original"]!.ToString());
        var before = Snapshot(state);
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state,
            ["page:requirements:1"], followup: Followup, purpose: PlanningCompactConstruction.SelectPurpose));
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_LIMIT");
        Assert.Equal(before, Snapshot(state));
        Assert.Equal("rejected_before_dispatch", Assert.Single(state.RequestMeasurements!,
            measurement => measurement.Phase == PlanningCompactConstruction.SelectPurpose).Admission);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task RepeatingACoherentPageStillStopsWithoutDiscardingItsEvidence()
    {
        var (state, _) = await Ready();
        PlanningConstruction.LoadEvidence(state, ["page:requirements:0"], followup: Followup,
            purpose: PlanningCompactConstruction.SelectPurpose);
        state = PlannerFixture.Clone(state);
        var before = Snapshot(state);
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state,
            ["page:requirements:0"], followup: Followup, purpose: PlanningCompactConstruction.SelectPurpose));
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(before, Snapshot(state));
        Assert.Equal(["req_0", "req_1", "req_2"], Ids(state.Construction!.Evidence!["page:requirements:0"]!["items"]!));
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready(string[]? clauses = null)
    {
        clauses ??= ["Read the original values. ", "Preserve their order and duplicates. ", "Return the complete result."];
        var runtime = new TestRuntime { Respond = (request, _) => new() { Json = new JsonObject
        {
            ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(clauses.Select(text => (JsonNode)new JsonObject
            { ["text"] = text, ["kind"] = "requirement", ["interfaces"] = "none" }).ToArray())
        } } };
        var state = await Advance(PlannerFixture.Capture(new() { Request = new()
        { TenantId = "requirement-pages", Prompt = string.Concat(clauses), Options = new() { ["automatic_correction"] = true } } }), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.NotNull(state.Construction!.Limits.PhasePolicy);
        return (state, runtime);
    }

    private static void AppendPage(PlanningSession state, string text)
    {
        var compact = state.Construction!.Compact!;
        var page = "request:" + state.Request.Prompt.Length;
        state.Request.Prompt += text;
        compact.Sources.Add(new() { Id = page, Kind = "page", Text = text, Indexed = true, Version = PlanningGraphCompiler.Fingerprint(text) });
        var id = page + ":clause:0";
        compact.Sources.Add(new() { Id = id, Kind = "requirement", Text = text, Indexed = true, Version = PlanningGraphCompiler.Fingerprint(text) });
        var requirement = "req_" + state.Requirements!.Outcomes.Count;
        state.Requirements.Outcomes.Add(new(requirement, text));
        state.Construction.Requirements.Add(new() { Id = requirement });
        compact.RequirementSources.Add(requirement, [id]);
    }

    private static LLMRequest Followup(PlanningSession state)
    {
        var request = PlanningCompactConstruction.Request(state, PlanningCompactConstruction.SelectGuidance(state),
            PlanningCompactConstruction.SelectionContext(state), PlanningCompactConstruction.SelectionSchema(state),
            PlanningCompactConstruction.SelectPurpose);
        return PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose, request.Prompt,
            request.StructuredOutputSchema!.AsObject(), state.Construction!.Limits.DraftOutputTokens);
    }

    private static JsonObject Evidence(PlanningSession state, string address) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["step"] = null,
        ["evidenceRequests"] = new JsonArray(address), ["clarifications"] = null
    };

    private static JsonObject Selection(PlanningSession state, params string[] ids) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["evidenceRequests"] = null, ["clarifications"] = null,
        ["step"] = new JsonObject
        {
            ["scope"] = "/root", ["kind"] = "value", ["objective"] = "Preserve the original values.",
            ["requirementIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["inputs"] = new JsonArray(), ["groupId"] = null, ["operationIds"] = new JsonArray(), ["closeScopes"] = null, ["prerequisites"] = null, ["dependencyTaskIds"] = new JsonArray(),
            ["expectedOutputs"] = new JsonArray(), ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray()
        }
    };
    private static string[] Ids(JsonNode nodes) => nodes.AsArray().Select(node => node!["id"]!.ToString()).ToArray();
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static string Originals(PlanningSession state) =>
        JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements) + "\n" +
        JsonSerializer.Serialize(state.Construction!.Compact!.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource);
    private static string Snapshot(PlanningSession state) =>
        JsonSerializer.Serialize(state.Construction, PlanningJsonContext.Default.PlanningConstructionState) + "\n" + Originals(state);
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
