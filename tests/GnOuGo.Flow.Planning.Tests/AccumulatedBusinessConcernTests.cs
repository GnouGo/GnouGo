using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class AccumulatedBusinessConcernTests
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public async Task ReviewRetainsEveryCitedObligationAndLargeBehaviorAcrossRestart(bool advisoryOptimization, int count)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready(advisoryOptimization);
        var original = "Double the supplied number while preserving this exact qualification: " + new string('q', 1500);
        var compact = state.Construction!.Compact!;
        var source = compact.Sources.Single(item => item.Id == compact.RequirementSources["work"][0]);
        source.Text = original; source.Version = PlanningGraphCompiler.Fingerprint(original);
        state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with { Description = original };
        state.Request.Prompt = original + state.Requirements.Outcomes[1].Description;
        var page = compact.Sources.Single(item => item.Kind == "page");
        page.Text = state.Request.Prompt; page.Version = PlanningGraphCompiler.Fingerprint(page.Text);
        var before = "Preserve the exact observation: " + new string('x', 1500);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = true,
            Tasks = Enumerable.Range(0, count).Select(index => new PlanTask
            {
                Id = "result_" + index, Kind = "value", Outputs = [new("observation", new() { Kind = "string", Text = before })]
            }).ToList()
        });
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = new JsonObject { ["decision"] = new JsonObject
            {
                ["verdict"] = "needs_change", ["reason"] = "Check every contribution against both original obligations.",
                ["obligations"] = new JsonArray("work", "later"),
                ["targets"] = new JsonArray(Enumerable.Range(0, count).Select(index => (JsonNode?)JsonValue.Create("result_" + index)).ToArray())
            } } };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW");
        var candidate = state.Construction!.Candidate!;
        var reviewed = candidate.Reviews.Single(review => review["outcome"]?.ToString() == "needs_change");
        var findings = reviewed["defectWitness"]!["findings"]!.AsArray();
        Assert.Equal(count, findings.Count);
        Assert.All(findings, finding => Assert.True(finding!["before"]!["omitted"]!.GetValue<bool>()));
        Assert.Equal(advisoryOptimization, (state.ThresholdWarnings ?? []).Any(warning => warning.Kind == "finding_count" && warning.Threshold == 3));
        var exactReview = reviewed.ToJsonString();
        foreach (var task in candidate.Plan.Root.Tasks) task.Outputs[0].Value.Text = before + " Corrected.";
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        state = BusinessReviewProtocolTests.Clone(state);
        Assert.Equal(exactReview, state.Construction!.Candidate!.Reviews.Single(review => review["outcome"]?.ToString() == "needs_change").ToJsonString());
        var (canonical, raw) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        var request = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, raw.Prompt,
            raw.StructuredOutputSchema!.AsObject(), null, null, out var addresses);
        var context = BusinessProjectionDeclarationsTests.Context(request);
        Assert.Null(context["interpretationReconsideration"]);
        var concerns = context["priorConcerns"]!.AsArray();
        Assert.Equal(count, concerns.Count);
        for (var index = 0; index < concerns.Count; index++)
        {
            var concern = concerns[index]!;
            Assert.Equal(new[] { "work", "later" }, concern["obligations"]!.AsArray().Select(obligation => obligation!["id"]!.ToString()));
            Assert.Equal(original, concern["obligations"]![0]!["original"]!.ToString());
            var target = addresses!.Addresses.Single(pair => pair.Value == "task:result_" + index).Key;
            Assert.Equal(target, Assert.Single(concern["targets"]!.AsArray())!.ToString());
            Assert.Equal(before, concern["before"]!["outputs"]![0]!["value"]!["text"]!.ToString());
            Assert.Equal(before + " Corrected.", concern["current"]!["outputs"]![0]!["value"]!["text"]!.ToString());
            Assert.Equal(["reason", "obligations", "targets", "before", "current"], concern.AsObject().Select(pair => pair.Key));
        }
        var call = new PlanningModelCall { Id = request.ClientRequestId!, Purpose = PlanningConstruction.LocalPurpose, Request = request, EvidenceAddresses = addresses };
        Assert.True(JsonNode.DeepEquals(canonical, BusinessProjectionDeclarationsTests.Context(PlanningModelCalls.CanonicalEvidenceRequest(call))));
        Assert.Equal(exactReview, state.Construction.Candidate.Reviews.Single(review => review["outcome"]?.ToString() == "needs_change").ToJsonString());
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(4, false)]
    [InlineData(8, true)]
    [InlineData(8, false)]
    public async Task LaterReviewAssessesEveryOutstandingConcernWithoutAHostOnlyLoop(int count, bool approves)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        var extension = new PlanningConstructionExtension
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = true,
            Tasks = Enumerable.Range(0, count).Select(index => new PlanTask
            {
                Id = "result_" + index, Kind = "value", Objective = "Compute the requested numerical contribution.",
                Outputs = [new("value", new() { Kind = "number", Number = index })]
            }).ToList()
        };
        state.Construction!.Candidate = PlanningConstruction.Apply(state, extension);
        var candidate = state.Construction.Candidate;
        var context = PlanningBusinessProjection.Build(state, false);
        for (var offset = 0; offset < count; offset += 2)
        {
            var findings = new JsonArray(Enumerable.Range(offset, 2).Select(index => (JsonNode)new JsonObject
            {
                ["kind"] = "business_fulfillment", ["location"] = "/actions/" + index,
                ["evidence"] = new JsonArray("/requirements/0", "/actions/" + index),
                ["explanation"] = "Check this actual numerical contribution against the original obligation.",
                ["uncertainty"] = ""
            }).ToArray());
            var witness = PlanningConstruction.DefectWitness(state, context, findings);
            Assert.NotNull(witness);
            candidate.Reviews.Add(new() { ["outcome"] = "needs_change", ["context"] = context.DeepClone(), ["defectWitness"] = witness });
        }
        var witnessIds = candidate.Reviews.SelectMany(review => review["defectWitness"]!["findings"]!.AsArray())
            .Select(finding => finding!["id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var task in candidate.Plan.Root.Tasks) task.Outputs[0].Value.Number += 10;
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.False(candidate.BusinessReviewed);
        var beforeCalls = state.ModelCalls; var beforeRepairs = state.ReplanAttempts;
        var reviews = 0;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            reviews++;
            var presented = BusinessProjectionDeclarationsTests.Context(request);
            var concerns = presented["priorConcerns"]!.AsArray();
            Assert.Equal(count, concerns.Count);
            Assert.All(concerns, concern =>
            {
                Assert.Equal(["reason", "obligations", "targets", "before", "current"], concern!.AsObject().Select(pair => pair.Key));
                Assert.Equal("work", Assert.Single(concern["obligations"]!.AsArray())!["id"]!.ToString());
                Assert.Single(concern["targets"]!.AsArray());
                Assert.False(JsonNode.DeepEquals(concern["before"], concern["current"]));
            });
            var changeSchema = request.StructuredOutputSchema!["properties"]!["decision"]!["anyOf"]!.AsArray()
                .Single(branch => branch!["properties"]!["verdict"]!["enum"]![0]!.ToString() == "needs_change")!;
            Assert.Equal(3, changeSchema["properties"]!["targets"]!["maxItems"]!.GetValue<int>());
            return new() { Json = approves ? BusinessReviewProtocolTests.Report("ok") : new JsonObject
            {
                ["decision"] = new JsonObject { ["verdict"] = "needs_change", ["reason"] = "The first contribution still needs correction.",
                    ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("result_0") }
            } };
        };

        state = await BusinessReviewProtocolTests.Advance(BusinessReviewProtocolTests.Clone(state), runtime);
        Assert.Equal(1, reviews);
        Assert.Equal(beforeCalls + 1, state.ModelCalls);
        Assert.Equal(beforeRepairs, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        Assert.DoesNotContain(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_REVIEW_INCONCLUSIVE");
        if (approves)
        {
            Assert.Empty(state.Diagnostics);
            var accepted = Assert.Single(state.Construction!.Checkpoints);
            var resolved = accepted.Reviews.Last()["resolvedWitnesses"]!.AsArray().Select(id => id!.ToString()).ToHashSet(StringComparer.Ordinal);
            Assert.True(witnessIds.SetEquals(resolved));
            Assert.Null(state.Construction.Candidate);
        }
        else
        {
            Assert.Empty(state.Construction!.Checkpoints);
            Assert.False(state.Construction.Candidate!.BusinessReviewed);
            Assert.All(state.Construction.Candidate.Reviews, review => Assert.Null(review["resolvedWitnesses"]));
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW");
        }
    }
}
