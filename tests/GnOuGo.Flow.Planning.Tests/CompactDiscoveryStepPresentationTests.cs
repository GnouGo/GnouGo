using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactDiscoveryStepPresentationTests
{
    [Fact]
    public void DiscoveryKeepsExactDecisionDataAndSemanticBindingsWithoutDraftAuthority()
    {
        var state = State();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.InputTaskIds = ["observed"];
        step.DependencyTaskIds = ["authorized"];
        step.OperationIds = ["retained_producer"];
        step.SchemaPointers = ["/input/properties/data"];
        step.Inputs.Add(new() { Kind = "object", Members =
        [
            new("zero", new() { Kind = "number", Number = 0 }),
            new("false", new() { Kind = "boolean", Boolean = false }),
            new("null", new() { Kind = "null" }),
            new("empty", new() { Kind = "array" }),
            new("source", new() { Kind = "output", Source = "observed", Port = "records" }),
            new("text", new() { Kind = "string", Text = "Exact \"value\" é🙂" })
        ] });
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var view = context["step"]!.AsObject();
        foreach (var field in new[] { "id", "scope", "kind", "objective", "expectedOutputs",
            "inputTaskIds", "dependencyTaskIds", "operationIds", "schemaPointers" })
            Assert.True(JsonNode.DeepEquals(Node(step)[field], view[field]), field);
        var inputs = view["inputs"]!.AsArray();
        Assert.Equal(step.Inputs.Count, inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
            Assert.Equal(JsonSerializer.Serialize(step.Inputs[index], PlanningJsonContext.Default.TaskValue),
                JsonSerializer.Serialize(inputs[index]!.Deserialize(PlanningJsonContext.Default.TaskValue),
                    PlanningJsonContext.Default.TaskValue));
        Assert.Equal(2, inputs[0]!.AsObject().Count);
        foreach (var field in new[] { "baseHash", "requirementIds", "closeRequirementIds", "outputNames", "discoveryComplete" })
            Assert.False(view.ContainsKey(field), field);
        Assert.Equal("req_exact", context["requirements"]![0]!["id"]!.ToString());
        Assert.Equal(state.Requirements!.Outcomes[0].Description, context["requirements"]![0]!["original"]!.ToString());
        Assert.Equal(state.Construction.Compact.Sources[1].Text, context["globalConstraints"]![0]!["text"]!.ToString());
        Assert.Equal(state.Request.Policy.Instructions, context["instructions"]!.ToString());
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void HistoricalPresentationKeepsEverySerializedStepField(int? version)
    {
        var state = State();
        state.Construction!.Compact!.PresentationVersion = version;
        var original = Node(state.Construction.Compact.CurrentStep!);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.True(JsonNode.DeepEquals(original, context["step"]));
        Assert.True(context["step"]!["inputs"]![0]!.AsObject().ContainsKey("number"));
        Assert.NotNull(context["step"]!["baseHash"]);
    }

    [Fact]
    public void LongOriginalRequirementsFitTheCompleteDiscoveryEnvelopeWithoutTruncation()
    {
        var state = State();
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var compact = Complete(state, context);
        var compactMeasurement = PlanningRequestMeasurements.Measure(compact, PlanningCompactConstruction.DiscoveryPurpose, 3000);
        var fullContext = context.DeepClone().AsObject();
        fullContext["step"] = Node(state.Construction!.Compact!.CurrentStep!);
        var fullMeasurement = PlanningRequestMeasurements.Measure(Complete(state, fullContext),
            PlanningCompactConstruction.DiscoveryPurpose, 3000);
        Assert.True(fullMeasurement.EstimatedInputTokens > 3000,
            $"Full selected-step request should reproduce excess: {fullMeasurement.EstimatedInputTokens}.");
        Assert.True(compactMeasurement.EstimatedInputTokens <= 3000,
            $"Complete compact request is {compactMeasurement.CompleteRequestBytes} bytes/{compactMeasurement.EstimatedInputTokens} tokens; " +
            string.Join(", ", compactMeasurement.Components.Select(component => $"{component.Component}={component.SerializedBytes}")));
        Assert.Equal(compactMeasurement.CompleteRequestBytes, compactMeasurement.Components.Sum(component => component.SerializedBytes));
        Assert.True(JsonNode.DeepEquals(context["requirements"], fullContext["requirements"]));
        Assert.True(JsonNode.DeepEquals(context["globalConstraints"], fullContext["globalConstraints"]));
        Assert.True(JsonNode.DeepEquals(context["providerRequests"], fullContext["providerRequests"]));
        Assert.True(JsonNode.DeepEquals(compact.StructuredOutputSchema, Complete(state, fullContext).StructuredOutputSchema));
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task AlreadyIssuedFullStepRequestConsumesItsExactReceiptWithoutNewAccounting()
    {
        var state = State();
        state.Requirements!.Outcomes[0] = new("req_exact", "Read the authorized original record.");
        state.Construction!.Compact!.CurrentStep!.Objective = "Read the authorized original record.";
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        context["step"] = Node(state.Construction.Compact.CurrentStep);
        var issued = Complete(state, context);
        Assert.InRange(PlanningRequestMeasurements.Measure(issued, PlanningCompactConstruction.DiscoveryPurpose, 3000).EstimatedInputTokens, 1, 3000);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued };
        state.ModelCalls = 1;
        var saved = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var original = JsonSerializer.Serialize(state.Construction.Compact.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var receipt = new LLMResponse { Json = new JsonObject
        {
            ["operationIds"] = null, ["discoveryRequests"] = null, ["clarifications"] = null,
            ["reselectStep"] = null, ["evidenceRequests"] = new JsonArray("page:sources:0")
        } };
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose);
            Assert.Equal(saved, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return receipt;
        } };
        var next = Complete(state, PlanningCompactConstruction.DiscoveryContext(state));
        Assert.NotEqual(saved, JsonSerializer.Serialize(next, PlanningJsonContext.Default.LLMRequest));
        state = PlannerFixture.Clone(state);
        var result = await PlanningModelCalls.CallAsync(state, runtime, PlanningCompactConstruction.DiscoveryPurpose,
            next.Prompt, next.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, 8192);
        Assert.True(JsonNode.DeepEquals(receipt.Json, result));
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(runtime.Calls);
        Assert.Null(state.PendingCall);
        Assert.Equal(original, JsonSerializer.Serialize(state.Construction!.Compact!.CurrentStep,
            PlanningJsonContext.Default.PlanningConstructionStep));
    }

    private static JsonObject Node(PlanningConstructionStep step) =>
        JsonSerializer.SerializeToNode(step, PlanningJsonContext.Default.PlanningConstructionStep)!.AsObject();

    private static LLMRequest Complete(PlanningSession state, JsonObject context)
    {
        var presentation = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions,
            context, PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose);
        return PlanningGenerationPolicy.Apply(new()
        {
            Prompt = presentation.Prompt, StructuredOutputSchema = presentation.StructuredOutputSchema,
            StructuredOutputStrict = true, UseBackgroundMode = true, MaxTokens = 8192,
            Provider = "OpenAi", Model = "gpt-5.5-2026-04-24",
            ClientRequestId = "compact-discovery-presentation:5:" + new string('a', 64)
        }, state.Request.Generation);
    }

    private static PlanningSession State()
    {
        var requirement = string.Concat(Enumerable.Repeat("Retain every original record and its exact identity. ", 24));
        var global = string.Concat(Enumerable.Repeat("Use only the authorized local workspace and preserve original provenance. ", 7));
        var state = new PlanningSession
        {
            IntentVersion = 3,
            Request = new()
            {
                TenantId = "compact-discovery-presentation", SessionId = "compact-discovery-presentation",
                Prompt = requirement + global, Generation = new("medium", 32000, 8192),
                Policy = new() { Instructions = "Never expand permissions." },
                Options = new() { ["construction_version"] = 2, ["construction_presentation_version"] = 2 }
            },
            Requirements = new() { Outcomes = [new("req_exact", requirement)] },
            Construction = new()
            {
                Version = 2, Requirements = [new() { Id = "req_exact" }],
                Compact = new()
                {
                    PresentationVersion = 2,
                    Sources = [new() { Id = "source_requirement", Version = new string('b', 64), Text = requirement },
                        new() { Id = "source_global", Version = new string('c', 64), Text = global }],
                    RequirementSources = new() { ["req_exact"] = ["source_requirement"] },
                    GlobalSourceIds = ["source_global"],
                    CurrentStep = new()
                    {
                        Id = "selected_step", BaseHash = new string('d', 64), Scope = "/root", Kind = "operation",
                        Objective = string.Concat(Enumerable.Repeat("Read the complete authorized original records. ", 11)),
                        RequirementIds = ["req_exact"], Inputs = [new() { Kind = "input", Source = "requestedSource" },
                            new() { Kind = "input", Source = "workspaceRoot" }],
                        ExpectedOutputs = ["Complete observed records with exact identities and source provenance."],
                        CloseRequirementIds = ["req_exact"], OutputNames = ["result"]
                    }
                }
            },
            Catalog = new() { AllowedStepTypes = ["mcp.call"] },
            Discovery = new()
            {
                Sources = [new("provider_" + new string('e', 24), new string('P', 192)),
                    new("provider_" + new string('f', 24), new string('Q', 192))]
            }
        };
        return state;
    }
}
