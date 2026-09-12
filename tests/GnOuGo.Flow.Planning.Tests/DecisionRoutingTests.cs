using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DecisionRoutingTests
{

    private sealed class SharedConsent(string response) : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => response switch
        {
            "timeout" => throw new TimeoutException("Simulated missing consent"),
            "cancel" => throw new OperationCanceledException("Simulated abandoned consent"),
            _ => Task.FromResult(JsonNode.Parse(response))
        };
    }

    [Theory]
    [InlineData("source")]
    [InlineData("capability")]
    [InlineData("pointer")]
    [InlineData("schema")]
    [InlineData("outcomes")]
    [InlineData("permission")]
    public void DistinctDecisionContractsCannotBeSilentlyCoalesced(string difference)
    {
        var prep = Preparation();
        foreach (var id in new[] { "a", "b" }) prep.Decisions.Add(new()
        {
            Group = id,
            SourceOperationId = "permission",
            SourceCapabilityId = "human",
            SourcePointer = "/response",
            ContractSource = PlanningDecisionContract.HumanConfirmation,
            ResponseSchema = new() { ["type"] = "boolean" },
            AllowedValues = ["EFFECT", "NO_EFFECT"],
            NoEffectValues = ["NO_EFFECT"],
            EffectOperationIds = [id],
            PermissionOperationIds = ["permission"]
        });
        var second = prep.Decisions[1];
        switch (difference)
        {
            case "source": second.SourceOperationId = "other"; break;
            case "capability": second.SourceCapabilityId = "other"; break;
            case "pointer": second.SourcePointer = "/other"; break;
            case "schema": second.ResponseSchema = new() { ["type"] = "string" }; break;
            case "outcomes": second.AllowedValues.Add("UNCERTAIN"); break;
            case "permission": second.PermissionOperationIds.Add("second_permission"); break;
        }
        var route = new PlanningNode { Key = "route", Type = "switch", Cases = [new("EFFECT", null, [new() { Key = "a", OperationIds = ["a"] }, new() { Key = "b", OperationIds = ["b"] }])] };
        Assert.Throws<PlanningDecisionRouting.AmbiguousDecisionException>(() => PlanningDecisionRouting.Contract(route, prep));
    }

    [Theory]
    [InlineData("return condition ? 'ALLOW' : undefined;", true)]
    [InlineData("(() => { return flag ? 'ACTION' : 'NONE'; })()", true)]
    [InlineData("return flag === true;", false)]
    [InlineData("const helper = () => 'label'; return helper() === 'label';", false)]
    public void OutcomeLabelsCannotMasqueradeAsBooleanConditions(string body, bool invalid)
        => Assert.Equal(invalid, PlanningComputations.HasNonBooleanResult(body));

    private sealed class ConsentProvider(bool consent) : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult<JsonNode?>(JsonValue.Create(consent));
    }

    [Fact]
    public void ArtifactIdentityFlowsThroughCallerInputs_AndRejectsTransformedCopies()
    {
        var producer = new PlanningNode { Key = "producer", Type = "mcp.call" };
        var caller = new PlanningWorkflow
        {
            Key = "main",
            Steps = [producer, new() { Key = "call", Type = "workflow.call", Input = Obj(("ref", new() { Kind = "workflow", Source = "child" }),
            ("args", Obj(("artifact", new() { Kind = "output", Source = "producer", Path = ["location"] })))) }]
        };
        var child = new PlanningWorkflow { Key = "child", Inputs = [new() { Name = "artifact" }] };
        var graph = new PlanningGraph { Workflows = [caller, child] };
        var value = new PlanningValue { Kind = "input", Source = "artifact" };
        Assert.True(PlanningValueProvenance.Proves(child, value, graph, (node, reference) => node == producer && reference.Path.SequenceEqual(new[] { "location" })));
        caller.Steps[1].Input.Members.Single(m => m.Name == "args").Value.Members[0] = new("artifact", Str("copied location"));
        Assert.False(PlanningValueProvenance.Proves(child, value, graph, (node, _) => node == producer));
    }

    [Theory]
    [InlineData("decision", "resolve")]
    [InlineData("décision", "calculer")]
    public void RoutingGetsItsExactLockedDecisionProducerWithoutInventingAnEffect(string key, string operation)
    {
        var preparation = Preparation();
        preparation.Capabilities.Add(new() { Id = "native", StepType = "decision.evaluate", Required = true, OperationIds = [operation], EffectKind = "none" });
        var routing = new PlanningBehaviorNode
        {
            Key = key,
            Kind = "decision",
            Purpose = "Route the result",
            OperationIds = [operation],
            InputDependencies = [],
            Outcomes = [new("YES", "Proceed", false, []), new("NONE", "No action", true, [])]
        };
        var plan = new PlanningBehaviorPlan { Summary = "Route a result", Workflows = [new() { Key = "main", Purpose = "Route", OperationIds = [operation], Steps = [routing] }] };
        PlanningBehaviorPlans.CompleteOwnership(plan, preparation);
        Assert.Empty(PlanningBehaviorPlans.Validate(plan, preparation));
        var producer = plan.Workflows[0].Steps[0];
        Assert.Equal("native", producer.CapabilityId); Assert.Equal("operation", producer.Kind);
        Assert.Same(routing, plan.Workflows[0].Steps[1]);
        PlanningBehaviorPlans.CompleteOwnership(plan, preparation);
        Assert.Equal(2, plan.Workflows[0].Steps.Count);
    }

    [Fact]
    public void ContainerProvenanceUnwrapsRawEnvelopesButNeverStructuredCopies()
    {
        var child = new PlanningNode { Key = "producer", Type = "mcp.call" };
        var workflow = new PlanningWorkflow { Key = "main", Steps = [new() { Key = "container", Type = "sequence", Steps = [child] }] };
        var graph = new PlanningGraph { Workflows = [workflow] };
        var value = new PlanningValue { Kind = "output", Source = "container", Path = ["producer", "response", "artifact"] };
        Assert.True(PlanningValueProvenance.Proves(workflow, value, graph, (node, reference) => node == child && reference.Path.SequenceEqual(new[] { "artifact" })));
        value.Path[1] = "json";
        Assert.False(PlanningValueProvenance.Proves(workflow, value, graph, (node, _) => node == child));
    }

    [Fact]
    public async Task PreparationCheckpointSurvivesTechnicalStopAndRestartWithoutLosingAnswers()
    {
        var state = new PlanningSnapshot { Request = new() { Prompt = "Inspect the resource", TenantId = "tenant" }, Intent = new() { Checked = true, Answers = [new("Permission?", new JsonObject { ["choice"] = "confirm" })] } };
        var runtime = new CheckpointRuntime();
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new(), runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.NotNull(state.PreparationCheckpoint);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        await Assert.ThrowsAsync<ArgumentException>(() => new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "retry", ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken));
        var retained = JsonSerializer.Serialize(state.PreparationCheckpoint, PlanningJsonContext.Default.PlanningPreparationCheckpoint);
        var stopped = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(retained, JsonSerializer.Serialize(stopped.PreparationCheckpoint, PlanningJsonContext.Default.PlanningPreparationCheckpoint));
        Assert.Null(stopped.Preparation); Assert.Single(stopped.Intent.Answers); Assert.Equal(1, runtime.InventoryCalls);

    }

    [Fact]
    public void RequiredArtifactRepairsItsOriginalProducerFailurePath()
    {
        var preparation = Preparation();
        preparation.Capabilities.Add(new() { Id = "producer", ArtifactContract = new(1, [new("artifact", "/value", "materialize")], []) });
        preparation.Capabilities.Add(new() { Id = "consumer", ArtifactContract = new(1, [], [new("artifact", "/value", true)]) });
        var producer = new PlanningNode { Key = "produce", Type = "mcp.call", CapabilityId = "producer", OnError = [new(null, "continue", null, null)] };
        var graph = new PlanningGraph { Workflows = [new() { Key = "main", Steps = [producer, new() { Key = "consume", Type = "mcp.call", CapabilityId = "consumer" }] }] };
        var finding = Assert.Single(PlanningArtifactBindings.PrerequisiteFindings(graph, preparation));
        Assert.Equal("/workflows/0/steps/0/onError", finding.Location);
        producer.OnError[0] = producer.OnError[0] with { Action = "stop" };
        Assert.Empty(PlanningArtifactBindings.PrerequisiteFindings(graph, preparation));
    }

    private sealed class CheckpointRuntime : IPlanningRuntime
    {
        public int InventoryCalls;
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningPreparation preparation, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlanningDiagnostic>>([]);
        public Task CheckpointAsync(PlanningSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
        public async Task<PlanningPreparationProgress> PrepareAsync(PlanningSnapshot snapshot, CancellationToken ct)
        {
            var checkpoint = snapshot.PreparationCheckpoint ??= new();
            if (checkpoint.ValidatedResults["inventory"] is null)
            {
                InventoryCalls++; checkpoint.ValidatedResults["inventory"] = true; await CheckpointAsync(snapshot, ct);
                throw CapabilityRecoveryTests.Failure();
            }
            return new(checkpoint, Preparation());
        }
        public Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
