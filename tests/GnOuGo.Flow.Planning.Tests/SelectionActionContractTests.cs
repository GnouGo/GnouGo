using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SelectionActionContractTests
{
    [Fact]
    public async Task StrictSelectionGrammarAllowsExactlyOneAvailableAction()
    {
        var (state, _) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var block = new JsonObject { ["objective"] = "Use the prepared result to deliver the original obligation",
            ["requirementIds"] = new JsonArray("later"), ["informationNeeds"] = new JsonArray(),
            ["expectedResults"] = new JsonArray("The original result"), ["dependencies"] = new JsonArray(),
            ["successCriteria"] = new JsonArray("The requested result is supplied") };
        var valid = new JsonObject { ["action"] = new JsonObject { ["kind"] = "block", ["block"] = block } };
        Assert.Empty(PlanningContractValidation.ValidateInstance(valid, schema));
        foreach (var invalid in new JsonNode[]
        {
            new JsonObject { ["block"] = null, ["evidenceRequests"] = null, ["clarifications"] = null },
            new JsonObject { ["action"] = null },
            new JsonObject { ["action"] = new JsonObject() },
            new JsonObject { ["action"] = new JsonObject { ["kind"] = "block", ["block"] = null } },
            new JsonObject { ["action"] = new JsonObject { ["kind"] = "completion", ["completionRequirementIds"] = new JsonArray("later") } },
            new JsonObject { ["action"] = new JsonObject { ["kind"] = "evidence", ["evidenceRequests"] = new JsonArray() } },
            new JsonObject { ["action"] = new JsonObject { ["kind"] = "clarification", ["clarifications"] = new JsonArray() } }
        }) Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, schema));
        var multiple = valid.DeepClone(); multiple["action"]!["completionRequirementIds"] = new JsonArray("later");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(multiple, schema));
        multiple = valid.DeepClone(); multiple["clarifications"] = new JsonArray();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(multiple, schema));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task VersionOneSelectionContinuationIsRejectedUnchanged()
    {
        var (state, runtime) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        state.Request.Options[PlanningCompactConstruction.BusinessSelectionContract] = 1;
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        await Assert.ThrowsAsync<PlanningConflictException>(() => BusinessReviewProtocolTests.Advance(state, runtime));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }
}
