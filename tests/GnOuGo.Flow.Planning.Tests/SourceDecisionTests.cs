using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SourceDecisionTests
{
    [Fact]
    public async Task HumanRevisionRetiresOnlySelectedObligationsAndReplaysWithoutRedispatch()
    {
        var state = TypedPlannerTests.Session(); state.Request.Prompt = "Read records. Publish records.";
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Require confirmation." };
        state.BehaviorRevision = new() { Text = "Remove publication." };
        var spans = PlanningReferences.Register(state, "request", "user_request", state.Request.Prompt);
        var host = Assert.Single(PlanningReferences.Register(state, "host", "host_constraint", "Require confirmation."));
        List<PlanningObligation> obligations = [new("read", [spans[0].Id], "capability_contract", "external_read", true),
            new("publish", [spans[1].Id], "capability_contract", "external_write", true),
            new("policy", [host.Id], "workflow", "confirmation_required", true)];
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_revision_obligations", phase); Assert.Equal("medium", request.Reasoning);
            var fields = request.StructuredOutputSchema!["properties"]!.AsObject();
            Assert.DoesNotContain(fields, p => p.Key.StartsWith("governing_policy_", StringComparison.Ordinal));
            return Task.FromResult(new LLMResponse { Json = new JsonObject(fields.Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                p.Key.StartsWith("governing_publish_", StringComparison.Ordinal) ? p.Value!["enum"]![1]!.DeepClone() : JsonValue.Create("retained")))) });
        } };
        var retained = await PlanningSourceDecisions.ApplyRevisionAsync(state, runtime, obligations, TestContext.Current.CancellationToken);
        Assert.Equal(["read", "policy"], retained.Select(o => o.Id));
        var count = runtime.Requests.Count;
        retained = await PlanningSourceDecisions.ApplyRevisionAsync(PlanningContext.Clone(state), runtime, obligations, TestContext.Current.CancellationToken);
        Assert.Equal(["read", "policy"], retained.Select(o => o.Id)); Assert.Equal(count, runtime.Requests.Count);
    }

    [Theory]
    [InlineData("Inspect  the record.", "the record.")]
    [InlineData("Vérifier  le résultat.", "le résultat.")]
    public void SpanBoundariesRecoverExactSourceAndCannotRequestArbitraryOffsets(string text, string selected)
    {
        var state = TypedPlannerTests.Session();
        var source = Assert.Single(PlanningReferences.Register(state, "request", "user_request", text));
        var boundaries = PlanningReferences.Boundaries(source, text);
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["start"] = "b1", ["end"] = "b3" }, boundaries.Schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["start"] = 1, ["end"] = 3, ["excerpt"] = selected }, boundaries.Schema));
        var reference = boundaries.Select("b1", "b3"); state.References.Add(reference);
        Assert.Equal(selected, PlanningReferences.Resolve(state, reference.Id, new Dictionary<string, string> { ["request"] = text }));
        Assert.Throws<PlanningConflictException>(() => boundaries.Select("b2", "b1"));
        Assert.Throws<PlanningConflictException>(() => PlanningReferences.Resolve(state, reference.Id, new Dictionary<string, string> { ["request"] = text + "changed" }));
    }
}
