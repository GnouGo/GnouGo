using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactIntentResolutionTests(ITestOutputHelper output)
{
    private static PlanningSession Fresh(string prompt) => new()
    {
        Request = new() { TenantId = "resolved-intent", Prompt = prompt,
            Options = new() { ["automatic_correction"] = true }, Generation = new("medium", 32_000, 8_192) }
    };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static JsonObject Classify(LLMRequest request, string direction, params (string Text, string Direction)[] clauses)
    {
        var source = Context(request)["sources"]![0]!;
        return new()
        {
            ["sourceId"] = source["id"]!.ToString(),
            ["clauses"] = new JsonArray((clauses.Length == 0 ? [(source["text"]!.ToString(), direction)] : clauses)
                .Select(c => (JsonNode)new JsonObject { ["text"] = c.Item1, ["kind"] = "requirement", ["interfaces"] = c.Item2 }).ToArray())
        };
    }
    private static JsonObject Resolve(LLMRequest request, List<TaskInput>? inputs = null, List<TaskInput>? outputs = null,
        string kind = "interfaces")
    {
        inputs ??= []; outputs ??= [];
        var context = Context(request);
        return new JsonObject
        {
            ["sourceId"] = context["sources"]![0]!["id"]!.ToString(),
            ["interfaces"] = new JsonObject
            {
                ["inputs"] = JsonSerializer.SerializeToNode(inputs, PlanningJsonContext.Default.ListTaskInput),
                ["outputs"] = JsonSerializer.SerializeToNode(outputs, PlanningJsonContext.Default.ListTaskInput),
                ["dispositions"] = new JsonArray(context["sources"]!.AsArray().Select(s => (JsonNode)new JsonObject
                {
                    ["sourceId"] = s!["id"]!.ToString(), ["version"] = s["version"]!.ToString(), ["kind"] = kind,
                    ["inputs"] = new JsonArray(inputs.Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()),
                    ["outputs"] = new JsonArray(outputs.Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()),
                    ["reason"] = kind == "interfaces" ? "The original clauses explicitly declare these caller/result interfaces." :
                        "The original clause requires a local file; it does not declare a public workflow output.",
                    ["evidence"] = new JsonArray(new JsonObject { ["sourceId"] = s["id"]!.ToString(), ["version"] = s["version"]!.ToString() })
                }).ToArray())
            },
            ["evidenceRequests"] = null, ["clarifications"] = null
        };
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    public async Task ProvisionalDirectionUsesEarlierOriginalTypeAndLaterExactName(string classification)
    {
        const string declaration = "Accept a caller-supplied text value. ";
        const string naming = "The public caller input is named suppliedText.";
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, classification,
            (declaration, "none"), (naming, classification))) };
        var state = await Advance(Fresh(declaration + naming), runtime);
        Assert.Equal(5, state.Construction!.Compact!.PresentationVersion);
        runtime.Respond = (r, _) =>
        {
            var context = Context(r);
            Assert.Contains(declaration, context["originalContext"]![0]!["text"]!.ToString(), StringComparison.Ordinal);
            Assert.Equal(naming, Assert.Single(context["sources"]!.AsArray())!["text"]!.ToString());
            var response = Resolve(r, inputs: [new() { Name = "suppliedText", Type = new() { Kind = "string" }, Required = true }]);
            var original = context["originalContext"]![0]!;
            response["interfaces"]!["dispositions"]![0]!["evidence"]!.AsArray().Add(new JsonObject
            { ["sourceId"] = original["id"]!.ToString(), ["version"] = original["version"]!.ToString() });
            return Wire(r, response);
        };
        state = await Advance(state, runtime);
        Assert.Equal("string", Assert.Single(state.Requirements!.Inputs!).Type.Kind);
        Assert.Equal("suppliedText", state.Requirements.Inputs![0].Name);
        Assert.Equal(2, state.Requirements.Outcomes.Count);
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete,
            string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.True(PlanningCompactConstruction.IntentDeclarationsComplete(state.Construction.Compact));
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(0, runtime.Discoveries);
    }

    [Fact]
    public async Task IssuedExtractionPreservesAbsentAndExplicitNullDefaultsWithStringDomains()
    {
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "input")) };
        var state = await Advance(Fresh("Accept required unrestricted text named label with no default, and optional nullable text named mode restricted to auto or manual with an explicit null default."), runtime);
        runtime.Respond = (r, _) =>
        {
            // Inspect the actual issued request: nested schema compaction removes
            // descriptions, so these distinct DTO semantics need prompt guidance.
            Assert.Contains("enum:null means unrestricted", r.Prompt, StringComparison.Ordinal);
            Assert.Contains("default:null means no default", r.Prompt, StringComparison.Ordinal);
            Assert.Contains("{\"kind\":\"null\"} is an explicit null literal", r.Prompt, StringComparison.Ordinal);
            return Wire(r, Resolve(r, inputs:
            [
                new() { Name = "label", Required = true, Type = new() { Kind = "string" } },
                new() { Name = "mode", Type = new() { Kind = "string", Nullable = true, Enum = ["auto", "manual"] },
                    Default = new() { Kind = "null" } }
            ]));
        };
        state = await Advance(state, runtime);
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        var inputs = state.Requirements!.Inputs!;
        Assert.Null(inputs.Single(i => i.Name == "label").Default);
        Assert.Null(inputs.Single(i => i.Name == "label").Type.Enum);
        Assert.Equal("null", inputs.Single(i => i.Name == "mode").Default!.Kind);
        Assert.Equal(["auto", "manual"], inputs.Single(i => i.Name == "mode").Type.Enum);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task RejectedDefaultFeedbackPreservesDeclarationsAndExactCorrectionRecovery(int version, bool interrupted)
    {
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "input")) };
        var state = await Advance(PlannerFixture.CapturedPresentation(Fresh("Accept required text input suppliedText with no default."), version), runtime);
        var original = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        string? initialSchema = null;
        foreach (var required in new[] { false, true })
        {
            var previous = state.Diagnostics.ToList();
            runtime.Respond = (r, _) =>
            {
                initialSchema ??= r.StructuredOutputSchema!.ToJsonString();
                Assert.Equal(initialSchema, r.StructuredOutputSchema!.ToJsonString());
                Assert.Equal(PlanningJsonTransport.Diagnostics(previous).ToJsonString(), Context(r)["diagnostics"]!.ToJsonString());
                return Wire(r, Resolve(r, inputs: [new() { Name = "suppliedText", Required = required,
                    Type = new() { Kind = "string" }, Default = new() { Kind = "null" } }]));
            };
            state = await Advance(state, runtime);
            var diagnostic = Assert.Single(state.Diagnostics);
            Assert.Equal("TASK_DEFAULT_INVALID", diagnostic.Code);
            Assert.Equal(version == 4 ? "/inputs/suppliedText/default" : "/inputs/suppliedText", diagnostic.Location);
            Assert.Equal("A default violates its business type." + (version == 4
                ? $" Rejected declaration: type.kind=string, type.nullable=false, default.kind=null, required={required.ToString().ToLowerInvariant()}." : ""), diagnostic.Message);
            Assert.Null(diagnostic.Rule); Assert.Null(diagnostic.ValidationStage);
            Assert.Equal(original, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
            var extraction = Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
            Assert.False(extraction.Complete); Assert.Null(extraction.Dispositions);
            Assert.Null(state.Plan); Assert.Empty(state.Construction.Checkpoints);
        }
        Assert.Equal(1, state.ReplanAttempts);
        // Existing diagnostic fields are sufficient durable correction context;
        // no rejected interface is added to the authoritative requirements.
        state = PlannerFixture.Clone(state);
        var extractionState = Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
        var expected = PlanningCompactConstruction.Request(PlanningCompactConstruction.InterfaceGuidance(state),
            PlanningCompactConstruction.InterfaceContext(state, extractionState),
            PlanningCompactConstruction.ResolvableInterfaceSchema(state, extractionState), PlanningCompactConstruction.IntentPurpose);
        var issuedBytes = JsonSerializer.Serialize(PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.IntentPurpose,
            expected.Prompt, expected.StructuredOutputSchema!.AsObject(), state.Construction.Limits.DraftOutputTokens), PlanningJsonContext.Default.LLMRequest);
        var callsBefore = state.ModelCalls;
        if (interrupted)
        {
            runtime.Respond = (_, _) => throw new OperationCanceledException();
            _ = await Advance(state, runtime);
            state = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall is not null));
            Assert.Equal(issuedBytes, JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest));
            state.Status = PlanningStatus.Generating;
            state.Diagnostics = [new("SUBSEQUENT_CONTEXT", "/", "This later diagnostic must not replace the saved request.")];
        }
        var recovery = new TestRuntime { Respond = (r, _) =>
        {
            Assert.Equal(issuedBytes, JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(initialSchema, r.StructuredOutputSchema!.ToJsonString());
            Assert.Equal(version == 4 ? "/inputs/suppliedText/default" : "/inputs/suppliedText",
                Context(r)["diagnostics"]![0]!["locations"]![0]!.ToString());
            return Wire(r, Resolve(r, inputs: [new() { Name = "suppliedText", Required = true, Type = new() { Kind = "string" } }]));
        } };
        state = await Advance(state, recovery);
        Assert.Single(recovery.Calls); Assert.Equal(callsBefore + 1, state.ModelCalls);
        Assert.Equal(2, state.ReplanAttempts); Assert.Empty(state.Diagnostics); Assert.Null(state.PendingCall);
        var accepted = Assert.Single(state.Requirements!.Inputs!);
        Assert.True(accepted.Required); Assert.False(accepted.Type.Nullable); Assert.Null(accepted.Default);
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.Null(state.Plan); Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task DefaultFeedbackBoundsFactsWithoutReplacingNestedConstraintValidation()
    {
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "input")) };
        var state = await Advance(Fresh("Accept optional numeric array entries with default [1]."), runtime);
        var original = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var largeValue = new string('x', 1_000);
        runtime.Respond = (r, _) => Wire(r, Resolve(r, inputs: [new() { Name = "entries", Required = false,
            Type = new() { Kind = "array", Items = new() { Kind = "number" } },
            Default = new() { Kind = "array", Items = Enumerable.Range(0, 10).Select(_ => new TaskValue { Kind = "string", Text = largeValue }).ToList() } }]));
        state = await Advance(state, runtime);
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal("TASK_DEFAULT_INVALID", diagnostic.Code);
        Assert.Equal("/inputs/entries/default", diagnostic.Location);
        Assert.Equal("A default violates its business type. Rejected declaration: type.kind=array, type.nullable=false, default.kind=array, required=false.", diagnostic.Message);
        Assert.Equal(original, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        var context = PlanningCompactConstruction.InterfaceContext(state, Assert.Single(state.Construction!.Compact!.InterfaceExtractions!));
        Assert.DoesNotContain(largeValue, context.ToJsonString(), StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(context["diagnostics"]!.ToJsonString()) < 400);
        Assert.False(state.Construction.Compact.InterfaceExtractions![0].Complete);
    }

    [Fact]
    public async Task ExplicitPublicFilePathRemainsAnInterfaceAlongsideTheFileObligation()
    {
        const string original = "Write a local inventory.json artifact containing a status string, an entries list and an observed item count; return its path as public text output reportPath.";
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "output")) };
        var state = await Advance(Fresh(original), runtime);
        runtime.Respond = (r, _) =>
        {
            Assert.Contains("Explicitly returned paths or contents still declare interfaces.", r.Prompt, StringComparison.Ordinal);
            Assert.Equal(original, Context(r)["sources"]![0]!["text"]!.ToString());
            return Wire(r, Resolve(r,
                outputs: [new() { Name = "reportPath", Required = true, Type = new() { Kind = "string" } }]));
        };
        state = await Advance(state, runtime);
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        var output = Assert.Single(state.Requirements!.Outputs!);
        Assert.Equal("reportPath", output.Name);
        Assert.Equal("string", output.Type.Kind);
        Assert.True(output.Required);
        Assert.Equal(original, Assert.Single(state.Requirements.Outcomes).Description);
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task FileObligationCanExplicitlyDeclinePublicInterfaceWithoutLosingRequirement()
    {
        const string original = "Write an isolated local inventory.json artifact containing a status string, an entries list and an observed item count, then remove the temporary directory.";
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "output")) };
        var state = await Advance(Fresh(original), runtime);
        runtime.Respond = (r, _) =>
        {
            Assert.Contains("A public result is a value returned to the caller", r.Prompt, StringComparison.Ordinal);
            Assert.Contains("writing an artifact and specifying its contents do not themselves declare one. Use no_public_interface when no caller interface is declared, retaining all business obligations.", r.Prompt, StringComparison.Ordinal);
            Assert.Equal(original, Context(r)["sources"]![0]!["text"]!.ToString());
            Assert.Contains(original, Context(r)["originalContext"]![0]!["text"]!.ToString(), StringComparison.Ordinal);
            return Wire(r, Resolve(r, kind: "no_public_interface"));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Requirements!.Inputs!); Assert.Empty(state.Requirements.Outputs!);
        Assert.Equal(original, Assert.Single(state.Requirements.Outcomes).Description);
        var extraction = Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
        Assert.True(extraction.Complete);
        Assert.Equal("no_public_interface", Assert.Single(extraction.Dispositions!).Kind);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        var cloned = PlannerFixture.Clone(state);
        Assert.True(PlanningCompactConstruction.IntentDeclarationsComplete(cloned.Construction!.Compact!));
        cloned.Construction!.Compact!.InterfaceExtractions![0].Dispositions![0].Version = "stale";
        Assert.False(PlanningCompactConstruction.IntentDeclarationsComplete(cloned.Construction.Compact));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("unlinked")]
    public async Task InvalidResolutionDoesNotAcceptOtherwiseValidInterfaces(string defect)
    {
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "input")) };
        var state = await Advance(Fresh("Accept caller input suppliedText as required text."), runtime);
        var original = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        runtime.Respond = (r, _) =>
        {
            var response = Resolve(r, inputs: [new() { Name = "suppliedText", Type = new() { Kind = "string" }, Required = true }]);
            if (defect == "missing") response["interfaces"]!["dispositions"] = new JsonArray();
            else if (defect == "stale") response["interfaces"]!["dispositions"]![0]!["evidence"]![0]!["version"] = "stale";
            else response["interfaces"]!["dispositions"]![0]!["inputs"] = new JsonArray("otherName");
            return Wire(r, response);
        };
        state = await Advance(state, runtime);
        Assert.False(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.Equal(original, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.NotEmpty(state.Diagnostics); Assert.Empty(state.Construction.Checkpoints);
        Assert.False(PlanningCompactConstruction.IntentDeclarationsComplete(state.Construction.Compact));
    }

    [Fact]
    public async Task MissingTypeClarifiesThenUsesTheExactAnswerBeforeInterfaceAcceptance()
    {
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "input")) };
        var state = await Advance(Fresh("Accept caller input payload; its type is not specified."), runtime);
        runtime.Respond = (r, _) => Wire(r, new JsonObject
        {
            ["sourceId"] = Context(r)["sources"]![0]!["id"]!.ToString(), ["interfaces"] = null, ["evidenceRequests"] = null,
            ["clarifications"] = new JsonArray(new JsonObject { ["id"] = "payloadType", ["question"] = "What type does payload carry?",
                ["alternatives"] = new JsonArray(), ["recommended"] = null })
        });
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Clarification, state.Status);
        Assert.Empty(state.Requirements!.Inputs!);
        Assert.False(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.Empty(state.Construction.Checkpoints);
        const string answer = "The caller input payload is required text, with no default.";
        var pendingId = state.Construction.Compact.InterfaceExtractions![0].Id;
        runtime.Respond = (r, _) => Wire(r, Classify(r, "input"));
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = [new("payloadType", Text: answer)] }, runtime, PlannerFixture.Ct);
        // Answer handling is model-free; its exact source must be indexed before
        // resolving either the original declaration or its answer constraints.
        if (state.Construction!.Compact!.IndexedAnswers == 0) state = await Advance(state, runtime);
        Assert.Empty(state.Requirements!.Inputs!);
        runtime.Respond = (r, _) =>
        {
            var context = Context(r);
            var supplied = context["originalContext"]!.AsArray().Single(s => s!["text"]!.ToString().Contains(answer, StringComparison.Ordinal))!;
            var response = Resolve(r, inputs: [new() { Name = "payload", Required = true, Type = new() { Kind = "string" } }]);
            foreach (var disposition in response["interfaces"]!["dispositions"]!.AsArray())
                disposition!["evidence"]!.AsArray().Add(new JsonObject
                { ["sourceId"] = supplied["id"]!.ToString(), ["version"] = supplied["version"]!.ToString() });
            return Wire(r, response);
        };
        state = await Advance(state, runtime);
        Assert.True(state.Construction!.Compact!.InterfaceExtractions!.Single(e => e.Id == pendingId).Complete);
        Assert.Equal("string", Assert.Single(state.Requirements!.Inputs!).Type.Kind);
        Assert.Contains(state.Requirements.Outcomes, o => o.Description.Contains("type is not specified", StringComparison.Ordinal));
        Assert.Contains(state.AnswerHistory!.Single().Answers, a => a.Text == answer);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task InterruptedResolutionRecoversItsExactIssuedContextAndAccounting(int version)
    {
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "output")) };
        var state = await Advance(PlannerFixture.CapturedPresentation(Fresh("Write the local report file."), version), runtime);
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall is not null));
        saved.Status = PlanningStatus.Generating;
        var issued = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls; var repairs = saved.ReplanAttempts;
        saved.Request.Prompt += " Retain this subsequently supplied constraint.";
        var recovery = new TestRuntime { Respond = (r, _) => Wire(r, Resolve(r, kind: "no_public_interface")) };
        var resumed = await Advance(saved, recovery);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(recovery.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, resumed.ModelCalls); Assert.Equal(repairs, resumed.ReplanAttempts);
        Assert.Equal(version, resumed.Construction!.Compact!.PresentationVersion);
        Assert.True(Assert.Single(resumed.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.False(PlanningCompactConstruction.SourcesComplete(resumed));
    }

    [Fact]
    public async Task VersionFourGuidanceAloneChangesWhileRetainedVersionThreeExtractionRemainsExact()
    {
        var historical = PlannerFixture.CapturedPresentation(Fresh("Accept required text input suppliedText."), 3);
        var retained = JsonSerializer.Serialize(historical, PlanningJsonContext.Default.PlanningSession);
        PlanningSessionCreation.Capture(historical);
        Assert.Equal(retained, JsonSerializer.Serialize(historical, PlanningJsonContext.Default.PlanningSession));
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "input")) };
        historical = await Advance(historical, runtime);
        Assert.Equal(3, historical.Construction!.Compact!.PresentationVersion);
        var current = PlannerFixture.Clone(historical);
        current.Construction!.Compact!.PresentationVersion = 4;
        current.Request.Options["construction_presentation_version"] = 4;
        var historicalExtraction = Assert.Single(historical.Construction.Compact.InterfaceExtractions!);
        var currentExtraction = Assert.Single(current.Construction.Compact.InterfaceExtractions!);
        Assert.Equal("2310da1959e1defcbe772a9bd5c9c5c27bc92f27a7f1263fc2c120726a1aea88",
            PlanningGraphCompiler.Fingerprint(PlanningCompactConstruction.InterfaceGuidance(historical)));
        Assert.Equal(PlanningCompactConstruction.InterfaceContext(historical, historicalExtraction).ToJsonString(),
            PlanningCompactConstruction.InterfaceContext(current, currentExtraction).ToJsonString());
        Assert.Equal(PlanningCompactConstruction.ResolvableInterfaceSchema(historical, historicalExtraction).ToJsonString(),
            PlanningCompactConstruction.ResolvableInterfaceSchema(current, currentExtraction).ToJsonString());
        runtime.Respond = (r, _) => Wire(r, Resolve(r,
            inputs: [new() { Name = "suppliedText", Type = new() { Kind = "string" }, Required = true }]));
        historical = await Advance(historical, runtime);
        var candidateRuntime = new TestRuntime { Respond = runtime.Respond };
        current = await Advance(current, candidateRuntime);
        var control = runtime.Calls[^1];
        var candidate = Assert.Single(candidateRuntime.Calls);
        Assert.Equal(Context(control).ToJsonString(), Context(candidate).ToJsonString());
        Assert.Equal(control.StructuredOutputSchema!.ToJsonString(), candidate.StructuredOutputSchema!.ToJsonString());
        var normalized = JsonSerializer.Deserialize(JsonSerializer.Serialize(candidate, PlanningJsonContext.Default.LLMRequest),
            PlanningJsonContext.Default.LLMRequest)!;
        normalized.Prompt = control.Prompt;
        normalized.ClientRequestId = control.ClientRequestId;
        Assert.Equal(JsonSerializer.Serialize(control, PlanningJsonContext.Default.LLMRequest),
            JsonSerializer.Serialize(normalized, PlanningJsonContext.Default.LLMRequest));
        var instructions = new[] { control, candidate }.Select(request =>
            System.Text.Encoding.UTF8.GetByteCount(request.Prompt[..request.Prompt.LastIndexOf('\n')])).ToArray();
        Assert.Equal(new[] { 1618, 1451 }, instructions);
        var measured = new[] { control, candidate }.Select(request => PlanningRequestMeasurements.Measure(request,
            PlanningCompactConstruction.IntentPurpose, PlanningConstructionInputPolicy.EffectiveLimit(current.Request,
                PlanningCompactConstruction.IntentPurpose))).ToArray();
        Assert.All(measured, measurement => Assert.Equal(measurement.CompleteRequestBytes,
            measurement.Components.Sum(component => component.SerializedBytes)));
        Assert.True(measured[1].CompleteRequestBytes < measured[0].CompleteRequestBytes);
        output.WriteLine(new JsonObject
        {
            ["schema_hash"] = PlanningGraphCompiler.Fingerprint(control.StructuredOutputSchema.ToJsonString()),
            ["context_hash"] = PlanningGraphCompiler.Fingerprint(Context(control).ToJsonString()),
            ["control_instruction_bytes"] = instructions[0], ["candidate_instruction_bytes"] = instructions[1],
            ["control_request_bytes"] = measured[0].CompleteRequestBytes, ["candidate_request_bytes"] = measured[1].CompleteRequestBytes,
            ["control_estimated_tokens"] = measured[0].EstimatedInputTokens, ["candidate_estimated_tokens"] = measured[1].EstimatedInputTokens,
            ["schema_bytes"] = System.Text.Encoding.UTF8.GetByteCount(control.StructuredOutputSchema.ToJsonString()),
            ["context_bytes"] = System.Text.Encoding.UTF8.GetByteCount(Context(control).ToJsonString()),
            ["provider_calls"] = 0
        }.ToJsonString());
        Assert.Equal(3, historical.Construction!.Compact!.PresentationVersion);
        Assert.True(PlanningCompactConstruction.IntentDeclarationsComplete(historical.Construction.Compact));
        Assert.Equal("suppliedText", Assert.Single(historical.Requirements!.Inputs!).Name);
        Assert.Equal(JsonSerializer.Serialize(historical.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            JsonSerializer.Serialize(current.Requirements, PlanningJsonContext.Default.PlanningRequirements));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CompletedExtractionCannotLoseItsOriginalDispositionBeforeGeneration(int version)
    {
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "output")) };
        var state = await Advance(PlannerFixture.CapturedPresentation(Fresh("Write the local report file."), version), runtime);
        runtime.Respond = (r, _) => Wire(r, Resolve(r, kind: "no_public_interface"));
        state = await Advance(state, runtime);
        Assert.True(PlanningCompactConstruction.IntentDeclarationsComplete(state.Construction!.Compact!));
        var retained = PlannerFixture.Clone(state);
        Assert.Single(retained.Construction!.Compact!.InterfaceExtractions!).Dispositions = null;
        Assert.False(PlanningCompactConstruction.IntentDeclarationsComplete(retained.Construction.Compact));
        var calls = runtime.Calls.Count;
        await Assert.ThrowsAsync<PlanningConflictException>(() => Advance(retained, runtime));
        Assert.Equal(calls, runtime.Calls.Count);
        Assert.Empty(retained.Construction.Checkpoints);
        Assert.Equal(0, retained.ReplanAttempts);
    }

    [Fact]
    public async Task OriginalEvidenceIsAdmittedAndRecoveredWithinTheSameIntentPhase()
    {
        const string typeDeclaration = "The caller value is required text with no default. ";
        var original = string.Concat(Enumerable.Repeat("Preserve each supplied value without changing it. ", 20)) + typeDeclaration +
            string.Concat(Enumerable.Repeat("Preserve each supplied value without changing it. ", 80)) + "The public caller input is named suppliedText.";
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r,
            Context(r)["sources"]![0]!["text"]!.ToString().Contains("suppliedText", StringComparison.Ordinal) ? "input" : "none")) };
        var state = Fresh(original);
        do { state = await Advance(state, runtime); }
        while (state.Construction!.Compact!.Sources.Any(s => !s.Indexed));
        Assert.True(state.Construction.Compact.Sources.Count(s => s.Kind == "page") > 3);
        string? requested = null;
        runtime.Respond = (r, _) =>
        {
            var context = Context(r);
            var supplied = context["originalContext"]!.AsArray().Select(s => s!["id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
            requested = context["originalDirectory"]!.AsArray().First(s => !supplied.Contains(s!["id"]!.ToString()) &&
                state.Construction!.Compact!.Sources.Single(originalSource => originalSource.Id == s["id"]!.ToString())
                    .Text.Contains(typeDeclaration, StringComparison.Ordinal))!["evidence"]!.ToString();
            return Wire(r, new JsonObject { ["sourceId"] = context["sources"]![0]!["id"]!.ToString(), ["interfaces"] = null,
                ["evidenceRequests"] = new JsonArray(requested), ["clarifications"] = null });
        };
        state = await Advance(state, runtime);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(requested![7..], Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).EvidenceSourceIds!);
        Assert.False(state.Construction.Compact.InterfaceExtractions![0].Complete);
        state = PlannerFixture.Clone(state);
        JsonObject? followupContext = null;
        runtime.Respond = (r, _) =>
        {
            followupContext = Context(r);
            var response = Resolve(r, inputs: [new() { Name = "suppliedText", Type = new() { Kind = "string" }, Required = true }]);
            var originalSource = state.Construction!.Compact!.Sources.Single(source => source.Id == requested[7..]);
            response["interfaces"]!["dispositions"]![0]!["evidence"]!.AsArray().Add(new JsonObject
            { ["sourceId"] = originalSource.Id, ["version"] = originalSource.Version });
            return Wire(r, response);
        };
        state = await Advance(state, runtime);
        Assert.NotNull(followupContext);
        Assert.NotNull(followupContext["evidence"]![requested]);
        Assert.Contains(typeDeclaration, followupContext["evidence"]![requested]!["text"]!.ToString(), StringComparison.Ordinal);
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete,
            string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(0, runtime.Discoveries);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.All(runtime.Calls, r => Assert.Equal(PlanningCompactConstruction.IntentPurpose, r.StructuredOutputSchema!["title"]!.ToString()));
    }
}
