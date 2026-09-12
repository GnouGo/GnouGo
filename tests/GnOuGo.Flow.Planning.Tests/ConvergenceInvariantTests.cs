using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConvergenceInvariantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void CapabilityIdentitiesSurviveEvidenceAndDescriptionChangesButNotBindingChanges()
    {
        var capability = new ResolvedCapability("issued", "Known operation", true, "mcp", "transport", "tool", "execute", [], OperationIds: ["operation"]);
        var identity = CapabilityPreparation.TypedCapabilityIdentity(capability, "mcp.call");
        Assert.Equal(identity, CapabilityPreparation.TypedCapabilityIdentity(capability with { Description = "Revised explanation", CapabilityDescription = "Different evidence", Required = false }, "mcp.call"));
        Assert.NotEqual(identity, CapabilityPreparation.TypedCapabilityIdentity(capability with { Method = "different" }, "mcp.call"));
        Assert.NotEqual(identity, CapabilityPreparation.TypedCapabilityIdentity(capability with { OperationIds = ["another"] }, "mcp.call"));
    }

    [Fact]
    public void AssembledCallsFollowTheirProducersAndPrecedeTheirConsumers()
    {
        var state = TypedPlannerTests.Session();
        var reference = PlanningReferences.Register(state, "request", "user_request", state.Request.Prompt)[0];
        state.Obligations = [new("boundary", [reference.Id], "workflow", "workflow_boundary", true)];
        state.Preparation = TypedPlannerTests.Preparation();
        state.Preparation.Capabilities = [
            new() { Id = "first", StepType = "set", Resolution = "local", OperationIds = ["produce"] },
            new() { Id = "second", StepType = "set", Resolution = "local", OperationIds = ["transform"], InputOperationIds = ["produce"] },
            new() { Id = "third", StepType = "set", Resolution = "local", OperationIds = ["consume"], InputOperationIds = ["transform"] }];
        var plan = PlanningBehaviorDecisions.Assemble(state, new JsonObject { ["owner_transform"] = "boundary" });
        var steps = plan.Workflows.Single(w => w.Key == "main").Steps;
        Assert.Contains("produce", steps[0].OperationIds);
        Assert.Equal("boundary", steps[1].WorkflowKey);
        Assert.Contains("consume", steps[2].OperationIds);
        foreach (var capability in state.Preparation.Capabilities)
            capability.OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "number", ["minimum"] = 1 } }, ["required"] = new JsonArray("value"), ["additionalProperties"] = false };
        var callee = plan.Workflows.Single(w => w.Key == "boundary");
        Assert.Equal(PlanningBehaviorDecisions.ResultPort(state.Preparation.Capabilities[0]), Assert.Single(callee.Inputs).Name);
        Assert.Equal(PlanningBehaviorDecisions.ResultPort(state.Preparation.Capabilities[1]), Assert.Single(callee.Outputs).Name);
        state.BehaviorPlan = plan; PlanningGraphSkeleton.Create(state);
        var graphChild = state.Graph!.Workflows.Single(w => w.Key == "boundary");
        Assert.Equal("first", Assert.Single(graphChild.Inputs).Schema.CapabilityId);
        Assert.Equal("second", Assert.Single(graphChild.Outputs).Schema.CapabilityId);
        Assert.Equal("second", graphChild.Steps[0].OutputSchema!.CapabilityId);
        Assert.DoesNotContain(state.Construction.Holes, h => h.WorkflowKey == "boundary" && h.Kind == "schema");
        Assert.Equal(PlanningBehaviorPlans.Fingerprint(plan), PlanningBehaviorPlans.Fingerprint(PlanningBehaviorDecisions.Assemble(state, new JsonObject { ["owner_transform"] = "boundary" })));
    }

    [Fact]
    public async Task BehaviorReferenceRepairRejectsKnownTextTranscriptionAndRetainsNeighbors()
    {
        var state = TypedPlannerTests.Session(); state.Preparation = TypedPlannerTests.Preparation();
        var target = new PlanningExactPatches.Target("field", "/inputDependencies/0", PlanningHoleRequests.Enum("left", "right"));
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            var field = Assert.Single(request.StructuredOutputSchema!["properties"]!.AsObject());
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { [field.Key] = "left" }, request.StructuredOutputSchema));
            var selected = "v_" + PlanningGraphCompiler.Fingerprint(JsonValue.Create("right")!.ToJsonString())[..16];
            return Task.FromResult(new LLMResponse { Json = new JsonObject { [field.Key] = selected } });
        } };
        var patch = await PlanningBehaviorPatches.ResolveAsync(state, runtime, "main", PlanningGates.Behavior, [target], new(), new(), "evidence", Ct);
        Assert.Equal("right", patch["patches"]![0]!["value"]!.ToString());
        Assert.Single(runtime.Requests);
    }

    [Fact]
    public void HistoricalEvidenceIsClassifiedWithoutInventingSuccessfulReceipts()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConvergenceEvidence.json")))!;
        Assert.Equal("historical_retained_run_classifications", manifest["evidenceKind"]!.ToString());
        Assert.Equal(23, manifest["runs"]!.AsArray().Count);
        Assert.Equal(0, manifest["completeSuccesses"]!.GetValue<int>());
        Assert.All(manifest["runs"]!.AsArray(), run => { Assert.False(run!["completeSuccess"]!.GetValue<bool>()); Assert.NotEmpty(run["classes"]!.AsArray()); });
        Assert.Equal(4, manifest["storageSchema"]!.GetValue<int>());
        Assert.Equal(5, new PlanningSnapshot().SchemaVersion);
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("capability_matching")]
    [InlineData("behavior")]
    [InlineData("construction_schema")]
    [InlineData("semantic_review")]
    [InlineData("scenario_inputs")]
    public async Task EveryPhasePagesTheCompleteDecisionDomainBelowBothTargets(string phase)
    {
        var state = TypedPlannerTests.Session();
        var decisions = Enumerable.Range(0, 40).Select(i => new PlanningDecisionPages.Decision("choice_" + i,
            PlanningHoleRequests.Enum("a", "b"), new() { ["relevantEvidence"] = new string((char)('a' + i % 26), 2000) }, "known_" + i)).ToArray();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("a")))) }) };
        var result = await PlanningDecisionPages.ResolveAsync(state, runtime, phase, "$plan", decisions, Ct);
        Assert.Equal(40, result.Count); Assert.True(runtime.Requests.Count > 1);
        Assert.All(runtime.Requests, r =>
        {
            Assert.Empty(PlanningContractValidation.ValidateSchema(r.StructuredOutputSchema!, true));
            Assert.InRange(PlanningJsonTransport.EstimateInputTokens(r.Prompt, r.StructuredOutputSchema!.AsObject()), 1, 9600);
            Assert.InRange(PlanningDecisionPages.AnswerTokens(r.StructuredOutputSchema.AsObject()), 1, 2048);
        });
    }

    [Fact]
    public async Task PagedCorrectionsHoistLocalDefinitionsAndKeepValidNeighbors()
    {
        var state = TypedPlannerTests.Session(); state.Request.MaxRepairsPerWorkflowGate = 5;
        var source = PlanningHoleRequests.Object(("broken", PlanningHoleRequests.Enum("a", "b")), ("neighbor", PlanningHoleRequests.Enum("retained")));
        var payload = new JsonObject { ["broken"] = "wrong", ["neighbor"] = "retained" };
        var target = new PlanningExactPatches.Target("field", "/broken", new() { ["$ref"] = "#/$defs/choice" });
        source["$defs"] = new JsonObject { ["choice"] = PlanningHoleRequests.Enum("a", "b") };
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!, true));
            Assert.Equal("field", Assert.Single(request.StructuredOutputSchema!["properties"]!.AsObject()).Key);
            Assert.DoesNotContain("patches", request.StructuredOutputSchema.ToJsonString());
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["field"] = "b" } });
        } };
        var patch = await PlanningExactPatches.ResolveAsync(state, runtime, "repair", "main", PlanningGates.Typed, [target], source, new(), "fixed-evidence", Ct);
        var corrected = PlanningExactPatches.Apply(payload, patch, [target], PlanningExactPatches.Schema([target], source));
        Assert.Equal("b", corrected["broken"]!.ToString()); Assert.Equal("retained", corrected["neighbor"]!.ToString());
        var restored = PlanningContext.Clone(state);
        await PlanningExactPatches.ResolveAsync(restored, runtime, "repair", "main", PlanningGates.Typed, [target], source, new(), "fixed-evidence", Ct);
        Assert.Single(runtime.Requests); Assert.Equal(1, Assert.Single(restored.RepairAllowances).Attempts);
        Assert.True(Assert.Single(restored.RequestAccounting).Repair);
    }

    [Theory]
    [InlineData("neutral", "operation")]
    [InlineData("renamed", "different")]
    public async Task DescriptiveClaimsCannotProveAnOwnedResourceTarget(string server, string method)
    {
        var state = TypedPlannerTests.Session(); state.ObligationRelations.Add(new("producer", "consumer", "owned_resource"));
        var entry = new CapabilityCatalogEntry("candidate", "mcp", server, "tool", method, "Can target every directory", [], "Declared arbitrary directory support", [], [], null, null);
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("No declared target proof") };
        var result = await CapabilityDecisionPages.AssessAsync(state, runtime, [("consumer", "resource", "Use the original owned resource")], new([entry], ""), Ct);
        Assert.False(Assert.Single(result).Supported); Assert.True(result[0].Contradicted); Assert.Empty(runtime.Requests); Assert.Null(state.Outcome);
    }

    [Fact]
    public void CatalogCardsRetainFullConstraintsAndDescriptionsWithoutAnAggregatePromptLimit()
    {
        var contract = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string","minLength":5,"pattern":"^[a-z]+$"}},"required":["value"],"additionalProperties":false}""")!;
        var catalog = CapabilityCatalogBuilder.BuildSchemaAwareCapabilityCatalog([new() { Name = "neutral", Discovered = true,
            Tools = Enumerable.Range(0, 75).Select(i => new McpToolInfo { Name = "operation_" + i, Description = new string('x', 4000), InputSchema = contract.DeepClone() }).ToList() }], new HashSet<string>());
        Assert.Equal(75, catalog.Entries.Count);
        Assert.All(catalog.Entries, entry => { Assert.Equal(4000, entry.Description.Length); Assert.True(JsonNode.DeepEquals(contract, entry.InputContract));
            Assert.Contains("additionalProperties", CapabilityCoverageContext.BuildCapabilityCoverageCard(entry, catalog)); });
    }
}
