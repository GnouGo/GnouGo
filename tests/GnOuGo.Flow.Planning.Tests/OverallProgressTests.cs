using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class OverallProgressTests
{
    [Fact]
    public async Task BusinessSelectionKeepsLoadedOriginalEvidenceWithoutOfferingTheSameReadAgain()
    {
        var (state, runtime) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        var source = state.Construction!.Compact!.Sources[0];
        var address = "source:" + source.Id;
        var remaining = "source:" + state.Construction.Compact.Sources[1].Id;
        Assert.Contains('"' + address + '"', PlanningCompactConstruction.SelectionRequest(state).StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        var baseline = PlanningConstruction.Hash(state.Plan); var calls = runtime.Calls.Count;
        PlanningConstruction.LoadEvidence(state, [address], followup: PlanningCompactConstruction.SelectionRequest,
            purpose: PlanningCompactConstruction.SelectPurpose);
        var restored = PlannerFixture.Clone(state);
        var request = PlanningCompactConstruction.SelectionRequest(restored);
        Assert.DoesNotContain('"' + address + '"', request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains('"' + remaining + '"', request.StructuredOutputSchema.ToJsonString(), StringComparison.Ordinal);
        var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        Assert.Equal(source.Text, context["evidence"]![address]!["text"]!.ToString());
        Assert.Equal(baseline, PlanningConstruction.Hash(restored.Plan)); Assert.Equal(calls, runtime.Calls.Count);
    }

    [Fact]
    public async Task BothSelectionsSharePureGlobalFactsWithoutCopyingOriginalText()
    {
        var (state, runtime) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var calls = runtime.Calls.Count;
        var business = PlanningCompactConstruction.SelectionContext(state);
        var implementation = PlanningCompactConstruction.ImplementationContext(state);

        Assert.NotNull(business["overallProgress"]);
        Assert.True(JsonNode.DeepEquals(business["overallProgress"], implementation["overallProgress"]));
        var view = business["overallProgress"]!;
        Assert.Equal(state.Requirements!.Outcomes.Select(requirement => requirement.Id),
            view["expectedResults"]!.AsArray().Select(row => row!["requirementId"]!.ToString()));
        Assert.Equal(business["availableResults"]!.AsArray().Select(result => result!["id"]!.ToString()),
            view["acceptedResults"]!.AsArray().Select(id => id!.ToString()));
        Assert.Equal("required", view["globalReview"]!.ToString());
        Assert.False(view["executionObserved"]!.GetValue<bool>());
        foreach (var requirement in state.Requirements.Outcomes)
            Assert.DoesNotContain(requirement.Description, view.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(calls, runtime.Calls.Count);
        Assert.Equal(view.ToJsonString(), PlanningCompactConstruction.SelectionContext(PlannerFixture.Clone(state))["overallProgress"]!.ToJsonString());
    }

    [Fact]
    public async Task RequiredDeliverableConnectionTracksActualLineageAndMissingBindings()
    {
        var (state, _) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        state.Requirements!.Outputs = [new() { Name = "result", Required = true, Type = new() { Kind = "number" } },
            new() { Name = "optional", Required = false, Type = new() { Kind = "string" } }];
        state.Requirements.Outcomes[1] = state.Requirements.Outcomes[1] with
            { Commitments = [new("remote_calculation") { Result = new("result", "result") }] };
        var missing = View(state);
        Assert.Equal(new[] { "result" }, missing["missingDeliverableBindings"]!.AsArray().Select(row => row!["name"]!.ToString()));
        Assert.Equal("not_bound", Result(missing)["bindingStatus"]!.ToString());
        Assert.Equal("disconnected", Result(missing)["producerConnection"]!.ToString());
        Assert.False(missing["completionAvailable"]!.GetValue<bool>());

        BusinessSelectionCoverageContextTests.AddCall(state, conditional: false);
        state.Plan!.Root.Outputs = [new("result", new() { Kind = "number", Number = 9 })];
        var wrong = View(state);
        Assert.Empty(wrong["missingDeliverableBindings"]!.AsArray());
        Assert.Equal("disconnected", Result(wrong)["producerConnection"]!.ToString());
        Assert.False(wrong["completionAvailable"]!.GetValue<bool>());
        state.Plan.Root.Outputs = [new("result", new() { Kind = "output", Source = "invoke", Port = "result" })];
        var connected = View(state);
        Assert.Equal("established", Result(connected)["producerConnection"]!.ToString());
        Assert.True(connected["completionAvailable"]!.GetValue<bool>());
        Assert.False(connected["executionObserved"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ConditionalConnectionsRemainUnverifiedAndOptionalGroupsCreateNoDeliverable()
    {
        var (state, _) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        BusinessSelectionCoverageContextTests.AddCall(state, conditional: true);
        var view = View(state);
        Assert.Empty(view["missingDeliverableBindings"]!.AsArray());
        Assert.True(view["completionAvailable"]!.GetValue<bool>());
        Assert.False(view["executionObserved"]!.GetValue<bool>());
        Assert.Contains(PlanningCompactConstruction.ImplementationContext(state)["availableResults"]!.AsArray(),
            result => result!["connectionStatus"]!.ToString() == "structurally_reachable");
        state.Requirements!.Outcomes[1] = state.Requirements.Outcomes[1] with { Commitments = [] };
        state.Plan!.Root.Tasks.RemoveAll(task => task.Id == "guard");
        Assert.True(View(state)["completionAvailable"]!.GetValue<bool>());
        Assert.Empty(View(state)["declaredResults"]!.AsArray());
    }

    [Fact]
    public async Task MissingGroupExportCannotAppearConnectedUntilItsActualBoundaryIsRestored()
    {
        var (state, _) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        BusinessSelectionCoverageContextTests.AddCall(state, conditional: false);
        state.Requirements!.Outputs = [new() { Name = "result", Required = true, Type = new() { Kind = "number" } }];
        state.Requirements.Outcomes[1] = state.Requirements.Outcomes[1] with
            { Commitments = [new("remote_calculation") { Result = new("result", "result") }] };
        state.Plan!.Root.Outputs = [new("result", new() { Kind = "output", Source = "invoke", Port = "result" })];
        var boundary = state.Plan.Groups.Single().Body.Outputs;
        var export = Assert.Single(boundary);
        boundary.Clear();
        Assert.Equal("unverified", Result(View(state))["producerConnection"]!.ToString());
        Assert.Equal("blocked", View(state)["technicalValidation"]!.ToString());
        Assert.False(View(state)["completionAvailable"]!.GetValue<bool>());
        boundary.Add(export);
        Assert.Equal("established", Result(View(state))["producerConnection"]!.ToString());
        Assert.True(View(state)["completionAvailable"]!.GetValue<bool>());
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task GlobalConstraintsAndCompositionFindingsRemainVisibleAtBothSelections()
    {
        var (state, _) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        const string constraint = "Preserve order, keep supplied data private, and always release temporary resources.";
        var compact = state.Construction!.Compact!;
        compact.Sources.Add(new() { Id = "constraints", Kind = "global", Indexed = true, Text = constraint,
            Version = PlanningGraphCompiler.Fingerprint(constraint) });
        compact.GlobalSourceIds.Add("constraints");
        foreach (var context in new[] { PlanningCompactConstruction.SelectionContext(state), PlanningCompactConstruction.ImplementationContext(state) })
        {
            Assert.Equal("source:constraints", Assert.Single(context["overallProgress"]!["globalConstraints"]!.AsArray())!.ToString());
            Assert.Equal(constraint, Assert.Single(context["globalConstraints"]!.AsArray())!["text"]!.ToString());
            Assert.Equal("/currentPlanFindings", context["overallProgress"]!["compositionFindingsAt"]!.ToString());
            Assert.Contains("prepared", context["currentPlanFindings"]![0]!["nextNeed"]!.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("remote_calculation", context["overallProgress"]!.ToJsonString(), StringComparison.Ordinal);
            Assert.False(context["overallProgress"]!["globalReviewCurrent"]!.GetValue<bool>());
        }
        BusinessSelectionCoverageContextTests.AddCall(state, conditional: false);
        Assert.Null(View(state)["compositionFindingsAt"]);
    }

    [Fact]
    public async Task BothSelectionsOfferTheirProgressOutputAndPageLinksThroughExistingEvidence()
    {
        var (state, _) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        state.Requirements!.Outputs = Enumerable.Range(0, 10).Select(index => new TaskInput
            { Name = "deliverable_" + index, Required = index >= 8, Type = new() { Kind = "number" } }).ToList();
        foreach (var (purpose, request) in new[] { (PlanningCompactConstruction.SelectPurpose, PlanningCompactConstruction.SelectionRequest(state)),
                     (PlanningCompactConstruction.ImplementationPurpose, PlanningCompactConstruction.ImplementationRequest(state)) })
        {
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            var progress = context["overallProgress"]!;
            Assert.Equal(10, progress["declaredResultCount"]!.GetValue<int>());
            Assert.Equal("page:outputs:8", progress["nextDeclaredResults"]!.ToString());
            foreach (var link in new[] { "page:outputs:8", "output:deliverable_8", "output:deliverable_9" })
                Assert.Contains('"' + link + '"', request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
            var issued = PlanningModelCalls.PrepareRequest(state, purpose, request.Prompt,
                request.StructuredOutputSchema!.AsObject(), state.Construction!.Limits.DraftOutputTokens);
            var issuedProgress = JsonNode.Parse(issued.Prompt[(issued.Prompt.LastIndexOf('\n') + 1)..])!["overallProgress"]!;
            foreach (var link in new[] { issuedProgress["nextDeclaredResults"],
                         issuedProgress["missingDeliverableBindings"]![0]!["evidence"], issuedProgress["missingDeliverableBindings"]![0]!["directory"] })
                Assert.Contains(link!.ToJsonString(), issued.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("page:outputs:8")]
    [InlineData("output:deliverable_8")]
    [InlineData("requirement")]
    public async Task BusinessProgressEvidenceUsesExistingCursorsAndPreservesAcceptedWork(string requested)
    {
        var (state, runtime) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        state.Requirements!.Outputs = Enumerable.Range(0, 18).Select(index => new TaskInput
            { Name = "deliverable_" + index, Required = index >= 8,
                Type = new() { Kind = "string", Enum = ["original_" + index] } }).ToList();
        var requirement = state.Requirements.Outcomes[0];
        var address = requested == "requirement" ? "requirement:" + requirement.Id : requested;
        var planHash = PlanningConstruction.Hash(state.Plan);
        var blocks = state.Construction!.Compact!.Blocks!.Select(block =>
            JsonSerializer.Serialize(block, PlanningJsonContext.Default.PlanningConstructionBlock)).ToArray();
        var calls = runtime.Calls.Count;
        PlanningConstruction.LoadEvidence(state, [address], followup: PlanningCompactConstruction.SelectionRequest,
            purpose: PlanningCompactConstruction.SelectPurpose);
        var request = PlanningCompactConstruction.SelectionRequest(state);
        var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        if (requested.StartsWith("page:", StringComparison.Ordinal))
        {
            Assert.Equal(18, context["overallProgress"]!["declaredResultCount"]!.GetValue<int>());
            Assert.Equal("deliverable_8", context["overallProgress"]!["declaredResults"]![0]!["name"]!.ToString());
            Assert.Equal("page:outputs:16", context["overallProgress"]!["nextDeclaredResults"]!.ToString());
            Assert.Contains("page:outputs:16", request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        }
        else if (requested.StartsWith("output:", StringComparison.Ordinal))
            Assert.Equal("original_8", Assert.Single(context["evidence"]![address]!["type"]!["enum"]!.AsArray())!.ToString());
        else
            Assert.Contains(context["requirements"]!.AsArray(), row => row!["id"]!.ToString() == requirement.Id);
        Assert.DoesNotContain("scope:/root", context.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(planHash, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(blocks, Assert.IsType<List<PlanningConstructionBlock>>(state.Construction.Compact.Blocks).Select(block =>
            JsonSerializer.Serialize(block, PlanningJsonContext.Default.PlanningConstructionBlock)).ToArray());
        Assert.Equal(calls, runtime.Calls.Count);
        Assert.Empty(state.Diagnostics);
    }

    [Fact]
    public async Task IssuedSelectionRecoversItsExactProgressAndEvidenceWithoutAnotherLogicalCall()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        var compact = state.Construction.Compact!;
        compact.CurrentStep = null; compact.CurrentBlockId = null; compact.Blocks!.Clear();
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await BusinessReviewProtocolTests.Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.First(checkpoint => checkpoint.PendingCall?.Purpose == PlanningCompactConstruction.SelectPurpose));
        var issued = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls;
        var recovery = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Contains("overallProgress", request.Prompt, StringComparison.Ordinal);
            return new() { Json = new JsonObject { ["action"] = new JsonObject { ["kind"] = "block", ["block"] = new JsonObject
            {
                ["objective"] = "Produce the requested numeric result", ["requirementIds"] = new JsonArray("work"),
                ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("The doubled input"),
                ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray("The result equals twice the supplied input")
            } } } };
        } };
        var resumed = await BusinessReviewProtocolTests.Advance(saved, recovery);
        Assert.Empty(resumed.Diagnostics);
        Assert.Equal(calls, resumed.ModelCalls);
        Assert.Single(recovery.Calls);
        Assert.Equal("work", Assert.Single(PlanningCompactConstruction.CurrentBlock(resumed)!.RequirementIds));
    }

    private static JsonNode View(PlanningSession state) => PlanningCompactConstruction.SelectionContext(state)["overallProgress"]!;
    private static JsonNode Result(JsonNode view) => view["declaredResults"]!.AsArray().Single(result => result!["name"]!.ToString() == "result")!;
}
