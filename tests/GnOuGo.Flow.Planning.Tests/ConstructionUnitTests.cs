using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstructionUnitTests
{
    [Theory]
    [InlineData("source-a", "Return every observed item", "Observe all pages")]
    [InlineData("renamed-source", "Retourner tous les elements observes", "Observer toutes les pages")]
    public void ContractContextKeepsDeclaredConsumersAndAncestorsWithoutUnrelatedImplementations(string sourceId, string consumerPurpose, string loopPurpose)
    {
        var state = ApprovedSkeleton(); var workflow = state.Graph!.Workflows[0];
        var producer = workflow.Steps[0]; producer.CapabilityId = sourceId; producer.OperationIds = ["read"];
        workflow.Steps = [new() { Key = "traversal", Type = "loop.sequential", Purpose = loopPurpose, Steps = [producer] }];
        state.Preparation!.Capabilities =
        [
            new() { Id = sourceId, StepType = "mcp.call", OperationIds = ["read"], OutputSchema = new() { ["type"] = "string" } },
            new() { Id = "consumer-contract", StepType = "mcp.call", OperationIds = ["consume"], InputOperationIds = ["read"] }
        ];
        state.Graph.Workflows.Add(new() { Key = "consumer-workflow", Steps = [new() { Key = "consumer-node", OperationIds = ["consume"], Purpose = consumerPurpose }] });
        state.Feedback = "Preserve complete traversal";
        state.Request.Options["generator"] = new JsonObject { ["context"] = "Retain host policy" };
        var unit = new PlanningConstructionUnit { Key = "contract", WorkflowKey = "main", Kind = "contracts", NodeKeys = [producer.Key], ContractVersion = PlanningDataflow.ContractVersion };
        var before = TypedWorkflowPlanner.ContractPrompt(state, workflow, unit, state.Preparation);
        workflow.Steps.Insert(0, new() { Key = "unrelated-preceding-action", OperationIds = ["read"], Purpose = "A preceding action in the same composite operation" });
        for (var i = 0; i < 40; i++) workflow.Steps.Add(new() { Key = "unrelated-" + i, Purpose = new string('x', 2_000) });
        workflow.Steps.Add(new() { Key = "unrelated-implementation", Expr = new() { Kind = "expression", Text = "unrelatedHelper()" }, Input = Str(new string('x', 20_000)) });
        var after = TypedWorkflowPlanner.ContractPrompt(state, workflow, unit, state.Preparation);
        Assert.Equal(before, after);
        Assert.Contains(consumerPurpose, after); Assert.Contains(loopPurpose, after);
        Assert.Contains("Retain host policy", after); Assert.Contains(state.Feedback, after);
        Assert.Contains(sourceId, after); Assert.DoesNotContain("unrelated", after);
        Assert.DoesNotContain("JavaScript", after); Assert.DoesNotContain("helper", after);
        Assert.InRange(PlanningConstruction.EstimateInputTokens(after, PlanningConstruction.Schema(workflow, unit, state.Preparation, state.Graph)), 1, 12_000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionCeilingRetainsTheCandidateAndReportsTransportOutcome(bool repairing)
    {
        var state = ApprovedSkeleton();
        state.ConstructionUnits = [new() { Key = "contract", WorkflowKey = "main", Kind = "contracts", NodeKeys = ["greeting"], ContractVersion = PlanningDataflow.ContractVersion }];
        if (repairing)
        {
            state.ConstructionUnits[0].Candidate = new JsonObject { ["retained"] = "invalid but reviewable" };
            state.ConstructionUnits[0].CandidateHash = "retained-hash"; state.ConstructionUnits[0].Calls = 1;
            state.ConstructionUnits[0].Diagnostics = [new("UNIT_RESPONSE_INVALID", "/units/contract", "Previous incomplete response")];
        }
        var original = state.ConstructionUnits[0].Candidate?.DeepClone(); var calls = 0;
        var runtime = new FakeRuntime { OnCall = (_, request, _) =>
        {
            calls++; Assert.Equal(8192, request.MaxTokens);
            return Task.FromResult(new LLMResponse { CompletionStatus = "output_limit", Json = new JsonObject { ["partial"] = "must not replace retained fields" } });
        } };
        state = await Advance(new TypedWorkflowPlanner(), state, runtime);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.Equal(1, calls); Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Equal("output_limit", state.ConstructionUnits[0].DispatchOutcome);
        Assert.True(JsonNode.DeepEquals(original, state.ConstructionUnits[0].Candidate));
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT" && d.Message.Contains("8192", StringComparison.Ordinal));
        Assert.Equal(repairing ? 1 : 0, state.ConstructionUnits[0].RepairCalls);
        Assert.Contains(state.Attempts.Last().Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT");
    }

    [Fact]
    public async Task CompletionCeilingSplitsUnstartedNodesAndPreservesTheChargedParent()
    {
        var state = ApprovedSkeleton();
        state.BehaviorPlan!.Workflows[0].Steps.Add(new() { Key = "other", Purpose = "Return another greeting" });
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation!);
        state.ConstructionUnits = [new() { Key = "contracts", WorkflowKey = "main", Kind = "contracts", NodeKeys = ["greeting", "other"], ContractVersion = PlanningDataflow.ContractVersion }];
        var runtime = new FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" }) };
        state = await Advance(new TypedWorkflowPlanner(), state, runtime);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Equal("superseded", state.ConstructionUnits[0].Status); Assert.Equal(1, state.ConstructionUnits[0].Calls);
        Assert.All(state.ConstructionUnits.Skip(1), u => { Assert.Single(u.NodeKeys); Assert.Equal(0, u.Calls); });
        Assert.Contains(state.Attempts.Last().Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OversizedSingleNodeGeneratesDurableFieldGroupsAndChargesOnlyInvalidResponsesToRepair(int invalidResponses)
    {
        var state = ApprovedSkeleton(); state.Request.Generation.MaxInputTokensPerUnit = 4_000; state.Request.MaxRepairs = 1;
        var properties = new JsonObject(Enumerable.Range(0, 12).Select(i => new KeyValuePair<string, JsonNode?>("argument" + i,
            new JsonObject { ["type"] = "string", ["description"] = new string('x', 900) })));
        state.Preparation!.Capabilities.Add(new() { Id = "tool", StepType = "mcp.call", Server = "fixture", Method = "work", Kind = "tool",
            InputSchema = new() { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()), ["additionalProperties"] = false } });
        state.BehaviorPlan!.Workflows[0].Steps[0].CapabilityId = "tool"; state.BehaviorPlan.Workflows[0].Outputs.Clear();
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation);
        state.ConstructionUnits = [new() { Key = "unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["greeting"], ContractVersion = PlanningDataflow.ContractVersion }];
        var calls = 0; var repairs = 0; var completed = new Dictionary<string, JsonNode?>();
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            calls++; if (phase == "repair_unit") repairs++;
            Assert.InRange(PlanningConstruction.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject()), 1, 4_000);
            Assert.Equal(8_192, request.MaxTokens);
            if (calls <= invalidResponses) return Task.FromResult(new LLMResponse { Json = new JsonObject() });
            var changes = new JsonObject();
            foreach (var field in request.StructuredOutputSchema!["properties"]!["changes"]!["properties"]!.AsObject())
            {
                Assert.DoesNotContain(field.Key, completed.Keys);
                JsonNode? value = field.Key == "functions" ? null : field.Key.EndsWith("/onError", StringComparison.Ordinal) ? new JsonArray() : new JsonObject { ["kind"] = "string", ["text"] = field.Key };
                changes[field.Key] = value; completed[field.Key] = value?.DeepClone();
            }
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["changes"] = changes, ["remove"] = new JsonArray() } });
        } };
        var planner = new TypedWorkflowPlanner(); var sawPartial = false;
        for (var i = 0; i < 20 && state.ConstructionUnits[0].Status != "validated" && state.Status == PlanningStatus.Generating; i++)
        {
            state = await Advance(planner, state, runtime);
            sawPartial |= state.ConstructionUnits[0].PartialCandidate;
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        }
        Assert.True(sawPartial);
        if (invalidResponses == 2)
        {
            Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.Equal("recovery", state.ConstructionUnits[0].Status);
            Assert.Equal(2, calls); Assert.Equal(1, repairs); Assert.Equal(0, state.ConstructionUnits[0].GeneratedFieldGroups);
            Assert.Contains(state.Diagnostics, d => d.Code == "UNIT_PATCH_REJECTED"); return;
        }
        Assert.Equal("validated", state.ConstructionUnits[0].Status);
        Assert.False(state.ConstructionUnits[0].PartialCandidate); Assert.True(state.ConstructionUnits[0].GeneratedFieldGroups > 1);
        Assert.Equal(invalidResponses, repairs); Assert.Equal(repairs, state.ConstructionUnits[0].RepairCalls);
        Assert.Equal(calls, state.ConstructionUnits[0].Calls);
        Assert.Equal(12, state.Graph!.Workflows[0].Steps[0].Input.Members.Single(m => m.Name == "request").Value.Members.Count);
        new PlanningGraphCompiler().Compile(state.Graph, state.Preparation!);
    }

    [Theory]
    [InlineData("resource", "read_group", false)]
    [InlineData("ressource", "groupe_renomme", true)]
    public async Task ParentConstructionDefersAggregateInputChecksUntilChildrenExist(string input, string key, bool retained)
    {
        var state = ApprovedSkeleton(); var behavior = state.BehaviorPlan!.Workflows[0];
        behavior.Inputs = [new(input, "Runtime resource", true)]; behavior.Outputs = [];
        behavior.Steps = [new() { Key = key, Kind = "parallel", Purpose = "Read the runtime resource", InputDependencies = [input],
            Steps = [new() { Key = "child", Purpose = "Read", InputDependencies = [] }] }];
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation!);
        state.Graph.Workflows[0].Inputs[0].Schema = new() { Type = "string" };
        state.Dataflow = new() { InputObligations = { ["main/" + key] = [input] } };
        state.ConstructionUnits = [new() { Key = "parent", WorkflowKey = "main", Kind = "implementation", NodeKeys = [key], ContractVersion = PlanningDataflow.ContractVersion },
            new() { Key = "child", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["child"], Dependencies = ["parent"], ContractVersion = PlanningDataflow.ContractVersion }];
        if (retained)
        {
            var unit = state.ConstructionUnits[0];
            unit.Candidate = new() { ["nodes"] = new JsonObject { [key] = new JsonObject() }, ["functions"] = null };
            unit.CandidateHash = PlanningGraphCompiler.Fingerprint(unit.Candidate.ToJsonString());
            unit.Diagnostics = [new("BUSINESS_INPUT_BINDING_MISSING", "/workflows/0/steps/0/input", "Previous aggregate finding")];
            unit.Status = "invalid"; unit.ContractVersion--;
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        }
        var runtime = new FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("An empty native container requires no model call.") };
        state = await Advance(new TypedWorkflowPlanner(), state, runtime);
        Assert.Equal("validated", state.ConstructionUnits[0].Status);
        Assert.Empty(state.Diagnostics);
        var parent = state.Graph!.Workflows[0].Steps[0];
        Assert.False(TypedWorkflowPlanner.ConstructionInputsAvailable(state, state.ConstructionUnits[0], parent));
        // Deferred construction validation never exempts the full artifact from the obligation.
        Assert.Contains(PlanningBehaviorPlans.ValidateImplementation(state.BehaviorPlan!, state.Graph, state.Preparation!), d => d.Code == "BUSINESS_INPUT_BINDING_MISSING");
        state.ConstructionUnits[1].Status = "validated";
        Assert.True(TypedWorkflowPlanner.ConstructionInputsAvailable(state, state.ConstructionUnits[0], parent));
        parent.Branches[0].Steps[0].Input = Obj(("value", new() { Kind = "input", Source = input }));
        Assert.DoesNotContain(PlanningBehaviorPlans.ValidateImplementation(state.BehaviorPlan!, state.Graph, state.Preparation!), d => d.Code == "BUSINESS_INPUT_BINDING_MISSING");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HelperValidationIsIdenticalOnRetainedCandidatesAndRepairs(bool exhausted)
    {
        var state = ApprovedSkeleton(); var calls = 0;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            if (phase == "repair_unit")
            {
                calls++; Assert.Contains("require", request.Prompt);
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["changes"] = new JsonObject
                { ["functions"] = exhausted ? "function invalid() { return require('module'); }" : null }, ["remove"] = new JsonArray() } });
            }
            var response = FakeRuntime.ConstructionResponse(request, phase);
            if (phase == "fragment_implementation") response["functions"] = "function invalid() { return require('module'); }";
            return Task.FromResult(new LLMResponse { Json = response });
        } };
        var planner = new TypedWorkflowPlanner();
        for (var i = 0; i < 8 && !state.ConstructionUnits.Any(u => u.Diagnostics.Any(d => d.Code == "UNIT_HELPER_DEPENDENCY_INVALID")); i++) state = await Advance(planner, state, runtime);
        Assert.Contains(state.Diagnostics, d => d.Code == "UNIT_HELPER_DEPENDENCY_INVALID");
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        state = await Advance(planner, state, runtime);
        Assert.Equal(1, calls);
        Assert.Equal(exhausted ? PlanningStatus.Recovery : PlanningStatus.Generating, state.Status);
        Assert.Equal(exhausted ? "recovery" : "validated", state.ConstructionUnits.Single(u => u.Kind == "implementation").Status);
    }

    [Fact]
    public async Task DeterministicConversionFailureStopsWithoutRepeatingTheSameCandidateOrCallingTheModel()
    {
        var state = ApprovedSkeleton();
        state.Preparation!.Decisions.Add(new() { Group = "permission", ContractSource = PlanningDecisionContract.HumanConfirmation,
            SourceOperationId = "missing_permission", AllowedValues = ["EFFECT", "NO_EFFECT"], NoEffectValues = ["NO_EFFECT"], EffectOperationIds = ["change"] });
        state.Graph!.Workflows[0].Steps = [new() { Key = "route", Type = "switch", Cases = [new("EFFECT", null, [new() { Key = "effect", OperationIds = ["change"] }]), new("NO_EFFECT", null, [])] }];
        state.ConstructionUnits = [new() { Key = "route", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["route"], ContractVersion = PlanningDataflow.ContractVersion }];
        var runtime = new FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("A deterministic unit must not call the model.") };
        var planner = new TypedWorkflowPlanner();
        for (var i = 0; i < 3 && state.Status == PlanningStatus.Generating; i++) state = await Advance(planner, state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "UNIT_CONTRACT_UNRESOLVED");
        Assert.Equal(0, Assert.Single(state.ConstructionUnits).Calls);
        Assert.InRange(state.Attempts.Count, 0, 1);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningConstructionUnit Unit(string kind, params string[] nodes) => new() { Key = "unit", WorkflowKey = "main", Kind = kind, NodeKeys = nodes.ToList() };

    [Theory]
    [InlineData("route", "ACT", "NONE", 2)]
    [InlineData("décision/renommée", "AGIR", "RIEN", 2)]
    [InlineData("renamed", "WRITE", "SKIP", 3)]
    public void DefaultIsNotAnEditableCase_AndExactKeysCannotBeInvented(string key, string effect, string noEffect, int count)
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Steps = [new() { Key = key, Type = "switch", Expr = Str(effect), Cases = [new(effect, null, []), new(noEffect, null, [])] }];
        if (count == 3) workflow.Steps[0].Cases.Add(new("UNCERTAIN", null, []));
        var unit = Unit("implementation", key); var schema = PlanningConstruction.Schema(workflow, unit, Preparation());
        Assert.DoesNotContain("caseConditions", schema.ToJsonString());
        var response = PlanningConstruction.Values(workflow, unit);
        response["nodes"]![key]!["caseConditions"] = new JsonArray(new JsonObject { ["index"] = count, ["when"] = true });
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(response, schema, unit));
        Assert.Throws<InvalidOperationException>(() => PlanningConstruction.Apply(graph, unit, response, Preparation()));
        Assert.Equal(count, workflow.Steps[0].Cases.Count);
        response["nodes"]![key]!.AsObject().Remove("caseConditions");
        var lowered = PlanningConstruction.Apply(graph, unit, response, Preparation());
        Assert.Equal(workflow.Steps[0].Cases.Select(c => c.Value), lowered.Workflows[0].Steps[0].Cases.Select(c => c.Value));
        Assert.All(lowered.Workflows[0].Steps[0].Cases, c => Assert.Null(c.When));
        Assert.Empty(lowered.Workflows[0].Steps[0].Default);
    }

    [Fact]
    public void StrictStructuredContractsPreserveOptionalityAsNullable()
    {
        var graph = Graph(); var node = graph.Workflows[0].Steps[0]; node.Type = "llm.call";
        node.StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "detail", Required = false, Schema = new() { Type = "string" } }] });
        var unit = Unit("contracts", node.Key);
        var result = PlanningConstruction.Apply(graph, unit, PlanningConstruction.Values(graph.Workflows[0], unit), Preparation());
        var schema = result.Workflows[0].Steps[0].StructuredOutput!.Schema;
        Assert.True(schema.Properties[0].Required); Assert.True(schema.Properties[0].Schema.Nullable);
        Assert.Empty(PlanningContractValidation.ValidateSchema(PlanningGraphCompiler.ToJsonSchema(schema, Preparation()), strict: true));
        Assert.False(node.StructuredOutput.Schema.Properties[0].Required);
    }

    [Fact]
    public void ConfirmationUsesRuntimeScalarChoicesAndBooleanResult()
    {
        var graph = Graph(); var node = graph.Workflows[0].Steps[0]; node.Type = "human.input"; node.Purpose = "Allow the write?";
        graph.Workflows[0].Outputs = [new() { Name = "confirmed", Schema = new() { Type = "boolean" }, Value = new() { Kind = "output", Source = node.Key, Path = ["response"] } }];
        var unit = Unit("implementation", node.Key);
        var result = PlanningConstruction.Apply(graph, unit, PlanningConstruction.Values(graph.Workflows[0], unit), Preparation());
        var input = result.Workflows[0].Steps[0].Input;
        Assert.Equal(new[] { "approve", "reject" }, input.Members.Single(p => p.Name == "choices").Value.Items.Select(v => v.Text));
        Assert.Empty(PlanningExecutableValidation.Validate(result, Preparation()));
        new PlanningGraphCompiler().Compile(result, Preparation());
    }

    [Fact]
    public void PartitionHasBoundedNodesAndExplicitContractDependencies()
    {
        var workflow = Graph().Workflows[0];
        workflow.Steps = Enumerable.Range(0, 13).Select(i => new PlanningNode { Key = "node" + i }).ToList();
        var units = PlanningConstruction.Partition(workflow, 4);
        Assert.All(units, u => Assert.InRange(u.NodeKeys.Count, 0, 4));
        var contracts = units.Where(u => u.Kind == "contracts").Select(u => u.Key).ToArray();
        Assert.Equal(4, contracts.Length);
        Assert.All(units.Where(u => u.Kind == "implementation"), u => Assert.All(contracts, key => Assert.Contains(key, u.Dependencies)));
        Assert.Equal(13, units.Where(u => u.Kind == "implementation").SelectMany(u => u.NodeKeys).Distinct().Count());
    }

    [Fact]
    public void PublicOutputSchemaIsDerivedFromItsProducerInsteadOfModelAnnotations()
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Outputs[0].Schema = new() { Type = "integer" };
        var unit = Unit("outputs"); var response = PlanningConstruction.Values(workflow, unit);
        Assert.Null(response["outputs"]![workflow.Outputs[0].Name]!["schema"]);
        var result = PlanningConstruction.Apply(graph, unit, response, Preparation());
        Assert.Equal("string", result.Workflows[0].Outputs[0].Schema.Type);
        Assert.Empty(PlanningExecutableValidation.Validate(result, Preparation()));
        response["outputs"]![workflow.Outputs[0].Name]!["reference"] = "unknown";
        Assert.Throws<InvalidOperationException>(() => PlanningConstruction.Apply(graph, unit, response, Preparation()));
    }

    [Fact]
    public void OptionalProducerFieldsAreNotOfferedAsUnconditionalPublicReferences()
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Steps[0].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Required = false, Schema = new() { Type = "string" } }] };
        var unit = Unit("outputs");
        var invalid = PlanningConstruction.Values(workflow, unit);
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(invalid, PlanningConstruction.Schema(workflow, unit, Preparation()), unit));
        workflow.Outputs[0].Value.Path.Clear();
        var valid = PlanningConstruction.Values(workflow, unit);
        var result = PlanningConstruction.Apply(graph, unit, valid, Preparation());
        Assert.Equal("object", result.Workflows[0].Outputs[0].Schema.Type);
        Assert.False(result.Workflows[0].Outputs[0].Schema.Properties[0].Required);
    }

    [Fact]
    public async Task TemplateObjectsAndArraysLowerNestedReferencesAsExpressions()
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Inputs = [new() { Name = "name", Schema = new() { Type = "string" } }];
        workflow.Steps[0].Input = new() { Kind = "object", Members = [new("message", new()
        {
            Kind = "template", Text = "Context: {{payload}}", Members = [new("payload", new()
            {
                Kind = "object", Members = [new("name", new() { Kind = "input", Source = "name" }), new("nested", new()
                { Kind = "array", Items = [Str("ready"), new() { Kind = "object", Members = [new("again", new() { Kind = "input", Source = "name" })] }] })]
            })]
        })] };
        var yaml = new PlanningGraphCompiler().Compile(graph, Preparation());
        var compiled = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["name"] = "Ada" }, Ct);
        Assert.True(result.Success); Assert.Contains("Ada", result.Outputs!["message"]!.GetValue<string>());
        Assert.Contains("ready", result.Outputs["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("request_url")]
    [InlineData("adresse_demande")]
    public void InputPortsCannotBeMisclassifiedAsWorkflowReferences(string name)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; workflow.Inputs = [new() { Name = name }];
        workflow.Steps[0].Input = new() { Kind = "object", Members = [new("message", new()
        { Kind = "template", Text = "{{value}}", Members = [new("value", new() { Kind = "workflow", Source = name })] })] };
        Assert.Contains(PlanningExecutableValidation.Validate(graph, Preparation()), d => d.Code == "WORKFLOW_REFERENCE_INVALID");
        var unit = Unit("implementation", workflow.Steps[0].Key);
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(PlanningConstruction.Values(workflow, unit), PlanningConstruction.Schema(workflow, unit, Preparation()), unit));
    }

    [Fact]
    public async Task InvalidConstructionGetsTargetedRepairAndRetainsTheCandidateAcrossRestart()
    {
        var state = ApprovedSkeleton(); var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal(8192, request.MaxTokens); Assert.True(request.RequireOutputTokenLimit); Assert.True(request.DisableTransportRetries);
            var json = phase == "repair_unit" ? new JsonObject { ["changes"] = new JsonObject(), ["remove"] = new JsonArray("nodes/greeting/caseConditions") }
                : FakeRuntime.ConstructionResponse(request, phase);
            if (phase == "fragment_implementation") json["nodes"]!["greeting"]!["caseConditions"] = new JsonArray(new JsonObject { ["index"] = 0, ["when"] = null });
            return Task.FromResult(new LLMResponse { Json = json });
        } };
        var planner = new TypedWorkflowPlanner();
        for (var i = 0; i < 10 && !state.ConstructionUnits.Any(u => u.Status == "invalid"); i++) state = await Advance(planner, state, runtime);
        var invalid = Assert.Single(state.ConstructionUnits, u => u.Status == "invalid");
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.NotNull(invalid.Candidate);
        var contracts = state.ConstructionUnits.Single(u => u.Kind == "contracts"); var calls = contracts.Calls;
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        state = await Advance(new TypedWorkflowPlanner(), state, runtime);
        Assert.Equal("validated", state.ConstructionUnits.Single(u => u.Key == invalid.Key).Status);
        Assert.Equal(1, state.ConstructionUnits.Single(u => u.Key == invalid.Key).RepairCalls);
        Assert.Equal(calls, state.ConstructionUnits.Single(u => u.Kind == "contracts").Calls);
        Assert.Empty(state.Diagnostics); Assert.NotNull(state.ApprovedBehaviorHash);
    }

    [Fact]
    public async Task ExhaustedShapeRepairPausesWithoutLosingValidatedUnitsOrResettingUsage()
    {
        var state = ApprovedSkeleton(); state.Request.MaxRepairs = 1;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) => Task.FromResult(new LLMResponse
        { Json = phase is "fragment_implementation" or "repair_unit" ? new JsonObject() : FakeRuntime.ConstructionResponse(request, phase) }) };
        var planner = new TypedWorkflowPlanner();
        for (var i = 0; i < 10 && state.Status != PlanningStatus.Recovery; i++) state = await Advance(planner, state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.Null(state.Outcome);
        Assert.Equal(1, state.ConstructionUnits.Single(u => u.Kind == "implementation").RepairCalls);
        Assert.Contains(state.ConstructionUnits, u => u.Kind == "contracts" && u.Status == "validated");
        var total = state.ConstructionUnits.Sum(u => u.Calls);
        state = await planner.AdvanceAsync(state, new() { Kind = "retry", ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Equal(total, state.ConstructionUnits.Sum(u => u.Calls));
        Assert.Equal(1, state.ConstructionUnits.Single(u => u.Kind == "implementation").RepairCalls);
    }

    [Fact]
    public async Task ConfigureGenerationPreservesBehaviorAnswersAndRejectsStaleCommands()
    {
        var state = ApprovedSkeleton(); state.Status = PlanningStatus.Recovery;
        state.Answers.Add(new("Choose behavior", new() { ["answer"] = "accepted" })); state.ClarificationForms = 1;
        var approval = state.ApprovedBehaviorHash; var planner = new TypedWorkflowPlanner();
        var next = await planner.AdvanceAsync(state, new() { Kind = "configure_generation", ExpectedRevision = state.Revision, Generation = new() { Reasoning = "low" } }, new FakeRuntime(), Ct);
        Assert.Equal(approval, next.ApprovedBehaviorHash); Assert.Single(next.Answers); Assert.Equal(1, next.ClarificationForms);
        Assert.Single(next.GenerationHistory); Assert.Equal("low", next.Request.Options["generator"]?["reasoning"]?.GetValue<string>());
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(next, new() { Kind = "configure_generation", ExpectedRevision = state.Revision, Generation = new() }, new FakeRuntime(), Ct));
    }

    [Fact]
    public async Task OversizedSingleContractStopsBeforeDispatchWithoutInventingExhaustedRepair()
    {
        var state = ApprovedSkeleton(); state.Request.Prompt = new string('x', 10_000); state.Request.Generation.MaxInputTokensPerUnit = 512;
        var runtime = new FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("No model request may be dispatched.") };
        var planner = new TypedWorkflowPlanner();
        for (var i = 0; i < 8 && state.Status != PlanningStatus.Recovery; i++) state = await Advance(planner, state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "UNIT_CONTEXT_TOO_LARGE");
        Assert.DoesNotContain(state.Diagnostics, d => d.Code.Contains("EXHAUSTED", StringComparison.Ordinal));
        Assert.All(state.ConstructionUnits, u => { Assert.Equal(0, u.Calls); Assert.Equal(0, u.RepairCalls); });
    }

    [Fact]
    public async Task ADispatchFailureDoesNotReplaceTheRetainedCandidatesValidationFindings()
    {
        var state = ApprovedSkeleton(); var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            var value = FakeRuntime.ConstructionResponse(request, phase);
            if (phase == "fragment_implementation") value["unexpected"] = true;
            return Task.FromResult(new LLMResponse { Json = value });
        } };
        var planner = new TypedWorkflowPlanner();
        for (var i = 0; i < 8 && !state.ConstructionUnits.Any(u => u.Status == "invalid"); i++) state = await Advance(planner, state, runtime);
        var invalid = state.ConstructionUnits.Single(u => u.Status == "invalid"); var hash = invalid.CandidateHash;
        state.Request.Generation.MaxInputTokensPerUnit = 512;
        state = await Advance(planner, state, runtime);
        var retained = state.ConstructionUnits.Single(u => u.Key == invalid.Key);
        Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.Equal(hash, retained.CandidateHash);
        Assert.Contains(retained.Diagnostics, d => d.Code == "UNIT_RESPONSE_INVALID");
        Assert.Contains(retained.DispatchDiagnostics, d => d.Code == "UNIT_CONTEXT_TOO_LARGE");
        Assert.Contains(state.Diagnostics, d => d.Code == "UNIT_RESPONSE_INVALID");
        Assert.Contains(state.Diagnostics, d => d.Code == "UNIT_CONTEXT_TOO_LARGE");
        Assert.Equal(0, retained.RepairCalls);
    }

    [Fact]
    public async Task CatalogFingerprintChangeInvalidatesDependentCheckpointWithoutResettingCallCounts()
    {
        var state = ApprovedSkeleton(); var runtime = new FakeRuntime(); var planner = new TypedWorkflowPlanner();
        for (var i = 0; i < 8 && !state.ConstructionUnits.Any(u => u.Kind == "contracts" && u.Status == "validated"); i++) state = await Advance(planner, state, runtime);
        var before = state.ConstructionUnits.Single(u => u.Kind == "contracts").Calls;
        state.Preparation!.Fingerprint = "changed-contract";
        state = await Advance(planner, state, runtime);
        Assert.DoesNotContain(state.ConstructionUnits, u => u.Kind == "contracts" && u.Status == "validated");
        state = await Advance(planner, state, runtime);
        Assert.Equal(before, state.ConstructionUnits.Single(u => u.Kind == "contracts").Calls);
        Assert.Equal("validated", state.ConstructionUnits.Single(u => u.Kind == "contracts").Status);
    }

    [Fact]
    public async Task FinalRepairRevalidatesOlderUnitsBeforeUsingTheWholeGraphRepairPath()
    {
        var state = ApprovedSkeleton(); state.Graph = Graph(); state.RepairAttempt = 1;
        state.ConstructionUnits = PlanningConstruction.Partition(state.Graph.Workflows[0], 4);
        foreach (var unit in state.ConstructionUnits)
        {
            unit.ContractVersion = PlanningDataflow.ContractVersion - 1;
            unit.Candidate = PlanningConstruction.Values(state.Graph.Workflows[0], unit);
            unit.Calls = 1;
        }
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.NotEqual("repair", phase);
            return Task.FromResult(new LLMResponse { Json = FakeRuntime.ConstructionResponse(request, phase) });
        } };
        state = await Advance(new TypedWorkflowPlanner(), state, runtime);
        Assert.Equal(0, state.RepairAttempt);
        Assert.All(state.ConstructionUnits, unit => Assert.Equal(PlanningDataflow.ContractVersion, unit.ContractVersion));
        Assert.NotEqual(PlanningStatus.Failed, state.Status);
    }

    internal static PlanningSnapshot ApprovedSkeleton()
    {
        var state = Session(PlanningStatus.Generating); state.Preparation = Preparation(); state.BehaviorPlan = BehaviorPlan();
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation); return state;
    }
    private static Task<PlanningSnapshot> Advance(IWorkflowPlanner planner, PlanningSnapshot state, IPlanningRuntime runtime) => planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
}
