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
        var runtime = new TestRuntime();
        runtime.Respond = (request, _) =>
        {
            var host = HostRequest(runtime, request);
            var displayed = JsonNode.Parse(host.Prompt[(host.Prompt.LastIndexOf('\n') + 1)..])!;
            next = displayed["nextScopes"]!.ToString();
            return new() { Json = new JsonObject { ["baseHash"] = plan, ["step"] = null, ["clarifications"] = null,
                ["evidenceRequests"] = new JsonArray(runtime.EvidenceId(request, next)) } };
        };
        var calls = state.ModelCalls;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(int.Parse(next["page:scopes:".Length..], System.Globalization.CultureInfo.InvariantCulture), state.Construction!.ScopeCursor);
        var exact = state.Construction.Evidence![next]!.ToJsonString();
        state = PlannerFixture.Clone(state);
        string? selectedScope = null;
        runtime.Respond = (request, _) =>
        {
            var host = HostRequest(runtime, request);
            var displayed = JsonNode.Parse(host.Prompt[(host.Prompt.LastIndexOf('\n') + 1)..])!;
            Assert.Equal(exact, displayed["evidence"]![next]!.ToJsonString());
            Assert.Equal(DisplayedScopes(displayed), ScopeDomain(request.StructuredOutputSchema!.AsObject()));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(ScopedResponse(state, "/root"), request.StructuredOutputSchema));
            selectedScope = ScopeDomain(request.StructuredOutputSchema!.AsObject())[0];
            var response = ScopedResponse(state, selectedScope);
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema));
            return new() { Json = response };
        };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(selectedScope, state.Construction!.Compact!.CurrentStep!.Scope);
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
        compact.RejectedSteps.Add(new JsonObject { ["reason"] = "Retain the exact selected boundary." });
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Equal([compact.ReplacementScope], ScopeDomain(schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(ScopedResponse(state, "/root"), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(ScopedResponse(state, compact.ReplacementScope), schema));
        var step = ScopedResponse(state, compact.ReplacementScope)["step"]!.Deserialize(PlanningJsonContext.Default.PlanningConstructionStep)!;
        PlanningCompactConstruction.ValidateSelection(state, step);
        PlanningConstruction.LoadEvidence(state, ["page:scopes:8"]);
        Assert.Equal([compact.ReplacementScope], ScopeDomain(PlanningCompactConstruction.SelectionSchema(state)));
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
        var request = PlanningCompactConstruction.Request(state, PlanningCompactConstruction.SelectGuidance(state),
            context, schema, PlanningCompactConstruction.SelectPurpose);
        request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose, request.Prompt,
            request.StructuredOutputSchema!.AsObject());
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens;
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, limit).EstimatedInputTokens, 1, limit);
        Assert.Equal(500, state.Construction!.OpenScopes.Count);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(500)]
    public async Task EveryIndependentScopePageKeepsExactOmissionLinksAndChoiceDomains(int count)
    {
        var baseline = await Ready(); AddScopes(baseline, count);
        var retained = JsonSerializer.Serialize(baseline, PlanningJsonContext.Default.PlanningSession);
        var reached = new HashSet<string>(StringComparer.Ordinal); var offset = 0;
        while (true)
        {
            // Each probe tests one independent page of the retained frontier;
            // this is neither a model conversation nor a live efficiency claim.
            var state = PlannerFixture.Clone(baseline); state.Construction!.ScopeCursor = offset;
            var request = PlanningCompactConstruction.Request(state, PlanningCompactConstruction.SelectGuidance(state),
                PlanningCompactConstruction.SelectionContext(state), PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
            var page = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            var scopes = DisplayedScopes(page); Assert.Equal(scopes, ScopeDomain(request.StructuredOutputSchema!.AsObject()));
            var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens;
            var complete = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose, request.Prompt, request.StructuredOutputSchema.AsObject());
            Assert.InRange(PlanningRequestMeasurements.Measure(complete, PlanningCompactConstruction.SelectPurpose, limit).EstimatedInputTokens, 1, limit);
            Assert.Equal(baseline.Construction!.OpenScopes[offset], scopes[0]);
            foreach (var scope in scopes) Assert.True(reached.Add(scope), scope);
            if (page["nextScopes"] is not { } next) break;
            var address = next.ToString();
            var response = new JsonObject { ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["step"] = null, ["clarifications"] = null, ["evidenceRequests"] = new JsonArray(address) };
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema));
            PlanningConstruction.LoadEvidence(state, [address]);
            Assert.Equal(offset + scopes.Length, state.Construction.ScopeCursor);
            offset = state.Construction.ScopeCursor;
        }
        Assert.Equal(baseline.Construction!.OpenScopes.Order(StringComparer.Ordinal), reached.Order(StringComparer.Ordinal));
        Assert.Equal(retained, JsonSerializer.Serialize(baseline, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task ScopeAdmissionPreservesRetainedConsumerBoundaryAndRejectsAnIndivisibleCore()
    {
        var state = await Ready(); AddScopes(state, 18);
        var required = state.Construction!.OpenScopes[7];
        state.Construction.Compact!.SuspendedSteps = [new() { Id = "waiting", Consumer = new() { Id = "consume", Scope = required, RequirementIds = ["req_0"], Objective = "Consume the exact prerequisite." },
            Prerequisite = new() { Need = "A required original result.", ExpectedType = new() { Kind = "string" }, EvidenceAddresses = ["requirement:req_0"] } }];
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var request = PlanningCompactConstruction.Request(state, PlanningCompactConstruction.SelectGuidance(state),
            PlanningCompactConstruction.SelectionContext(state), PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
        var page = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        Assert.Contains(required, DisplayedScopes(page)); Assert.Contains(required, ScopeDomain(request.StructuredOutputSchema!.AsObject()));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var runtime = new TestRuntime();
        var failure = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningCompactConstruction.SelectPurpose, request.Prompt, request.StructuredOutputSchema.AsObject(), PlannerFixture.Ct));
        Assert.Equal("MODEL_INPUT_LIMIT", failure.Code); Assert.Empty(runtime.Calls); Assert.Null(state.PendingCall);
    }

    private static LLMRequest HostRequest(TestRuntime runtime, LLMRequest request) =>
        PlanningModelCalls.CanonicalEvidenceRequest(runtime.Checkpoints.Last(checkpoint =>
            checkpoint.PendingCall?.Id == request.ClientRequestId).PendingCall!);

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

    private static IEnumerable<JsonObject> StepBranches(JsonObject schema)
    {
        IEnumerable<JsonObject> Visit(JsonNode node)
        {
            while (node["$ref"] is JsonValue reference) node = schema["$defs"]![reference.ToString()[8..]]!;
            if (node["properties"] is JsonObject) { yield return node.AsObject(); yield break; }
            if (node["anyOf"] is JsonArray alternatives)
                foreach (var branch in alternatives.OfType<JsonObject>()) foreach (var candidate in Visit(branch)) yield return candidate;
        }
        return Visit(schema["properties"]!["step"]!);
    }
    private static string[] ScopeDomain(JsonObject schema)
    {
        JsonNode Resolve(JsonNode node)
        {
            while (node["$ref"] is JsonValue reference) node = schema["$defs"]![reference.ToString()[8..]]!;
            return node;
        }
        return StepBranches(schema).SelectMany(step => Resolve(step["properties"]!["scope"]!)["enum"]!.AsArray())
            .Select(value => value!.ToString()).Distinct(StringComparer.Ordinal).ToArray();
    }

    [Theory]
    [InlineData("req_3")]
    [InlineData("request:0:clause:0")]
    [InlineData("request:0:clause:3")]
    [InlineData("unknown")]
    public async Task FreshSchemaRejectsUndisplayedRequirementsAndSourceIdentities(string invalid)
    {
        var state = await Ready(fillPage: true);
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
        var state = await Ready(fillPage: true);
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
        state.Construction.Compact.RejectedSteps.Add(new JsonObject { ["reason"] = "Retain the accepted requirement boundary." });
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
        var request = PlanningCompactConstruction.Request(state, PlanningCompactConstruction.SelectGuidance(state),
            PlanningCompactConstruction.SelectionContext(state), schema, PlanningCompactConstruction.SelectPurpose);
        request = PlanningGenerationPolicy.Apply(new LLMRequest
        {
            Prompt = request.Prompt, StructuredOutputSchema = request.StructuredOutputSchema, StructuredOutputStrict = true,
            Provider = "OpenAi", Model = "retained-model", UseBackgroundMode = true, MaxTokens = 8192,
            ClientRequestId = new string('s', 64) + ":9:" + new string('h', 64)
        }, state.Request.Generation);
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens;
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, limit).EstimatedInputTokens, 1, limit);
        Assert.Equal(["req_0", "req_1", "req_2", "req_499"], Domain(request.StructuredOutputSchema!.AsObject()));
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
        var request = PlanningCompactConstruction.Request(state, PlanningCompactConstruction.SelectGuidance(state),
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

        Assert.Null(PlanningCompactConstruction.SelectionContext(state)["originalRequirementCount"]);
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
            while (node["$ref"] is JsonValue reference) node = schema["$defs"]![reference.ToString()[8..]]!;
            return node;
        }
        return StepBranches(schema).SelectMany(step => Resolve(Resolve(step["properties"]![property]!)["items"]!)["enum"]!.AsArray())
            .Select(value => value!.ToString()).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static JsonObject Response(PlanningSession state, params string[] requirements) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["evidenceRequests"] = null, ["clarifications"] = null,
        ["step"] = new JsonObject
        {
            ["scope"] = "/root", ["kind"] = "value", ["objective"] = "Preserve the original supplied values.",
            ["requirementIds"] = new JsonArray(requirements.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["inputs"] = new JsonArray(), ["groupId"] = null, ["operationIds"] = new JsonArray(), ["closeScopes"] = null, ["prerequisites"] = null, ["dependencyTaskIds"] = new JsonArray(),
            ["expectedOutputs"] = new JsonArray(), ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray()
        }
    };

    private static async Task<PlanningSession> Ready(bool fillPage = false)
    {
        string[] clauses = ["Read the original values. ", "Keep their order. ", "Return the retained values. ", "Preserve exact identities."];
        // Omission/retrieval tests must reach the real presentation byte bound;
        // an indexing-page boundary alone no longer hides adjacent originals.
        if (fillPage) clauses[0] += new string('x', 900);
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
        var state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Capture(new() { Request = new()
        { TenantId = "selection-schema", Prompt = string.Concat(clauses), Options = new() { ["automatic_correction"] = true } } }),
            new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        return state;
    }
}
