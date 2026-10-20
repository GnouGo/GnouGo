using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactSelectionSchemaTests
{
    [Theory]
    [InlineData("req_1")]
    [InlineData("request:0:clause:0")]
    [InlineData("request:0:clause:3")]
    [InlineData("unknown")]
    public async Task FreshSchemaRejectsUndisplayedRequirementsAndSourceIdentities(string invalid)
    {
        var state = await Ready();
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Equal(["req_0"], Domain(schema));
        Assert.Equal(["req_0"], Domain(schema, "closeRequirementIds"));
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
        PlanningConstruction.LoadEvidence(state, ["page:requirements:1"]);
        Assert.Equal(["req_0", "req_1", "req_2"], Domain(PlanningCompactConstruction.SelectionSchema(state)));
        var record = state.Construction!.Evidence!["page:requirements:1"]!["items"]![0]!;
        if (changed == "version") record["sources"]![0]!["version"] = "stale";
        else record[changed] = "changed";
        Assert.Equal(["req_0", "req_2"], Domain(PlanningCompactConstruction.SelectionSchema(state)));
        PlanningConstruction.LoadEvidence(state, ["requirement:req_2"]);
        Assert.Equal(["req_0", "req_2"], Domain(PlanningCompactConstruction.SelectionSchema(state)));
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
        Assert.Equal(["req_0", "req_499"], Domain(schema));
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
        var historical = PlannerFixture.Clone(state);
        historical.Construction!.Compact!.PresentationVersion = null;
        historical.Request.Options.Remove("construction_presentation_version");
        var broad = PlanningCompactConstruction.SelectionSchema(historical);
        Assert.Null(broad["properties"]!["step"]!["anyOf"]![0]!["properties"]!["requirementIds"]!["items"]!["enum"]);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(state, "req_1"), broad));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(state, "req_1"), PlanningCompactConstruction.SelectionSchema(state)));
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
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = Response(state, "req_1") } };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(["req_1"], state.Construction!.Compact!.CurrentStep!.RequirementIds);
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
        var state = await new HybridWorkflowPlanner().AdvanceAsync(new() { Request = new()
        { TenantId = "selection-schema", Prompt = string.Concat(clauses), Options = new() { ["automatic_correction"] = true } } },
            new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        return state;
    }
}
