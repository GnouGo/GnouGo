using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactSelectionPresentationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FreshFactoringPreservesStrictDomainsAndEverySchemaConstraint()
    {
        var state = await Ready();
        var original = Unfactored(state);
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.DoesNotContain(Walk(original), node => node["description"] is JsonValue);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.All(Walk(schema).Where(node => node.ContainsKey("$ref")), node => Assert.Single(node));
        Assert.Equal(PlanningConstruction.Hash(state.Plan), schema["properties"]!["baseHash"]!["enum"]![0]!.ToString());
        Assert.True(schema.ToJsonString().Length < original.ToJsonString().Length);
        var valid = Response(state);
        valid["step"]!["inputs"] = new JsonArray(new JsonObject
        {
            ["kind"] = "field", ["port"] = "name", ["items"] = new JsonArray(new JsonObject
            { ["kind"] = "output", ["source"] = "accepted", ["port"] = "payload" })
        });
        Assert.Empty(PlanningContractValidation.ValidateInstance(valid, schema));
        Check(valid, true);
        var invalid = valid.DeepClone().AsObject(); invalid["baseHash"] = new string('0', 64); Check(invalid, false);
        invalid = valid.DeepClone().AsObject(); invalid["step"]!["requirementIds"] = new JsonArray("unknown"); Check(invalid, false);
        invalid = valid.DeepClone().AsObject(); invalid["step"]!["closeRequirementIds"] = new JsonArray("unknown"); Check(invalid, false);
        invalid = valid.DeepClone().AsObject(); invalid["step"]!["objective"] = new string('x', 513); Check(invalid, false);
        invalid = valid.DeepClone().AsObject(); invalid["step"]!["inputs"]![0]!["items"] = new JsonArray(); Check(invalid, false);
        invalid = valid.DeepClone().AsObject(); invalid["step"]!["inputs"]![0]!["port"] = new string('x', 97); Check(invalid, false);
        invalid = valid.DeepClone().AsObject(); invalid["step"]!["outputNames"] = new JsonArray("undeclared"); Check(invalid, false);
        invalid = valid.DeepClone().AsObject(); invalid["step"]!["invented"] = true; Check(invalid, false);
        var evidence = valid.DeepClone().AsObject(); evidence["step"] = null;
        evidence["evidenceRequests"] = new JsonArray("page:requirements:0"); Check(evidence, true);
        evidence["evidenceRequests"] = new JsonArray(new string('x', 2049)); Check(evidence, false);
        void Check(JsonObject value, bool accepted)
        {
            Assert.Equal(accepted, PlanningContractValidation.ValidateInstance(value, original).Count == 0);
            Assert.Equal(accepted, PlanningContractValidation.ValidateInstance(value, schema).Count == 0);
        }
    }

    [Fact]
    public async Task SelectionKeepsAllOriginalEvidenceAndInterfacesWithoutDuplicateIdentityOrBudget()
    {
        var state = await Ready();
        PlanningConstruction.LoadEvidence(state, ["page:requirements:0"]);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.Null(context["baseHash"]);
        Assert.Null(context["budget"]);
        Assert.True(JsonNode.DeepEquals(context["evidence"], state.Construction!.Evidence));
        var originalContext = context.DeepClone().AsObject();
        originalContext["baseHash"] = PlanningConstruction.Hash(state.Plan);
        originalContext["budget"] = new JsonObject { ["remainingCalls"] = PlanningModelCalls.RemainingCalls(state) };
        var prior = Measure(originalContext, Unfactored(state));
        var current = Measure(context, PlanningCompactConstruction.SelectionSchema(state));
        Assert.Equal(PlanningConstruction.Hash(state.Plan), current.BaselineHash);
        Assert.Equal(prior.BaselineHash, current.BaselineHash);
        Assert.Equal(current.CompleteRequestBytes, current.Components.Sum(component => component.SerializedBytes));
        Assert.True(current.CompleteRequestBytes < prior.CompleteRequestBytes - 100);
        Assert.InRange(current.EstimatedInputTokens, 1, 3000);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        foreach (var key in context.Select(pair => pair.Key))
            Assert.True(JsonNode.DeepEquals(context[key], originalContext[key]), key);
        var historical = PlannerFixture.Clone(state);
        historical.Construction!.Compact!.PresentationVersion = null;
        Assert.NotNull(PlanningCompactConstruction.SelectionContext(historical)["baseHash"]);
        Assert.NotNull(PlanningCompactConstruction.SelectionContext(historical)["budget"]);
        Assert.True(JsonNode.DeepEquals(PlanningCompactConstruction.SelectionSchema(historical),
            PlanningCompactConstruction.SelectionSchema(PlannerFixture.Clone(historical))));

        PlanningRequestMeasurement Measure(JsonObject input, JsonObject schema)
        {
            var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state),
                input, schema, PlanningCompactConstruction.SelectPurpose);
            request.Provider = "OpenAi"; request.Model = "retained-model"; request.StructuredOutputStrict = true;
            request.UseBackgroundMode = true; request.MaxTokens = 8192;
            request.ClientRequestId = new string('s', 64) + ":9:" + new string('h', 64);
            return PlanningRequestMeasurements.Measure(PlanningGenerationPolicy.Apply(request, state.Request.Generation),
                PlanningCompactConstruction.SelectPurpose, 3000);
        }
    }

    [Fact]
    public void MeasurementUsesOnlyAnExactSingletonSelectionEnumAndPreservesContextPrecedence()
    {
        var hash = new string('a', 64);
        var request = new LLMRequest { Prompt = "Choose a step.\n{}",
            StructuredOutputSchema = PlanningSchemas.Object(("baseHash", PlanningSchemas.Enum(hash))) };
        var original = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var measurement = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, 3000);
        Assert.Equal(hash, measurement.BaselineHash);
        Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(component => component.SerializedBytes));
        Assert.Equal(original, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        Assert.Null(PlanningRequestMeasurements.Measure(request, PlanningConstruction.DraftPurpose, 8000).BaselineHash);
        request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"] = new JsonArray(hash, "another");
        Assert.Null(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, 3000).BaselineHash);
        request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"] = new JsonArray(42);
        Assert.Null(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, 3000).BaselineHash);
        request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"] = new JsonArray(hash);
        request.Prompt = "Choose a step.\n{\"baseHash\":\"retained-context\"}";
        Assert.Equal("retained-context", PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, 3000).BaselineHash);
    }

    [Fact]
    public async Task SavedUnfactoredFreshSelectionResumesItsExactContextAndSchema()
    {
        var state = await Ready();
        var context = PlanningCompactConstruction.SelectionContext(state);
        context["baseHash"] = PlanningConstruction.Hash(state.Plan);
        context["budget"] = new JsonObject { ["remainingCalls"] = PlanningModelCalls.RemainingCalls(state) };
        var request = PlanningGenerationPolicy.Apply(PlanningCompactConstruction.Request(
            PlanningCompactConstruction.SelectGuidance(state), context, Unfactored(state), PlanningCompactConstruction.SelectPurpose), state.Request.Generation);
        request.StructuredOutputStrict = true;
        request.ClientRequestId = state.Request.SessionId + ":" + (state.ModelCalls + 1) + ":" +
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        state.ModelCalls++;
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = PlanningCompactConstruction.SelectPurpose, Request = request };
        state = PlannerFixture.Clone(state);
        var exact = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = state.ModelCalls;
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = Response(state) } };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(exact, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(["req_0"], state.Construction!.Compact!.CurrentStep!.RequirementIds);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task SelectionAdmissionReducesOptionalNavigationWithoutChangingItsChoicesOrOriginals(int presentation)
    {
        var state = await Ready();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        state.Plan = new() { Root = new() { Tasks = Enumerable.Range(0, 4).Select(index => new PlanTask
        {
            Id = "producer_" + index, Kind = "value", Objective = new string((char)('a' + index), 220 - index * 30),
            Outputs = [new("number", new() { Kind = "number", Number = index })]
        }).ToList() } };
        state.Request.Generation.MaxInputTokensPerRequest = 32_000;
        state.Request.Options["construction_limits"]!["phasePolicy"]!["navigation"]!["maxInputTokens"] = 12_000;
        state.Construction.Evidence = new JsonObject { ["task:producer_0"] = PlanningConstruction.TaskNode(state.Plan.Root.Tasks[0]) };
        var full = Request();
        var originalContext = Context(full);
        Assert.Null(originalContext["nextDecision"]?["localComputations"]); // An absent catalog is unknown, not a grant or denial.
        var originalSchema = full.StructuredOutputSchema!.ToJsonString();
        var estimate = Complete(full).EstimatedInputTokens;
        state.Request.Generation.MaxInputTokensPerRequest = checked((int)estimate - 25);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var fitted = Request();
        var measured = Complete(fitted);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var context = Context(fitted);
        Assert.True(JsonNode.DeepEquals(originalContext["requirements"], context["requirements"]));
        Assert.True(JsonNode.DeepEquals(originalContext["globalConstraints"], context["globalConstraints"]));
        Assert.True(JsonNode.DeepEquals(originalContext["evidence"], context["evidence"]));
        Assert.Equal(originalSchema, fitted.StructuredOutputSchema!.ToJsonString());
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        output.WriteLine($"presentation={presentation}; original_tokens={estimate}; limit={state.Request.Generation.MaxInputTokensPerRequest}; result_tokens={measured.EstimatedInputTokens}; result_bytes={measured.CompleteRequestBytes}");
        if (presentation == 11)
        {
            Assert.InRange(measured.EstimatedInputTokens, 1, state.Request.Generation.MaxInputTokensPerRequest);
            var first = context["interfaces"]![0]!;
            Assert.Null(first["objective"]);
            Assert.True(first["objectiveOmitted"]!.GetValue<bool>());
            Assert.Equal("task:producer_0", first["evidence"]!.ToString());
            Assert.NotNull(context["interfaces"]![1]!["objective"]);
        }
        else Assert.True(measured.EstimatedInputTokens > state.Request.Generation.MaxInputTokensPerRequest);
        Assert.Equal(JsonSerializer.Serialize(fitted, PlanningJsonContext.Default.LLMRequest),
            JsonSerializer.Serialize(Request(), PlanningJsonContext.Default.LLMRequest));
        state.Request.Generation.MaxInputTokensPerRequest = 512;
        var beforeStop = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var oversized = Request();
        Assert.True(Complete(oversized).EstimatedInputTokens > 512);
        Assert.True(JsonNode.DeepEquals(originalContext["requirements"], Context(oversized)["requirements"]));
        Assert.True(JsonNode.DeepEquals(originalContext["evidence"], Context(oversized)["evidence"]));
        Assert.Equal(beforeStop, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        LLMRequest Request() => PlanningCompactConstruction.Request(state, PlanningCompactConstruction.SelectGuidance(state),
            PlanningCompactConstruction.SelectionContext(state), PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
        PlanningRequestMeasurement Complete(LLMRequest request) => PlanningRequestMeasurements.Measure(
            PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject()),
            PlanningCompactConstruction.SelectPurpose, 3000);
        static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    }

    private static JsonObject Unfactored(PlanningSession state)
    {
        var historical = PlannerFixture.Clone(state);
        historical.Construction!.Compact!.PresentationVersion = null;
        var schema = PlanningCompactConstruction.SelectionSchema(historical);
        var step = schema["properties"]!["step"]!["anyOf"]![0]!["properties"]!;
        foreach (var field in new[] { "requirementIds", "closeRequirementIds" })
            step[field]!["items"] = PlanningSchemas.Enum("req_0");
        return schema;
    }
    private static IEnumerable<JsonObject> Walk(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var child in obj.SelectMany(pair => Walk(pair.Value))) yield return child;
        }
        else if (node is JsonArray array)
            foreach (var child in array.SelectMany(Walk)) yield return child;
    }
    private static JsonObject Response(PlanningSession state) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["evidenceRequests"] = null, ["clarifications"] = null,
        ["step"] = new JsonObject
        {
            ["scope"] = "/root", ["kind"] = "value", ["objective"] = "Preserve the original supplied values.",
            ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(), ["groupId"] = null,
            ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray(),
            ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray()
        }
    };
    private static async Task<PlanningSession> Ready()
    {
        const string objective = "Preserve the exact original supplied values and their ordering.";
        var runtime = new TestRuntime { Respond = (request, _) => new() { Json = new JsonObject
        {
            ["sourceId"] = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = objective, ["kind"] = "requirement", ["interfaces"] = "none" })
        } } };
        var state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.CapturedPresentation(new() { Request = new()
        { TenantId = "selection-presentation", Prompt = objective, Options = new() { ["automatic_correction"] = true } } }),
            new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        return state;
    }
}
