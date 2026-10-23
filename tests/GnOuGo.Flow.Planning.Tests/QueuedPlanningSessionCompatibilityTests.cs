using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class QueuedPlanningSessionCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalSerializedZeroProgressSessionKeepsItsCapturedOrAbsentProfiles(bool captured)
    {
        var state = PlannerFixture.HistoricalSession();
        if (captured)
        {
            state.Request.Options["planning_input_review_version"] = 1;
            state.Request.Options["business_judge_review_version"] = 1;
        }
        state = PlannerFixture.Clone(state); // A queued session persisted by the previous binary.
        Assert.Equal(0, state.Revision);
        Assert.Equal(0, state.ModelCalls);
        Assert.Null(state.Plan);
        Assert.Null(state.PendingCall);
        var runtime = new TestRuntime();
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Null(state.Construction);
        Assert.Null(state.Request.Options["planning_diagnostics_version"]);
        Assert.Equal(captured ? "1" : null, state.Request.Options["planning_input_review_version"]?.ToString());
        Assert.Equal(captured ? "1" : null, state.Request.Options["business_judge_review_version"]?.ToString());
        Assert.Single(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task FreshDirectSessionCapturesNewProfilesBeforeItsCoordinatorClone()
    {
        var state = await PlannerFixture.RunAsync(IncrementalRuntime(), new PlanningSession { Request = PlannerFixture.HistoricalSession().Request });
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.True(state.Construction!.Complete);
        Assert.Equal(2, state.Construction.Version);
        Assert.Equal(4, state.Construction.Compact!.PresentationVersion);
        Assert.Equal("4", state.Request.Options["construction_presentation_version"]!.ToString());
        Assert.Single(state.Construction.Checkpoints);
        Assert.Equal("1", state.Request.Options["planning_diagnostics_version"]!.ToString());
        Assert.Equal("2", state.Request.Options["planning_input_review_version"]!.ToString());
        Assert.Equal("2", state.Request.Options["business_judge_review_version"]!.ToString());
        Assert.DoesNotContain("isNewlyConstructed", JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var artifact = state.ComputeArtifactHash();
        var historical = PlannerFixture.Clone(state);
        historical.Request.Options["construction_presentation_version"] = 3;
        historical.Construction!.Compact!.PresentationVersion = 3;
        Assert.NotEqual(artifact, historical.ComputeArtifactHash());
        Assert.Equal(artifact, PlannerFixture.Clone(state).ComputeArtifactHash());
    }

    [Fact]
    public async Task AlreadyCapturedProfilesRemainStableOnAnUnserializedSession()
    {
        var state = new PlanningSession { Request = PlannerFixture.HistoricalSession().Request };
        state.Request.Options["compilation_profile"] = "compact-bindings-v10";
        state.Request.Options["planning_input_review_version"] = 1;
        state.Request.Options["business_judge_review_version"] = 1;
        state = await PlannerFixture.RunAsync(IncrementalRuntime(), state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.True(state.Construction!.Complete);
        Assert.Equal(4, state.Construction.Compact!.PresentationVersion);
        Assert.Equal("4", state.Request.Options["construction_presentation_version"]!.ToString());
        Assert.Equal("compact-bindings-v10", state.Request.Options["compilation_profile"]!.ToString());
        Assert.Equal("1", state.Request.Options["planning_input_review_version"]!.ToString());
        Assert.Equal("1", state.Request.Options["business_judge_review_version"]!.ToString());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ExplicitIntentRevisionRetainsPresentationAndCumulativeAccounting(int version)
    {
        var runtime = IncrementalRuntime();
        var initial = PlannerFixture.CapturedPresentation(new PlanningSession { Request = PlannerFixture.HistoricalSession().Request }, version);
        var state = await PlannerFixture.RunAsync(runtime, initial);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var calls = state.ModelCalls;
        var dispatches = runtime.Calls.Count;
        var repairs = state.ReplanAttempts;
        var accepted = PlanningConstruction.Hash(state.Plan);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "revise", ExpectedRevision = state.Revision, Text = "Return an updated greeting.", PreserveRequirements = false },
            runtime, PlannerFixture.Ct);
        Assert.Equal(version, state.Construction!.Compact!.PresentationVersion);
        Assert.Equal(version, state.Request.Options["construction_presentation_version"]!.GetValue<int>());
        Assert.True(PlanningCompactConstruction.CapturedPresentationValid(state));
        Assert.Empty(state.Construction.Compact.InterfaceExtractions!);
        Assert.NotNull(state.Construction.Compact.InterfaceExtractions);
        Assert.Null(state.Plan);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Request.Baseline));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(repairs, state.ReplanAttempts);
        Assert.Equal(dispatches, runtime.Calls.Count);
    }

    private static TestRuntime IncrementalRuntime()
    {
        var runtime = new TestRuntime();
        runtime.Proposal.Requirements!.Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }];
        runtime.Respond = (request, purpose) =>
        {
            var context = purpose is PlanningCompactConstruction.IntentPurpose or PlanningCompactConstruction.SelectPurpose or PlanningConstruction.DraftPurpose
                ? JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])! : new JsonObject();
            JsonObject response;
            if (purpose == PlanningCompactConstruction.IntentPurpose)
                response = request.StructuredOutputSchema!["properties"]!["clauses"] is not null
                    ? new() { ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(),
                        ["clauses"] = new JsonArray(new JsonObject { ["text"] = context["sources"]![0]!["text"]!.DeepClone(),
                            ["kind"] = "requirement", ["interfaces"] = "output" }) }
                    : new() { ["sourceId"] = request.StructuredOutputSchema["properties"]!["sourceId"]!["enum"]![0]!.DeepClone(),
                        ["interfaces"] = new JsonObject
                        {
                            ["inputs"] = new JsonArray(),
                            ["outputs"] = JsonSerializer.SerializeToNode(runtime.Proposal.Requirements!.Outputs, PlanningJsonContext.Default.ListTaskInput),
                            ["dispositions"] = new JsonArray(context["sources"]!.AsArray().Select(source => (JsonNode)new JsonObject
                            {
                                ["sourceId"] = source!["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone(),
                                ["kind"] = "interfaces", ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray("message"),
                                ["reason"] = "The original request asks for this greeting result.",
                                ["evidence"] = new JsonArray(new JsonObject
                                { ["sourceId"] = source["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone() })
                            }).ToArray())
                        }, ["evidenceRequests"] = null, ["clarifications"] = null };
            else if (purpose == PlanningCompactConstruction.SelectPurpose)
                response = new() { ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(), ["step"] = new JsonObject
                    { ["scope"] = "/root", ["kind"] = "value", ["objective"] = "Return the greeting.",
                        ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(), ["dependencyTaskIds"] = new JsonArray(),
                        ["expectedOutputs"] = new JsonArray("The greeting."), ["closeRequirementIds"] = new JsonArray("req_0"), ["outputNames"] = new JsonArray("message") } };
            else if (purpose == PlanningConstruction.DraftPurpose)
                response = new() { ["baseHash"] = context["baseHash"]!.DeepClone(), ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
                    { Tasks = runtime.Proposal.Plan!.Root.Tasks, Outputs = runtime.Proposal.Plan.Root.Outputs,
                        RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true }, PlanningJsonContext.Default.PlanningConstructionExtension) };
            else
                response = new() { ["findings"] = new JsonArray(), ["uncertainty"] = "Offline fixture; execution is unverified." };
            var schema = request.StructuredOutputSchema!.AsObject();
            return new() { Json = PlanningCorpus.Transport(response, schema, schema) };
        };
        return runtime;
    }
}
