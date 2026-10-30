using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class AccumulatedBusinessConcernTests
{
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
            Scope = "/root", RequirementIds = ["work"],
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
                Assert.Equal(["reason", "before", "current"], concern!.AsObject().Select(pair => pair.Key));
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
