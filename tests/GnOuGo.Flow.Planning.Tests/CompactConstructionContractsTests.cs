using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactConstructionContractsTests
{
    [Fact]
    public void UnusedOptionalInputAndOutputTreesStayOutOfTheSelectedRequest()
    {
        var state = State();
        var capability = state.Discovery.Resolved[0];
        capability.InputSchema["properties"]!["optional"]!["description"] = "optional-marker" + new string('x', 100000);
        capability.OutputSchema["properties"]!["result"]!["description"] = "output-marker" + new string('y', 100000);
        var before = JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability);
        var views = PlanningCompactConstruction.ContractViews(state).ToJsonString();
        var schema = PlanningCompactConstruction.DraftSchema(state).ToJsonString();
        Assert.Contains("required-marker", views, StringComparison.Ordinal);
        Assert.DoesNotContain("optional-marker", views, StringComparison.Ordinal);
        Assert.DoesNotContain("output-marker", views, StringComparison.Ordinal);
        Assert.DoesNotContain("optional-marker", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("output-marker", schema, StringComparison.Ordinal);
        Assert.Contains("schema:read@v1:/input/properties/optional", views, StringComparison.Ordinal);
        Assert.Contains("schema:read@v1:/output/properties/result", views, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));
        Assert.InRange(views.Length + schema.Length, 1, 18000);
    }

    [Fact]
    public void ASelectedOptionalInputIsExactAndRemainsSelectedAcrossEvidenceBatches()
    {
        var state = State();
        var evidence = PlanningCompactConstruction.LoadContractEvidence(state, "schema:read@v1:/input/properties/optional")!;
        Assert.True(JsonNode.DeepEquals(state.Discovery.Resolved[0].InputSchema["properties"]!["optional"], evidence["schema"]));
        Assert.Contains("optional-marker", PlanningCompactConstruction.ContractViews(state).ToJsonString(), StringComparison.Ordinal);
        state.Construction!.Evidence = new() { ["unrelated"] = true };
        Assert.Contains("optional-marker", PlanningCompactConstruction.ContractViews(state).ToJsonString(), StringComparison.Ordinal);
        Assert.Single(state.Construction.Compact!.CurrentStep!.SchemaPointers);
        var contractSchema = PlanningCompactConstruction.DraftSchema(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(contractSchema, strict: true));
        Assert.Contains("optional", contractSchema.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredNestedFieldsRemainAndOptionalDescendantsNeedExplicitSelection()
    {
        var original = JsonNode.Parse("""
            {"type":"object","properties":{"payload":{"type":"object","description":"Keep the required identity exact.",
            "properties":{"identity":{"type":"string","pattern":"^[A-Z]+$"},"extra":{"type":"string","maxLength":17}},
            "required":["identity"],"additionalProperties":false}},"required":["payload"],"additionalProperties":false}
            """)!.AsObject();
        var omitted = new List<string>();
        var view = PlanningCompactConstruction.ProjectInputSchema(original, new HashSet<string>(), omitted);
        Assert.Equal("^[A-Z]+$", view["properties"]!["payload"]!["properties"]!["identity"]!["pattern"]!.ToString());
        Assert.Null(view["properties"]!["payload"]!["properties"]!["extra"]);
        Assert.Contains("/input/properties/payload/properties/extra", omitted);
        view = PlanningCompactConstruction.ProjectInputSchema(original, new HashSet<string> { "/input/properties/payload/properties/extra" }, []);
        Assert.Equal(17, view["properties"]!["payload"]!["properties"]!["extra"]!["maxLength"]!.GetValue<int>());
        Assert.Empty(PlanningContractValidation.ValidateSchema(view));
    }

    [Theory]
    [InlineData("oneOf")]
    [InlineData("allOf")]
    [InlineData("anyOf")]
    public void CoupledConstraintsKeepTheWholeContainingOriginal(string keyword)
    {
        var state = State();
        var original = state.Discovery.Resolved[0].InputSchema;
        original[keyword] = new JsonArray(new JsonObject { ["required"] = new JsonArray("optional") });
        var result = PlanningCompactConstruction.ProjectInputSchema(original, new HashSet<string>(), []);
        Assert.True(JsonNode.DeepEquals(original, result));
    }

    [Fact]
    public void ReferencedDefinitionsKeepTheirExactTransitiveClosure()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"value":{"$ref":"#/$defs/first"}},"required":["value"],"additionalProperties":false,
             "$defs":{"first":{"type":"array","items":{"$ref":"#/$defs/second"}},"second":{"type":"string","enum":["A","B"]},
             "unused":{"type":"string","description":"unused-definition-marker"}}}
            """)!.AsObject();
        var result = PlanningCompactConstruction.ProjectInputSchema(schema, new HashSet<string>(), []);
        Assert.True(JsonNode.DeepEquals(schema["$defs"]!["first"], result["$defs"]!["first"]));
        Assert.True(JsonNode.DeepEquals(schema["$defs"]!["second"], result["$defs"]!["second"]));
        Assert.Null(result["$defs"]!["unused"]);
        Assert.Empty(PlanningContractValidation.ValidateSchema(result));
    }

    [Fact]
    public void DataNamedReferenceInAnAnnotationIsNeverTreatedAsASchemaReference()
    {
        var original = JsonNode.Parse("""
            {"type":"object","properties":{"value":{"type":"object","default":{"$ref":"business-reference"},
            "properties":{"$ref":{"type":"string"}},"required":["$ref"],"additionalProperties":false}},"required":["value"],"additionalProperties":false}
            """)!.AsObject();
        var projected = PlanningCompactConstruction.ProjectInputSchema(original, new HashSet<string>(), []);
        Assert.True(JsonNode.DeepEquals(original, projected));
        Assert.Empty(PlanningContractValidation.ValidateSchema(projected));
    }

    [Fact]
    public void JudgeDoesNotReintroduceUnusedNestedOptionalBranchesOfABoundInput()
    {
        var state = State();
        state.Discovery.Resolved[0].InputSchema["properties"]!["value"] = JsonNode.Parse("""
            {"type":"object","properties":{"identity":{"type":"string"},"unused":{"type":"string","description":"unused-nested-marker"}},
             "required":["identity"],"additionalProperties":false}
            """);
        state.Construction!.Candidate = new() { Extension = new() { Tasks = [new() { Id = "read_value", Kind = "operation", Operation = "read",
            Inputs = [new("value", new() { Kind = "object", Members = [new("identity", new() { Kind = "string", Text = "exact" })] })] }] } };
        var view = PlanningCompactConstruction.ContractViews(state, review: true).ToJsonString();
        Assert.DoesNotContain("unused-nested-marker", view, StringComparison.Ordinal);
        Assert.DoesNotContain("schema-page:", view, StringComparison.Ordinal);
        Assert.DoesNotContain("inputSchema", view, StringComparison.Ordinal);
        Assert.Contains("schema:read@v1:/input/properties/value", view, StringComparison.Ordinal);
    }

    [Fact]
    public void JudgeContractMetadataDeclaresOmittedNormativeProseAndAddressesItsOriginal()
    {
        var state = State();
        state.Discovery.Resolved[0].Description = new string('A', 10000) + "Final normative restriction.";
        var view = Assert.Single(PlanningCompactConstruction.ContractViews(state, review: true))!;
        Assert.False(view["descriptionComplete"]!.GetValue<bool>());
        Assert.Equal(384, view["description"]!.ToString().Length);
        Assert.Equal("description:read@v1:0", view["descriptionEvidence"]!.ToString());
        Assert.Null(view["inputSchema"]);
        var final = PlanningCompactConstruction.LoadContractEvidence(state, "description:read@v1:10000")!;
        Assert.Equal("Final normative restriction.", final["text"]!.ToString());
    }

    [Fact]
    public void InputViewsPreserveRootRestrictionsAndCompleteNormativeProse()
    {
        var state = State();
        var contract = state.Discovery.Resolved[0];
        contract.Description = "Use only the caller-authorized isolated resource. " + new string('z', 20000);
        contract.InputSchema["description"] = "Do not infer a default for absent values.";
        var view = Assert.Single(PlanningCompactConstruction.ContractViews(state));
        Assert.Equal(contract.Description, view!["description"]!.ToString());
        Assert.False(view["inputSchema"]!["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("Do not infer a default for absent values.", view["inputSchema"]!["description"]!.ToString());
        Assert.Equal("read", view["effect"]!.ToString());
    }

    [Theory]
    [InlineData("schema:read@v0:/input/properties/optional")]
    [InlineData("schema:read@v1:/input/optional")]
    [InlineData("schema:read@v1:/input/properties/missing")]
    [InlineData("schema:read:/input/properties/optional")]
    [InlineData("schema:read@v1:/input/properties/~2bad")]
    public void StaleAndUnissuedSchemaPointersCannotChangeSelectedViews(string address)
    {
        var state = State();
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.LoadContractEvidence(state, address));
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.SchemaPointers);
    }

    [Fact]
    public void EvidenceCarriesAncestorConstraintsAndTheExactOriginalHash()
    {
        var state = State();
        var capability = state.Discovery.Resolved[0];
        var result = PlanningCompactConstruction.LoadContractEvidence(state, "schema:read@v1:/input/properties/value")!;
        Assert.True(JsonNode.DeepEquals(capability.InputSchema["required"], result["ancestors"]![0]!["constraints"]!["required"]));
        Assert.False(result["ancestors"]![0]!["constraints"]!["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability)), result["hash"]!.ToString());
        Assert.Equal("v1", result["version"]!.ToString());
    }

    [Fact]
    public void ExactInputEvidenceIncludesRequiredSiblingSchemasAndTheirReferences()
    {
        var state = State();
        var input = state.Discovery.Resolved[0].InputSchema;
        input["properties"]!["sibling"] = new JsonObject { ["$ref"] = "#/$defs/identity" };
        input["required"]!.AsArray().Add((JsonNode)JsonValue.Create("sibling")!);
        input["$defs"] = new JsonObject { ["identity"] = new JsonObject { ["type"] = "string", ["pattern"] = "^exact-[0-9]+$" } };
        var evidence = PlanningCompactConstruction.LoadContractEvidence(state, "schema:read@v1:/input/properties/value")!;
        Assert.True(JsonNode.DeepEquals(input["properties"]!["sibling"], evidence["ancestors"]![0]!["requiredSiblings"]!["sibling"]));
        Assert.True(JsonNode.DeepEquals(input["$defs"]!["identity"], evidence["referenceClosure"]!["$defs"]!["identity"]));
    }

    [Fact]
    public void DescriptionAndSchemaDirectoriesPageOriginalEvidenceWithoutDroppingIt()
    {
        var state = State();
        var contract = state.Discovery.Resolved[0];
        contract.Description = new string('A', 2048) + "last normative clause";
        var first = PlanningCompactConstruction.LoadContractEvidence(state, "description:read@v1:0")!;
        var second = PlanningCompactConstruction.LoadContractEvidence(state, first["next"]!.ToString())!;
        Assert.Equal(contract.Description, first["text"]!.ToString() + second["text"]!.ToString());
        Assert.Null(second["next"]);
        var properties = contract.InputSchema["properties"]!.AsObject();
        for (var i = 0; i < 20; i++) properties["extra" + i] = new JsonObject { ["type"] = "string" };
        var pages = new List<string>(); string? address = "schema-page:read@v1:/input:0";
        while (address is not null)
        {
            var page = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
            pages.AddRange(page["items"]!.AsArray().Select(n => n!["pointer"]!.ToString()));
            address = page["next"]?.ToString();
        }
        Assert.Equal(properties.Count, pages.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PreviousInspectionsDoNotMakeUnselectedOperationsMandatory()
    {
        var state = State();
        var other = Capability("old", "old-unrelated-contract");
        state.Discovery.Resolved.Add(other);
        state.Discovery.Inspections = [new("source", OperationIds: ["old"])];
        var views = PlanningCompactConstruction.ContractViews(state);
        Assert.Equal("read", Assert.Single(views)!["id"]!.ToString());
        Assert.DoesNotContain("old-unrelated-contract", views.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveredOperationProseIsRetrievableBeforeSelectingOrResolvingItsSchema()
    {
        var state = State();
        var capability = Capability("unselected", "Exact original operation restrictions.");
        state.Discovery.Pages = [new("source", null, [new(capability.Id, "source", "Unselected operation",
            "A different discovery summary.", capability.StepType, capability.EffectKind, capability.Version,
            Operation: TaskOperations.Describe(capability))], null)];
        var descriptor = Assert.Single(PlanningCompactConstruction.ContractDescriptors(state))!;
        Assert.Equal(capability.Description, descriptor["description"]!.ToString());
        var evidence = PlanningCompactConstruction.LoadContractEvidence(state, descriptor["descriptionEvidence"]!.ToString())!;
        Assert.Equal(capability.Description, evidence["text"]!.ToString());
        Assert.Equal(PlanningGraphCompiler.Fingerprint(capability.Description), evidence["descriptionHash"]!.ToString());
        Assert.Equal("read", Assert.Single(state.Discovery.Resolved).Id);
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.SchemaPointers);
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.LoadContractEvidence(state, "schema:unselected@v1:/input"));
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.LoadContractEvidence(state, "description:unselected@stale:0"));
        state.Catalog!.Policy.DeniedCapabilityIds.Add(capability.Id);
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.LoadContractEvidence(state, descriptor["descriptionEvidence"]!.ToString()));
    }

    [Fact]
    public void OutputAliasesBeyondTheInitialDirectoryRemainExactlyRetrievable()
    {
        var state = State();
        var capability = state.Discovery.Resolved[0];
        var properties = capability.OutputSchema["properties"]!.AsObject();
        properties.Clear(); capability.OutputSchema["required"] = new JsonArray();
        for (var i = 0; i < 18; i++)
        {
            properties["wire" + i] = new JsonObject { ["type"] = "string" };
            capability.OutputSchema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("wire" + i));
        }
        capability.Operation = TaskOperations.Describe(capability);
        for (var i = 0; i < capability.Operation.Outputs.Count; i++) capability.Operation.Outputs[i].Name = "business_" + i;
        Assert.Empty(TaskOperations.Validate(capability));
        var original = JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability);
        var view = Assert.Single(PlanningCompactConstruction.ContractViews(state))!;
        var names = view["outputs"]!.AsArray().Select(p => p!["name"]!.ToString()).ToList();
        string? address = view["outputDirectory"]!.ToString();
        Assert.Equal("port-page:read@v1:/output:8", address);
        while (address is not null)
        {
            var page = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
            Assert.Equal(PlanningGraphCompiler.Fingerprint(original), page["hash"]!.ToString());
            Assert.Equal("v1", page["version"]!.ToString());
            Assert.InRange(page["items"]!.AsArray().Count, 1, 8);
            foreach (var item in page["items"]!.AsArray())
            {
                names.Add(item!["name"]!.ToString());
                var schema = PlanningCompactConstruction.LoadContractEvidence(state, item["schema"]!.ToString())!;
                Assert.Equal("string", schema["schema"]!["type"]!.ToString());
            }
            address = page["next"]?.ToString();
        }
        Assert.Equal(capability.Operation.Outputs.Select(p => p.Name), names);
        Assert.Equal(original, JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.LoadContractEvidence(state, "port-page:read@stale:/output:8"));
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.LoadContractEvidence(state, "port-page:read@v1:/output:999"));
    }

    [Fact]
    public void CompactEvidenceSchemasPermitTheExactLongAddressesTheyIssue()
    {
        var state = State();
        state.Discovery.Sources = [new(new string('s', 192), "Original source restrictions.")];
        var sourceAddress = PlanningCompactConstruction.SourceDescriptors(state)["items"]![0]!["descriptionEvidence"]!.ToString();
        Assert.True(sourceAddress.Length > 256);
        var selection = new JsonObject { ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["step"] = null,
            ["evidenceRequests"] = new JsonArray(sourceAddress), ["clarifications"] = null };
        Assert.Empty(PlanningContractValidation.ValidateInstance(selection, PlanningCompactConstruction.SelectionSchema(state)));
        var discovery = new JsonObject { ["operationIds"] = null, ["discoveryRequests"] = null,
            ["evidenceRequests"] = new JsonArray(sourceAddress), ["clarifications"] = null };
        Assert.Empty(PlanningContractValidation.ValidateInstance(discovery, PlanningCompactConstruction.DiscoverySchema(state)));
        Assert.Equal("Original source restrictions.", PlanningCompactConstruction.LoadContractEvidence(state, sourceAddress)!["text"]!.ToString());

        var pointer = "/input/properties/value";
        var current = state.Discovery.Resolved[0].InputSchema["properties"]!["value"]!.AsObject();
        for (var depth = 0; depth < 8; depth++)
        {
            var name = new string('p', 64) + depth;
            current.Clear(); current["type"] = "object";
            current["properties"] = new JsonObject { [name] = new JsonObject() };
            current["required"] = new JsonArray(name); current["additionalProperties"] = false;
            current = current["properties"]![name]!.AsObject(); pointer += "/properties/" + name;
        }
        current["type"] = "string"; current["description"] = "Exact terminal restriction.";
        var schemaAddress = "schema:read@v1:" + pointer;
        Assert.InRange(schemaAddress.Length, 513, 2048);
        var review = new JsonObject { ["findings"] = null, ["uncertainty"] = "The original restriction is needed.",
            ["evidenceRequests"] = new JsonArray(schemaAddress) };
        Assert.Empty(PlanningContractValidation.ValidateInstance(review, HybridWorkflowPlanner.ConstructionReviewSchema(compact: true)));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(review, HybridWorkflowPlanner.ConstructionReviewSchema()));
        Assert.Equal("Exact terminal restriction.", PlanningCompactConstruction.LoadContractEvidence(state, schemaAddress)!["schema"]!["description"]!.ToString());
        review["evidenceRequests"] = new JsonArray(new string('x', 2049));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(review, HybridWorkflowPlanner.ConstructionReviewSchema(compact: true)));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("漢")]
    [InlineData("🙂")]
    public void FullDiscoveryPageFitsItsCompleteRequestWithoutPortTreesOrGiantSourceDescriptions(string descriptionSymbol)
    {
        var state = State();
        state.Construction!.Requirements = [new() { Id = "result" }];
        state.Discovery.Sources = Enumerable.Range(0, 12).Select(i => new CapabilitySource("source_" + i.ToString("d24"),
            string.Concat(Enumerable.Repeat(descriptionSymbol, i == 0 ? 100000 : 300)))).ToList();
        var capabilities = Enumerable.Range(0, 8).Select(i =>
        {
            var capability = Capability("operation_" + i.ToString("d24"), string.Concat(Enumerable.Repeat(descriptionSymbol, 400)));
            capability.Version = new string((char)('a' + i), 64);
            foreach (var direction in new[] { capability.InputSchema, capability.OutputSchema })
                for (var p = 0; p < 8; p++) direction["properties"]!["field" + p] = new JsonObject { ["type"] = "string", ["description"] = new string('P', 5000) };
            return capability;
        }).ToArray();
        state.Discovery.Pages = [new(state.Discovery.Sources[0].Id, null, capabilities.Select(c => new CapabilitySummary(c.Id,
            state.Discovery.Sources[0].Id, "declared_operation_name", c.Description, c.StepType, c.EffectKind, c.Version,
            Operation: TaskOperations.Describe(c))).ToList(), null)];
        var descriptors = PlanningCompactConstruction.ContractDescriptorPage(state);
        var sources = PlanningCompactConstruction.SourceDescriptors(state);
        Assert.Equal(4, descriptors["items"]!.AsArray().Count);
        Assert.Equal("page:operations:4", descriptors["next"]!.ToString());
        Assert.Equal(2, sources["items"]!.AsArray().Count);
        Assert.All(descriptors["items"]!.AsArray(), d =>
        {
            Assert.Null(d!["inputs"]); Assert.Null(d["outputs"]); Assert.False(d["descriptionComplete"]!.GetValue<bool>());
            var excerpt = d["description"]!.ToString();
            if (descriptionSymbol == "A") Assert.Equal(192, excerpt.Length);
            else Assert.InRange(excerpt.Length, 1, 191);
        });
        Assert.All(sources["items"]!.AsArray(), s => Assert.False(s!["descriptionComplete"]!.GetValue<bool>()));
        var remainder = PlanningCompactConstruction.ContractDescriptorPage(state, 4);
        Assert.Equal(4, remainder["items"]!.AsArray().Count);
        Assert.Null(remainder["next"]);
        Assert.Equal(8, descriptors["items"]!.AsArray().Concat(remainder["items"]!.AsArray()).Select(d => d!["id"]!.ToString()).Distinct().Count());
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var presentation = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions, context,
            PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose);
        var request = PlanningGenerationPolicy.Apply(new LLMRequest { Prompt = presentation.Prompt, StructuredOutputSchema = presentation.StructuredOutputSchema,
            StructuredOutputStrict = true, UseBackgroundMode = true, MaxTokens = 8192, Provider = "OpenAi", Model = "gpt-5.5-2026-04-24",
            ClientRequestId = new string('s', 64) + ":123:" + new string('h', 64) }, state.Request.Generation);
        var measurement = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, 3000, "step");
        Assert.True(measurement.EstimatedInputTokens <= 3000,
            $"Complete discovery request: {measurement.EstimatedInputTokens} estimated tokens; " +
            string.Join(", ", measurement.Components.Select(c => $"{c.Component}={c.EstimatedTokens}")));
        Assert.DoesNotContain(new string('P', 100), request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(string.Concat(Enumerable.Repeat(descriptionSymbol, 193)), request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceDirectoriesAndVersionedOriginalDescriptionsArePaged()
    {
        var state = State();
        var original = new string('S', 2047) + "🙂" + "the exact final restriction";
        state.Discovery.Sources = Enumerable.Range(0, 9).Select(i => new CapabilitySource("source_" + i, i == 0 ? original : "A source")).ToList();
        var first = PlanningCompactConstruction.SourceDescriptors(state);
        Assert.Equal("page:sources:2", first["next"]!.ToString());
        var address = first["items"]![0]!["descriptionEvidence"]!.ToString();
        var prefix = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
        var suffix = PlanningCompactConstruction.LoadContractEvidence(state, prefix["next"]!.ToString())!;
        Assert.Equal(original, prefix["text"]!.ToString() + suffix["text"]!.ToString());
        state.Discovery.Sources[0] = state.Discovery.Sources[0] with { Description = "A changed source" };
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.LoadContractEvidence(state, address));
    }

    [Theory]
    [InlineData("A", false)]
    [InlineData("漢", false)]
    [InlineData("🙂", false)]
    [InlineData("A", true)]
    [InlineData("漢", true)]
    [InlineData("🙂", true)]
    public void ExactDescriptionEvidenceFitsCompleteDiscoveryRequestsForEveryUnicodePage(string symbol, bool sourceDescription)
    {
        var state = State();
        state.Construction!.Requirements = [new() { Id = "result" }];
        var original = string.Concat(Enumerable.Repeat(symbol, 2048)) + " Exact final restriction.";
        var capability = state.Discovery.Resolved.Single();
        capability.Description = original; capability.Version = new string('v', 64);
        var source = new CapabilitySource(new string('s', 192), original);
        state.Discovery.Sources = [source];
        state.Discovery.Pages = [new(source.Id, null, [new(capability.Id, source.Id, "Declared operation", capability.Description,
            capability.StepType, capability.EffectKind, capability.Version, Operation: TaskOperations.Describe(capability))], null)];
        string? address = sourceDescription
            ? PlanningCompactConstruction.SourceDescriptors(state)["items"]![0]!["descriptionEvidence"]!.ToString()
            : Assert.Single(PlanningCompactConstruction.ContractDescriptors(state))!["descriptionEvidence"]!.ToString();
        var retained = new System.Text.StringBuilder();
        while (address is not null)
        {
            PlanningConstruction.LoadEvidence(state, [address]);
            var page = state.Construction.Evidence![address]!;
            Assert.Equal(retained.Length, page["offset"]!.GetValue<int>());
            var text = page["text"]!.ToString();
            Assert.NotEmpty(text); Assert.False(char.IsLowSurrogate(text[0])); Assert.False(char.IsHighSurrogate(text[^1]));
            if (symbol == "A" && retained.Length == 0) Assert.Equal(2048, text.Length);
            retained.Append(text);
            var context = PlanningCompactConstruction.DiscoveryContext(state);
            Assert.Null(context["sources"]); Assert.Null(context["descriptors"]);
            var presentation = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions, context,
                PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose);
            var request = PlanningGenerationPolicy.Apply(new LLMRequest { Prompt = presentation.Prompt,
                StructuredOutputSchema = presentation.StructuredOutputSchema, StructuredOutputStrict = true, UseBackgroundMode = true,
                MaxTokens = 8192, Provider = "OpenAi", Model = "gpt-5.5-2026-04-24",
                ClientRequestId = new string('s', 64) + ":123:" + new string('h', 64) }, state.Request.Generation);
            var measurement = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, 3000, "step");
            Assert.True(measurement.EstimatedInputTokens <= 3000,
                $"Complete {symbol} description page request: {measurement.EstimatedInputTokens} estimated tokens; " +
                string.Join(", ", measurement.Components.Select(c => $"{c.Component}={c.EstimatedTokens}")));
            address = page["next"]?.ToString();
        }
        Assert.Equal(original, retained.ToString());
        Assert.Equal(original, capability.Description); Assert.Equal(original, source.Description);
    }

    [Fact]
    public void ReselectionIsAvailableOnlyForDiagnosedTentativeWork()
    {
        var state = State();
        Assert.Equal("null", PlanningCompactConstruction.DraftSchema(state)["properties"]!["reselectStep"]!["type"]!.ToString());
        state.Diagnostics = [new("CONSTRUCTION_BUSINESS_REVIEW", "/construction/candidate", "The selected kind cannot perform the original effect.")];
        Assert.NotNull(PlanningCompactConstruction.DraftSchema(state)["properties"]!["reselectStep"]!["anyOf"]);
        state.Construction!.Candidate = new() { AuthorizedPaths = ["/tasks/fixed/outputs/value"] };
        Assert.Equal("null", PlanningCompactConstruction.DraftSchema(state)["properties"]!["reselectStep"]!["type"]!.ToString());
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("value")]
    [InlineData("transform")]
    [InlineData("conditional")]
    [InlineData("foreach")]
    [InlineData("parallel")]
    [InlineData("sequence")]
    [InlineData("group")]
    [InlineData("choice")]
    [InlineData("close")]
    public void EachDraftSchemaContainsOnlyItsSelectedSemanticKind(string kind)
    {
        var state = State();
        state.Construction!.Compact!.CurrentStep!.Kind = kind;
        if (kind != "operation") state.Construction.Compact.CurrentStep.OperationIds.Clear();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var taskKinds = Walk(schema).Where(n => n["properties"]?["objective"] is not null)
            .SelectMany(n => n["properties"]!["kind"]?["enum"]?.AsArray() ?? []).Select(n => n!.ToString()).Distinct().ToArray();
        Assert.Equal(kind is "group" or "choice" or "close" ? [] : new[] { kind }, taskKinds);
        if (kind != "operation") Assert.DoesNotContain("\"read\"", schema.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("null", schema["properties"]!["discoveryRequests"]!["type"]!.ToString());
        Assert.Equal("null", schema["properties"]!["requirements"]!["type"]!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequirementOnlyClosureIsReviewedAndCheckpointedWithoutRegeneratingAcceptedWork(bool retrieveRequirement)
    {
        var selected = 0;
        var evidenceBaselines = new List<string>();
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose is PlanningConstruction.LocalPurpose or PlanningConstruction.IntermediatePurpose)
                return Wire(new() { ["findings"] = new JsonArray(), ["uncertainty"] = "Runtime outcome remains unverified.", ["evidenceRequests"] = null });
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
            JsonObject response;
            if (purpose == PlanningCompactConstruction.IntentPurpose)
                response = new() { ["sourceId"] = context["sources"]![0]!["id"]!.ToString(),
                    ["clauses"] = new JsonArray(new JsonObject { ["text"] = context["sources"]![0]!["text"]!.ToString(), ["kind"] = "requirement" }),
                    ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray(new JsonObject
                    { ["name"] = "message", ["type"] = new JsonObject { ["kind"] = "string", ["nullable"] = false }, ["required"] = true }) };
            else if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                if (retrieveRequirement && context["evidence"] is null)
                {
                    evidenceBaselines.Add(context["baseHash"]!.ToString());
                    return Wire(new() { ["baseHash"] = context["baseHash"]!.DeepClone(), ["step"] = null,
                        ["evidenceRequests"] = new JsonArray("page:requirements:0"), ["clarifications"] = null });
                }
                selected++;
                response = new() { ["baseHash"] = context["baseHash"]!.ToString(), ["evidenceRequests"] = null, ["clarifications"] = null,
                    ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = selected == 1 ? "value" : "close",
                        ["objective"] = "Return exactly Hello.", ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(),
                        ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray(), ["groupId"] = null,
                        ["outputNames"] = selected == 1 ? new JsonArray("message") : new JsonArray(),
                        ["closeRequirementIds"] = selected == 2 ? new JsonArray("req_0") : new JsonArray() } };
            }
            else if (purpose == PlanningConstruction.DraftPurpose)
            {
                var extension = new PlanningConstructionExtension { RequirementIds = ["req_0"] };
                if (selected == 1)
                {
                    extension.Tasks = [new() { Id = "greet", Kind = "value", Objective = "Return exactly Hello.",
                        Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] }];
                    extension.Outputs = [new("message", new() { Kind = "output", Source = "greet", Port = "message" })];
                }
                if (selected == 3) { extension.CloseScopes = ["/root"]; extension.Complete = true; }
                response = new() { ["baseHash"] = context["baseHash"]!.ToString(), ["requirements"] = null, ["commitments"] = null,
                    ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension),
                    ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
                    ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null };
            }
            else throw new InvalidOperationException("Unexpected phase: " + purpose);
            return Wire(response);

            LLMResponse Wire(JsonObject value) => new()
            { Json = PlanningCorpus.Transport(value, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
        } };
        var state = await PlannerFixture.RunAsync(runtime, new() { Request = new() { TenantId = "closure", Prompt = "Return exactly Hello." } });
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(3, state.Construction!.Checkpoints.Count);
        var closed = state.Construction.Checkpoints[1];
        Assert.Empty(closed.TaskIds); Assert.Empty(closed.ClosedScopes);
        Assert.Equal(new[] { "req_0" }, closed.ClosedRequirementIds);
        Assert.Equal(state.Construction.Checkpoints[0].PlanHash, closed.PlanHash);
        Assert.Equal(2, closed.Reviews.Count);
        Assert.Equal("closed", Assert.Single(state.Construction.Requirements).Status);
        Assert.Single(state.Plan!.Root.Tasks);
        if (retrieveRequirement)
        {
            Assert.Equal(3, evidenceBaselines.Count);
            // The same exact page may be read after reviewed coverage advances,
            // even though the authoritative TaskPlan itself stays unchanged.
            Assert.Equal(evidenceBaselines[1], evidenceBaselines[2]);
        }
        PlanningArtifactApproval.Verify(state);
    }

    [Theory]
    [InlineData(1, "remaining", "close")]
    [InlineData(2, "closed", "close")]
    [InlineData(2, "remaining", "value")]
    public void EmptyExtensionsCannotClaimProgressWithoutANewHostIssuedRequirementClosure(int version, string status, string kind)
    {
        var state = State();
        state.Construction!.Version = version; state.Construction.OpenScopes = ["/root"];
        state.Construction.Requirements = [new() { Id = "result", Status = status }];
        state.Construction.Compact!.CurrentStep!.Kind = kind;
        state.Construction.Compact.CurrentStep.CloseRequirementIds = ["result"];
        var rejected = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state, new() { RequirementIds = ["result"] }));
        Assert.Contains(rejected.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
    }

    [Fact]
    public void CompleteForeachDraftRequestRetainsIterationAndDependencySemanticsAfterProjection()
    {
        var state = State();
        state.Construction!.Requirements = [new() { Id = "result" }];
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "foreach"; step.OperationIds.Clear();
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        var description = request.StructuredOutputSchema!["description"]!.ToString();
        Assert.Contains("TOTAL input-item ceiling; excess fails", description, StringComparison.Ordinal);
        Assert.Contains("maxConcurrency limits simultaneous workers, not total items", description, StringComparison.Ordinal);
        Assert.Contains("preserving input order and repetitions", description, StringComparison.Ordinal);
        Assert.Contains("preceding same-scope tasks", description, StringComparison.Ordinal);
        Assert.Contains("ancestor values use captures", description, StringComparison.Ordinal);
        Assert.DoesNotContain("conditional chooses", description, StringComparison.Ordinal);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema.AsObject(), strict: true));
        Assert.InRange(PlanningRequestMeasurements.Measure(Complete(request), PlanningConstruction.DraftPurpose, 8000).EstimatedInputTokens, 1, 8000);
    }

    [Theory]
    [InlineData("foreach", "array on the enclosing task")]
    [InlineData("conditional", "matching names, types, nullability and requiredness")]
    public void CompleteClosureRequestExplainsItsSelectedBoundaryWithoutReintroducingTaskGrammar(string ownerKind, string expected)
    {
        var state = State();
        state.Construction!.Requirements = [new() { Id = "result" }];
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "container", Kind = ownerKind,
            Objective = "Preserve the required values across the boundary", Body = new(),
            Otherwise = ownerKind == "conditional" ? new() : null }] } };
        state.Construction.OpenScopes = ["/root", "/tasks/container/body"];
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "close"; step.Scope = "/tasks/container/body"; step.OperationIds.Clear();
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        var description = request.StructuredOutputSchema!["description"]!.ToString();
        Assert.Contains(expected, description, StringComparison.Ordinal);
        Assert.Contains("consumers cannot read hidden child tasks directly", description, StringComparison.Ordinal);
        Assert.Contains("empty closeScopes and no new tasks or exports", description, StringComparison.Ordinal);
        Assert.Contains("Close children before their parent", description, StringComparison.Ordinal);
        Assert.DoesNotContain("TOTAL input-item ceiling", description, StringComparison.Ordinal);
        Assert.DoesNotContain(Walk(request.StructuredOutputSchema), n => n["properties"]?["objective"] is not null);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema.AsObject(), strict: true));
        Assert.InRange(PlanningRequestMeasurements.Measure(Complete(request), PlanningConstruction.DraftPurpose, 8000).EstimatedInputTokens, 1, 8000);
    }

    private static LLMRequest Complete(LLMRequest presentation) => PlanningGenerationPolicy.Apply(new()
    {
        Prompt = presentation.Prompt, StructuredOutputSchema = presentation.StructuredOutputSchema,
        StructuredOutputStrict = true, UseBackgroundMode = true, MaxTokens = 8192,
        ClientRequestId = "compact-contract-test:1:" + new string('0', 64)
    }, new("medium", 32000, 8192));

    [Fact]
    public void AContainerOpeningCannotHideARecursiveGeneratedWorkflow()
    {
        var state = State();
        state.Construction!.Compact!.CurrentStep!.Kind = "foreach";
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var response = JsonNode.Parse("""
            {"baseHash":"","requirements":null,"commitments":null,"discoveryRequests":null,"clarifications":null,"evidenceRequests":null,"rollbackTask":null,"rollbackReason":null,"reselectStep":null,
             "extension":{"scope":"/root","tasks":[{"id":"loop","kind":"foreach","objective":"Visit every supplied item","dependsOn":[],"requires":null,
             "items":{"kind":"array","items":[]},"body":{"tasks":[],"outputs":[],"always":[]},"parallel":false,"maxItems":10,"maxConcurrency":1}],
             "always":[],"outputs":[],"groups":[],"choices":[],"closeScopes":[],"requirementIds":[],"complete":false}}
            """)!;
        response["baseHash"] = state.Construction.Compact.CurrentStep.BaseHash;
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, schema));
        response["extension"]!["tasks"]![0]!["body"]!["tasks"]!.AsArray().Add((JsonNode)JsonNode.Parse("""
            {"id":"hidden","kind":"value","objective":"A hidden nested task","dependsOn":[],"requires":null,"outputs":[]}
            """)!);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, schema));
    }

    private static IEnumerable<JsonObject> Walk(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var (_, child) in obj) foreach (var nested in Walk(child)) yield return nested;
        }
        else if (node is JsonArray array) foreach (var child in array) foreach (var nested in Walk(child)) yield return nested;
    }

    private static PlanningSession State()
    {
        var contract = Capability("read", "Read only the caller-authorized value.");
        var state = new PlanningSession
        {
            IntentVersion = 3, Request = new() { TenantId = "compact-contract-test", Prompt = "Read the exact requested value.", Generation = new("medium", 32000, 8192) },
            Requirements = new() { Summary = "Read the exact requested value", Inputs = [], Outputs = [], Outcomes = [new("result", "Read the exact requested value")] },
            Construction = new() { Version = 2, Compact = new() { CurrentStep = new() { Id = "s1", Scope = "/root", Kind = "operation",
                Objective = "Read the exact requested value", OperationIds = ["read"], RequirementIds = ["result"] } } },
            Catalog = new() { AllowedStepTypes = ["mcp.call"] },
            Discovery = new() { Resolved = [contract], Sources = [new("source", "Available operations")] }
        };
        state.Construction.Compact.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan);
        return state;
    }

    private static PlanningCapability Capability(string id, string description) => new()
    {
        Id = id, Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = id, Description = description, EffectKind = "read",
        InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
            { ["value"] = new JsonObject { ["type"] = "string", ["description"] = "required-marker", ["enum"] = new JsonArray("A", "B") },
              ["optional"] = new JsonObject { ["type"] = "string", ["description"] = "optional-marker", ["maxLength"] = 20 } },
            ["required"] = new JsonArray("value"), ["additionalProperties"] = false },
        OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["result"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("result"), ["additionalProperties"] = false }
    };
}
