using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class OutcomePlacementSchemaTests
{
    [Theory]
    [InlineData("data", false, false, "once", true)]
    [InlineData("data", true, false, "once", false)]
    [InlineData("data", false, true, "once", false)]
    [InlineData("data", false, false, "each_item", false)]
    [InlineData("read", false, false, "once", true)]
    [InlineData("write", true, false, "once", true)]
    [InlineData("lifecycle", true, true, "each_item", true)]
    public void NewSchemasMatchExistingPlacementSemantics(string effect, bool always, bool conditional, string coverage, bool valid)
    {
        var schema = RequirementsSchema();
        var requirements = JsonNode.Parse("""{"summary":"Requested work","outcomes":[{"id":"arbitrary","description":"Requested result"}],"inputs":[]}""")!;
        var outcome = requirements["outcomes"]![0]!;
        outcome["execution"] = effect; outcome["always"] = always; outcome["conditional"] = conditional; outcome["coverage"] = coverage;
        Assert.Equal(valid, PlanningContractValidation.ValidateInstance(requirements, schema).Count == 0);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, true));
    }

    [Fact]
    public async Task RetainedMappingCannotTreatNestedPortsAsRootOutputsOrSkipAcceptedWork()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SchemaPortability", "live-outcome-mapping.json")))!;
        var proposal = fixture["responses"]![1]!;
        var state = await ComposedOutcomeTests.State();
        state.Plan = proposal["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        state.Requirements = proposal["requirements"]!.Deserialize(PlanningJsonContext.Default.PlanningRequirements)!;
        state.OutcomeBindings = proposal["outcomeBindings"]!.Deserialize(PlanningJsonContext.Default.ListPlanningOutcomeBinding)!;
        Assert.Contains(PlanningOutcomeValidation.Findings(state), d => d.Code == "OUTCOME_OUTPUT_INVALID");
        Assert.Contains("plan.root.outputs", new PlanningPrompt(state).Request().Prompt);
        Assert.Contains("cannot weaken an accepted unconditional outcome", new PlanningPrompt(state).Request().Prompt);
    }

    [Fact]
    public async Task RetainedLiveResponsesFailAtWireValidationWithoutConsumingSemanticRepairs()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SchemaPortability", "live-outcome-placement.json")))!;
        foreach (var requirements in fixture["requirements"]!.AsArray())
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(requirements, RequirementsSchema()));
        var state = PlannerFixture.Session(); state.IntentVersion = 1; state.OutcomeVersion = 2;
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        Assert.Contains("cleanup/finally after failure", new PlanningPrompt(state).Request().Prompt);
        Assert.Contains("Every plan MUST include outcomeBindings", new PlanningPrompt(state).Request().Prompt);
    }

    [Fact]
    public async Task MissingBindingsFromLiveProposalStillCannotApproveExternalWork()
    {
        var state = await ComposedOutcomeTests.State(); state.OutcomeBindings = null;
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SchemaPortability", "live-missing-bindings.json")))!;
        state.Plan = fixture["response"]!["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan);
        Assert.Equal("OUTCOME_BINDINGS_INVALID", Assert.Single(PlanningOutcomeValidation.Findings(state)).Code);
        var schema = PlanningSchemas.FullProposal(state, compact: false);
        Assert.Contains("non-null with a plan", schema["properties"]!["outcomeBindings"]!["description"]!.ToString());
    }

    private static JsonObject RequirementsSchema()
    {
        var state = PlannerFixture.Session(); state.IntentVersion = 1; state.OutcomeVersion = 2;
        var proposal = PlanningSchemas.FullProposal(state, compact: false);
        var schema = proposal["$defs"]!["requirements"]!.DeepClone().AsObject();
        schema["$defs"] = proposal["$defs"]!.DeepClone(); return schema;
    }
}
