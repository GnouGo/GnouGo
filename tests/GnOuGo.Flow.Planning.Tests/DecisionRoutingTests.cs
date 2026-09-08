using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DecisionRoutingTests
{
    [Theory]
    [InlineData("inspect", "route", "decision")]
    [InlineData("examiner", "aiguiller", "choix")]
    public void DeclaredRawObjectDoesNotEraseRequiredStructuredDecision(string producerKey, string routeKey, string field)
    {
        var prep = Preparation();
        prep.Capabilities.Add(new() { Id = "source", StepType = "mcp.call", Server = "renamed", Method = "inspect", OperationIds = ["analysis"],
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["summary"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("summary") } });
        prep.Decisions.Add(new() { Group = "effect", SourceCapabilityId = "source", SourceOperationId = "analysis", ContractSource = "structured_output",
            SourcePointer = "/json/" + field, ResponseSchema = new() { ["type"] = "string", ["enum"] = new JsonArray("ACT", "SKIP") },
            AllowedValues = ["ACT", "SKIP"], NoEffectValues = ["SKIP"], EffectOperationIds = ["write"] });
        var producer = new PlanningNode { Key = producerKey, Type = "mcp.call", CapabilityId = "source", OperationIds = ["analysis"] };
        var route = new PlanningNode { Key = routeKey, Type = "switch", Cases = [new("ACT", null, [new() { Key = "effect", Type = "set", OperationIds = ["write"], Input = Str("done") }]), new("SKIP", null, [])] };
        var graph = new PlanningGraph { Workflows = [new() { Key = "main", Steps = [producer, route] }] };
        var unit = new PlanningConstructionUnit { Key = "contract", WorkflowKey = "main", Kind = "contracts", NodeKeys = [producerKey], ContractVersion = PlanningDataflow.ContractVersion };
        var schema = PlanningConstruction.Schema(graph.Workflows[0], unit, prep, graph);
        var candidate = new JsonObject { ["nodes"] = new JsonObject { [producerKey] = new JsonObject { ["structuredOutput"] = null } } };
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        Assert.Equal("DECISION_PRODUCER_CONTRACT_REQUIRED", Assert.Single(PlanningProducerContracts.Findings(graph, prep)).Code);
        var state = new PlanningSnapshot { Graph = graph, Preparation = prep, Request = new() { Prompt = "Inspect and route the result" } };
        Assert.Contains("requiredStructuredDecisions", TypedWorkflowPlanner.ContractPrompt(state, graph.Workflows[0], unit, prep));
        producer.StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = field, Required = true, Schema = new() { Type = "string", Enum = ["ACT", "OTHER"] } }] });
        Assert.Equal("DECISION_PRODUCER_CONTRACT_INVALID", Assert.Single(PlanningProducerContracts.Findings(graph, prep)).Code);
        producer.StructuredOutput.Schema.Properties[0].Schema.Enum = ["ACT", "SKIP"];
        Assert.Empty(PlanningProducerContracts.Findings(graph, prep));
        var resolved = PlanningDecisionRouting.Resolve(graph.Workflows[0], route, prep, graph);
        var binding = Assert.Single(resolved.Items);
        Assert.Equal(producerKey, binding.Source); Assert.Equal("structured", binding.ResultChannel); Assert.Equal([field], binding.Path);
        Assert.Contains(PlanningDataflow.Index(graph.Workflows[0], prep, graph, routeKey).Values, b => b.Value.Source == producerKey && b.Value.Path.SequenceEqual(new[] { "summary" }) && b.Value.ResultChannel is not "structured");
    }

    [Theory]
    [InlineData("true", 2)]
    [InlineData("false", 0)]
    [InlineData("null", 0)]
    [InlineData("\"invalid\"", 0)]
    [InlineData("timeout", 0)]
    [InlineData("cancel", 0)]
    public async Task SharedConfirmationRoutesBothEffectsAndNeverAuthorizesMissingConsent(string response, int expected)
    {
        var prep = Preparation(); var effects = new[] { "first_effect", "second_effect" };
        foreach (var effect in effects) prep.Decisions.Add(new() { Group = effect, SourceOperationId = "permission", SourceCapabilityId = "human",
            SourcePointer = "/response", ContractSource = PlanningDecisionContract.HumanConfirmation, ResponseSchema = new() { ["type"] = "boolean" },
            AllowedValues = ["EFFECT", "NO_EFFECT"], NoEffectValues = ["NO_EFFECT"], EffectOperationIds = [effect], PermissionOperationIds = ["permission"] });
        foreach (var id in effects.Append("cleanup")) prep.Capabilities.Add(new() { Id = id, StepType = "mcp.call", Server = "fixture", Method = id, Kind = "tool", InputSchema = new() { ["type"] = "object" } });
        prep.Capabilities.Add(new() { Id = "human", StepType = "human.input", OperationIds = ["permission"] });
        var route = new PlanningNode { Key = "route", Type = "switch", Cases = [new("EFFECT", null, effects.Select(id => new PlanningNode { Key = id, Type = "mcp.call", CapabilityId = id, OperationIds = [id] }).ToList()), new("NO_EFFECT", null, [])] };
        var graph = new PlanningGraph { Workflows = [new() { Key = "main", Steps = [
            new() { Key = "human", Type = "human.input", CapabilityId = "human", OperationIds = ["permission"], Input = PlanningConstruction.Literal(HumanInputContract.ConfirmationInput("Allow both actions?")) }, route],
            Finally = [new() { Key = "cleanup", Type = "mcp.call", CapabilityId = "cleanup" }] }] };
        Assert.Equal(effects, PlanningDecisionRouting.Contract(route, prep)!.EffectOperationIds);
        var unit = new PlanningConstructionUnit { Key = "route", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["route"] };
        graph = PlanningConstruction.Apply(graph, unit, TypedWorkflowPlanner.EmptyConstruction(PlanningConstruction.Schema(graph.Workflows[0], unit, prep, graph)), prep);
        var writes = 0; var cleanups = 0; var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("fixture", new() { Tools = effects.Append("cleanup").Select(id => new McpToolInfo { Name = id, InputSchema = new JsonObject { ["type"] = "object" } }).ToList(),
            ToolHandlers = effects.Append("cleanup").ToDictionary(id => id, id => (Func<JsonNode?, McpCallResult>)(_ => { if (id == "cleanup") cleanups++; else writes++; return new() { Content = new JsonObject { ["ok"] = true } }; })) });
        var compiled = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, prep)));
        await new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new SharedConsent(response) }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.Equal(expected, writes); Assert.Equal(1, cleanups);
    }

    private sealed class SharedConsent(string response) : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => response switch
        {
            "timeout" => throw new TimeoutException("Simulated missing consent"), "cancel" => throw new OperationCanceledException("Simulated abandoned consent"),
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
        foreach (var id in new[] { "a", "b" }) prep.Decisions.Add(new() { Group = id, SourceOperationId = "permission", SourceCapabilityId = "human", SourcePointer = "/response",
            ContractSource = PlanningDecisionContract.HumanConfirmation, ResponseSchema = new() { ["type"] = "boolean" }, AllowedValues = ["EFFECT", "NO_EFFECT"], NoEffectValues = ["NO_EFFECT"], EffectOperationIds = [id], PermissionOperationIds = ["permission"] });
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

    [Theory]
    [InlineData("confirm", "publish", "route")]
    [InlineData("consentement", "action", "decision_renamed")]
    public void ConfirmationRoutingIsNotAnEditableGenerationField(string confirmation, string effect, string routing)
    {
        var preparation = Preparation();
        preparation.Decisions.Add(new() { Group = "permission", ContractSource = PlanningDecisionContract.HumanConfirmation,
            SourceOperationId = "permission", AllowedValues = ["EFFECT", "NO_EFFECT"], NoEffectValues = ["NO_EFFECT"], EffectOperationIds = ["change"] });
        var workflow = new PlanningWorkflow { Key = "main", Steps =
        [
            new() { Key = "analysis", Type = "set", Input = Obj(("decision", Str("EFFECT"))), OutputSchema = new() { Type = "object", Properties = [new() { Name = "decision", Schema = new() { Type = "string" } }] } },
            new() { Key = confirmation, Type = "human.input", OperationIds = ["permission"], Input = PlanningConstruction.Literal(HumanInputContract.ConfirmationInput("Proceed?")) },
            new() { Key = routing, Type = "switch", Cases = [new("EFFECT", null, [new() { Key = effect, Type = "set", OperationIds = ["change"], Input = Obj(("done", new() { Kind = "boolean", Boolean = true })) }]), new("NO_EFFECT", null, [])] }
        ] };
        var graph = new PlanningGraph { Workflows = [workflow] };
        var unit = new PlanningConstructionUnit { Key = "route", WorkflowKey = "main", Kind = "implementation", NodeKeys = [routing], ContractVersion = PlanningDataflow.ContractVersion };
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        Assert.False(schema["properties"]!["nodes"]!["properties"]![routing]!["properties"]!.AsObject().ContainsKey("expr"));
        var candidate = TypedWorkflowPlanner.EmptyConstruction(schema);
        Assert.Empty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        var result = PlanningConstruction.Apply(graph, unit, candidate, preparation);
        var decision = result.Workflows[0].Steps[2].Expr!;
        Assert.Equal("confirmation", decision.Kind);
        Assert.Equal(confirmation, Assert.Single(decision.Items).Source);
        Assert.Empty(PlanningExecutableValidation.Validate(result, preparation));
        var yaml = new PlanningGraphCompiler().Compile(result, preparation);
        Assert.Contains("=== true", yaml);
        candidate["nodes"]![routing]!["expr"] = new JsonObject { ["kind"] = "string", ["text"] = "EFFECT" };
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        Assert.Throws<InvalidOperationException>(() => PlanningConstruction.Apply(graph, unit, candidate, preparation));
    }

    [Theory]
    [InlineData(true, true, "APPROVE")]
    [InlineData(true, false, "REQUEST_CHANGES")]
    [InlineData(false, true, "NO_EFFECT")]
    [InlineData(false, false, "NO_EFFECT")]
    public async Task ExplicitReducerKeepsReviewOutcomeSeparateFromMandatoryPermission(bool consent, bool approve, string expected)
    {
        var preparation = Preparation(); preparation.AllowedStepTypes.Add("decision.evaluate");
        preparation.Capabilities.Add(new() { Id = "reducer", StepType = "decision.evaluate", OperationIds = ["decide"] });
        preparation.Capabilities.Add(new() { Id = "human", StepType = "human.input", OperationIds = ["permission"] });
        preparation.Decisions.Add(new() { ContractSource = "local_decision", SourceCapabilityId = "reducer", SourceOperationId = "decide", SourcePointer = "/outcome",
            AllowedValues = ["APPROVE", "REQUEST_CHANGES", "NO_EFFECT"], NoEffectValues = ["NO_EFFECT"], InputOperationIds = ["permission"], PermissionOperationIds = ["permission"],
            ResponseSchema = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("APPROVE", "REQUEST_CHANGES", "NO_EFFECT") } });
        var workflow = new PlanningWorkflow { Key = "main", Steps = [
            new() { Key = "confirm", Type = "human.input", Purpose = "Publish?", CapabilityId = "human", OperationIds = ["permission"], Input = PlanningConstruction.Literal(HumanInputContract.ConfirmationInput("Publish?")) },
            new() { Key = "reduce", Type = "decision.evaluate", CapabilityId = "reducer", OperationIds = ["decide"] }],
            Outputs = [new() { Name = "outcome", Schema = new() { Type = "string" }, Value = new() { Kind = "output", Source = "reduce", Path = ["outcome"] } }] };
        var graph = new PlanningGraph { Workflows = [workflow] };
        var unit = new PlanningConstructionUnit { Key = "unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["reduce"], ContractVersion = PlanningDataflow.ContractVersion };
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        var fields = schema["properties"]!["nodes"]!["properties"]!["reduce"]!["properties"]!.AsObject();
        Assert.False(fields.ContainsKey("input")); Assert.False(fields.ContainsKey("onError"));
        var outcomes = new JsonObject
        {
            ["APPROVE"] = new JsonObject { ["kind"] = "boolean", ["boolean"] = approve },
            ["REQUEST_CHANGES"] = new JsonObject { ["kind"] = "boolean", ["boolean"] = !approve }
        };
        var reducer = new JsonObject { ["conditions"] = new JsonObject { ["outcome"] = outcomes } };
        var candidate = new JsonObject { ["nodes"] = new JsonObject { ["reduce"] = reducer }, ["functions"] = null };
        var result = PlanningConstruction.Apply(graph, unit, candidate, preparation);
        Assert.Empty(PlanningExecutableValidation.Validate(result, preparation));
        result.Workflows[0].Steps[0].Input.Members.Add(new("context", Str("Retain review context")));
        unit.NodeKeys.Insert(0, "confirm");
        var fingerprint = PlanningGraphCompiler.Fingerprint(result);
        for (var i = 0; i < 3; i++)
        {
            var values = PlanningConstruction.UpgradeCandidate(result, unit, PlanningConstruction.Values(result.Workflows[0], unit, preparation), preparation);
            Assert.NotNull(values["nodes"]!["reduce"]!["conditions"]);
            Assert.Equal("Retain review context", values["nodes"]!["confirm"]!["context"]!["text"]!.GetValue<string>());
            result = PlanningConstruction.Apply(result, unit, values, preparation);
            Assert.Equal(fingerprint, PlanningGraphCompiler.Fingerprint(result));
        }
        var compiled = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(new PlanningGraphCompiler().Compile(result, preparation)));
        var executed = await new WorkflowEngine { HumanInputProvider = new ConsentProvider(consent) }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(executed.Success, executed.Error?.Message); Assert.Equal(expected, executed.Outputs!["outcome"]!.GetValue<string>());
        var invalid = PlanningDecisionRouting.ConditionsValues(result.Workflows[0].Steps[1], preparation);
        invalid["outcome"]!["APPROVE"] = new JsonObject { ["kind"] = "compute", ["text"] = "return 'APPROVE';", ["members"] = new JsonArray() };
        PlanningDecisionRouting.ApplyConditions(result.Workflows[0], result.Workflows[0].Steps[1], invalid, preparation, result);
        Assert.Contains(PlanningExecutableValidation.Validate(result, preparation), d => d.Code == "DECISION_CONDITION_INVALID" && d.Location.EndsWith("/input/decisions/outcome", StringComparison.Ordinal));
    }

    private sealed class ConsentProvider(bool consent) : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult<JsonNode?>(JsonValue.Create(consent));
    }

    [Fact]
    public void ArtifactIdentityFlowsThroughCallerInputs_AndRejectsTransformedCopies()
    {
        var producer = new PlanningNode { Key = "producer", Type = "mcp.call" };
        var caller = new PlanningWorkflow { Key = "main", Steps = [producer, new() { Key = "call", Type = "workflow.call", Input = Obj(("ref", new() { Kind = "workflow", Source = "child" }),
            ("args", Obj(("artifact", new() { Kind = "output", Source = "producer", Path = ["location"] })))) }] };
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
        var routing = new PlanningBehaviorNode { Key = key, Kind = "decision", Purpose = "Route the result", OperationIds = [operation],
            InputDependencies = [], Outcomes = [new("YES", "Proceed", false, []), new("NONE", "No action", true, [])] };
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
    public async Task PreparationCheckpointSurvivesRecoveryAndRestart_WithoutLosingAnswers()
    {
        var state = new PlanningSnapshot { Request = new() { Prompt = "Inspect the resource", TenantId = "tenant" }, IntentChecked = true,
            Answers = [new("Permission?", new JsonObject { ["choice"] = "confirm" })] };
        var runtime = new CheckpointRuntime();
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new(), runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.NotNull(state.PreparationCheckpoint);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "retry", ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.NotNull(state.Preparation); Assert.Single(state.Answers); Assert.Equal(1, runtime.InventoryCalls);
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

    [Fact]
    public async Task ObsoleteTechnicalQuestionCanBeRetriedWithoutAnsweringOrResettingCounters()
    {
        var state = new PlanningSnapshot { Request = new() { TenantId = "tenant", Prompt = "Inspect" }, Status = PlanningStatus.Clarification,
            IntentChecked = true, ClarificationForms = 2, ClarificationQuestions = 5,
            PreparationCheckpoint = new() { ValidatedResults = new JsonObject { ["matching_candidate"] = new JsonObject() } },
            Question = new() { StepId = "capability-clarification-15", Prompt = "Technical alternatives", Mode = "form" },
            Answers = [new("Permission", new JsonObject { ["choice"] = "confirm" })] };
        var runtime = new CheckpointRuntime();
        var result = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "retry" }, runtime, TestContext.Current.CancellationToken);
        Assert.Null(result.Question); Assert.Single(result.Answers); Assert.Equal(2, result.ClarificationForms); Assert.Equal(5, result.ClarificationQuestions);
        Assert.Single(result.IntentHistory);
        state.Question.StepId = "intent-15";
        await Assert.ThrowsAsync<PlanningConflictException>(() => new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "retry" }, runtime, TestContext.Current.CancellationToken));
    }

    private sealed class CheckpointRuntime : IPlanningRuntime
    {
        public int InventoryCalls;
        public Task<PlanningPreparation> PrepareAsync(PlanningRequest request, CancellationToken ct) => throw new NotSupportedException();
        public async Task<PlanningPreparationProgress> AdvancePreparationAsync(PlanningRequest request, PlanningPreparationCheckpoint checkpoint, Func<CancellationToken, Task> persist, CancellationToken ct)
        {
            if (checkpoint.ValidatedResults["inventory"] is null)
            {
                InventoryCalls++; checkpoint.ValidatedResults["inventory"] = true; await persist(ct);
                throw CapabilityRecoveryTests.Failure();
            }
            return new(checkpoint, Preparation());
        }
        public Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(string yaml, PlanningRequest request, PlanningPreparation preparation, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, CancellationToken ct) => throw new NotSupportedException();
    }
}
