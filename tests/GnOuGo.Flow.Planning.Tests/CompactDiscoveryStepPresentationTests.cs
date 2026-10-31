using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

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
        Assert.Null(context["scopeGroup"]);
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
    [InlineData(false)]
    [InlineData(true)]
    public void DiscoveryRetainsTheActualIsolatedGroupInterfaceAndUnreviewedOpeningIntent(bool nestedControl)
    {
        var state = GroupState(nestedControl);
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var group = context["scopeGroup"]!;
        Assert.True(JsonNode.DeepEquals(PlanningCompactConstruction.GroupInterface(state, "worker"), group));
        Assert.True(JsonNode.DeepEquals(PlanningCompactConstruction.Context(state)["scopeGroup"], group));
        Assert.Equal("worker", group["id"]!.ToString());
        Assert.True(group["open"]!.GetValue<bool>());
        Assert.Equal("accepted_declarations", group["basis"]!.ToString());
        Assert.Null(group["outputs"]); // Existing exports of an open group are not consumable results.
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(state.Plan!.Groups[0].Inputs,
            PlanningJsonContext.Default.ListTaskInput), group["inputs"]));
        Assert.True(group["inputs"]![1]!["type"]!["nullable"]!.GetValue<bool>());
        Assert.False(group["inputs"]![1]!["required"]!.GetValue<bool>());
        Assert.Equal("preserve", group["inputs"]![1]!["type"]!["fields"]![0]!["type"]!["enum"]![0]!.ToString());
        var opening = state.Construction!.Checkpoints[0].Step!;
        Assert.Equal(opening.Objective, group["intent"]!["objective"]!.ToString());
        Assert.Equal(opening.ExpectedOutputs, group["intent"]!["expectedOutputs"]!.AsArray().Select(value => value!.ToString()));
        Assert.Equal(opening.RequirementIds, group["intent"]!["requirementIds"]!.AsArray().Select(value => value!.ToString()));
        Assert.Equal(nestedControl ? "task:nested" : null, context["scopeOwner"]?["address"]?.ToString());
        Assert.DoesNotContain("root_only_secret", context.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(state.Construction.Checkpoints[0].Reviews);
        Assert.True(state.Construction.Checkpoints[0].Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Null(state.Construction.Compact!.CurrentStep!.Kind);
        var request = Complete(state, context, out var requestAddresses);
        var withoutGroup = context.DeepClone().AsObject(); withoutGroup.Remove("scopeGroup");
        var withoutGroupRequest = Complete(state, withoutGroup, out var withoutGroupAddresses);
        Assert.True(JsonNode.DeepEquals(HostSchema(withoutGroupRequest, withoutGroupAddresses), HostSchema(request, requestAddresses)));
        var limit = PlanningConstructionInputPolicy.EffectiveLimit(state.Request, PlanningCompactConstruction.DiscoveryPurpose);
        var measured = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, limit);
        Assert.InRange(measured.EstimatedInputTokens, 1, limit);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task MandatoryGroupContextCannotBypassCompleteRequestAdmission()
    {
        var state = GroupState(nestedControl: false);
        state.Construction!.Checkpoints[0].Step!.Objective = new string('x', 30000);
        var request = Complete(state, PlanningCompactConstruction.DiscoveryContext(state));
        var limit = PlanningConstructionInputPolicy.EffectiveLimit(state.Request, PlanningCompactConstruction.DiscoveryPurpose);
        var measured = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, limit);
        Assert.True(measured.EstimatedInputTokens > limit);
        Assert.Contains(state.Construction.Checkpoints[0].Step!.Objective, request.Prompt, StringComparison.Ordinal);
        var plan = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (_, _) => throw new InvalidOperationException("Oversized discovery must stop before dispatch.") };
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningCompactConstruction.DiscoveryPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, 8192));
        Assert.Equal("MODEL_INPUT_LIMIT", error.Code);
        Assert.Empty(runtime.Calls);
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Null(state.Construction.Compact!.CurrentStep!.Kind);
    }


    [Fact]
    public void LongOriginalRequirementsFitTheCompleteDiscoveryEnvelopeWithoutTruncation()
    {
        var state = State();
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var compact = Complete(state, context, out var compactAddresses);
        var limit = PlanningConstructionInputPolicy.EffectiveLimit(state.Request, PlanningCompactConstruction.DiscoveryPurpose);
        var compactMeasurement = PlanningRequestMeasurements.Measure(compact, PlanningCompactConstruction.DiscoveryPurpose, limit);
        var fullContext = context.DeepClone().AsObject();
        fullContext["step"] = Node(state.Construction!.Compact!.CurrentStep!);
        var full = Complete(state, fullContext, out var fullAddresses);
        var fullMeasurement = PlanningRequestMeasurements.Measure(full, PlanningCompactConstruction.DiscoveryPurpose, limit);
        Assert.True(fullMeasurement.EstimatedInputTokens > compactMeasurement.EstimatedInputTokens,
            $"Selected-step projection must remove duplicated authority metadata: {fullMeasurement.EstimatedInputTokens}/{compactMeasurement.EstimatedInputTokens}.");
        Assert.True(compactMeasurement.EstimatedInputTokens <= limit,
            $"Complete compact request is {compactMeasurement.CompleteRequestBytes} bytes/{compactMeasurement.EstimatedInputTokens} tokens; " +
            string.Join(", ", compactMeasurement.Components.Select(component => $"{component.Component}={component.SerializedBytes}")));
        Assert.Equal(compactMeasurement.CompleteRequestBytes, compactMeasurement.Components.Sum(component => component.SerializedBytes));
        Assert.True(JsonNode.DeepEquals(context["requirements"], fullContext["requirements"]));
        Assert.True(JsonNode.DeepEquals(context["globalConstraints"], fullContext["globalConstraints"]));
        Assert.True(JsonNode.DeepEquals(context["providerRequests"], fullContext["providerRequests"]));
        Assert.True(JsonNode.DeepEquals(HostSchema(compact, compactAddresses), HostSchema(full, fullAddresses)));
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
        var issued = Complete(state, context, out var evidenceAddresses);
        var limit = PlanningConstructionInputPolicy.EffectiveLimit(state.Request, PlanningCompactConstruction.DiscoveryPurpose);
        Assert.InRange(PlanningRequestMeasurements.Measure(issued, PlanningCompactConstruction.DiscoveryPurpose, limit).EstimatedInputTokens, 1, limit);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued, EvidenceAddresses = evidenceAddresses };
        state.ModelCalls = 1;
        var saved = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var original = JsonSerializer.Serialize(state.Construction.Compact.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var receipt = new LLMResponse { Json = new JsonObject
        {
            ["operationIds"] = null, ["discoveryRequests"] = null, ["clarifications"] = null,
            ["reselectStep"] = null, ["evidenceRequests"] = new JsonArray("page:sources:0")
        } };
        receipt.Json = PlanningCorpus.Transport(TestRuntime.WireEvidence(receipt.Json!, evidenceAddresses), issued.StructuredOutputSchema!.AsObject(), issued.StructuredOutputSchema.AsObject());
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(receipt.Json, issued.StructuredOutputSchema.AsObject()));
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose);
            Assert.Equal(saved, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return receipt;
        } };
        runtime.CaptureIssued(state.PendingCall);
        var next = Complete(state, PlanningCompactConstruction.DiscoveryContext(state));
        Assert.NotEqual(saved, JsonSerializer.Serialize(next, PlanningJsonContext.Default.LLMRequest));
        state = PlannerFixture.Clone(state);
        var result = await PlanningModelCalls.CallAsync(state, runtime, PlanningCompactConstruction.DiscoveryPurpose,
            next.Prompt, next.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, 8192);
        Assert.True(JsonNode.DeepEquals(PlanningModelCalls.NormalizeEvidenceResponse(receipt.Json!, evidenceAddresses), result));
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(runtime.Calls);
        Assert.Null(state.PendingCall);
        Assert.Equal(original, JsonSerializer.Serialize(state.Construction!.Compact!.CurrentStep,
            PlanningJsonContext.Default.PlanningConstructionStep));
    }

    private static JsonObject Node(PlanningConstructionStep step) =>
        JsonSerializer.SerializeToNode(step, PlanningJsonContext.Default.PlanningConstructionStep)!.AsObject();

    private static LLMRequest Complete(PlanningSession state, JsonObject context) => Complete(state, context, out _);

    private static LLMRequest Complete(PlanningSession state, JsonObject context, out PlanningEvidenceAddresses? evidenceAddresses)
    {
        var presentation = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
            context, PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose);
        return PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose,
            presentation.Prompt, presentation.StructuredOutputSchema!.AsObject(), 8192, null, out evidenceAddresses);
    }

    private static PlanningSession State()
    {
        var requirement = string.Concat(Enumerable.Repeat("Retain every original record and its exact identity. ", 24));
        var global = string.Concat(Enumerable.Repeat("Use only the authorized local workspace and preserve original provenance. ", 7));
        var state = new PlanningSession
        {
            Request = new()
            {
                TenantId = "compact-discovery-presentation", SessionId = "compact-discovery-presentation",
                Prompt = requirement + global, Generation = new("medium", 32000, 8192),
                Policy = new() { Instructions = "Never expand permissions." }
            },
            Requirements = new() { Outcomes = [new("req_exact", requirement)] },
            Construction = new()
            {
                Requirements = [new() { Id = "req_exact" }],
                Compact = new()
                {
                    Sources = [new() { Id = "source_requirement", Version = new string('b', 64), Text = requirement },
                        new() { Id = "source_global", Version = new string('c', 64), Text = global }],
                    RequirementSources = new() { ["req_exact"] = ["source_requirement"] },
                    GlobalSourceIds = ["source_global"]
                }
            },
            Catalog = new() { AllowedStepTypes = ["mcp.call"] },
            Discovery = new()
            {
                Sources = [new("provider_" + new string('e', 24), new string('P', 192)),
                    new("provider_" + new string('f', 24), new string('Q', 192))]
            }
        };
        PlanningSessionCreation.Capture(state);
        state.Construction!.Compact!.CurrentStep = new()
        {
            Id = "selected_step", BaseHash = new string('d', 64), Scope = "/root", Kind = "operation",
            Objective = string.Concat(Enumerable.Repeat("Read the complete authorized original records. ", 11)),
            RequirementIds = ["req_exact"], Inputs = [new() { Kind = "input", Source = "requestedSource" },
                new() { Kind = "input", Source = "workspaceRoot" }],
            ExpectedOutputs = ["Complete observed records with exact identities and source provenance."],
            CloseRequirementIds = ["req_exact"], OutputNames = ["result"]
        };
        return PlannerFixture.Clone(state);
    }

    private static JsonObject HostSchema(LLMRequest request, PlanningEvidenceAddresses? addresses)
    {
        var schema = PlanningModelCalls.CanonicalEvidenceRequest(new()
        {
            Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose,
            Request = request, EvidenceAddresses = addresses
        }).StructuredOutputSchema!.AsObject();
        return schema["$defs"] is JsonObject ? PlanningSchemas.Compact(schema, preserveDescriptions: true) : schema;
    }

    private static PlanningSession GroupState(bool nestedControl)
    {
        // This is accepted technical navigation with pending business review,
        // not a manufactured reviewed checkpoint or callable completed group.
        var state = State();
        var group = new TaskGroup
        {
            Id = "worker", Inputs = [new() { Name = "query", Type = new() { Kind = "string" } },
                new() { Name = "options", Required = false, Default = new() { Kind = "null" },
                    Type = new() { Kind = "object", Nullable = true, Fields = [new() { Name = "mode",
                        Type = new() { Kind = "string", Enum = ["preserve", "inspect"] } }] } }],
            Body = new() { Outputs = [new("pending_export", new() { Kind = "string", Text = "Not a completed result." })] }
        };
        if (nestedControl) group.Body.Tasks.Add(new() { Id = "nested", Kind = "sequence", Objective = "Keep the existing cleanup owner.", Body = new() });
        state.Plan = new() { Inputs = [new() { Name = "root_only_secret", Type = new() { Kind = "string" } }], Groups = [group] };
        var construction = state.Construction!;
        construction.OpenScopes = new TaskPlanSymbols(state.Plan).Scopes.Select(scope => scope.Path).ToList();
        construction.Checkpoints = [new() { Sequence = 1, PlanHash = PlanningConstruction.Hash(state.Plan),
            Step = new() { Scope = "/root", Kind = "group", Objective = "Preserve the exact requested observations and their original identities within the declared reusable interface.",
                RequirementIds = ["req_exact"], ExpectedOutputs = ["All requested observations with exact provenance, or explicit observed errors."] },
            Fragment = new() { ["groups"] = new JsonArray("worker"), ["businessReviewPending"] = true } }];
        var step = construction.Compact!.CurrentStep!;
        step.Scope = nestedControl ? "/tasks/nested/body" : "/groups/worker/body";
        step.Kind = null; step.DiscoveryComplete = false; step.BaseHash = PlanningConstruction.Hash(state.Plan);
        step.Inputs = [new() { Kind = "input", Source = "query" }];
        step.CloseRequirementIds = []; step.OutputNames = [];
        return state;
    }
}
