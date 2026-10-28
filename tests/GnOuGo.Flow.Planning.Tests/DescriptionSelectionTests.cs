using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DescriptionSelectionTests
{
    [Fact]
    public async Task OriginalDescriptionCriteriaRemainAvailableWithoutWideningRepairAuthority()
    {
        const string criteria = "Select one complete description or characteristics block even without a heading; retain null for genuine absence or uncertain subject association.";
        var runtime = new TestRuntime { Respond = (issued, _) =>
        {
            var context = JsonNode.Parse(issued.Prompt[(issued.Prompt.LastIndexOf('\n') + 1)..])!;
            return new() { Json = PlanningCorpus.Transport(new JsonObject
            {
                ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(),
                ["clauses"] = new JsonArray(new JsonObject { ["text"] = criteria, ["kind"] = "requirement", ["interfaces"] = "none" })
            }, issued.StructuredOutputSchema!.AsObject(), issued.StructuredOutputSchema.AsObject()) };
        } };
        var initial = PlannerFixture.Session(); initial.Request.Prompt = criteria;
        var state = await new HybridWorkflowPlanner().AdvanceAsync(initial, new() { ExpectedRevision = initial.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        var fixture = RepairPatchTests.State();
        state.Plan = fixture.Plan; state.Catalog = fixture.Catalog;
        state.Diagnostics = fixture.Diagnostics; state.RevisionScope = fixture.RevisionScope;
        state.Construction!.Compact!.CurrentStep = new() { Id = "repair", Scope = "/root", Kind = "operation",
            RequirementIds = ["req_0"], Objective = "Retain the original description rule.", DiscoveryComplete = true,
            BaseHash = PlanningConstruction.Hash(state.Plan) };
        var before = state.ComputeArtifactHash();
        var request = PlanningConstruction.RepairRequest(state, state);
        Assert.Contains(criteria, request.Prompt);
        Assert.DoesNotContain(criteria, PlanningPrompt.RepairInstructions);
        Assert.DoesNotContain("Requested descriptions:", request.Prompt);
        var context = PlanningRepairPatch.RequestContext(request);
        Assert.DoesNotContain(context["repair"]!["slots"]!.AsArray(), slot => slot!["location"]!.ToString().EndsWith("/objective", StringComparison.Ordinal));
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
        Assert.Equal(before, state.ComputeArtifactHash());
    }
}
