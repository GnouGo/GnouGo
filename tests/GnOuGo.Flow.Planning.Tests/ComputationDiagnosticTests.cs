using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ComputationDiagnosticTests
{
    private const string Unsupported = "(() => { const parsed = JSON.parse(text); const alias = parsed; return alias.name; })()";
    private static GroundedPlan Plan(string expression) => new()
    {
        Inputs = [new("text", new() { Type = "string" }, false, new() { Kind = "string", Text = "sample" })],
        Operations = [
            new CalculateGroundedOperation { Id = "parse", Value = new() { Kind = "compute", Text = expression, Members = [new("text", new() { Kind = "input", Source = "text" })] } },
            new ValidateGroundedOperation { Id = "validated", Value = new() { Kind = "result", Source = "parse" }, ResultType = new() { Type = "string" } }],
        Outputs = [new("value", new() { Kind = "result", Source = "validated" })]
    };

    [Fact]
    public void UnsupportedInferenceExplainsAliasOriginAndKnownArguments()
    {
        var finding = Assert.Single(PlanningComputationContracts.Inspect(Unsupported, new Dictionary<string, JsonObject> { ["text"] = new() { ["type"] = "string" } }));
        Assert.Equal("COMPUTATION_FIELD_UNDECLARED", finding.Code);
        Assert.Equal("inference_unsupported", finding.Context.Limitation);
        Assert.Equal("alias.name", finding.Context.Expression);
        Assert.Equal("JSON.parse(text)", finding.Context.OriginExpression);
        Assert.Equal("string", finding.Context.ParameterContracts["text"]!["type"]!.ToString());
        Assert.Contains("whole result", finding.Message);
        Assert.DoesNotContain("Declared fields: .", finding.Message);

        var declared = Assert.Single(PlanningComputationContracts.Inspect("record.missing", new Dictionary<string, JsonObject>
        { ["record"] = JsonNode.Parse("""{"type":"object","properties":{"known":{"type":"string"}},"required":["known"],"additionalProperties":false}""")!.AsObject() }));
        Assert.Equal("field_undeclared", declared.Context.Limitation);
        Assert.Contains("Declared fields: known", declared.Message);
        var opaque = Assert.Single(PlanningComputationContracts.Inspect("raw.name", new Dictionary<string, JsonObject> { ["raw"] = new() { ["x-gnougo-opaque"] = true } }));
        Assert.Equal("producer_contract_missing", opaque.Context.Limitation);
    }

    [Fact]
    public async Task RootAndDependentFindingsAreDistinctAndPersistTheirCause()
    {
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, TestContext.Current.CancellationToken);
        var plan = Plan(Unsupported);
        plan.Operations.Add(new CalculateGroundedOperation { Id = "independent", Value = new() { Kind = "compute", Text = "text.invented", Members = [new("text", new() { Kind = "input", Source = "text" })] } });
        var result = GroundedPlanValidator.Validate(plan, catalog);
        Assert.Null(result.Plan);
        var root = Assert.Single(result.Diagnostics, d => d.Location == "/scopes/main/operations/parse");
        var dependent = Assert.Single(result.Diagnostics, d => d.Location == "/scopes/main/operations/validated");
        var independent = Assert.Single(result.Diagnostics, d => d.Location == "/scopes/main/operations/independent");
        Assert.All(result.Diagnostics, d => { Assert.True(d.Required); Assert.Equal("GROUNDED_CONTRACT_INVALID", d.Code); });
        Assert.Equal(root.Location, root.Computation!.ProducerLocation);
        Assert.Equal(root.Location, dependent.Computation!.ProducerLocation);
        Assert.Equal(independent.Location, independent.Computation!.ProducerLocation);
        Assert.StartsWith("Blocked by computation", dependent.Message);
        var state = PlannerFixture.Session(); state.Diagnostics = result.Diagnostics.ToList();
        var json = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var restored = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningSession)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSession));
        var prompt = PlanningJsonTransport.Diagnostics(restored.Diagnostics);
        Assert.Equal(3, prompt.Count);
        Assert.All(prompt, d => Assert.NotNull(d!["computation"]!["producerLocation"]));
        Assert.Equal(["main"], GroundedReplanning.AffectedOwners(plan, restored.Diagnostics));
    }

    [Fact]
    public async Task WholeOpaqueCalculationMayFlowButProjectionNeedsAValidationBoundary()
    {
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, TestContext.Current.CancellationToken);
        var plan = Plan("JSON.parse(text)");
        Assert.NotNull(GroundedPlanValidator.Validate(plan, catalog).Plan);
        plan.Outputs[0] = new("value", new() { Kind = "result", Source = "parse", Path = ["name"] });
        var failure = Assert.Single(GroundedPlanValidator.Validate(plan, catalog).Diagnostics);
        Assert.Equal("inference_unsupported", failure.Computation!.Limitation);
        Assert.Equal("JSON.parse(text)", failure.Computation.Expression);
        Assert.Equal("/scopes/main/operations/parse", failure.Computation.ProducerLocation);
        Assert.Contains("whole value", failure.Message);
    }

    [Fact]
    public async Task TechnicalRepairKeepsUsageAndNeverOffersADecisionAcrossRestart()
    {
        var runtime = new TestRuntime(Plan(Unsupported));
        runtime.Respond = request =>
        {
            var properties = request.StructuredOutputSchema!["properties"]!;
            var repair = properties["operations"] is not null && properties["summary"] is null;
            var phase = repair ? "replan" : properties["actions"] is not null ? "semantic" : "binding";
            return GnOuGo.Planning.Examples.PlanningCorpus.FixtureResponse(request, phase, Plan(repair ? "String(text).trim()" : Unsupported));
        };
        var state = PlannerFixture.Session(); var planner = new TypedWorkflowPlanner();
        for (var i = 0; i < 8 && state.Diagnostics.Count == 0 && !PlanningStatus.IsTerminal(state.Status) && !PlanningStatus.IsWaiting(state.Status); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(2, state.ModelCalls); Assert.Null(state.PendingDecision);
        Assert.NotEmpty(state.Diagnostics);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Message)));
        Assert.Equal(3, state.ModelCalls); Assert.Equal(3, runtime.Calls.Count); Assert.Equal(1, state.ReplanAttempts);
        Assert.Contains("inference_unsupported", runtime.Calls[2].Prompt);
        Assert.Contains("producerLocation", runtime.Calls[2].Prompt);
        Assert.Empty(state.Decisions); Assert.Null(state.PendingDecision); Assert.Null(state.ApprovedHash);
        Assert.NotEmpty(state.Scenarios); Assert.DoesNotContain(state.Diagnostics, d => d.Required);
    }

    [Fact]
    public void LegacyDiagnosticsKeepTheirSerializedShape()
    {
        const string legacy = """[{"code":"OLD","location":"/","message":"old finding","required":true,"validationStage":null,"rule":null}]""";
        var restored = JsonSerializer.Deserialize(legacy, PlanningJsonContext.Default.ListPlanningDiagnostic)!;
        Assert.Null(Assert.Single(restored).Computation);
        Assert.Equal(legacy, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.ListPlanningDiagnostic));
    }
}
