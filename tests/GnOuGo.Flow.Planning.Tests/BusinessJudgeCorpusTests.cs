using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessJudgeCorpusTests
{
    public static TheoryData<string> Names => new(BusinessReviewCorpus.Cases().Select(item => item.Id));

    [Theory]
    [MemberData(nameof(Names))]
    public void FrozenBusinessCasesAreTechnicallyValidAndExposeActualBehavior(string id)
    {
        var item = BusinessReviewCorpus.Cases().Single(item => item.Id == id);
        var state = BusinessReviewCorpus.Session(item);
        var candidate = state.Construction!.Candidate!;
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request, candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        if (item.Global) Assert.Empty(new TaskPlanCompiler().Compile(candidate.Plan, state.Catalog!, state.Request).Diagnostics);
        var (context, proposal) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, item.Global);
        Assert.Equal(item.Original, Assert.Single(context["requirements"]!.AsArray())!["original"]!.ToString());
        Assert.Equal(item.ExpectedVerdict == "ok", item.Id.StartsWith("valid-", StringComparison.Ordinal));
        Assert.Equal(item.Plan.Root.Tasks.Count, context["actions"]!.AsArray().Count);
        var request = PlanningModelCalls.PrepareRequest(state, item.Global ? PlanningConstruction.GlobalPurpose : PlanningConstruction.LocalPurpose,
            proposal.Prompt, proposal.StructuredOutputSchema!.AsObject());
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(request, PlanningJsonContext.Default.LLMRequest).Length, 1, 13_197);
        Assert.DoesNotContain("inputSchema", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("acknowledgments", proposal.StructuredOutputSchema.ToJsonString(), StringComparison.Ordinal);
        if (id == "wrong-semantic-port")
        {
            var save = context["actions"]!.AsArray().Single(action => action!["id"]!.ToString() == "save")!;
            Assert.Equal("flat", save["arguments"]![1]!["value"]!["port"]!.ToString());
            Assert.Contains("omits pagination", context.ToJsonString(), StringComparison.Ordinal);
        }
        if (id == "valid-open-fragment")
        {
            Assert.Equal("current_block", context["review"]!.ToString());
            Assert.True(context["scopes"]![0]!["open"]!.GetValue<bool>());
            Assert.False(context["claims"]!["workflowComplete"]!.GetValue<bool>());
        }
    }
}
