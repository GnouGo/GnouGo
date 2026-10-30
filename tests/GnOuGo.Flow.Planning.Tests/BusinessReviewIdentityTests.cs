using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessReviewIdentityTests
{
    [Fact]
    public async Task IssuedReviewAliasesImplementationReferencesButPreservesPublicNamesAndLiteralBytes()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var candidate = state.Construction!.Candidate!;
        candidate.Plan.Root.Outputs.Add(new("compute", new() { Kind = "string", Text = "compute /root\n é" }));
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        var originalPlan = JsonSerializer.Serialize(candidate.Plan, PlanningJsonContext.Default.TaskPlan);
        var (canonical, draft) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, draft.Prompt,
            draft.StructuredOutputSchema!.AsObject(), null, null, out var map);
        var call = new PlanningModelCall { Id = issued.ClientRequestId!, Purpose = PlanningConstruction.LocalPurpose, Request = issued, EvidenceAddresses = map };
        PlanningModelCalls.ValidateEvidenceAddresses(call);
        var context = BusinessProjectionDeclarationsTests.Context(issued);
        var task = map!.Addresses.Single(pair => pair.Value == "task:compute").Key;
        var root = map.Addresses.Single(pair => pair.Value == "scope:/root").Key;
        Assert.Equal(task, context["actions"]![0]!["id"]!.ToString());
        Assert.Equal(root, context["actions"]![0]!["scope"]!.ToString());
        Assert.Equal(root, context["claims"]!["scope"]!.ToString());
        var output = Assert.Single(context["publicOutputs"]!.AsArray(), item => item!["name"]!.ToString() == "compute")!;
        Assert.Equal("compute /root\n é", output["value"]!["text"]!.ToString());
        Assert.False(context.ContainsKey("technicalValidation"));
        Assert.Equal(originalPlan, JsonSerializer.Serialize(candidate.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.True(JsonNode.DeepEquals(canonical, BusinessProjectionDeclarationsTests.Context(PlanningModelCalls.CanonicalEvidenceRequest(call))));

        var report = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_change", ["reason"] = "The reviewed result does not fulfill the original obligation.",
            ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray(task, root) } };
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(report, issued.StructuredOutputSchema!));
        var raw = report.ToJsonString();
        var normalized = PlanningModelCalls.NormalizeEvidenceResponse(report, map);
        Assert.Equal(new[] { "compute", "/root" }, normalized["decision"]!["targets"]!.AsArray().Select(item => item!.ToString()));
        Assert.Equal(raw, report.ToJsonString());
        normalized["decision"]!["targets"]![0] = "e00000000_999";
        Assert.Throws<PlanningConflictException>(() => PlanningModelCalls.NormalizeEvidenceResponse(normalized, map));
        map.ReviewImplementationIds = false;
        Assert.Throws<PlanningConflictException>(() => PlanningModelCalls.ValidateEvidenceAddresses(call));
    }

    [Fact]
    public async Task PriorConcernSnapshotsUseTheCurrentIssuedImplementationAliases()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        runtime.Respond = (_, _) => new() { Json = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_change",
            ["reason"] = "The reviewed value violates the original obligation.", ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("compute", "/root") } } };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW");
        var (canonical, draft) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, draft.Prompt,
            draft.StructuredOutputSchema!.AsObject(), null, null, out var map);
        var context = BusinessProjectionDeclarationsTests.Context(issued);
        var task = map!.Addresses.Single(pair => pair.Value == "task:compute").Key;
        var scope = map.Addresses.Single(pair => pair.Value == "scope:/root").Key;
        var concerns = context["priorConcerns"]!.AsArray();
        Assert.Equal(2, concerns.Count);
        foreach (var side in new[] { "before", "current" })
        {
            Assert.Equal(task, concerns[0]![side]!["id"]!.ToString());
            Assert.Equal(scope, concerns[0]![side]!["scope"]!.ToString());
            Assert.Equal(scope, concerns[1]![side]!["id"]!.ToString());
            Assert.Equal(task, concerns[1]![side]!["tasks"]![0]!.ToString());
        }
        Assert.All(concerns, concern => Assert.Null(concern!["status"]));
        var call = new PlanningModelCall { Id = issued.ClientRequestId!, Purpose = PlanningConstruction.LocalPurpose, Request = issued, EvidenceAddresses = map };
        PlanningModelCalls.ValidateEvidenceAddresses(call);
        Assert.True(JsonNode.DeepEquals(canonical, BusinessProjectionDeclarationsTests.Context(PlanningModelCalls.CanonicalEvidenceRequest(call))));
    }

    [Fact]
    public void HistoricalCanonicalTaskNamesAreNotReinterpretedAsNewAliases()
    {
        const string name = "e12345678_1";
        var original = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_change",
            ["reason"] = "The original finding.", ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray(name) } };
        var retained = new PlanningEvidenceAddresses { Addresses = { [name] = "task:different" } };
        Assert.True(JsonNode.DeepEquals(original, PlanningModelCalls.NormalizeEvidenceResponse(original, retained)));
        Assert.DoesNotContain("reviewImplementationIds", JsonSerializer.Serialize(retained, PlanningJsonContext.Default.PlanningEvidenceAddresses));
    }

    [Fact]
    public async Task RestoredReviewUsesItsIssuedAliasesAfterNewerNavigationChanges()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        runtime.Respond = (_, _) => throw new InvalidOperationException("No completion received in this fixture.");
        _ = await BusinessReviewProtocolTests.Advance(state, runtime);
        var saved = BusinessReviewProtocolTests.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null && checkpoint.Status == PlanningStatus.Generating));
        var pending = saved.PendingCall!;
        var bytes = JsonSerializer.Serialize(pending.Request, PlanningJsonContext.Default.LLMRequest);
        var alias = pending.EvidenceAddresses!.Addresses.Single(pair => pair.Value == "task:compute").Key;
        saved.Construction!.Compact!.FailedActions.Add("later-navigation");
        var recovered = new TestRuntime { EncodeCanonicalEvidence = false };
        recovered.CaptureIssued(pending);
        recovered.Respond = (request, _) =>
        {
            Assert.Equal(bytes, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_change", ["reason"] = "The reviewed value violates the original obligation.",
                ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray(alias) } } };
        };
        saved = await BusinessReviewProtocolTests.Advance(saved, recovered);
        Assert.Contains(saved.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW" && diagnostic.Location == "/tasks/compute");
        var review = saved.Construction!.Candidate!.Reviews.Last();
        Assert.Equal(alias, review["rawResponse"]!["decision"]!["targets"]![0]!.ToString());
        Assert.Equal("compute", review["response"]!["decision"]!["targets"]![0]!.ToString());
    }
}
