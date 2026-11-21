using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessReviewIdentityTests
{
    [Fact]
    public async Task ReviewAliasesBusinessResultDependenciesWithoutRewritingLiteralValues()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var resultId = "result:" + state.Construction!.Compact!.Blocks![0].Id;
        PlanningCompactConstruction.SelectBlock(state, new JsonObject
        {
            ["objective"] = "Produce the later result using the established contribution.", ["requirementIds"] = new JsonArray("later"),
            ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("The later result"),
            ["dependencies"] = new JsonArray(new JsonObject { ["resultId"] = resultId, ["reason"] = "Preserve the preceding calculation." }),
            ["successCriteria"] = new JsonArray("The later result retains the requested information.")
        });
        var block = PlanningCompactConstruction.CurrentBlock(state)!;
        state.Construction.Compact.CurrentStep = new() { Id = "later_step", BlockId = block.Id, BaseHash = PlanningConstruction.Hash(state.Plan),
            Scope = "/root", Kind = "value", Objective = block.Objective, RequirementIds = block.RequirementIds.ToList(),
            ExpectedOutputs = block.ExpectedResults.ToList(), DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["later"], CompleteBlock = true,
            Tasks = [new() { Id = "later_result", Kind = "value", Outputs = [new("label", new() { Kind = "string", Text = resultId })] }]
        });
        var (canonical, raw) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        var request = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, raw.Prompt,
            raw.StructuredOutputSchema!.AsObject(), null, null, out var addresses);
        var context = BusinessProjectionDeclarationsTests.Context(request);
        var alias = addresses!.Addresses.Single(pair => pair.Value == resultId).Key;
        Assert.Equal(alias, context["businessBlock"]!["dependencies"]![0]!["resultId"]!.ToString());
        Assert.DoesNotContain(resultId, context["businessBlock"]!.ToJsonString(), StringComparison.Ordinal);
        var actionId = addresses.Addresses.Single(pair => pair.Value == "task:later_result").Key;
        var action = context["actions"]!.AsArray().Single(item => item!["id"]!.ToString() == actionId)!;
        Assert.Equal(resultId, action["outputs"]![0]!["value"]!["text"]!.ToString());
        var call = new PlanningModelCall { Id = request.ClientRequestId!, Purpose = PlanningConstruction.LocalPurpose, Request = request, EvidenceAddresses = addresses };
        PlanningModelCalls.ValidateEvidenceAddresses(call);
        Assert.True(JsonNode.DeepEquals(canonical, BusinessProjectionDeclarationsTests.Context(PlanningModelCalls.CanonicalEvidenceRequest(call))));
        Assert.Equal(resultId, Assert.Single(block.Dependencies).ResultId);
    }

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
        Assert.False(context["claims"]!.AsObject().ContainsKey("scope"));
        Assert.Equal(root, context["scopes"]![0]!["id"]!.ToString());
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
        Assert.Equal(task, concerns[0]!["targets"]![0]!.ToString());
        Assert.Equal(scope, concerns[1]!["targets"]![0]!.ToString());
        Assert.All(concerns, concern => Assert.Equal("work", Assert.Single(concern!["obligations"]!.AsArray())!["id"]!.ToString()));
        var obligation = map.Addresses.Single(pair => pair.Value == "requirement:work").Key;
        Assert.All(concerns, concern => Assert.Equal(obligation, concern!["obligations"]![0]!["evidence"]!.ToString()));
        foreach (var side in new[] { "before", "current" })
        {
            Assert.Equal(task, concerns[0]![side]!["id"]!.ToString());
            Assert.Equal(task, concerns[0]![side]!["evidence"]!.ToString());
            Assert.Equal(scope, concerns[0]![side]!["scope"]!.ToString());
            Assert.Equal(scope, concerns[1]![side]!["id"]!.ToString());
            Assert.Equal(scope, concerns[1]![side]!["evidence"]!.ToString());
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
