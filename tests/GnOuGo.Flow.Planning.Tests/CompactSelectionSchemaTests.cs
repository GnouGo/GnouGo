using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactSelectionSchemaTests
{
    [Fact]
    public async Task ScopeChoicesFollowTheDisplayedOpenFrontierAndItsExactNextPage()
    {
        var state = await Ready();
        AddScopes(state, 18);
        state.Plan!.Root.Tasks.Add(new() { Id = "closed", Kind = "sequence", Body = new() });
        var plan = PlanningConstruction.Hash(state.Plan);
        var context = PlanningCompactConstruction.SelectionContext(state);
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Equal(DisplayedScopes(context), ScopeDomain(schema));
        Assert.Equal(8, ScopeDomain(schema).Length);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(ScopedResponse(state, "/root"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(ScopedResponse(state, "/tasks/closed/body"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(ScopedResponse(state, "/tasks/scope_8/body"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(ScopedResponse(state, "/tasks/invented/body"), schema));

        var next = context["nextScopes"]!.ToString();
        Assert.Equal("page:scopes:8", next);
        var runtime = new TestRuntime { Respond = (_, _) => new()
        { Json = new JsonObject { ["baseHash"] = plan, ["step"] = null, ["clarifications"] = null, ["evidenceRequests"] = new JsonArray(next) } } };
        var calls = state.ModelCalls;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(8, state.Construction!.ScopeCursor);
        var exact = state.Construction.Evidence![next]!.ToJsonString();
        state = PlannerFixture.Clone(state);
        runtime.Respond = (request, _) =>
        {
            var displayed = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            Assert.Equal(exact, displayed["evidence"]![next]!.ToJsonString());
            Assert.Equal(DisplayedScopes(displayed), ScopeDomain(request.StructuredOutputSchema!.AsObject()));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(ScopedResponse(state, "/root"), request.StructuredOutputSchema));
            var response = ScopedResponse(state, "/tasks/scope_8/body");
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema));
            return new() { Json = response };
        };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("/tasks/scope_8/body", state.Construction!.Compact!.CurrentStep!.Scope);
        Assert.Equal(calls + 2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task ReplacementScopeRemainsFixedOutsideTheCurrentDirectoryPage()
    {
        var state = await Ready();
        AddScopes(state, 18);
        var compact = state.Construction!.Compact!;
        compact.ReplacementScope = "/tasks/scope_16/body";
        compact.ReplacementRequirements = ["req_0"];
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Equal([compact.ReplacementScope], ScopeDomain(schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(ScopedResponse(state, "/root"), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(ScopedResponse(state, compact.ReplacementScope), schema));
        var step = ScopedResponse(state, compact.ReplacementScope)["step"]!.Deserialize(PlanningJsonContext.Default.PlanningConstructionStep)!;
        PlanningCompactConstruction.ValidateSelection(state, step);
        PlanningConstruction.LoadEvidence(state, ["page:scopes:8"]);
        Assert.Equal([compact.ReplacementScope], ScopeDomain(PlanningCompactConstruction.SelectionSchema(state)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviouslyIssuedBroadScopeSelectionKeepsItsExactReceiptAndAccounting(bool historicalPolicy)
    {
        var state = await Ready();
        AddScopes(state, 18);
        var historical = PlannerFixture.Clone(state);
        historical.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        historical.Construction!.Limits.PhasePolicy = null;
        var broad = PlanningCompactConstruction.SelectionSchema(historical);
        var response = ScopedResponse(state, "/tasks/scope_16/body");
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, broad));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, PlanningCompactConstruction.SelectionSchema(state)));
        if (historicalPolicy) state = historical;
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state),
            PlanningCompactConstruction.SelectionContext(state), broad, PlanningCompactConstruction.SelectPurpose);
        request = PlanningGenerationPolicy.Apply(request, state.Request.Generation);
        request.StructuredOutputStrict = true;
        request.UseBackgroundMode = true;
        request.ClientRequestId = state.Request.SessionId + ":" + (state.ModelCalls + 1) + ":" +
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        state.ModelCalls++;
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = PlanningCompactConstruction.SelectPurpose, Request = request,
            CorrectionAccounting = historicalPolicy ? null : 0 };
        state = PlannerFixture.Clone(state);
        var issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = state.ModelCalls;
        var corrections = state.ReplanAttempts;
        var plan = PlanningConstruction.Hash(state.Plan);
        var receipts = new Dictionary<string, LLMResponse>(StringComparer.Ordinal) { [state.PendingCall.Id] = new() { Json = response } };
        var runtime = new TestRuntime { Respond = (saved, _) =>
        {
            Assert.Equal(issued, JsonSerializer.Serialize(saved, PlanningJsonContext.Default.LLMRequest));
            return receipts[saved.ClientRequestId!];
        } };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.Null(state.PendingCall);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(corrections, state.ReplanAttempts);
        Assert.Equal("/tasks/scope_16/body", state.Construction!.Compact!.CurrentStep!.Scope);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task LargeScopeFrontierKeepsOnlyEightDisplayedChoicesInTheCompleteRequest()
    {
        var state = await Ready();
        AddScopes(state, 18);
        var before = PlanningCompactConstruction.SelectionSchema(state);
        AddScopes(state, 500);
        var context = PlanningCompactConstruction.SelectionContext(state);
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Equal(ScopeDomain(before), ScopeDomain(schema));
        Assert.Equal(DisplayedScopes(context), ScopeDomain(schema));
        Assert.Equal(8, ScopeDomain(schema).Length);
        Assert.DoesNotContain("/tasks/scope_499/body", schema.ToJsonString(), StringComparison.Ordinal);
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state),
            context, schema, PlanningCompactConstruction.SelectPurpose);
        request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose, request.Prompt,
            request.StructuredOutputSchema!.AsObject());
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens;
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, limit).EstimatedInputTokens, 1, limit);
        Assert.Equal(500, state.Construction!.OpenScopes.Count);
    }

    private static void AddScopes(PlanningSession state, int count)
    {
        state.Plan ??= new();
        for (var index = state.Plan.Root.Tasks.Count + 1; index < count; index++)
            state.Plan.Root.Tasks.Add(new() { Id = "scope_" + index, Kind = "sequence", Body = new() });
        state.Construction!.OpenScopes = ["/root", .. state.Plan.Root.Tasks.Select(task => "/tasks/" + task.Id + "/body")];
    }

    private static JsonObject ScopedResponse(PlanningSession state, string scope)
    {
        var response = Response(state, "req_0");
        response["step"]!["scope"] = scope;
        return response;
    }

    private static string[] DisplayedScopes(JsonNode context) => context["scopes"]!.AsArray().Select(scope => scope!["path"]!.ToString()).ToArray();

    private static string[] ScopeDomain(JsonObject schema)
    {
        JsonNode Resolve(JsonNode node)
        {
            while (node["$ref"] is JsonValue reference) node = schema["$defs"]![reference.ToString()["#/$defs/".Length..]]!;
            return node;
        }
        var step = Resolve(Resolve(schema["properties"]!["step"]!)["anyOf"]![0]!);
        return Resolve(step["properties"]!["scope"]!)["enum"]!.AsArray().Select(value => value!.ToString()).ToArray();
    }

    [Theory]
    [InlineData("req_3")]
    [InlineData("request:0:clause:0")]
    [InlineData("request:0:clause:3")]
    [InlineData("unknown")]
    public async Task FreshSchemaRejectsUndisplayedRequirementsAndSourceIdentities(string invalid)
    {
        var state = await Ready();
        AppendOriginalPage(state);
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Equal(["req_0", "req_1", "req_2"], Domain(schema));
        Assert.Equal(["req_0", "req_1", "req_2"], Domain(schema, "closeRequirementIds"));
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(state, "req_0"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(state, invalid), schema));
        var response = Response(state, "req_0");
        response["step"]!["closeRequirementIds"] = new JsonArray(invalid);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, schema));
        Assert.Contains(PlanningCompactConstruction.SelectionContext(state)["globalConstraints"]!.AsArray(),
            source => source!["id"]!.ToString() == "request:0:clause:3");
    }

    [Theory]
    [InlineData("original")]
    [InlineData("status")]
    [InlineData("version")]
    public async Task RequirementEvidenceExpandsTheDomainOnlyWhileItsExactRecordMatches(string changed)
    {
        var state = await Ready();
        AppendOriginalPage(state);
        PlanningConstruction.LoadEvidence(state, ["page:requirements:3"]);
        Assert.Equal(["req_0", "req_1", "req_2", "req_3", "req_4"], Domain(PlanningCompactConstruction.SelectionSchema(state)));
        var record = state.Construction!.Evidence!["page:requirements:3"]!["items"]![0]!;
        if (changed == "version") record["sources"]![0]!["version"] = "stale";
        else record[changed] = "changed";
        Assert.Equal(["req_0", "req_1", "req_2", "req_4"], Domain(PlanningCompactConstruction.SelectionSchema(state)));
        PlanningConstruction.LoadEvidence(state, ["requirement:req_3"]);
        Assert.Equal(["req_0", "req_1", "req_2", "req_3", "req_4"], Domain(PlanningCompactConstruction.SelectionSchema(state)));
        Assert.All(state.Construction.Requirements, requirement => Assert.Equal("remaining", requirement.Status));
    }

    [Fact]
    public async Task FixedReselectionDomainPreservesItsExactRequirementBoundary()
    {
        var state = await Ready();
        PlanningConstruction.LoadEvidence(state, ["page:requirements:0"]);
        state.Construction!.Compact!.ReplacementScope = "/root";
        state.Construction.Compact.ReplacementRequirements = ["req_2", "req_1"];
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Equal(["req_1", "req_2"], Domain(schema));
        Assert.Equal(["req_1", "req_2"], Domain(schema, "closeRequirementIds"));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(state, "req_0"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(state, "req_1"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(state, "req_1", "req_2", "req_1"), schema));
        var response = Response(state, "req_1", "req_2");
        response["step"]!["closeRequirementIds"] = new JsonArray("req_1");
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, schema));
        var step = response["step"]!.Deserialize(PlanningJsonContext.Default.PlanningConstructionStep)!;
        PlanningCompactConstruction.ValidateSelection(state, step);
        step.RequirementIds = ["req_1"];
        Assert.Contains(Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, step)).Diagnostics,
            diagnostic => diagnostic.Code == "CONSTRUCTION_STEP_INVALID");
        step.RequirementIds = ["req_1", "req_2", "req_1"];
        Assert.Contains(Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, step)).Diagnostics,
            diagnostic => diagnostic.Code == "CONSTRUCTION_STEP_INVALID");

        var historical = PlannerFixture.Clone(state);
        historical.Construction!.Compact!.PresentationVersion = null;
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(state, "req_1"),
            PlanningCompactConstruction.SelectionSchema(historical)));
    }

    [Fact]
    public async Task EmptyRequirementDomainAllowsEvidenceButCannotInventAStep()
    {
        var state = await Ready();
        state.Requirements!.Outcomes.Clear();
        state.Construction!.Requirements.Clear();
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Equal("null", schema["properties"]!["step"]!["type"]!.ToString());
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var response = Response(state, "invented");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, schema));
        response["step"] = null;
        response["evidenceRequests"] = new JsonArray("page:requirements:0");
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, schema));
    }

    [Fact]
    public async Task FiveHundredOriginalRequirementsKeepOnlyDisplayedIdsInTheCompleteRequest()
    {
        var state = await Ready();
        for (var index = state.Requirements!.Outcomes.Count; index < 500; index++)
        {
            var id = "req_" + index;
            var source = "retained:clause:" + index;
            var text = "Preserve original obligation " + index + ".";
            state.Requirements.Outcomes.Add(new(id, text));
            state.Construction!.Requirements.Add(new() { Id = id });
            state.Construction.Compact!.Sources.Add(new()
            { Id = source, Text = text, Version = PlanningGraphCompiler.Fingerprint(text), Kind = "requirement", Indexed = true });
            state.Construction.Compact.RequirementSources[id] = [source];
        }
        PlanningConstruction.LoadEvidence(state, ["page:requirements:499"]);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Equal(["req_0", "req_1", "req_2", "req_499"], Domain(schema));
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectInstructions,
            PlanningCompactConstruction.SelectionContext(state), schema, PlanningCompactConstruction.SelectPurpose);
        request = PlanningGenerationPolicy.Apply(new LLMRequest
        {
            Prompt = request.Prompt, StructuredOutputSchema = request.StructuredOutputSchema, StructuredOutputStrict = true,
            Provider = "OpenAi", Model = "retained-model", UseBackgroundMode = true, MaxTokens = 8192,
            ClientRequestId = new string('s', 64) + ":9:" + new string('h', 64)
        }, state.Request.Generation);
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, 3000).EstimatedInputTokens, 1, 3000);
        Assert.Contains("business action to execute", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("discovery is a separate host phase", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task SelectionEvidenceFitsWithOriginalGlobalClausesAndExactSourceVersions()
    {
        var state = await Ready();
        var compact = state.Construction!.Compact!;
        compact.GlobalSourceIds.Clear();
        for (var index = 0; index < 7; index++)
        {
            var text = $"Constraint {index}: Preserve supplied values, order, duplicates and exact identities throughout this work. ";
            var source = new PlanningConstructionSource
            {
                Id = "request:0:clause:" + (index + 3), Text = text,
                Version = PlanningGraphCompiler.Fingerprint(text), Kind = "global", Indexed = true
            };
            compact.Sources.RemoveAll(s => s.Id == source.Id);
            compact.Sources.Add(source);
            compact.GlobalSourceIds.Add(source.Id);
        }
        state.Requirements!.Inputs = [
            new() { Name = "originalValues", Type = new() { Kind = "string" } },
            new() { Name = "instructions", Type = new() { Kind = "string" } }];
        state.Requirements.Outputs = [
            new() { Name = "result", Type = new() { Kind = "string" } },
            new() { Name = "report", Type = new() { Kind = "object" } }];
        PlanningConstruction.LoadEvidence(state, ["page:requirements:1"]);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.SelectionContext(state);
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state),
            context, PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
        request = PlanningGenerationPolicy.Apply(new LLMRequest
        {
            Prompt = request.Prompt, StructuredOutputSchema = request.StructuredOutputSchema, StructuredOutputStrict = true,
            Provider = "OpenAi", Model = "retained-model", UseBackgroundMode = true, MaxTokens = 8192,
            ClientRequestId = new string('s', 64) + ":9:" + new string('h', 64)
        }, state.Request.Generation);
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens;
        var measurement = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, limit);
        Assert.True(measurement.EstimatedInputTokens <= limit, $"Complete selection request: {measurement.EstimatedInputTokens} tokens; ceiling: {limit}.");
        Assert.Equal(7, context["globalConstraints"]!.AsArray().Count);
        foreach (var source in compact.Sources.Where(s => compact.GlobalSourceIds.Contains(s.Id)))
        {
            var presented = context["globalConstraints"]!.AsArray().Single(s => s!["id"]!.ToString() == source.Id)!;
            Assert.Equal(source.Text, presented["text"]!.ToString());
            Assert.Equal(source.Version, presented["version"]!.ToString());
        }
        Assert.True(JsonNode.DeepEquals(state.Construction.Evidence, context["evidence"]));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        state.Construction.Compact!.PresentationVersion = null;
        state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        state.Construction.Limits.PhasePolicy = null;
        Assert.Equal(PlanningCompactConstruction.SelectInstructions, PlanningCompactConstruction.SelectGuidance(state));
        Assert.Equal(PlanningCompactConstruction.DiscoveryInstructions, PlanningCompactConstruction.DiscoveryGuidance(state));
        Assert.Equal(state.Requirements.Outcomes.Count,
            PlanningCompactConstruction.SelectionContext(state)["originalRequirementCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task RetainedBroadSelectionRequestResumesItsExactSchemaWithoutNarrowingAuthority()
    {
        var state = await Ready();
        AppendOriginalPage(state);
        var historical = PlannerFixture.Clone(state);
        historical.Construction!.Compact!.PresentationVersion = null;
        historical.Request.Options.Remove("construction_presentation_version");
        var broad = PlanningCompactConstruction.SelectionSchema(historical);
        Assert.Null(broad["properties"]!["step"]!["anyOf"]![0]!["properties"]!["requirementIds"]!["items"]!["enum"]);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(state, "req_3"), broad));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(state, "req_3"), PlanningCompactConstruction.SelectionSchema(state)));
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectInstructions,
            PlanningCompactConstruction.SelectionContext(state), broad, PlanningCompactConstruction.SelectPurpose);
        request = PlanningGenerationPolicy.Apply(request, state.Request.Generation);
        request.StructuredOutputStrict = true;
        request.UseBackgroundMode = true;
        request.ClientRequestId = state.Request.SessionId + ":" + (state.ModelCalls + 1) + ":" +
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        state.ModelCalls++;
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = PlanningCompactConstruction.SelectPurpose, Request = request };
        state = PlannerFixture.Clone(state);
        var issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = state.ModelCalls;
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = Response(state, "req_3") } };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(["req_3"], state.Construction!.Compact!.CurrentStep!.RequirementIds);
    }

    private static void AppendOriginalPage(PlanningSession state)
    {
        var compact = state.Construction!.Compact!;
        var page = "request:" + state.Request.Prompt.Length;
        string[] clauses = ["Observe the next independent input. ", "Preserve that complete observation."];
        var text = string.Concat(clauses);
        state.Request.Prompt += text;
        compact.Sources.Add(new() { Id = page, Text = text, Kind = "page", Indexed = true, Version = PlanningGraphCompiler.Fingerprint(text) });
        for (var index = 0; index < clauses.Length; index++)
        {
            var id = "req_" + state.Requirements!.Outcomes.Count;
            var source = page + ":clause:" + index;
            compact.Sources.Add(new() { Id = source, Text = clauses[index], Kind = "requirement", InterfaceKind = "none", Indexed = true,
                Version = PlanningGraphCompiler.Fingerprint(clauses[index]) });
            compact.RequirementSources.Add(id, [source]);
            state.Requirements.Outcomes.Add(new(id, clauses[index]));
            state.Construction.Requirements.Add(new() { Id = id });
        }
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
    }

    private static string[] Domain(JsonObject schema, string property = "requirementIds")
    {
        JsonNode Resolve(JsonNode node)
        {
            while (node["$ref"] is JsonValue reference)
                node = schema["$defs"]![reference.ToString()["#/$defs/".Length..]]!;
            return node;
        }
        var step = Resolve(Resolve(schema["properties"]!["step"]!)["anyOf"]![0]!);
        var array = Resolve(step["properties"]![property]!);
        return Resolve(array["items"]!)["enum"]!.AsArray().Select(value => value!.ToString()).ToArray();
    }

    private static JsonObject Response(PlanningSession state, params string[] requirements) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["evidenceRequests"] = null, ["clarifications"] = null,
        ["step"] = new JsonObject
        {
            ["scope"] = "/root", ["kind"] = "value", ["objective"] = "Preserve the original supplied values.",
            ["requirementIds"] = new JsonArray(requirements.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["inputs"] = new JsonArray(), ["groupId"] = null, ["dependencyTaskIds"] = new JsonArray(),
            ["expectedOutputs"] = new JsonArray(), ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray()
        }
    };

    private static async Task<PlanningSession> Ready()
    {
        string[] clauses = ["Read the original values. ", "Keep their order. ", "Return the retained values. ", "Preserve exact identities."];
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            return new() { Json = new JsonObject
            {
                ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(),
                ["clauses"] = new JsonArray(clauses.Select((text, index) => (JsonNode)new JsonObject
                { ["text"] = text, ["kind"] = index == clauses.Length - 1 ? "global" : "requirement", ["interfaces"] = "none" }).ToArray())
            } };
        } };
        var state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.CapturedPresentation(new() { Request = new()
        { TenantId = "selection-schema", Prompt = string.Concat(clauses), Options = new() { ["automatic_correction"] = true } } }),
            new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        return state;
    }
}
