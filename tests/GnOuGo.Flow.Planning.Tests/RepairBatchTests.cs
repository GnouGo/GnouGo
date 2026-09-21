using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RepairBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonNode Context(LLMRequest request) => JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
    private static LLMResponse Correct(LLMRequest request) => new() { Json = new JsonObject {
        ["changes"] = new JsonArray(Context(request)["targets"]!.AsArray().Select(t => (JsonNode)new JsonObject {
            ["target"] = t!["id"]!.DeepClone(), ["replacement"] = new JsonObject { ["kind"] = "number", ["number"] = 3 }
        }).ToArray()) } };
    private static async Task<(PlanningSession State, TestRuntime Runtime)> InvalidInvocations(int count)
    {
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(1, """{"count":{"type":"number"}}""", ["count"]));
        var state = PlannerFixture.Session(); state.Request.MaxRepairAttempts = 6;
        state.Request.Generation = new() { MaxInputTokensPerRequest = 64_000, MaxOutputTokens = 32_768 };
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.ModelCalls = 1;
        state.IntentPlan = new() { Operations = Enumerable.Range(0, count).Select(i => (IntentOperation)new InvokeIntentOperation {
            Id = "measure_" + i, Capability = state.Catalog.Capabilities[0].Id,
            Arguments = [new("count", new() { Kind = "string", Text = "invalid" })] }).ToList() };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        state.Diagnostics = PlanningExecutableValidation.Validate(state.Graph, state.Catalog).ToList();
        runtime.Respond = Correct; return (state, runtime);
    }

    [Fact]
    public async Task IndependentTargetsUseThreeSmallBatchesAndRevalidateEverything()
    {
        var (state, runtime) = await InvalidInvocations(7);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal([3, 3, 1], runtime.Calls.Select(r => Context(r)["targets"]!.AsArray().Count));
        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"/operations/{i}/arguments/0/value"), runtime.Calls.SelectMany(r => Context(r)["targets"]!.AsArray().Select(t => t!["path"]!.ToString())));
        Assert.All(runtime.Calls, r => { Assert.Equal(8192, r.MaxTokens); Assert.True(PlanningJsonTransport.EstimateInputTokens(r.Prompt, r.StructuredOutputSchema!.AsObject()) <= 12_000); });
        Assert.Equal(3, state.RepairAttempts); Assert.Equal(4, state.ModelCalls); Assert.Null(state.ApprovedHash);
        Assert.Equal([4, 1, 0], runtime.Calls.Select(r => Context(r)["deferredTargetCount"]!.GetValue<int>()));
    }

    [Fact]
    public async Task DeferredEditsAreRejectedWithoutMutatingIntent()
    {
        var (state, runtime) = await InvalidInvocations(4);
        var deferred = PlanningCorrections.Targets(state).Last(); var before = PlanningJsonTransport.Intent(state.IntentPlan!).ToJsonString();
        runtime.Respond = r => {
            Assert.DoesNotContain(deferred.Id, r.StructuredOutputSchema!.ToJsonString());
            return new() { Json = new JsonObject { ["changes"] = new JsonArray(new JsonObject { ["target"] = deferred.Id, ["replacement"] = new JsonObject { ["kind"] = "number", ["number"] = 3 } }) } };
        };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(before, PlanningJsonTransport.Intent(state.IntentPlan!).ToJsonString());
        Assert.Contains(state.Diagnostics, d => d.Code == "INTENT_SCHEMA_INVALID"); Assert.Null(state.Yaml);
    }

    [Fact]
    public async Task UpstreamProducerIsCorrectedBeforeItsEarlierConsumer()
    {
        var (state, runtime) = await InvalidInvocations(2);
        ((InvokeIntentOperation)state.IntentPlan!.Operations[0]).Arguments = [new("count", new() { Kind = "result", Source = "measure_1", Path = ["value"] })];
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog!);
        // The consumer finding becomes obsolete once the producer is repaired.
        state.Diagnostics = [new("CAPABILITY_ARGUMENT_INVALID", "/operations/0/arguments/0/value", "Invalid upstream producer.", ValidationStage: "intent"),
            new("CAPABILITY_ARGUMENT_INVALID", "/operations/1/arguments/0/value", "Expected number.", ValidationStage: "intent")];
        runtime.Respond = r => { Assert.Equal("/operations/1/arguments/0/value", Assert.Single(Context(r)["targets"]!.AsArray())!["path"]!.ToString()); return Correct(r); };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls);
        Assert.Equal("result", ((InvokeIntentOperation)state.IntentPlan!.Operations[0]).Arguments[0].Value.Kind);
    }

    [Fact]
    public async Task TwoRepairDefaultStopsWithDeferredErrorsAndNeverApproves()
    {
        var (state, runtime) = await InvalidInvocations(7); state.Request.MaxRepairAttempts = new PlanningRequest().MaxRepairAttempts;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(2, state.RepairAttempts); Assert.Equal(3, state.ModelCalls);
        Assert.NotEmpty(state.Diagnostics); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task OversizedSingleTargetStopsBeforeReservationEvenWithLargeSessionAllowance()
    {
        var (state, runtime) = await InvalidInvocations(1);
        ((InvokeIntentOperation)state.IntentPlan!.Operations[0]).Arguments[0].Value.Text = new string('x', 40_000);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Empty(runtime.Calls); Assert.Equal(0, state.RepairAttempts); Assert.Equal(1, state.ModelCalls);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_INPUT_LIMIT" && d.Message.Contains("/operations/0/arguments/0/value") && d.Message.Contains("12000"));
    }

    [Fact]
    public async Task BatchShrinksWithoutDroppingSelectedEvidence()
    {
        var (state, runtime) = await InvalidInvocations(3);
        foreach (var op in state.IntentPlan!.Operations.Cast<InvokeIntentOperation>()) op.Arguments[0].Value.Text = new string('x', 14_000);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.True(runtime.Calls.Count >= 2);
        Assert.All(runtime.Calls, r => { Assert.True(PlanningJsonTransport.EstimateInputTokens(r.Prompt, r.StructuredOutputSchema!.AsObject()) <= 12_000); Assert.Contains(new string('x', 14_000), r.Prompt); });
    }

    [Fact]
    public async Task FocusedContextKeepsHostRulesAndOmitsUnrelatedContracts()
    {
        var (state, runtime) = await InvalidInvocations(1); state.Request.Policy.Instructions = "Host constraint marker";
        state.Catalog!.Capabilities.Add(new() { Id = "unrelated", Method = "unrelated", Description = "Expected number. schema binding output", InputSchema = new() { ["type"] = "object", ["description"] = new string('z', 60_000) }, OutputSchema = new() { ["type"] = "object" } });
        state.IntentPlan!.Inputs.Add(new("unused_private_input", new() { Type = "string" }, true));
        state = await PlannerFixture.RunAsync(runtime, state);
        var call = Assert.Single(runtime.Calls); Assert.DoesNotContain("unrelated", call.Prompt); Assert.DoesNotContain("unused_private_input", call.Prompt);
        Assert.Contains("Host constraint marker", call.Prompt); Assert.Equal(PlanningStatus.FinalReview, state.Status);
    }

    [Fact]
    public async Task UnissuedToolNameKeepsItsAllowedMatchesInRepairAdviceWithoutResolvingIt()
    {
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(6, """{"count":{"type":"number"}}""", ["count"]));
        var state = PlannerFixture.Session(); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.ModelCalls = 1;
        var expected = state.Catalog.Capabilities.Last();
        foreach (var capability in state.Catalog.Capabilities.Where(c => c != expected))
            capability.Description = "Read recent laboratory sensor temperature measurements with count and provenance.";
        var invoke = new InvokeIntentOperation { Id = "observe", Purpose = "Read recent laboratory sensor temperature measurements with count and provenance.",
            Capability = expected.Method, Arguments = [new("count", new() { Kind = "string", Text = "invalid" })] };
        state.IntentPlan = new() { Operations = [invoke] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        state.Diagnostics = PlanningExecutableValidation.Validate(state.Graph, state.Catalog).ToList();
        Assert.Contains(state.Diagnostics, d => d.Code == "CAPABILITY_UNKNOWN");
        var targets = PlanningCorrections.Batch(state);
        var context = Context(new() { Prompt = PlanningCorrections.Prompt(state, targets) });
        var advice = Assert.Single(context["alternatives"]!.AsArray())!["capabilities"]!.AsArray();
        Assert.Equal(expected.Id, advice[0]!["id"]!.ToString());
        Assert.Equal(4, advice.Count);
        Assert.Equal(expected.Method, invoke.Capability);
        Assert.Contains(PlanningExecutableValidation.Validate(state.Graph, state.Catalog), d => d.Code == "CAPABILITY_UNKNOWN");
        Assert.Empty(runtime.Calls);
        runtime.Respond = _ => new() { Json = new JsonObject { ["changes"] = new JsonArray(new JsonObject {
            ["target"] = Assert.Single(targets).Id,
            ["replacement"] = PlanningJsonTransport.Intent(new() { Operations = [new InvokeIntentOperation { Id = "observe", Capability = expected.Id,
                Arguments = [new("count", new() { Kind = "number", Number = 3 })] }] })["operations"]![0]!.DeepClone()
        }) } };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Single(runtime.Calls);
        Assert.Equal(1, state.RepairAttempts);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task ReservedLargeBatchReplaysItsExactSchemaTargetsAndOutputCeiling()
    {
        var (state, runtime) = await InvalidInvocations(4); var targets = PlanningCorrections.Targets(state);
        var prompt = PlanningCorrections.Prompt(state, targets);
        var start = prompt.IndexOf("\n{", StringComparison.Ordinal);
        var historical = JsonNode.Parse(prompt[start..])!; historical["historicalContext"] = new string('x', 50_000);
        var original = new LLMRequest { ClientRequestId = "original", Prompt = prompt[..start] + "\n" + historical.ToJsonString(), StructuredOutputSchema = PlanningCorrections.Schema(targets), MaxTokens = 32_768, Model = "test" };
        Assert.True(PlanningJsonTransport.EstimateInputTokens(original.Prompt, original.StructuredOutputSchema.AsObject()) > 12_000);
        state.Request.Generation.MaxInputTokensPerRequest = 512;
        state.PendingCall = new() { Id = "original", Purpose = "repair", Request = original }; state.ModelCalls = 2; state.RepairAttempts = 1;
        var wire = JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest);
        runtime.Respond = r => { Assert.Equal(wire, JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest)); Assert.Equal(4, Context(r)["targets"]!.AsArray().Count); return Correct(r); };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts);
    }

    [Fact]
    public async Task RestartBetweenBatchesPreservesCountersDiagnosticsAndApprovalInvalidation()
    {
        var (state, runtime) = await InvalidInvocations(4); state.ApprovedHash = "stale";
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(1, state.RepairAttempts); Assert.Equal(2, state.ModelCalls); Assert.NotEmpty(state.Diagnostics);
        Assert.Null(state.ApprovedHash); Assert.Null(state.Yaml);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        var restarted = new TestRuntime { Respond = Correct };
        state = await PlannerFixture.RunAsync(restarted, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.RepairAttempts); Assert.Equal(3, state.ModelCalls);
        Assert.Single(Context(Assert.Single(restarted.Calls))["targets"]!.AsArray());
        Assert.Equal(0, restarted.Discoveries);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public async Task RepairAllowanceNeverOverridesTheEightCallCeiling(int repairs)
    {
        var (state, runtime) = await InvalidInvocations(7); state.ModelCalls = 7; state.Request.MaxRepairAttempts = repairs;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(8, state.ModelCalls);
        Assert.Equal(1, state.RepairAttempts); Assert.Single(runtime.Calls); Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task LowerConfiguredLimitsStillApply()
    {
        var (state, runtime) = await InvalidInvocations(1);
        state.Request.Generation = new() { MaxInputTokensPerRequest = 512, MaxOutputTokens = 1024 };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Empty(runtime.Calls); Assert.Equal(0, state.RepairAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_INPUT_LIMIT" && d.Message.Contains("512"));
        (state, runtime) = await InvalidInvocations(1);
        state.Request.Generation = new() { MaxInputTokensPerRequest = 12_000, MaxOutputTokens = 1024 };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(1024, Assert.Single(runtime.Calls).MaxTokens); Assert.Equal(PlanningStatus.FinalReview, state.Status);
    }

    [Fact]
    public async Task PartialCorrectionKeepsDeferredAndUnchangedErrorsBlocking()
    {
        var (state, runtime) = await InvalidInvocations(4);
        runtime.Respond = request => { var response = Correct(request); var edits = response.Json!["changes"]!.AsArray(); while (edits.Count > 1) edits.RemoveAt(edits.Count - 1); return response; };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal(3, PlanningCorrections.Targets(state).Count); Assert.Equal(1, state.RepairAttempts);
        Assert.Equal(["/operations/1/arguments/0/value", "/operations/2/arguments/0/value", "/operations/3/arguments/0/value"], PlanningCorrections.Batch(state).Select(t => t.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedCorrectionPreservesEveryOutstandingErrorAndTargets(bool duplicate)
    {
        var (state, runtime) = await InvalidInvocations(4);
        var errors = state.Diagnostics.ToArray(); var before = PlanningJsonTransport.Intent(state.IntentPlan!).ToJsonString();
        runtime.Respond = request => {
            var response = Correct(request); var changes = response.Json!["changes"]!.AsArray();
            if (duplicate) changes[1] = changes[0]!.DeepClone(); else changes[1]!["target"] = "unknown";
            return response;
        };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(before, PlanningJsonTransport.Intent(state.IntentPlan!).ToJsonString());
        Assert.All(errors, error => Assert.Contains(error, state.Diagnostics));
        Assert.Equal(4, PlanningCorrections.Targets(state).Count); Assert.Equal(3, PlanningCorrections.Batch(state).Count);
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task TopologyReplacementIsAloneAndCannotBeCombinedWithNestedEdits()
    {
        var (state, _) = await InvalidInvocations(2);
        state.IntentPlan!.Subflows.Add(new("helper", [], [new CalculateIntentOperation { Id = "cycle", Value = new() { Kind = "number", Number = 1 }, After = ["cycle"] }], []));
        state.Diagnostics = [new("DEPENDENCY_CYCLE", "/operations/0", "Cycle.", ValidationStage: "intent"),
            new("DEPENDENCY_CYCLE", "/subflows/0/operations/0", "Cycle.", ValidationStage: "intent")];
        var group = Assert.Single(PlanningCorrections.Batch(state)); Assert.Equal("/operations", group.Path);
        var nested = new PlanningCorrections.Target("nested", "/operations/0", "operation", group.Fragment![0]!.DeepClone());
        var before = PlanningJsonTransport.Intent(state.IntentPlan).ToJsonString();
        Assert.Throws<PlanningResponseException>(() => PlanningCorrections.Apply(state, [group, nested], new JsonObject { ["changes"] = new JsonArray(
            new JsonObject { ["target"] = group.Id, ["replacement"] = group.Fragment.DeepClone() },
            new JsonObject { ["target"] = nested.Id, ["replacement"] = nested.Fragment!.DeepClone() }) }));
        Assert.Equal(before, PlanningJsonTransport.Intent(state.IntentPlan).ToJsonString());
    }

    [Fact]
    public async Task NestedScopesUseStableIntentOrderWithoutConflatingRepeatedIds()
    {
        var (state, _) = await InvalidInvocations(1);
        CalculateIntentOperation Bad() => new() { Id = "same", Value = new() { Kind = "compute", Text = "unknown + 1" } };
        state.IntentPlan = new() { Operations = [new ChooseIntentOperation { Id = "route", Condition = new() { Kind = "boolean", Boolean = true },
            Then = new([Bad()], new() { Kind = "number", Number = 1 }), Otherwise = new([Bad()], new() { Kind = "number", Number = 1 }) },
            new CleanupIntentOperation { Id = "cleanup", Operations = [Bad()] }],
            Subflows = [new("helper", [], [Bad()], [])] };
        // Deliberately reverse the diagnostic order; scope and intent position are authoritative.
        state.Diagnostics = new[] { "/subflows/0/operations/0/value", "/operations/1/operations/0/value", "/operations/0/otherwise/operations/0/value", "/operations/0/then/operations/0/value" }
            .Select(p => new PlanningDiagnostic("COMPUTATION_BINDING_INVALID", p, "Undeclared parameter.", ValidationStage: "intent")).ToList();
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog!);
        var batch = PlanningCorrections.Batch(state);
        Assert.Equal(["/operations/0/then/operations/0/value", "/operations/0/otherwise/operations/0/value", "/operations/1/operations/0/value"], batch.Select(t => t.Path));
        var context = Context(new() { Prompt = PlanningCorrections.Prompt(state, batch) });
        Assert.DoesNotContain("/subflows/0/operations/0", context["bindings"]!.ToJsonString());
    }

    [Fact]
    public async Task InputProducerPrecedesLoopCaptureAndSubflowOutputPrecedesCaller()
    {
        var (state, _) = await InvalidInvocations(1);
        state.IntentPlan = new() { Inputs = [new("items", new() { Type = "array", Items = new() { Type = "number" } }, Default: new() { Kind = "string", Text = "invalid" })],
            Operations = [new EachIntentOperation { Id = "repeat", Items = new() { Kind = "input", Source = "items" },
                Body = new([new CalculateIntentOperation { Id = "member", Value = new() { Kind = "item", Source = "repeat" } }], new() { Kind = "result", Source = "member" }) }] };
        state.Diagnostics = [new("SCHEMA_INVALID", "/operations/0/body/operations/0/value", "Missing source contract.", ValidationStage: "intent"),
            new("DEFAULT_INVALID", "/inputs/0/default", "Expected array.", ValidationStage: "intent")];
        Assert.Equal("/inputs/0", Assert.Single(PlanningCorrections.Batch(state)).Path);
        state.IntentPlan = new() { Operations = [new CallIntentOperation { Id = "call", Flow = "helper" }],
            Subflows = [new("helper", [], [new CalculateIntentOperation { Id = "producer", Value = new() { Kind = "compute", Text = "unknown + 1" } }], [new("result", new() { Kind = "result", Source = "producer" })])] };
        state.Diagnostics = [new("SCHEMA_INVALID", "/operations/0", "Unresolved result.", ValidationStage: "intent"),
            new("COMPUTATION_BINDING_INVALID", "/subflows/0/operations/0/value", "Undeclared parameter.", ValidationStage: "intent")];
        Assert.Equal("/subflows/0/operations/0/value", Assert.Single(PlanningCorrections.Batch(state)).Path);
    }

    [Fact]
    public async Task UnresolvedInvocationRetrievesByPurposeDespiteMalformedArguments()
    {
        var (state, _) = await InvalidInvocations(1); var wanted = state.Catalog!.Capabilities[0]; wanted.Description = "Estimate geometric volume";
        for (var i = 0; i < 12; i++) state.Catalog.Capabilities.Add(new() { Id = "distractor_" + i, Method = "generic", Description = "Invalid capability field requires correction" });
        var operation = (InvokeIntentOperation)state.IntentPlan!.Operations[0]; operation.Capability = null; operation.Purpose = "Estimate geometric volume";
        operation.Arguments = [new("unsupported", new() { Kind = "string", Text = "not a valid count" })];
        state.Diagnostics = [new("HOLE_UNRESOLVED", "/operations/0", "Invalid capability field requires correction", ValidationStage: "intent")];
        var context = Context(new() { Prompt = PlanningCorrections.Prompt(state, PlanningCorrections.Batch(state)) });
        var alternatives = Assert.Single(context["alternatives"]!.AsArray())!["capabilities"]!.AsArray();
        Assert.Equal(4, alternatives.Count); Assert.Equal(wanted.Id, alternatives[0]!["id"]!.ToString());
        Assert.Empty(context["contracts"]!.AsArray());
    }

    [Fact]
    public void ProjectedContractsRetainConstraintsAndReferencesWithoutUnrelatedSchemas()
    {
        var root = JsonNode.Parse("""{"type":"object","properties":{"value":{"$ref":"#/$defs/range"},"noise":{"type":"string","description":"unrelated"}},"$defs":{"range":{"type":"integer","minimum":2,"maximum":6},"unused":{"type":"boolean"}}}""")!.AsObject();
        var projected = PlanningCapabilityCards.ValueContract(root, ["value"]);
        Assert.Empty(projected["path"]!.AsArray()); Assert.DoesNotContain("unrelated", projected.ToJsonString()); Assert.DoesNotContain("unused", projected.ToJsonString());
        var schema = projected["schema"]!.AsObject();
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonValue.Create(4), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonValue.Create(8), schema));
        var composed = JsonNode.Parse("""{"allOf":[{"type":"object","properties":{"value":{"type":"number"}}},{"properties":{"value":{"minimum":5}}}]}""")!.AsObject();
        var retained = PlanningCapabilityCards.ValueContract(composed, ["value"]);
        Assert.True(JsonNode.DeepEquals(composed, retained["schema"])); Assert.Equal("value", retained["path"]![0]!.ToString());
    }

    [Fact]
    public async Task FixedCatalogArgumentsAreNotEditableRepairContracts()
    {
        var (state, _) = await InvalidInvocations(1); var capability = state.Catalog!.Capabilities[0];
        capability.InputSchema["properties"]!["hostKey"] = new JsonObject { ["type"] = "string" };
        capability.InputSchema["required"]!.AsArray().Add("hostKey");
        capability.RequestBindings.Add(new("/hostKey", JsonValue.Create("fixed")));
        var editable = PlanningCapabilityCards.EditableArguments(capability);
        Assert.Null(editable["properties"]!["hostKey"]); Assert.DoesNotContain("hostKey", editable["required"]!.ToJsonString());
        Assert.NotNull(capability.InputSchema["properties"]!["hostKey"]); Assert.Single(capability.RequestBindings);
    }

    [Fact]
    public async Task CalculationValueIsCorrectedBeforeItsResultDeclaration()
    {
        var (state, _) = await InvalidInvocations(1);
        state.IntentPlan = new() { Operations = [new CalculateIntentOperation { Id = "total", Value = new() { Kind = "compute", Text = "unknown + 1" }, ResultType = new() { Type = "object" } }] };
        state.Diagnostics = [new("SCHEMA_INVALID", "/operations/0/resultType", "Untyped object.", ValidationStage: "intent"),
            new("COMPUTATION_BINDING_INVALID", "/operations/0/value", "Undeclared parameter.", ValidationStage: "intent")];
        Assert.Equal("/operations/0/value", Assert.Single(PlanningCorrections.Batch(state)).Path);
    }

    [Fact]
    public async Task FocusedNeighborsIncludeOnlyReferencedResultFieldsAndControlAvailability()
    {
        var (state, _) = await InvalidInvocations(1); var cap = state.Catalog!.Capabilities[0];
        cap.OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"count":{"type":"integer","minimum":1},"other":{"type":"string","description":"unrelated payload"}},"required":["count"]}""")!.AsObject();
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation { Id = "source", Capability = cap.Id, Arguments = [new("count", new() { Kind = "number", Number = 1 })] },
            new ChooseIntentOperation { Id = "route", Condition = new() { Kind = "boolean", Boolean = false }, Then = new([new CalculateIntentOperation {
                Id = "consumer", Value = new() { Kind = "compute", Text = "unknown + n", Members = [new("n", new() { Kind = "result", Source = "source", Path = ["count"] })] }
            }], new() { Kind = "number", Number = 1 }), Otherwise = new([], new() { Kind = "number", Number = 1 }) }] };
        state.Diagnostics = [new("COMPUTATION_BINDING_INVALID", "/operations/1/then/operations/0/value", "Unknown parameter.", ValidationStage: "intent")];
        var context = Context(new() { Prompt = PlanningCorrections.Prompt(state, PlanningCorrections.Batch(state)) });
        var value = Assert.Single(context["bindings"]!["values"]!.AsArray())!;
        Assert.Equal(1, value["contract"]!["schema"]!["minimum"]!.GetValue<int>());
        Assert.DoesNotContain("unrelated payload", context.ToJsonString());
        Assert.False(Assert.Single(context["bindings"]!["controls"]!.AsArray())!["condition"]!["boolean"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedCorrectionDoesNotManufactureATopologyTarget(bool invalidJson)
    {
        var (state, runtime) = await InvalidInvocations(4); var errors = state.Diagnostics.ToArray();
        runtime.Respond = _ => invalidJson ? new() { Text = "{" } : new() { Json = new JsonObject() };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.All(errors, error => Assert.Contains(error, state.Diagnostics));
        Assert.Equal(4, PlanningCorrections.Targets(state).Count);
        Assert.All(PlanningCorrections.Batch(state), target => Assert.Equal("value", target.Shape));
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task FallbackCorrectionIncludesItsAuthoritativeResultContractWithoutAConsumer()
    {
        var (state, _) = await InvalidInvocations(1);
        var operation = (InvokeIntentOperation)state.IntentPlan!.Operations[0];
        operation.Fallback = new() { Kind = "string", Text = "invalid result" };
        state.Diagnostics = [new("FALLBACK_INVALID", "/operations/0/fallback", "Expected the declared result object.", ValidationStage: "intent")];
        var context = Context(new() { Prompt = PlanningCorrections.Prompt(state, PlanningCorrections.Batch(state)) });
        Assert.True(JsonNode.DeepEquals(state.Catalog!.Capabilities[0].OutputSchema, Assert.Single(context["contracts"]!.AsArray())!["result"]));
    }
}
