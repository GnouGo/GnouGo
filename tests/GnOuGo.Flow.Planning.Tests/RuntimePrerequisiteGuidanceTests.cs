using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RuntimePrerequisiteGuidanceTests
{
    [Theory]
    [InlineData("offered", true)]
    [InlineData("unoffered_definition", false)]
    [InlineData("abandon_only", false)]
    [InlineData("cyclic_reference", false)]
    [InlineData("boolean", false)]
    public void OnlyReachableSuspensionBranchesEnableGuidance(string mode, bool expected)
    {
        var choice = PlanningSchemas.Object(("disposition", PlanningSchemas.Enum(mode == "abandon_only" ? "abandon" : "suspend")));
        var schema = PlanningSchemas.Object(("reselectStep", new JsonObject { ["$ref"] = "#/$defs/choice" }));
        schema["$defs"] = new JsonObject { ["choice"] = choice };
        if (mode == "unoffered_definition") schema["properties"]!["reselectStep"] = PlanningSchemas.Type("null");
        if (mode == "cyclic_reference") schema["$defs"]!["choice"] = new JsonObject { ["$ref"] = "#/$defs/choice" };
        if (mode == "boolean") schema["properties"]!["reselectStep"] = false;
        var before = schema.ToJsonString();
        Assert.Equal(expected, PlanningPromptPresentation.OffersRuntimePrerequisite(schema));
        Assert.Equal(before, schema.ToJsonString());
    }

    [Fact]
    public async Task FreshDiagnosticDraftBindsGuidanceBeforeIdentityAndPreservesAcceptedCandidate()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var candidate = state.Construction!.Candidate;
        var schema = PlanningSchemas.Object(("reselectStep", PlanningSchemas.Nullable(
            PlanningSchemas.Object(("disposition", PlanningSchemas.Enum("suspend"))))));
        const string prompt = "Preserve the original obligations.\n{\"source\":\"Exact user text\"}";
        var request = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose, prompt, schema);
        Assert.Contains("planning evidence, not missing runtime values", request.Prompt, StringComparison.Ordinal);
        Assert.EndsWith(prompt[prompt.LastIndexOf('\n')..], request.Prompt, StringComparison.Ordinal);
        Assert.Equal(request.ClientRequestId, PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose, prompt, schema).ClientRequestId);
        Assert.Same(candidate, state.Construction.Candidate);
        Assert.Null(state.PendingCall);
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["reselectStep"] = null }, request.StructuredOutputSchema!));
    }
}
