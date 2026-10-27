using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactIntentResolutionTests(ITestOutputHelper output)
{
    private const string PreviousInterfaceGuidance = """
        Resolve candidate sources from the supplied originals; classifications are provisional. Return exactly one of interfaces, evidenceRequests or clarifications. Reuse supplied evidence; retrieve missing originals or clarify missing names/types. Use any only for explicitly unconstrained data.
        For interfaces, give each candidate source one disposition with its exact ID/version, displayed evidence citations, declared names and reason. Extract all explicit caller inputs/public results, merging only the same interface. Otherwise use no_public_interface, retaining business obligations. Artifact contents, effects and resources alone are not public results; explicitly returned paths/contents are.
        Preserve names, types, constraints, enums, nullability and literal defaults; existing declarations are immutable. Examples add no restrictions. Absent array bounds are null; maxItems:0 permits only []. String enum:null means unrestricted values; a list is the declared exhaustive set. default:null means no default; {"kind":"null"} is an explicit null literal. Outputs have no defaults. Emit compact JSON without whitespace outside strings; preserve string contents and verbatim originals exactly.
        """;

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
        Assert.Equal(11, state.Construction!.Compact!.PresentationVersion);
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
    [InlineData(5, false)]
    [InlineData(5, true)]
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
            Assert.Equal(version >= 4 ? "/inputs/suppliedText/default" : "/inputs/suppliedText", diagnostic.Location);
            Assert.Equal("A default violates its business type." + (version >= 4
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
            Assert.Equal(version >= 4 ? "/inputs/suppliedText/default" : "/inputs/suppliedText",
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

    [Theory]
    [InlineData("inputs", "missing", 3)]
    [InlineData("inputs", "unreferenced", 3)]
    [InlineData("inputs", "duplicate", 3)]
    [InlineData("inputs", "both", 3)]
    [InlineData("outputs", "missing", 3)]
    [InlineData("outputs", "unreferenced", 3)]
    [InlineData("outputs", "duplicate", 3)]
    [InlineData("outputs", "both", 3)]
    [InlineData("inputs", "missing", 4)]
    [InlineData("inputs", "unreferenced", 4)]
    [InlineData("inputs", "duplicate", 4)]
    [InlineData("inputs", "both", 4)]
    [InlineData("outputs", "missing", 4)]
    [InlineData("outputs", "unreferenced", 4)]
    [InlineData("outputs", "duplicate", 4)]
    [InlineData("outputs", "both", 4)]
    [InlineData("inputs", "missing", 5)]
    [InlineData("inputs", "unreferenced", 5)]
    [InlineData("inputs", "duplicate", 5)]
    [InlineData("inputs", "both", 5)]
    [InlineData("outputs", "missing", 5)]
    [InlineData("outputs", "unreferenced", 5)]
    [InlineData("outputs", "duplicate", 5)]
    [InlineData("outputs", "both", 5)]
    public async Task TypedInterfaceCoverageReportsTheExactMismatchWithoutAcceptingDeclarations(string direction, string defect, int version)
    {
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "both")) };
        var state = await Advance(PlannerFixture.CapturedPresentation(Fresh("Accept required text suppliedText and return required text resultText."), version), runtime);
        var original = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var repairs = state.ReplanAttempts;
        var name = direction == "inputs" ? "suppliedText" : "resultText";
        runtime.Respond = (r, _) =>
        {
            var response = Resolve(r, inputs: [new() { Name = "suppliedText", Required = true, Type = new() { Kind = "string" } }],
                outputs: [new() { Name = "resultText", Required = true, Type = new() { Kind = "string" } }]);
            var typed = response["interfaces"]![direction]!.AsArray();
            if (defect == "missing") typed.Clear();
            else if (defect == "both") typed[0]!["name"] = "extra";
            else
            {
                var additional = typed[0]!.DeepClone();
                if (defect == "unreferenced") additional["name"] = "extra";
                typed.Add(additional);
            }
            return Wire(r, response);
        };
        state = await Advance(state, runtime);
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal("CONSTRUCTION_SOURCE_COVERAGE", diagnostic.Code);
        Assert.Equal("/interfaces/" + direction, diagnostic.Location);
        if (version < 5) Assert.Equal("Each typed interface must be named by an exact source disposition.", diagnostic.Message);
        else
        {
            Assert.Equal(defect == "duplicate", diagnostic.Message.Contains("duplicate typed names", StringComparison.Ordinal));
            Assert.Equal(defect is "missing" or "both", diagnostic.Message.Contains("missing typed declarations", StringComparison.Ordinal));
            Assert.Equal(defect is "unreferenced" or "both", diagnostic.Message.Contains("typed names without a current-source disposition", StringComparison.Ordinal));
            Assert.Contains(defect == "unreferenced" ? "\"extra\"" : "\"" + name + "\"", diagnostic.Message, StringComparison.Ordinal);
        }
        Assert.Equal(original, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        var extraction = Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
        Assert.False(extraction.Complete); Assert.Null(extraction.Dispositions);
        Assert.Equal(repairs, state.ReplanAttempts); Assert.Empty(state.Construction.Checkpoints);
    }

    [Theory]
    [InlineData("inputs")]
    [InlineData("outputs")]
    public async Task MissingTypedNamesRemainWholeAndBoundedInFeedback(string direction)
    {
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "both")) };
        var state = await Advance(Fresh("Accept declared inputs and return declared results."), runtime);
        var names = Enumerable.Range(0, 12).Select(index => "name_" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 240)).ToArray();
        runtime.Respond = (r, _) =>
        {
            var response = Resolve(r, inputs: [new() { Name = "suppliedText", Type = new() { Kind = "string" } }],
                outputs: [new() { Name = "resultText", Type = new() { Kind = "string" } }]);
            response["interfaces"]![direction] = new JsonArray();
            response["interfaces"]!["dispositions"]![0]![direction] = new JsonArray(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
            return Wire(r, response);
        };
        state = await Advance(state, runtime);
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal("CONSTRUCTION_SOURCE_COVERAGE", diagnostic.Code);
        Assert.Contains(JsonValue.Create(names[0])!.ToJsonString(), diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(names[1], diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("omitted=11", diagnostic.Message, StringComparison.Ordinal);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(diagnostic.Message), 1, 700);
        Assert.False(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterCurrentSourceMayDeclineOrExplicitlyLinkAnUnchangedAcceptedInterface(bool repeat)
    {
        const string initial = "Accept required text suppliedText.";
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "input")) };
        var state = await Advance(Fresh(initial), runtime);
        runtime.Respond = (r, _) => Wire(r, Resolve(r, inputs: [new() { Name = "suppliedText", Required = true, Type = new() { Kind = "string" } }]));
        state = await Advance(state, runtime);
        var accepted = JsonSerializer.Serialize(state.Requirements!.Inputs, PlanningJsonContext.Default.ListTaskInput);
        var later = repeat ? " The same caller input suppliedText is required text." : " Write the report artifact with the supplied contents.";
        state.Request.Prompt += later;
        runtime.Respond = (r, _) => Wire(r, Classify(r, repeat ? "input" : "output"));
        state = await Advance(state, runtime);
        runtime.Respond = (r, _) =>
        {
            Assert.Equal(later, Assert.Single(Context(r)["sources"]!.AsArray())!["text"]!.ToString());
            return Wire(r, repeat ? Resolve(r, inputs: [new() { Name = "suppliedText", Required = true, Type = new() { Kind = "string" } }])
                : Resolve(r, kind: "no_public_interface"));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(accepted, JsonSerializer.Serialize(state.Requirements!.Inputs, PlanningJsonContext.Default.ListTaskInput));
        Assert.Single(state.Requirements.Inputs!); Assert.Empty(state.Requirements.Outputs!);
        Assert.All(state.Construction!.Compact!.InterfaceExtractions!, extraction => Assert.True(extraction.Complete));
        Assert.Equal(2, state.Construction.Compact.InterfaceExtractions!.Count);
        Assert.Equal(repeat ? "interfaces" : "no_public_interface", Assert.Single(state.Construction.Compact.InterfaceExtractions[^1].Dispositions!).Kind);
        Assert.Equal(0, state.ReplanAttempts); Assert.Empty(state.Construction.Checkpoints);
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
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public async Task InterruptedResolutionRecoversItsExactIssuedContextAndAccounting(int version, bool previousConsolidation)
    {
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "output")) };
        var state = await Advance(PlannerFixture.CapturedPresentation(Fresh("Write the local report file."), version), runtime);
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        if (version == 5)
        {
            // Retain both prior v5 instruction sets through the real journal
            // preparation path rather than rebuilding either issued request.
            var historical = PlannerFixture.Clone(state);
            historical.Construction!.Compact!.PresentationVersion = 4;
            historical.Request.Options["construction_presentation_version"] = 4;
            var instructions = previousConsolidation ? PreviousInterfaceGuidance : PlanningCompactConstruction.InterfaceGuidance(historical);
            if (!previousConsolidation) Assert.Equal("70b0782ec6d15d20faa176d7db5c296dccaa01d8796f02a10bc781240358aecb",
                PlanningGraphCompiler.Fingerprint(instructions));
            Assert.NotEqual(PlanningCompactConstruction.InterfaceGuidance(state), instructions);
            var extraction = Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
            var request = PlanningCompactConstruction.Request(instructions,
                PlanningCompactConstruction.InterfaceContext(state, extraction),
                PlanningCompactConstruction.ResolvableInterfaceSchema(state, extraction), PlanningCompactConstruction.IntentPurpose);
            using var correction = new PlanningCorrectionAttempt(state, false);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(state, runtime,
                PlanningCompactConstruction.IntentPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject(),
                PlannerFixture.Ct, state.Construction.Limits.DraftOutputTokens, correction));
        }
        else _ = await Advance(state, runtime);
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

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 5)]
    public async Task ConsolidatedGuidanceAloneChangesWhileHistoricalExtractionRemainsExact(
        int controlVersion, int candidateVersion)
    {
        var historical = PlannerFixture.CapturedPresentation(Fresh("Accept required text input suppliedText."), controlVersion);
        var retained = JsonSerializer.Serialize(historical, PlanningJsonContext.Default.PlanningSession);
        PlanningSessionCreation.Capture(historical);
        Assert.Equal(retained, JsonSerializer.Serialize(historical, PlanningJsonContext.Default.PlanningSession));
        var runtime = new TestRuntime { Respond = (r, _) => Wire(r, Classify(r, "input")) };
        historical = await Advance(historical, runtime);
        Assert.Equal(controlVersion, historical.Construction!.Compact!.PresentationVersion);
        var current = PlannerFixture.Clone(historical);
        current.Construction!.Compact!.PresentationVersion = candidateVersion;
        current.Request.Options["construction_presentation_version"] = candidateVersion;
        var historicalExtraction = Assert.Single(historical.Construction.Compact.InterfaceExtractions!);
        var currentExtraction = Assert.Single(current.Construction.Compact.InterfaceExtractions!);
        Assert.Equal(controlVersion == 3 ? "2310da1959e1defcbe772a9bd5c9c5c27bc92f27a7f1263fc2c120726a1aea88" :
            "70b0782ec6d15d20faa176d7db5c296dccaa01d8796f02a10bc781240358aecb",
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
        Assert.NotEqual(control.Prompt, candidate.Prompt);
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
        var measured = new[] { control, candidate }.Select(request => PlanningRequestMeasurements.Measure(request,
            PlanningCompactConstruction.IntentPurpose, PlanningConstructionInputPolicy.EffectiveLimit(current.Request,
                PlanningCompactConstruction.IntentPurpose))).ToArray();
        Assert.All(measured, measurement => Assert.Equal(measurement.CompleteRequestBytes,
            measurement.Components.Sum(component => component.SerializedBytes)));
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
        Assert.Equal(controlVersion, historical.Construction!.Compact!.PresentationVersion);
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

    private static async Task<(PlanningSession State, TestRuntime Runtime)> NeighboringOriginals(int containingPage, int version = 5)
    {
        var page = 0;
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request,
            Classify(request, page++ == containingPage ? "output" : "none")) };
        var original = string.Concat(Enumerable.Repeat(
            "Write the local report with supplied details and preserve each original value without changing its meaning. ", 45));
        var state = PlannerFixture.CapturedPresentation(Fresh(original), version);
        do { state = await Advance(state, runtime); }
        while (state.Construction!.Compact!.Sources.Any(source => !source.Indexed));
        Assert.True(state.Construction.Compact.Sources.Count(source => source.Kind == "page") >= 5);
        Assert.Single(state.Construction.Compact.InterfaceExtractions!);
        return (state, runtime);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ImmediateOriginalNeighborsFillOnlyUnusedInterfaceContextSlots(int containingPage)
    {
        var (state, runtime) = await NeighboringOriginals(containingPage);
        var pages = state.Construction!.Compact!.Sources.Where(source => source.Kind == "page").ToArray();
        var extraction = Assert.Single(state.Construction.Compact.InterfaceExtractions!);
        var candidates = extraction.SourceIds.ToArray();
        var outcomes = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var neighbor = pages[containingPage == 1 ? 2 : 1];
        var calls = state.ModelCalls;
        runtime.Respond = (request, _) =>
        {
            var context = Context(request);
            Assert.Equal(new[] { pages[0].Id, pages[containingPage].Id, neighbor.Id },
                context["originalContext"]!.AsArray().Select(source => source!["id"]!.ToString()));
            Assert.Equal(candidates, context["sources"]!.AsArray().Select(source => source!["id"]!.ToString()));
            var supplied = context["originalContext"]!.AsArray().Single(source => source!["id"]!.ToString() == neighbor.Id)!;
            Assert.Equal(neighbor.Text, supplied["text"]!.ToString());
            Assert.Equal(neighbor.Version, supplied["version"]!.ToString());
            var response = Resolve(request, kind: "no_public_interface");
            response["interfaces"]!["dispositions"]![0]!["evidence"]!.AsArray().Add(new JsonObject
            { ["sourceId"] = neighbor.Id, ["version"] = neighbor.Version });
            return Wire(request, response);
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        var disposition = Assert.Single(state.Construction.Compact.InterfaceExtractions![0].Dispositions!);
        Assert.Equal(Assert.Single(candidates), disposition.SourceId);
        Assert.Equal(neighbor.Version, disposition.Evidence[neighbor.Id]);
        Assert.Equal(outcomes, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts); Assert.Equal(0, runtime.Discoveries);
        Assert.Null(state.Plan); Assert.Empty(state.Construction.Checkpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingOriginalContextPrioritiesKeepAllThreeSlotsBeforeNeighbors(bool latestAnswer)
    {
        var (state, _) = await NeighboringOriginals(2);
        var compact = state.Construction!.Compact!;
        var pages = compact.Sources.Where(source => source.Kind == "page").ToArray();
        var extraction = Assert.Single(compact.InterfaceExtractions!);
        var other = pages[4];
        extraction.SourceIds.Add(compact.Sources.Single(source => source.Id == other.Id + ":clause:0").Id);
        if (latestAnswer)
        {
            other = new() { Id = "answer:clarification:0", Kind = "answer_page", Text = "Preserve this exact later answer.",
                Version = PlanningGraphCompiler.Fingerprint("Preserve this exact later answer."), Indexed = true };
            compact.Sources.Add(other);
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.InterfaceContext(state, extraction);
        Assert.Equal(new[] { pages[0].Id, pages[2].Id, other.Id },
            context["originalContext"]!.AsArray().Select(source => source!["id"]!.ToString()));
        Assert.Equal(extraction.SourceIds, context["sources"]!.AsArray().Select(source => source!["id"]!.ToString()));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task IssuedInterfaceContextKeepsItsOriginalPagesAcrossRecovery(int version)
    {
        var (state, runtime) = await NeighboringOriginals(2, version);
        var historical = PlannerFixture.Clone(state);
        if (version == 5)
        {
            historical.Construction!.Compact!.PresentationVersion = 4;
            historical.Request.Options["construction_presentation_version"] = 4;
        }
        var extraction = Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
        var oldContext = PlanningCompactConstruction.InterfaceContext(historical, extraction);
        Assert.Equal(2, oldContext["originalContext"]!.AsArray().Count);
        var currentContext = PlanningCompactConstruction.InterfaceContext(state, extraction);
        Assert.Equal(version == 5 ? 3 : 2, currentContext["originalContext"]!.AsArray().Count);
        if (version < 5) Assert.Equal(oldContext.ToJsonString(), currentContext.ToJsonString());
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.InterfaceGuidance(state), oldContext,
            PlanningCompactConstruction.ResolvableInterfaceSchema(historical, extraction), PlanningCompactConstruction.IntentPurpose);
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        using var correction = new PlanningCorrectionAttempt(state, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningCompactConstruction.IntentPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject(),
            PlannerFixture.Ct, state.Construction.Limits.DraftOutputTokens, correction));
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
        saved.Status = PlanningStatus.Generating;
        var issued = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls; var repairs = saved.ReplanAttempts;
        var recovery = new TestRuntime { Respond = (received, _) =>
        {
            Assert.Equal(issued, JsonSerializer.Serialize(received, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(2, Context(received)["originalContext"]!.AsArray().Count);
            return Wire(received, Resolve(received, kind: "no_public_interface"));
        } };
        var resumed = await Advance(saved, recovery);
        Assert.Single(recovery.Calls); Assert.Empty(resumed.Diagnostics);
        Assert.Equal(calls, resumed.ModelCalls); Assert.Equal(repairs, resumed.ReplanAttempts);
        Assert.True(Assert.Single(resumed.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.Equal(version, resumed.Construction.Compact.PresentationVersion);
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
