using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactExplicitRevisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReindexingCannotDischargeAnUnchangedAuthorizedRepair(bool extractDisposition)
    {
        var (state, runtime) = await PendingRevision();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var independent = JsonSerializer.Serialize(state.Plan!.Root.Tasks.Single(task => task.Id == "independent"), PlanningJsonContext.Default.PlanTask);
        const string clarification = " Keep the original numerical result and repair only its implementation.";
        state.Request.Prompt += clarification;
        state.Construction!.Compact!.Sources.Add(new() { Id = "revision:repair", Kind = "revision_page", Text = clarification,
            Version = PlanningGraphCompiler.Fingerprint(clarification) });
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.IntentPurpose, purpose);
            var context = Context(request); var source = context["sources"]![0]!;
            var response = request.StructuredOutputSchema!["properties"]?["clauses"] is not null
                ? new JsonObject { ["sourceId"] = source["id"]!.DeepClone(), ["clauses"] = new JsonArray(new JsonObject
                { ["text"] = source["text"]!.DeepClone(), ["kind"] = "global", ["interfaces"] = extractDisposition ? "output" : "none" }) }
                : new JsonObject { ["sourceId"] = source["id"]!.DeepClone(), ["interfaces"] = new JsonObject
                { ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray(), ["dispositions"] = new JsonArray(context["sources"]!.AsArray()
                    .Select(item => (JsonNode)new JsonObject { ["sourceId"] = item!["id"]!.DeepClone(), ["version"] = item["version"]!.DeepClone(),
                        ["kind"] = "no_public_interface", ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray(), ["reason"] = "Implementation revision, not a new caller output.",
                        ["evidence"] = new JsonArray(new JsonObject { ["sourceId"] = item["id"]!.DeepClone(), ["version"] = item["version"]!.DeepClone() }) }).ToArray()) } };
            return Wire(request, response);
        };
        for (var i = 0; i < 3 && (state.Construction!.Compact!.Sources.Any(source => !source.Indexed) ||
            state.Construction.Compact.InterfaceExtractions!.Any(extraction => !extraction.Complete)); i++)
            state = await BusinessReviewProtocolTests.Advance(BusinessReviewProtocolTests.Clone(state), runtime);
        Assert.Equal(accepted, state.Construction!.Candidate!.Hash);
        Assert.Contains(state.Construction.Candidate.Diagnostics, diagnostic => diagnostic.Code == "JUDGE_SCOPED_REVIEW" && diagnostic.Location == "/tasks/compute/outputs/result");
        Assert.DoesNotContain(state.Construction.Candidate.Reviews, review => review["technicalRejections"]?.ToJsonString().Contains("JUDGE_SCOPED_REVIEW", StringComparison.Ordinal) == true);
        Assert.Equal(0, state.ReplanAttempts);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            return Patch(request, state, "valid");
        };
        state = await BusinessReviewProtocolTests.Advance(BusinessReviewProtocolTests.Clone(state), runtime);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.True(accepted != state.Construction!.Candidate!.Hash, string.Join(";", state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Empty(state.Construction.Candidate.Diagnostics);
        Assert.False(state.Construction.Candidate.BusinessReviewed);
        Assert.Equal(1, state.ReplanAttempts);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(BusinessReviewProtocolTests.Clone(state), runtime);
        Assert.Null(state.Construction!.Candidate);
        Assert.Equal(2, state.Construction.Checkpoints.Count);
        Assert.Equal(independent, JsonSerializer.Serialize(state.Plan!.Root.Tasks.Single(task => task.Id == "independent"), PlanningJsonContext.Default.PlanTask));
        Assert.Equal(2, state.Plan.Root.Tasks.Single(task => task.Id == "compute").Outputs[0].Value.Items[1].Number);
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("noop")]
    [InlineData("outside")]
    public async Task ExplicitRevisionRejectsUnchangedOrUnauthorizedPatches(string action)
    {
        var (state, runtime) = await PendingRevision();
        var accepted = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, purpose) => { Assert.Equal("replan", purpose); return Patch(request, state, action); };
        state = await BusinessReviewProtocolTests.Advance(BusinessReviewProtocolTests.Clone(state), runtime);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(accepted, state.Construction!.Candidate!.Hash);
        Assert.False(state.Construction.Candidate.BusinessReviewed);
        Assert.NotEmpty(state.Diagnostics);
        Assert.True(state.ReplanAttempts == 1, string.Join(";", state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> PendingRevision()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        var candidate = state.Construction!.Candidate!;
        candidate.Plan.Root.Tasks[0].Outputs[0].Value.Items[1].Number = 3;
        candidate.Plan.Root.Tasks.Add(new() { Id = "independent", Kind = "value", Objective = "Retain the independent constant.", Outputs = [new("constant", new() { Kind = "number", Number = 42 })] });
        candidate.TaskIds.Add("independent"); candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Single(state.Construction!.Checkpoints);
        state = await new HybridWorkflowPlanner().AdvanceAsync(BusinessReviewProtocolTests.Clone(state), new()
        { Kind = "revise", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(), PreserveRequirements = true,
            EditablePaths = ["/tasks/compute/outputs/result"], Text = "Restore multiplication by two within the original result binding." }, runtime, TestContext.Current.CancellationToken);
        Assert.Contains(state.Construction!.Candidate!.Diagnostics, diagnostic => diagnostic.Code == "JUDGE_SCOPED_REVIEW");
        return (state, runtime);
    }

    private static LLMResponse Patch(LLMRequest request, PlanningSession state, string action)
    {
        Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
        var slot = Assert.Single(PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray())!;
        Assert.Equal("/tasks/compute/outputs/result", slot["location"]!.ToString());
        var value = state.Construction!.Candidate!.Plan.Root.Tasks.Single(task => task.Id == "compute").Outputs[0].Value;
        var replacement = JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue)!;
        replacement["items"]![1]!["number"] = action == "noop" ? 3 : 2;
        var response = new JsonObject { ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
        { ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace", ["value"] = replacement }) } };
        var wire = Wire(request, response);
        if (action == "outside") wire.Json!["patch"]!["edits"]![0]!["slot"] = "/tasks/independent/outputs/constant";
        return wire;
    }
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf("\n{", StringComparison.Ordinal) + 1)..])!.AsObject();
}
