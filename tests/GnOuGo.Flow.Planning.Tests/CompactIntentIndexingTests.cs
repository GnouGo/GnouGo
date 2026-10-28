using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactIntentIndexingTests
{
    // Explicit scripted responses exercise the single active protocol.
    private static PlanningSession Fresh(string prompt = "Return exactly Hello as the public message.")
    {
        var state = new PlanningSession
        {
            Request = new() { TenantId = "fresh-intent", Prompt = prompt, Options = new() { ["automatic_correction"] = true },
                Generation = new("medium", 32_000, 8_192) }
        };
        PlanningSessionCreation.Capture(state);

        return PlannerFixture.Clone(state);
    }
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static bool Classification(LLMRequest request) => request.StructuredOutputSchema!["properties"]!["clauses"] is not null;
    private static JsonObject Classify(LLMRequest request, string interfaces = "output", string kind = "requirement") => new()
    {
        ["sourceId"] = Context(request)["sources"]![0]!["id"]!.ToString(),
        ["clauses"] = new JsonArray(new JsonObject { ["text"] = Context(request)["sources"]![0]!["text"]!.ToString(), ["kind"] = kind, ["interfaces"] = interfaces })
    };
    private static JsonObject Interfaces(LLMRequest request, TaskType? type = null) => InterfaceResponse(request, new()
    {
        ["inputs"] = new JsonArray(),
        ["outputs"] = JsonSerializer.SerializeToNode(new List<TaskInput> { new() { Name = "message", Type = type ?? new() { Kind = "string" } } },
            PlanningJsonContext.Default.ListTaskInput)
    });
    // Construct an explicit response to the one active extraction schema.
    private static JsonObject InterfaceResponse(LLMRequest request, JsonObject declarations)
    {
        var inputs = declarations["inputs"]!.DeepClone(); var outputs = declarations["outputs"]!.DeepClone();
        return new()
        {
            ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
            ["interfaces"] = new JsonObject
            {
                ["inputs"] = inputs, ["outputs"] = outputs,
                ["dispositions"] = new JsonArray(Context(request)["sources"]!.AsArray().Select(source => (JsonNode)new JsonObject
                {
                    ["sourceId"] = source!["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone(), ["kind"] = "interfaces",
                    ["inputs"] = new JsonArray(inputs.AsArray().Select(input => input!["name"]!.DeepClone()).ToArray()),
                    ["outputs"] = new JsonArray(outputs.AsArray().Select(output => output!["name"]!.DeepClone()).ToArray()),
                    ["reason"] = "The exact original clauses jointly declare these named interfaces.",
                    ["evidence"] = new JsonArray(new JsonObject { ["sourceId"] = source["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone() })
                }).ToArray())
            }, ["evidenceRequests"] = null, ["clarifications"] = null
        };
    }
    private static JsonObject Selection(LLMRequest request, string kind = "value", string scope = "/root", bool close = true,
        TaskValue[]? inputs = null) => new()
    {
        ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.ToString(),
        ["step"] = new JsonObject { ["scope"] = scope, ["kind"] = kind, ["objective"] = "Produce the original requested result.",
            ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray((inputs ?? []).Select(input =>
                JsonSerializer.SerializeToNode(input, PlanningJsonContext.Default.TaskValue)).ToArray()),
            ["operationIds"] = new JsonArray(), ["closeScopes"] = kind == "close" ? new JsonArray(scope) : null, ["groupId"] = null, ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray("The exact requested result."),
            ["closeRequirementIds"] = close ? new JsonArray("req_0") : new JsonArray(), ["outputNames"] = scope == "/root" && close ? new JsonArray("message") : new JsonArray() },
        ["clarifications"] = null, ["evidenceRequests"] = null
    };
    private static PlanTask Value(string id = "greet") => new()
    { Id = id, Kind = "value", Objective = "Produce the exact requested greeting.", Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] };
    private static JsonObject Draft(LLMRequest request, PlanningConstructionExtension? extension = null) => new()
    {
        ["baseHash"] = Context(request)["baseHash"]!.ToString(), ["requirements"] = null, ["commitments"] = null,
        ["extension"] = JsonSerializer.SerializeToNode(extension ?? new() { Tasks = [Value()], RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true,
            Outputs = [new("message", new() { Kind = "output", Source = "greet", Port = "message" })] }, PlanningJsonContext.Default.PlanningConstructionExtension),
        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    };
    private static JsonObject Review() => new() { ["decision"] = new JsonObject { ["verdict"] = "ok", ["reason"] = "The actual greeting fulfills the original request." } };
    private static LLMResponse Simple(LLMRequest request, string purpose) => Wire(request, purpose switch
    {
        PlanningCompactConstruction.IntentPurpose => Classification(request) ? Classify(request) : Interfaces(request),
        PlanningCompactConstruction.SelectPurpose => Selection(request),
        PlanningConstruction.DraftPurpose => Draft(request),
        _ => Review()
    });
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);

    [Fact]
    public async Task FreshClassificationAndTargetedInterfaceCompleteOneDraftWithExactCoverage()
    {
        var runtime = new TestRuntime { Respond = Simple };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(5, runtime.Calls.Count);
        Assert.Equal(11, state.SchemaVersion);
        Assert.Null(runtime.Calls[0].StructuredOutputSchema!["$defs"]);
        Assert.Null(runtime.Calls[0].StructuredOutputSchema!["properties"]!["inputs"]);
        Assert.Null(runtime.Calls[1].StructuredOutputSchema!["properties"]!["clauses"]);
        Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
        Assert.True(state.Construction!.Compact!.InterfaceExtractions![0].Complete);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        PlanningArtifactApproval.Verify(state);
        var hash = state.ComputeArtifactHash();
        state.Request.Options["construction_limits"]!["phasePolicy"]!["draft"]!["maxInputTokens"] = 15_999;
        Assert.NotEqual(hash, state.ComputeArtifactHash());
        foreach (var request in runtime.Calls)
        {
            var phase = request.StructuredOutputSchema!["title"]!.ToString();
            Assert.Equal(phase.Contains("judge", StringComparison.Ordinal) ? 2048 : 8192, request.MaxTokens);
            Assert.InRange(PlanningRequestMeasurements.Measure(request, phase, 3000).EstimatedInputTokens, 1,
                PlanningConstructionInputPolicy.EffectiveLimit(state.Request, phase));
        }
    }

    [Fact]
    public async Task PendingInterfaceBlocksDiscoveryAndCannotBeRemovedFromCoverage()
    {
        var runtime = new TestRuntime { Respond = Simple };
        var state = await Advance(Fresh(), runtime);
        Assert.Single(state.Requirements!.Outcomes);
        Assert.Empty(state.Requirements.Outputs!);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(0, runtime.Discoveries);
        Assert.False(PlanningCompactConstruction.SourcesComplete(state));
        var tampered = PlannerFixture.Clone(state);
        tampered.Construction!.Compact!.InterfaceExtractions!.Clear();
        Assert.False(PlanningCompactConstruction.IntentDeclarationsComplete(tampered.Construction!.Compact));
        state = await Advance(state, runtime);
        Assert.Single(state.Requirements!.Outputs!);
        Assert.Equal(0, runtime.Discoveries);
        Assert.True(PlanningCompactConstruction.IntentDeclarationsComplete(state.Construction!.Compact!));
    }

    private static readonly string[] DeclarationClauses =
        ["Return the supplied text unchanged as the public string output named message. ", "The caller input is named phrase and is a required string. ", "Preserve punctuation."];

    [Fact]
    public async Task ContiguousInterfaceSpansExcludeActionProseWithoutDroppingItsObligations()
    {
        string[] clauses =
        [
            "Accept a required text input, ",
            "then read the authorized records and write their complete contents locally. Preserve every record in source order. ",
            "Name the input sourceName. ",
            "Return a string output named location."
        ];
        var original = string.Concat(clauses);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(original, Context(request)["sources"]![0]!["text"]!.ToString());
            Assert.Null(request.StructuredOutputSchema!["$defs"]);
            return Wire(request, new()
            {
                ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
                ["clauses"] = new JsonArray(clauses.Select((text, index) => (JsonNode)new JsonObject
                {
                    ["text"] = text, ["kind"] = "requirement",
                    ["interfaces"] = index == 1 ? "none" : index == 3 ? "output" : "input"
                }).ToArray())
            });
        } };
        var state = await Advance(Fresh(original), runtime);
        Assert.Empty(state.Diagnostics);
        var extraction = Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
        Assert.Equal(3, extraction.SourceIds.Count);
        Assert.False(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal(clauses, state.Requirements!.Outcomes.Select(requirement => requirement.Description));
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.IntentPurpose, purpose);
            var sources = Context(request)["sources"]!.AsArray();
            Assert.Equal(new[] { clauses[0], clauses[2], clauses[3] }, sources.Select(source => source!["text"]!.ToString()));
            Assert.Equal(extraction.SourceIds, sources.Select(source => source!["id"]!.ToString()));
            Assert.Contains(clauses[1], request.Prompt, StringComparison.Ordinal);
            return Wire(request, InterfaceResponse(request, new JsonObject()
            {
                ["sourceId"] = extraction.Id,
                ["inputs"] = JsonSerializer.SerializeToNode(new List<TaskInput>
                {
                    new TaskInput() { Name = "sourceName", Type = new TaskType() { Kind = "string" }, Required = true }
                }, PlanningJsonContext.Default.ListTaskInput),
                ["outputs"] = JsonSerializer.SerializeToNode(new List<TaskInput>
                {
                    new TaskInput() { Name = "location", Type = new TaskType() { Kind = "string" } }
                }, PlanningJsonContext.Default.ListTaskInput)
            }));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal(original, state.Request.Prompt);
        Assert.NotNull(state.Requirements);
        Assert.Equal(clauses, state.Requirements.Outcomes.Select(requirement => requirement.Description));
        Assert.All(state.Construction!.Requirements, requirement => Assert.Equal("remaining", requirement.Status));
        Assert.Equal("sourceName", Assert.Single(state.Requirements.Inputs!).Name);
        Assert.Equal("location", Assert.Single(state.Requirements.Outputs!).Name);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.False(state.Construction!.Complete);
        Assert.Equal(0, runtime.Discoveries);
        Assert.All(state.RequestMeasurements!, measurement => Assert.InRange(measurement.EstimatedInputTokens, 1, state.Construction!.Limits.PhasePolicy!.Navigation.MaxInputTokens));
    }

    [Fact]
    public async Task ActionCompositionAndPublicResultRemainOneOriginalSelectionObligation()
    {
        const string original = "Evaluate each supplied record. Run those evaluations sequentially and return their numeric count as message.";
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose == PlanningCompactConstruction.IntentPurpose)
                return Wire(request, Classification(request) ? Classify(request) : Interfaces(request, new() { Kind = "number" }));
            var requirement = Assert.Single(Context(request)["requirements"]!.AsArray())!;
            Assert.Equal(original, requirement["original"]!.ToString());
            Assert.Equal("req_0", requirement["id"]!.ToString());
            return Wire(request, Selection(request, close: false));
        } };
        var state = await Advance(Fresh(original), runtime);
        Assert.Equal(original, Assert.Single(state.Requirements!.Outcomes).Description);
        Assert.Single(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).SourceIds);
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal("number", Assert.Single(state.Requirements!.Outputs!).Type.Kind);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(["req_0"], state.Construction!.Compact!.CurrentStep!.RequirementIds);
        Assert.Equal("remaining", Assert.Single(state.Construction!.Requirements).Status);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.All(state.RequestMeasurements!, measurement => Assert.InRange(measurement.EstimatedInputTokens, 1, state.Construction!.Limits.PhasePolicy!.Navigation.MaxInputTokens));
    }

    private static JsonObject SplitDeclarations(LLMRequest request) => new()
    {
        ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
        ["clauses"] = new JsonArray(DeclarationClauses.Select((text, index) => (JsonNode)new JsonObject
        { ["text"] = text, ["kind"] = "requirement", ["interfaces"] = index == 0 ? "both" : index == 1 ? "input" : "none" }).ToArray())
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamePageDeclarationsRetainExactNamingConstraintsAndRecoverOneAtomicExtraction(bool interrupted)
    {
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, SplitDeclarations(request)) };
        var state = await Advance(Fresh(string.Concat(DeclarationClauses)), runtime);
        var extraction = Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
        Assert.Equal(2, extraction.SourceIds.Count);
        Assert.True(extraction.Inputs); Assert.True(extraction.Outputs);
        Assert.Equal(DeclarationClauses, state.Requirements!.Outcomes.Select(r => r.Description));
        Assert.All(state.Construction!.Requirements, requirement => Assert.Equal("remaining", requirement.Status));
        Assert.Empty(state.Requirements.Inputs!);
        var sourceIds = extraction.SourceIds.ToArray();
        string? issued = null;
        if (interrupted)
        {
            runtime.Respond = (_, _) => throw new OperationCanceledException();
            _ = await Advance(state, runtime);
            state = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
            state.Status = PlanningStatus.Generating;
            issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        }
        var calls = state.ModelCalls;
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(DeclarationClauses.Take(2), Context(request)["sources"]!.AsArray().Select(source => source!["text"]!.ToString()));
            Assert.Equal(sourceIds, Context(request)["sources"]!.AsArray().Select(source => source!["id"]!.ToString()));
            return Wire(request, InterfaceResponse(request, new JsonObject() { ["sourceId"] = extraction.Id, ["inputs"] = JsonSerializer.SerializeToNode(
                new List<TaskInput> { new TaskInput() { Name = "phrase", Type = new TaskType() { Kind = "string" } } }, PlanningJsonContext.Default.ListTaskInput),
                ["outputs"] = JsonSerializer.SerializeToNode(
                    new List<TaskInput> { new TaskInput() { Name = "message", Type = new TaskType() { Kind = "string" } } }, PlanningJsonContext.Default.ListTaskInput) }));
        } };
        state = await Advance(state, recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("phrase", Assert.Single(state.Requirements!.Inputs!).Name);
        Assert.Equal("message", Assert.Single(state.Requirements.Outputs!).Name);
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal(DeclarationClauses, state.Requirements.Outcomes.Select(r => r.Description));
        Assert.All(state.Construction!.Requirements, requirement => Assert.Equal("remaining", requirement.Status));
        Assert.All(state.RequestMeasurements!, measurement => Assert.InRange(measurement.EstimatedInputTokens, 1, state.Construction!.Limits.PhasePolicy!.Navigation.MaxInputTokens));
        Assert.Equal(calls + (interrupted ? 0 : 1), state.ModelCalls);
        if (issued is not null)
            Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(recovery.Calls), PlanningJsonContext.Default.LLMRequest));
    }

    [Fact]
    public async Task InvalidCoalescedExtractionDoesNotAcceptItsOtherwiseValidInput()
    {
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, SplitDeclarations(request)) };
        var state = await Advance(Fresh(string.Concat(DeclarationClauses)), runtime);
        runtime.Respond = (request, _) =>
        {
            var response = Interfaces(request, new() { Kind = "array", Items = new() { Kind = "string" }, MinItems = 3, MaxItems = 1 });
            response["interfaces"]!["inputs"] = JsonSerializer.SerializeToNode(new List<TaskInput> { new() { Name = "phrase", Type = new() { Kind = "string" } } },
                PlanningJsonContext.Default.ListTaskInput);
            return Wire(request, InterfaceResponse(request, response["interfaces"]!.DeepClone().AsObject()));
        };
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "TASK_TYPE_INVALID");
        Assert.Empty(state.Requirements!.Inputs!); Assert.Empty(state.Requirements.Outputs!);
        var extraction = Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
        Assert.Equal(2, extraction.SourceIds.Count); Assert.False(extraction.Complete);
        Assert.Equal(DeclarationClauses, state.Requirements.Outcomes.Select(requirement => requirement.Description));
        Assert.All(state.Construction!.Requirements, requirement => Assert.Equal("remaining", requirement.Status));
    }

    [Fact]
    public async Task InvalidExtractionPreservesExactClassifiedRequirementsAndDoesNotFreezeInterface()
    {
        var runtime = new TestRuntime { Respond = Simple };
        var state = await Advance(Fresh(), runtime);
        var original = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        runtime.Respond = (request, _) =>
        {
            var response = Interfaces(request);
            response["interfaces"]!["outputs"]![0]!["type"] = JsonNode.Parse("""{"kind":"array","nullable":false,"items":{"kind":"string","nullable":false},"minItems":3,"maxItems":1}""");
            return Wire(request, InterfaceResponse(request, response["interfaces"]!.DeepClone().AsObject()));
        };
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_TYPE_INVALID");
        Assert.Equal(original, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.False(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.Empty(state.Construction!.Checkpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptionResumesExactClassificationOrExtractionWithoutAnotherReservation(bool extraction)
    {
        var runtime = new TestRuntime { Respond = Simple };
        var state = Fresh();
        if (extraction) state = await Advance(state, runtime);
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall is not null));
        saved.Status = PlanningStatus.Generating;
        var issued = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls; var repairs = saved.ReplanAttempts;
        if (extraction) saved.Request.Prompt += " Preserve the subsequently supplied original constraint.";
        var recovery = new TestRuntime { Respond = Simple };
        var resumed = await Advance(saved, recovery);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(recovery.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, resumed.ModelCalls);
        Assert.Equal(repairs, resumed.ReplanAttempts);
        Assert.True(Assert.Single(resumed.Construction!.Compact!.InterfaceExtractions!).Complete == extraction, string.Join(";", resumed.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        if (extraction) Assert.Contains(resumed.Construction!.Compact!.Sources, source => source.Kind == "revision_page" && !source.Indexed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviouslyIssuedExtractionConsumesItsExactSavedReceiptWithoutRedispatch(bool outputLimited)
    {
        var state = await Advance(Fresh(), new TestRuntime { Respond = Simple });
        var extraction = Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
        var context = PlanningCompactConstruction.InterfaceContext(state, extraction);
        var issued = PlanningCompactConstruction.Request(PlanningCompactConstruction.InterfaceGuidance(state), context,
            PlanningCompactConstruction.ResolvableInterfaceSchema(state, extraction), PlanningCompactConstruction.IntentPurpose);
        issued = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.IntentPurpose, issued.Prompt, issued.StructuredOutputSchema!.AsObject());
        issued.ClientRequestId = state.Request.SessionId + ":2:" +
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        state.PendingCall = new() { Id = issued.ClientRequestId, Purpose = PlanningCompactConstruction.IntentPurpose, Request = issued };
        state.ModelCalls++;
        var measurement = PlanningRequestMeasurements.Measure(issued, PlanningCompactConstruction.IntentPurpose, 3000);
        measurement.Admission = "admitted";
        state.RequestMeasurements!.Add(measurement);
        var measurements = JsonSerializer.Serialize(state.RequestMeasurements, PlanningJsonContext.Default.ListPlanningRequestMeasurement);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var sources = JsonSerializer.Serialize(state.Construction!.Compact!.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource);
        var receipt = outputLimited ? ReasoningOnlyLimit() : Simple(issued, PlanningCompactConstruction.IntentPurpose);
        var receipts = new Dictionary<string, LLMResponse>(StringComparer.Ordinal) { [issued.ClientRequestId] = receipt };
        var physicalCalls = 0;
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            if (receipts.TryGetValue(request.ClientRequestId!, out var saved)) return saved;
            physicalCalls++;
            throw new InvalidOperationException("A retained extraction receipt must not dispatch another model call.");
        } };
        state = await Advance(PlannerFixture.Clone(state), recovery);
        Assert.Equal(0, physicalCalls);
        Assert.Single(recovery.Calls);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(measurements, JsonSerializer.Serialize(state.RequestMeasurements, PlanningJsonContext.Default.ListPlanningRequestMeasurement));
        Assert.Equal(sources, JsonSerializer.Serialize(state.Construction!.Compact!.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource));
        Assert.Null(state.PendingCall); Assert.Null(state.Construction!.Candidate);
        Assert.Null(state.Construction!.Compact!.CurrentStep); Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(0, recovery.Discoveries);
        Assert.Equal(!outputLimited, Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        if (outputLimited)
        {
            Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
            Assert.Equal(PlanningStatus.Stopped, state.Status);
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "MODEL_OUTPUT_LIMIT");
            Assert.False(PlanningCompactConstruction.SourcesComplete(state));
            Assert.Same(state, await Advance(state, recovery));
            Assert.Single(recovery.Calls);
        }
        else
        {
            Assert.Empty(state.Diagnostics);
            Assert.Equal("message", Assert.Single(state.Requirements!.Outputs!).Name);
            Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        }
    }

    [Fact]
    public async Task ReasoningOnlyExtractionLimitCannotAdvanceCoverageOrAutomaticallyRetry()
    {
        var runtime = new TestRuntime { Respond = Simple };
        var state = await Advance(Fresh(), runtime);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        runtime.Respond = (_, _) => ReasoningOnlyLimit();
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "MODEL_OUTPUT_LIMIT");
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(8192, runtime.Calls[1].MaxTokens);
        Assert.Equal("medium", state.Request.Generation.Reasoning);
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.False(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.False(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Null(state.PendingCall); Assert.Null(state.Construction!.Compact!.CurrentStep);
        Assert.Null(state.Construction!.Candidate); Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(0, runtime.Discoveries);
        Assert.Same(state, await Advance(state, runtime));
        Assert.Equal(2, runtime.Calls.Count);
    }

    private static LLMResponse ReasoningOnlyLimit() => new()
    {
        CompletionStatus = "output_limit",
        Usage = new JsonObject { ["output_tokens"] = 8192, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 8192 } }
    };

    [Theory]
    [InlineData("漢")]
    [InlineData("🧠")]
    [InlineData("\"\\\n")]
    public async Task CoherentPagesRetainExactUnicodeAndCompleteRequestAdmission(string token)
    {
        var original = string.Concat(Enumerable.Repeat("Preserve " + token + " exactly. ", 100));
        var state = Fresh(original); PlanningSessionCreation.Capture(state); PlanningCompactConstruction.InitializeSources(state);
        var pages = state.Construction!.Compact!.Sources.ToArray();
        Assert.Equal(original, string.Concat(pages.Select(s => s.Text)));
        Assert.All(pages, page => Assert.Null(page.ContinuationOf));
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, Classify(request, "none")) };
        foreach (var unused in pages) state = await Advance(state, runtime);
        Assert.Equal(original, string.Concat(state.Requirements!.Outcomes.Select(r => r.Description)));
        Assert.All(state.RequestMeasurements!, measurement => Assert.InRange(measurement.EstimatedInputTokens, 1, state.Construction!.Limits.PhasePolicy!.Navigation.MaxInputTokens));
    }

    [Fact]
    public async Task LongSentenceContinuationCannotBecomeIndependentClosableRequirements()
    {
        var original = "Preserve " + string.Concat(Enumerable.Repeat("every exact item and ", 150)) + "its final meaning.";
        var state = Fresh(original); PlanningSessionCreation.Capture(state); PlanningCompactConstruction.InitializeSources(state);
        var count = state.Construction!.Compact!.Sources.Count;
        Assert.True(count > 2);
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, Classify(request, "none")) };
        for (var i = 0; i < count; i++) state = await Advance(state, runtime);
        Assert.Equal(original, Assert.Single(state.Requirements!.Outcomes).Description);
        Assert.Equal(count, Assert.Single(state.Construction!.Compact!.RequirementSources).Value.Count);
        Assert.Single(state.Construction!.Requirements);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        Assert.All(state.Construction!.Compact!.Sources.Where(s => s.Kind == "page").Skip(1), s => Assert.NotNull(s.ContinuationOf));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task SoftWrappedObligationRemainsLinkedAcrossCoherentPages(string newline)
    {
        var original = "Preserve " + string.Concat(Enumerable.Repeat("every exact item and" + newline, 150)) + "its final meaning.";
        var state = Fresh(original); PlanningSessionCreation.Capture(state); PlanningCompactConstruction.InitializeSources(state);
        var pages = state.Construction!.Compact!.Sources.ToArray();
        Assert.True(pages.Length > 2);
        Assert.Equal(original, string.Concat(pages.Select(page => page.Text)));
        Assert.All(pages.Skip(1), page => Assert.NotNull(page.ContinuationOf));
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, Classify(request, "none")) };
        foreach (var unused in pages) state = await Advance(state, runtime);
        Assert.Equal(original, Assert.Single(state.Requirements!.Outcomes).Description);
        Assert.Equal(pages.Length, Assert.Single(state.Construction!.Compact!.RequirementSources).Value.Count);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndivisibleOriginalEvidenceExceedingPhaseAllowanceStopsWithoutOmissionOrDispatch(bool interfaceDeclaration)
    {
        var original = "Preserve " + string.Concat(Enumerable.Repeat("every exact original value and ", 450)) + "its final meaning.";
        var state = Fresh(original); PlanningSessionCreation.Capture(state); PlanningCompactConstruction.InitializeSources(state);
        var count = state.Construction!.Compact!.Sources.Count;
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, Classify(request, interfaceDeclaration ? "input" : "none")) };
        for (var i = 0; i < count; i++) state = await Advance(state, runtime);
        Assert.Equal(!interfaceDeclaration, PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal(original, Assert.Single(state.Requirements!.Outcomes).Description);
        var calls = state.ModelCalls;
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "MODEL_INPUT_LIMIT");
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(count, runtime.Calls.Count);
        Assert.Equal(original, Assert.Single(state.Requirements!.Outcomes).Description);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal("rejected_before_dispatch", state.RequestMeasurements!.Last().Admission);
    }

    [Fact]
    public async Task GlobalContinuationRetainsItsOriginalScopeAndRejectsReclassification()
    {
        var original = "Always preserve " + string.Concat(Enumerable.Repeat("all original identities and ", 100)) + "ordering.";
        var state = Fresh(original); PlanningSessionCreation.Capture(state); PlanningCompactConstruction.InitializeSources(state);
        var count = state.Construction!.Compact!.Sources.Count;
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, Classify(request, "none", "global")) };
        state = await Advance(state, runtime);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal("global", Context(request)["continuation"]!["kind"]!.ToString());
            Assert.Equal(original[..Context(request)["continuation"]!["text"]!.ToString().Length], Context(request)["continuation"]!["text"]!.ToString());
            return Wire(request, Classify(request, "none"));
        };
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, d => d.Code == "CONSTRUCTION_CONTINUATION_KIND");
        Assert.Single(state.Construction!.Compact!.GlobalSourceIds);
        Assert.Empty(state.Requirements!.Outcomes);
        Assert.False(state.Construction!.Compact!.Sources.Where(s => s.Kind == "page").Skip(1).First().Indexed);
        runtime.Respond = (request, _) => Wire(request, Classify(request, "none", "global"));
        for (var i = 1; i < count; i++) state = await Advance(state, runtime);
        Assert.Empty(state.Requirements!.Outcomes);
        var compact = state.Construction!.Compact!;
        Assert.Equal(original, string.Concat(compact.GlobalSourceIds.Select(id => compact.Sources.Single(s => s.Id == id).Text)));
        Assert.All(state.RequestMeasurements!, measurement => Assert.InRange(measurement.EstimatedInputTokens, 1, state.Construction!.Limits.PhasePolicy!.Navigation.MaxInputTokens));
    }

    [Fact]
    public async Task ContinuationMergeWithLaterClausesKeepsDistinctStableRequirementIds()
    {
        var original = string.Concat(Enumerable.Repeat("preserve exact data ", 100)) + ". Return the original order. " +
            string.Concat(Enumerable.Repeat("retain every record ", 100)) + ".";
        var state = Fresh(original); PlanningSessionCreation.Capture(state); PlanningCompactConstruction.InitializeSources(state);
        var count = state.Construction!.Compact!.Sources.Count;
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var response = Classify(request, "none");
            var text = Context(request)["sources"]![0]!["text"]!.ToString();
            var split = text.IndexOf(". ", StringComparison.Ordinal);
            if (split >= 0 && split + 2 < text.Length)
                response["clauses"] = new JsonArray(
                    new JsonObject { ["text"] = text[..(split + 2)], ["kind"] = "requirement", ["interfaces"] = "none" },
                    new JsonObject { ["text"] = text[(split + 2)..], ["kind"] = "requirement", ["interfaces"] = "none" });
            return Wire(request, response);
        } };
        for (var i = 0; i < count; i++) state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(state.Requirements!.Outcomes.Count, state.Requirements.Outcomes.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(original, string.Concat(state.Requirements.Outcomes.Select(r => r.Description)));
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
    }

    [Fact]
    public async Task FreshBaselineAndTargetedPublicInterfaceKeepExistingCallerDefaultsAndTasks()
    {
        var state = Fresh("Return exactly the existing greeting as public message.");
        state.Request.Baseline = new() { Inputs = [new() { Name = "retained", Type = new() { Kind = "string" }, Required = false,
            Default = new() { Kind = "string", Text = "original" } }], Root = new() { Tasks = [Value("baseline")] } };
        state.Plan = PlanningConstruction.Clone(state.Request.Baseline);

        var before = JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.TaskPlan);
        var runtime = new TestRuntime { Respond = (request, purpose) => purpose switch
        {
            PlanningCompactConstruction.SelectPurpose => Wire(request, Selection(request, "close",
                inputs: [new() { Kind = "output", Source = "baseline", Port = "message" }])),
            PlanningConstruction.DraftPurpose => Wire(request, Draft(request, new() { RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true,
                Outputs = [new("message", new() { Kind = "output", Source = "baseline", Port = "message" })] })),
            _ => Simple(request, purpose)
        } };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(before, JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal("original", Assert.Single(state.Requirements!.Inputs!).Default!.Text);
        Assert.Equal("baseline", Assert.Single(state.Plan!.Root.Tasks).Id);
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task CanonicalInputDefaultsPreserveAbsenceExplicitNullAndRecursiveLiterals()
    {
        var state = await Advance(Fresh("Accept required text, optional nullable empty default null, and optional settings with values defaulting to the exact list [\"é\", null]."),
            new TestRuntime { Respond = (request, _) => Wire(request, Classify(request, "input")) });
        var runtime = new TestRuntime { Respond = (request, _) => new() { Json = InterfaceResponse(request, new JsonObject
        {
            ["sourceId"] = request.StructuredOutputSchema!["properties"]!["sourceId"]!["enum"]![0]!.DeepClone(),
            ["outputs"] = new JsonArray(),
            ["inputs"] = JsonNode.Parse("""
                [{"name":"text","type":{"kind":"string","nullable":false,"enum":null},"required":true,"default":null},
                 {"name":"empty","type":{"kind":"string","nullable":true,"enum":null},"required":false,"default":{"kind":"null"}},
                 {"name":"settings","type":{"kind":"object","nullable":false,"fields":[
                    {"name":"values","type":{"kind":"array","nullable":false,"items":{"kind":"string","nullable":true,"enum":null},"minItems":null,"maxItems":null},"required":true,"default":null}]},
                  "required":false,"default":{"kind":"object","members":[{"name":"values","value":{"kind":"array","items":[{"kind":"string","text":"é"},{"kind":"null"}]}}]}}]
                """)
        }) } };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var inputs = state.Requirements!.Inputs!;
        Assert.Null(inputs[0].Default);
        Assert.Equal("null", inputs[1].Default!.Kind);
        Assert.Null(Assert.Single(inputs[2].Type.Fields).Default);
        var values = Assert.Single(inputs[2].Default!.Members).Value.Items;
        Assert.Equal("é", values[0].Text); Assert.Equal("null", values[1].Kind);
        var restored = PlannerFixture.Clone(state);
        Assert.Null(restored.Requirements!.Inputs![0].Default);
        Assert.Equal("null", restored.Requirements.Inputs[1].Default!.Kind);
        Assert.Equal(JsonSerializer.Serialize(inputs, PlanningJsonContext.Default.ListTaskInput),
            JsonSerializer.Serialize(restored.Requirements.Inputs, PlanningJsonContext.Default.ListTaskInput));
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
    }

    [Theory]
    [InlineData(true, "null", "TASK_DEFAULT_REQUIRED")]
    [InlineData(false, "{\"kind\":\"null\"}", "TASK_DEFAULT_INVALID")]
    public async Task CanonicalInputShapeDoesNotWeakenDefaultValidation(bool nullable, string literal, string diagnostic)
    {
        var state = await Advance(Fresh("Accept the declared optional value."),
            new TestRuntime { Respond = (request, _) => Wire(request, Classify(request, "input")) });
        var original = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var runtime = new TestRuntime { Respond = (request, _) => new() { Json = InterfaceResponse(request, new JsonObject
        {
            ["sourceId"] = request.StructuredOutputSchema!["properties"]!["sourceId"]!["enum"]![0]!.DeepClone(),
            ["outputs"] = new JsonArray(), ["inputs"] = new JsonArray(new JsonObject
            {
                ["name"] = "value", ["required"] = false, ["default"] = JsonNode.Parse(literal),
                ["type"] = new JsonObject { ["kind"] = "string", ["nullable"] = nullable, ["enum"] = null }
            })
        }) } };
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, finding => finding.Code == diagnostic);
        Assert.Equal(original, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.False(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.Null(state.Construction!.Compact!.CurrentStep);
    }

    [Fact]
    public async Task CanonicalStringInterfacesRetainEveryDeclaredNameAndExactAllowedValues()
    {
        const string original = "Accept unrestricted string documentId and string mode limited to brief or full. Return public string summary and a separate public string marker limited to the empty string.";
        var state = await Advance(Fresh(original),
            new TestRuntime { Respond = (request, _) => Wire(request, Classify(request, "both")) });
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.StartsWith(PlanningCompactConstruction.InterfaceGuidance(state), request.Prompt, StringComparison.Ordinal);
            Assert.Equal(original, Context(request)["sources"]![0]!["text"]!.ToString());
            var response = InterfaceResponse(request, new JsonObject
            {
                ["sourceId"] = request.StructuredOutputSchema!["properties"]!["sourceId"]!["enum"]![0]!.DeepClone(),
                ["inputs"] = JsonNode.Parse("""
                    [{"name":"documentId","required":true,"default":null,"type":{"kind":"string","nullable":false,"enum":null}},
                     {"name":"mode","required":true,"default":null,"type":{"kind":"string","nullable":false,"enum":["brief","full"]}}]
                    """),
                ["outputs"] = JsonNode.Parse("""
                    [{"name":"summary","required":true,"type":{"kind":"string","nullable":false,"enum":null}},
                     {"name":"marker","required":true,"type":{"kind":"string","nullable":false,"enum":[""]}}]
                    """)
            });
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema));
            foreach (var direction in new[] { "inputs", "outputs" })
            {
                var invalid = response.DeepClone().AsObject();
                var type = invalid["interfaces"]![direction]![0]!["type"]!.AsObject();
                Assert.True(type.Remove("enum"));
                Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, request.StructuredOutputSchema));
                type["enum"] = new JsonArray();
                Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, request.StructuredOutputSchema));
            }
            return new() { Json = response };
        } };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var inputs = state.Requirements!.Inputs!;
        var outputs = state.Requirements.Outputs!;
        Assert.Equal(new[] { "documentId", "mode" }, inputs.Select(input => input.Name));
        Assert.Equal(new[] { "summary", "marker" }, outputs.Select(output => output.Name));
        Assert.Null(inputs[0].Type.Enum);
        Assert.Equal(["brief", "full"], inputs[1].Type.Enum);
        Assert.Null(outputs[0].Type.Enum);
        Assert.Equal([""], outputs[1].Type.Enum);
        Assert.Null(TaskPlanCompiler.TypeSchema(inputs[0].Type)["enum"]);
        Assert.Equal("", TaskPlanCompiler.TypeSchema(outputs[1].Type)["enum"]![0]!.ToString());
        var restored = PlannerFixture.Clone(state);
        Assert.Equal(JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            JsonSerializer.Serialize(restored.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.True(PlanningCompactConstruction.SourcesComplete(restored));
        Assert.Null(restored.Construction!.Compact!.CurrentStep);
    }

    [Fact]
    public async Task TargetedInputExtractionPreservesDefaultsEnumsAndUnspecifiedBounds()
    {
        var state = Fresh("Accept optional choice A or B default B, an unbounded array named values, an unrestricted string named text and an empty-string-only input named empty; retain them.");
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            if (Classification(request)) return Wire(request, Classify(request, "input"));
            Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!, strict: true));
            var response = Interfaces(request);
            response["interfaces"]!["outputs"] = new JsonArray();
            response["interfaces"]!["inputs"] = JsonNode.Parse("""
                [{"name":"choice","required":false,"type":{"kind":"string","nullable":false,"enum":["A","B"]},"default":{"kind":"string","text":"B"}},
                 {"name":"values","required":true,"type":{"kind":"array","nullable":false,"items":{"kind":"number","nullable":false},"minItems":null,"maxItems":null}},
                 {"name":"text","required":true,"type":{"kind":"string","nullable":false}},
                 {"name":"empty","required":true,"type":{"kind":"string","nullable":false,"enum":[""]}}]
                """);
            return Wire(request, InterfaceResponse(request, response["interfaces"]!.DeepClone().AsObject()));
        } };
        state = await Advance(state, runtime); state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("B", state.Requirements!.Inputs![0].Default!.Text);
        Assert.Equal(["A", "B"], state.Requirements.Inputs[0].Type.Enum);
        Assert.Null(state.Requirements.Inputs[1].Type.MinItems);
        Assert.Null(state.Requirements.Inputs[1].Type.MaxItems);
        Assert.Null(state.Requirements.Inputs[2].Type.Enum);
        Assert.Null(TaskPlanCompiler.TypeSchema(state.Requirements.Inputs[2].Type)["enum"]);
        Assert.Equal([""], state.Requirements.Inputs[3].Type.Enum);
        Assert.Equal("", TaskPlanCompiler.TypeSchema(state.Requirements.Inputs[3].Type)["enum"]![0]!.GetValue<string>());
        Assert.True(PlanningCompactConstruction.IntentDeclarationsComplete(state.Construction!.Compact!));
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("both")]
    public async Task EmittedInterfaceRequestsKeepReferencesPureAndPreserveAbsentAndZeroBounds(string direction)
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            if (Classification(request)) return Wire(request, Classify(request, direction));
            var schema = request.StructuredOutputSchema!.AsObject();
            Assert.Equal(8192, request.MaxTokens);
            Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
            foreach (var (pointer, node) in PlanningSchemaReferences.Walk(schema, "", 0))
                if (node.ContainsKey("$ref"))
                    Assert.True(node.Count == 1, $"Provider reference at {pointer} contains sibling keywords.");
            Assert.Contains("Absent array bounds are null", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("maxItems:0 permits only []", request.Prompt, StringComparison.Ordinal);
            Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.IntentPurpose, 3000).EstimatedInputTokens, 1, 3000);
            var declarations = JsonSerializer.SerializeToNode(new List<TaskInput> { new()
            {
                Name = "values", Type = new() { Kind = "array", Items = new() { Kind = "string" }, MaxItems = 0 }
            } }, PlanningJsonContext.Default.ListTaskInput)!;
            return Wire(request, InterfaceResponse(request, new JsonObject()
            {
                ["sourceId"] = schema["properties"]!["sourceId"]!["enum"]![0]!.ToString(),
                ["inputs"] = direction is "input" or "both" ? declarations.DeepClone() : new JsonArray(),
                ["outputs"] = direction is "output" or "both" ? declarations.DeepClone() : new JsonArray()
            }));
        } };
        var state = await Advance(Fresh("Declare values as an array of strings with maximum length zero and no explicit minimum."), runtime);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        var declarations = state.Requirements!.Inputs!.Concat(state.Requirements.Outputs!);
        Assert.Equal(direction == "both" ? 2 : 1, declarations.Count());
        Assert.All(declarations, declaration =>
        {
            Assert.Null(declaration.Type.MinItems);
            Assert.Equal(0, declaration.Type.MaxItems);
        });
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("conditional")]
    [InlineData("foreach")]
    [InlineData("parallel")]
    public async Task FreshIntentProtocolPreservesIncrementalControlScopesAndJoins(string kind)
    {
        var container = new PlanTask { Id = "scope_work", Kind = kind, Objective = "Produce the requested scoped result." };
        var children = kind is "conditional" or "parallel" ? 2 : 1;
        if (kind == "parallel") container.Branches = [new(), new()]; else container.Body = new();
        if (kind == "conditional") { container.Condition = new() { Kind = "boolean", Boolean = true }; container.Otherwise = new(); }
        if (kind == "foreach") container.Items = new() { Kind = "array", Items = [new() { Kind = "string", Text = "Hello" }] };
        var steps = new List<(string Kind, string Scope, PlanningConstructionExtension Extension)>
            { (kind, "/root", new() { Tasks = [container], RequirementIds = ["req_0"] }) };
        for (var i = 0; i < children; i++)
        {
            var scope = kind == "parallel" ? "/tasks/scope_work/branches/" + i : "/tasks/scope_work/" + (i == 0 ? "body" : "otherwise");
            var task = Value("child_" + i);
            if (kind == "foreach") task.Outputs[0] = new("message", new() { Kind = "item" });
            steps.Add(("value", scope, new() { Scope = scope, Tasks = [task], RequirementIds = ["req_0"],
                Outputs = [new(kind == "parallel" && i == 1 ? "other" : "message", new() { Kind = "output", Source = task.Id, Port = "message" })], CloseScopes = [scope] }));
        }
        steps.Add(("close", "/root", new() { Outputs = [new("message", new() { Kind = "output", Source = "scope_work", Port = "message" })],
            CloseScopes = ["/root"], Complete = true, RequirementIds = ["req_0"] }));
        var index = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            JsonObject response;
            if (purpose == PlanningCompactConstruction.IntentPurpose)
                response = Classification(request) ? Classify(request) : Interfaces(request,
                    kind == "foreach" ? new() { Kind = "array", Items = new() { Kind = "string" } } : null);
            else if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                var step = steps[index];
                response = Selection(request, step.Kind, step.Scope, index == steps.Count - 1,
                    index == steps.Count - 1 ? [new() { Kind = "output", Source = "scope_work", Port = "message" }] :
                    kind == "foreach" && index == 1 ? [new() { Kind = "item" }] : []);
            }
            else if (purpose == PlanningConstruction.DraftPurpose) response = Draft(request, steps[index++].Extension);
            else response = Review();
            return Wire(request, response);
        } };
        var state = await PlannerFixture.RunAsync(runtime, Fresh());
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(steps.Count, state.Construction!.Checkpoints.Count);
        Assert.Contains(state.Construction!.Checkpoints.SelectMany(c => c.Reviews), r => r["purpose"]!.ToString() == PlanningConstruction.GlobalPurpose);
        PlanningArtifactApproval.Verify(state);
    }
}
