using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
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
        Assert.Single(state.Construction!.Compact!.CurrentStep!.SchemaPointers);
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
    public void NestedDirectoriesReachExactLeavesWithoutLoadingTheirHugeParents()
    {
        var state = State();
        var contract = state.Discovery.Resolved[0];
        var nested = JsonNode.Parse("""
            {"type":"object","properties":{"rows":{"type":"array","items":{"type":"object",
            "properties":{"identity":{"type":"string","pattern":"^original-[0-9]+$"},"unrelated":{"type":"string"}},
            "required":["identity"],"additionalProperties":false}}},"additionalProperties":false}
            """)!.AsObject();
        nested["properties"]!["rows"]!["items"]!["properties"]!["unrelated"]!["description"] = "huge-unused-tree-" + new string('x', 100000);
        contract.InputSchema["properties"]!["optional"] = nested;
        contract.OutputSchema["properties"]!["result"] = nested.DeepClone();
        var original = JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability);
        var view = Assert.Single(PlanningCompactConstruction.ContractViews(state))!;
        foreach (var root in new[] { "/input", "/output" })
        {
            var address = root == "/input" ? view["inputDirectory"]!.ToString() : view["outputs"]![0]!["directory"]!.ToString();
            var expected = root + "/properties/" + (root == "/input" ? "optional" : "result");
            if (root == "/input")
            {
                var page = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
                address = page["items"]!.AsArray().Single(i => i!["pointer"]!.ToString() == expected)!["directory"]!.ToString();
            }
            foreach (var suffix in new[] { "/properties/rows", "/items", "/properties/identity" })
            {
                expected += suffix;
                var page = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
                Assert.DoesNotContain("huge-unused-tree", page.ToJsonString(), StringComparison.Ordinal);
                Assert.Equal(PlanningGraphCompiler.Fingerprint(original), page["hash"]!.ToString());
                var item = page["items"]!.AsArray().Single(i => i!["pointer"]!.ToString() == expected)!;
                if (suffix != "/properties/identity") address = item["directory"]!.ToString();
                else
                {
                    Assert.Null(item["directory"]);
                    var leaf = PlanningCompactConstruction.LoadContractEvidence(state, item["evidence"]!.ToString())!;
                    Assert.Equal("^original-[0-9]+$", leaf["schema"]!["pattern"]!.ToString());
                    Assert.DoesNotContain("huge-unused-tree", leaf.ToJsonString(), StringComparison.Ordinal);
                }
            }
        }
        Assert.Equal(original, JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability));
        Assert.DoesNotContain("huge-unused-tree", PlanningCompactConstruction.ContractViews(state).ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("schema:read@v1:/input/properties/optional", state.Construction!.Compact!.CurrentStep!.SchemaPointers);
    }

    [Fact]
    public void RequiredSiblingViewsKeepRequiredReferenceClosureAndAddressOmittedIndependentBranches()
    {
        var state = State();
        var contract = state.Discovery.Resolved[0];
        var input = contract.InputSchema;
        input["properties"]!["sibling"] = JsonNode.Parse("""
            {"type":"object","properties":{"identity":{"$ref":"#/$defs/identity"},"unused":{"type":"string"}},
            "required":["identity"],"additionalProperties":false}
            """);
        input["properties"]!["sibling"]!["properties"]!["unused"]!["description"] = "unused-sibling-" + new string('x', 100000);
        input["required"]!.AsArray().Add((JsonNode)JsonValue.Create("sibling")!);
        input["$defs"] = JsonNode.Parse("""
            {"identity":{"$ref":"#/$defs/exact"},"exact":{"type":"string","pattern":"^original-[0-9]+$"}}
            """);
        var original = JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability);
        var evidence = PlanningCompactConstruction.LoadContractEvidence(state, "schema:read@v1:/input/properties/value")!;
        var ancestor = evidence["ancestors"]![0]!;
        var sibling = ancestor["requiredSiblings"]!["sibling"]!;
        Assert.Equal("#/$defs/identity", sibling["properties"]!["identity"]!["$ref"]!.ToString());
        Assert.Null(sibling["properties"]!["unused"]);
        Assert.False(sibling["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("^original-[0-9]+$", evidence["referenceClosure"]!["$defs"]!["exact"]!["pattern"]!.ToString());
        Assert.DoesNotContain("unused-sibling-", evidence.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(1, ancestor["omittedOptionalSiblingCount"]!.GetValue<int>());
        var address = Assert.Single(ancestor["omittedOptionalSiblings"]!.AsArray())!.ToString();
        Assert.Equal("schema:read@v1:/input/properties/sibling/properties/unused", address);
        Assert.NotNull(ancestor["directory"]);
        Assert.Equal(input["properties"]!["sibling"]!["properties"]!["unused"]!["description"]!.ToString(),
            PlanningCompactConstruction.LoadContractEvidence(state, address)!["schema"]!["description"]!.ToString());
        Assert.Equal(original, JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability));
    }

    [Theory]
    [InlineData("oneOf")]
    [InlineData("dependentRequired")]
    public async Task CoupledRequiredSiblingsRemainWholeAndOversizedEvidenceIsRefused(string keyword)
    {
        var state = State();
        var input = state.Discovery.Resolved[0].InputSchema;
        var sibling = JsonNode.Parse("""
            {"type":"object","properties":{"identity":{"type":"string"},"optional":{"type":"string"}},
            "required":["identity"],"additionalProperties":false}
            """)!.AsObject();
        sibling["properties"]!["optional"]!["description"] = "coupled-required-" + new string('x', 100000);
        sibling[keyword] = keyword == "oneOf" ? new JsonArray(new JsonObject { ["required"] = new JsonArray("optional") })
            : new JsonObject { ["identity"] = new JsonArray("optional") };
        input["properties"]!["sibling"] = sibling;
        input["required"]!.AsArray().Add((JsonNode)JsonValue.Create("sibling")!);
        var evidence = PlanningCompactConstruction.LoadContractEvidence(state, "schema:read@v1:/input/properties/value")!;
        Assert.True(JsonNode.DeepEquals(sibling, evidence["ancestors"]![0]!["requiredSiblings"]!["sibling"]));
        state.Construction!.Evidence = new() { ["schema:read@v1:/input/properties/value"] = evidence };
        state.Construction!.Requirements = [new() { Id = "result" }];
        var request = Complete(PlanningConstruction.DraftRequest(state, repair: false));
        Assert.True(PlanningRequestMeasurements.Measure(request, PlanningConstruction.DraftPurpose, 8000).EstimatedInputTokens > 8000);
        var runtime = new TestRuntime();
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningConstruction.DraftPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct));
        Assert.Equal("MODEL_INPUT_LIMIT", error.Code);
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public void ResultCommitmentSchemasContainOnlySelectedPublicOutputs()
    {
        var state = State();
        state.Requirements!.Outputs = [new() { Name = "selected", Type = new() { Kind = "string" }, Required = true }];

        state.Construction!.Compact!.CurrentStep!.OutputNames = ["selected"];
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var before = schema.ToJsonString();
        for (var i = 0; i < 1000; i++)
            state.Requirements.Outputs.Add(new() { Name = "unrelated_" + i, Type = new() { Kind = "string" }, Required = true });
        var response = new JsonObject
        {
            ["baseHash"] = state.Construction!.Compact!.CurrentStep.BaseHash, ["requirements"] = null, ["extension"] = null,
            ["commitments"] = new JsonArray(new JsonObject { ["requirementId"] = "result", ["operation"] = "read",
                ["result"] = new JsonObject { ["port"] = "result", ["output"] = "selected" } }),
            ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
            ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
        };
        response = PlanningCorpus.Transport(response, schema, schema)!.AsObject();
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, schema));
        response["commitments"]![0]!["result"]!["output"] = "unrelated_0";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, schema));
        state.Construction!.Compact!.CurrentStep.OutputNames.Clear();
        response["commitments"]![0]!["result"]!["output"] = "selected";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, PlanningCompactConstruction.DraftSchema(state)));
    }

    [Theory]
    [InlineData("read", "result", true)]
    [InlineData("read", null, true)]
    [InlineData("read", "feedback", false)]
    [InlineData("read", "other_result", false)]
    [InlineData("other", "other_result", true)]
    [InlineData("other", "result", false)]
    [InlineData("empty", null, true)]
    [InlineData("empty", "result", false)]
    public void FreshCommitmentsPairOnlyExactPortsOfTheirSelectedOperation(string operation, string? port, bool valid)
    {
        var state = CommitmentState();
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        var schema = request.StructuredOutputSchema!;
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var response = CommitmentResponse(state, operation, port);
        foreach (var key in response.Select(pair => pair.Key).Where(key => !schema["properties"]!.AsObject().ContainsKey(key)).ToArray())
            response.Remove(key);
        Assert.Equal(valid, PlanningContractValidation.ValidateInstance(response, schema).Count == 0);
        if (valid)
        {
            PlanningCompactConstruction.StageCommitments(state, response.Deserialize(ConstructionJsonContext.Default.PlanningConstructionResponse)!.Commitments);
            Assert.Contains(state.Construction!.Compact!.PendingCommitments["result"], item =>
                item.Operation == operation && item.Result?.Port == port);
        }
        else
        {
            var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
            Assert.Contains(Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.StageCommitments(state,
                response.Deserialize(ConstructionJsonContext.Default.PlanningConstructionResponse)!.Commitments)).Diagnostics,
                diagnostic => diagnostic.Code == "REQUIREMENTS_COMMITMENT_INVALID");
            Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        }
    }

    private static PlanningSession CommitmentState()
    {
        var state = State();

        state.Construction!.Requirements = [new() { Id = "result" }];
        state.Construction!.Compact!.CurrentStep!.OutputNames = ["feedback"];
        state.Construction!.Compact!.CurrentStep.OperationIds = ["read", "other", "empty"];
        state.Requirements!.Outputs = [new() { Name = "feedback", Type = new() { Kind = "string" }, Required = true }];
        var other = Capability("other", "A different producer with its own result port.");
        other.OutputSchema["properties"] = new JsonObject { ["other_result"] = new JsonObject { ["type"] = "string" } };
        other.OutputSchema["required"] = new JsonArray("other_result");
        var empty = Capability("empty", "An operation without result ports.");
        empty.OutputSchema["properties"] = new JsonObject(); empty.OutputSchema["required"] = new JsonArray();
        state.Discovery.Resolved.AddRange([other, empty]);
        return state;
    }

    private static JsonObject CommitmentResponse(PlanningSession state, string operation, string? port) => new()
    {
        ["baseHash"] = state.Construction!.Compact!.CurrentStep!.BaseHash, ["requirements"] = null, ["extension"] = null,
        ["commitments"] = new JsonArray(new JsonObject { ["requirementId"] = "result", ["operation"] = operation,
            ["result"] = port is null ? null : new JsonObject { ["port"] = port, ["output"] = "feedback" } }),
        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
        ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    };

    [Fact]
    public void SelectedResultCommitmentsAreValidatedAtomicallyOnlyForTheFreshCapturedProfile()
    {
        var state = State();
        state.Requirements!.Outputs = new[] { "selected", "unselected" }.Select(name => new TaskInput
            { Name = name, Type = new() { Kind = "string" }, Required = true }).ToList();
        var compact = state.Construction!.Compact!;

        compact.CurrentStep!.OutputNames = ["selected"];
        PlanningCompactConstruction.StageCommitments(state,
            [new() { RequirementId = "result", Operation = "read", Result = new("result", "selected") }]);
        var unselected = new PlanningConstructionCommitment { RequirementId = "result", Operation = "read", Result = new("result", "unselected") };
        var error = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.StageCommitments(state, [unselected]));
        Assert.Contains(error.Diagnostics, d => d.Code == "CONSTRUCTION_COMMITMENT_INVALID");
        Assert.Equal("selected", Assert.Single(compact.PendingCommitments["result"]).Result!.Output);
        Assert.Empty(state.Requirements.Outcomes[0].Commitments ?? []);
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.StageCommitments(state,
            [new() { RequirementId = "result", Operation = "read", Result = new("undeclared", "selected") }]));
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
            Operation: TaskOperations.Describe(capability))], null, Query: state.Construction!.Compact!.CurrentStep!.Objective)];
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
        var names = new List<string>();
        string? address = view["outputDirectory"]!.ToString();
        Assert.Equal("port-page:read@v1:/output:0", address);
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
        PlanningConstruction.LoadEvidence(state, ["page:sources:0"]);
        var selection = new JsonObject { ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["step"] = null,
            ["evidenceRequests"] = new JsonArray(sourceAddress), ["clarifications"] = null };
        Assert.Empty(PlanningContractValidation.ValidateInstance(selection, PlanningCompactConstruction.SelectionSchema(state)));
        var discovery = new JsonObject { ["binding"] = null, ["discoveryRequests"] = null,
            ["evidenceRequests"] = new JsonArray(sourceAddress), ["clarifications"] = null, ["reselectStep"] = null };
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
        var review = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_context",
            ["reason"] = "The original restriction is needed.", ["contextRequests"] = new JsonArray(schemaAddress) } };
        var context = new JsonObject { ["actions"] = new JsonArray(new JsonObject { ["evidence"] = schemaAddress }) };
        var reviewSchema = HybridWorkflowPlanner.ConstructionReviewSchema(context);
        Assert.Empty(PlanningContractValidation.ValidateInstance(review, reviewSchema));
        Assert.Equal("Exact terminal restriction.", PlanningCompactConstruction.LoadContractEvidence(state, schemaAddress)!["schema"]!["description"]!.ToString());
        review["decision"]!["contextRequests"] = new JsonArray(new string('x', 2049));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(review, reviewSchema));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("漢")]
    [InlineData("🙂")]
    public void FullDiscoveryPageFitsItsCapturedAllowanceWithoutPortTreesOrGiantSourceDescriptions(string descriptionSymbol)
    {
        var state = State(navigationCeiling: 8_000);
        state.Construction!.Requirements = [new() { Id = "result" }];
        state.Requirements!.Outcomes = [new("result", string.Concat(Enumerable.Repeat("Preserve the requested data, identity and order. ", 12)))];
        state.Construction!.Compact!.CurrentStep!.Objective = string.Concat(Enumerable.Repeat("Read the authorized original records. ", 6));
        state.Construction!.Compact!.CurrentStep.ExpectedOutputs = [string.Concat(Enumerable.Repeat("Complete observed records. ", 5))];
        state.Construction!.Compact!.CurrentStep.Inputs = [new() { Kind = "input", Source = "query" }];
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
            Operation: TaskOperations.Describe(c))).ToList(), "rank:" + new string('f', 64) + ":8", Query: state.Construction!.Compact!.CurrentStep.Objective)];
        var descriptors = PlanningCompactConstruction.ContractDescriptorPage(state);
        var initialContext = PlanningCompactConstruction.DiscoveryContext(state);
        var sources = PlanningCompactConstruction.SourceDescriptors(state);
        Assert.Equal(4, descriptors["items"]!.AsArray().Count);
        {
            Assert.Null(descriptors["next"]);
            Assert.True(descriptors["descriptionsOmitted"]!.GetValue<bool>());
            Assert.Equal(PlanningCompactConstruction.OperationPageAddress(state, 4), initialContext["directoryEvidence"]!["operations"]!.ToString());
        }
        Assert.Equal(2, sources["items"]!.AsArray().Count);
        Assert.All(descriptors["items"]!.AsArray(), d =>
        {
            Assert.Null(d!["inputs"]); Assert.Null(d["outputs"]);
            {
                Assert.StartsWith(d["description"]?.ToString() ?? "", capabilities.Single(c => c.Id == d["id"]!.ToString()).Description, StringComparison.Ordinal);
                Assert.StartsWith("description:", d["descriptionEvidence"]!.ToString(), StringComparison.Ordinal);
            }
        });
        Assert.All(sources["items"]!.AsArray(), source => Assert.NotNull(source!["descriptionEvidence"]));
        var discoveredIds = new HashSet<string>(StringComparer.Ordinal);
        var page = descriptors;
        var continuation = initialContext["directoryEvidence"]!["operations"]?.ToString();
        PlanningCompactConstruction.RecordPresentedOperations(state,
                PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state), initialContext,
                    PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose).Prompt);
        while (true)
        {
            foreach (var descriptor in page["items"]!.AsArray())
                Assert.True(discoveredIds.Add(descriptor!["id"]!.ToString()));
            if (continuation is null) break;
            PlanningConstruction.LoadEvidence(state, [continuation]);
            page = state.Construction!.Evidence![continuation]!.AsObject();
            continuation = PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]?.ToString();
        }
        Assert.Equal(8, discoveredIds.Count);
        state.Construction!.Evidence = null;
        var context = initialContext;
        Assert.Equal("page:sources:0", context["directoryEvidence"]!["sources"]!.ToString());
        Assert.StartsWith("page:provider-requests:", context["directoryEvidence"]!["providers"]!.ToString(), StringComparison.Ordinal);
        var presentation = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state), context,
            PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose);
        var request = PlanningGenerationPolicy.Apply(new LLMRequest { Prompt = presentation.Prompt, StructuredOutputSchema = presentation.StructuredOutputSchema,
            StructuredOutputStrict = true, UseBackgroundMode = true, MaxTokens = 8192, Provider = "OpenAi", Model = "gpt-5.5-2026-04-24",
            ClientRequestId = new string('s', 64) + ":123:" + new string('h', 64) }, state.Request.Generation);
        var measurement = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, 3000, "step");
        Assert.True(measurement.EstimatedInputTokens <= state.Construction!.Limits.PhasePolicy!.Navigation.MaxInputTokens,
            $"Complete discovery request: {measurement.EstimatedInputTokens} estimated tokens; " +
            string.Join(", ", measurement.Components.Select(c => $"{c.Component}={c.EstimatedTokens}")));
        Assert.DoesNotContain(new string('P', 100), request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(string.Concat(Enumerable.Repeat(descriptionSymbol, 193)), request.Prompt, StringComparison.Ordinal);
        PlanningConstruction.LoadEvidence(state, [context["directoryEvidence"]!["sources"]!.ToString()]);
        Assert.True(JsonNode.DeepEquals(sources, state.Construction!.Evidence!["page:sources:0"]));
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
    public async Task OriginalUnicodeDescriptionPagesRemainExactAndOversizedRequestsStopBeforeDispatch(string symbol, bool sourceDescription)
    {
        var state = State(navigationCeiling: 8_000);
        state.Construction!.Requirements = [new() { Id = "result" }];
        var original = string.Concat(Enumerable.Repeat(symbol, 16384)) + " Exact final restriction.";
        var capability = state.Discovery.Resolved.Single();
        capability.Description = original; capability.Version = new string('v', 64);
        var source = new CapabilitySource(new string('s', 192), original);
        state.Discovery.Sources = [source];
        state.Discovery.Pages = [new(source.Id, null, [new(capability.Id, source.Id, "Declared operation", capability.Description,
            capability.StepType, capability.EffectKind, capability.Version, Operation: TaskOperations.Describe(capability))], null, Query: state.Construction!.Compact!.CurrentStep!.Objective)];
        string? address = sourceDescription
            ? PlanningCompactConstruction.SourceDescriptors(state)["items"]![0]!["descriptionEvidence"]!.ToString()
            : Assert.Single(PlanningCompactConstruction.ContractDescriptors(state))!["descriptionEvidence"]!.ToString();
        var retained = new System.Text.StringBuilder();
        var stoppedBeforeDispatch = false;
        while (address is not null)
        {
            PlanningConstruction.LoadEvidence(state, [address]);
            var page = state.Construction!.Evidence![address]!;
            Assert.Equal(retained.Length, page["offset"]!.GetValue<int>());
            var text = page["text"]!.ToString();
            Assert.NotEmpty(text); Assert.False(char.IsLowSurrogate(text[0])); Assert.False(char.IsHighSurrogate(text[^1]));
            if (symbol == "A" && retained.Length == 0) Assert.Equal(2048, text.Length);
            retained.Append(text);
            var context = PlanningCompactConstruction.DiscoveryContext(state);
            Assert.NotNull(context["directoryEvidence"]);
            var presentation = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state), context,
                PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose);
            var request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose,
                presentation.Prompt, presentation.StructuredOutputSchema!.AsObject());
            var measurement = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, 3000, "step");
            if (measurement.EstimatedInputTokens > state.Construction!.Limits.PhasePolicy!.Navigation.MaxInputTokens)
            {
                var before = JsonSerializer.Serialize(state.Construction, PlanningJsonContext.Default.PlanningConstructionState);
                var runtime = new TestRuntime();
                var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
                    PlanningCompactConstruction.DiscoveryPurpose, presentation.Prompt, presentation.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct));
                Assert.Equal("MODEL_INPUT_LIMIT", error.Code); Assert.Empty(runtime.Calls); Assert.Equal(0, state.ModelCalls);
                stoppedBeforeDispatch = true;
                Assert.Equal(before, JsonSerializer.Serialize(state.Construction, PlanningJsonContext.Default.PlanningConstructionState));
                // The original remains exactly addressable despite local admission stopping.
                address = page["next"]?.ToString();
                while (address is not null)
                {
                    var remaining = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
                    retained.Append(remaining["text"]!.ToString()); address = remaining["next"]?.ToString();
                }
                break;
            }
            address = page["next"]?.ToString();
        }
        Assert.True(stoppedBeforeDispatch, "The actual prepared request must exceed its unchanged captured ceiling.");
        Assert.Equal(original, retained.ToString());
        Assert.Equal(original, capability.Description); Assert.Equal(original, source.Description);
    }

    [Fact]
    public void ReselectionIsAvailableOnlyForDiagnosedTentativeWork()
    {
        var state = State();
        Assert.Null(PlanningCompactConstruction.DraftSchema(state)["properties"]!["reselectStep"]);
        state.Diagnostics = [new("CONSTRUCTION_BUSINESS_REVIEW", "/construction/candidate", "The selected kind cannot perform the original effect.")];
        Assert.NotNull(PlanningCompactConstruction.DraftSchema(state)["properties"]!["reselectStep"]!["anyOf"]);
        state.Construction!.Candidate = new() { AuthorizedPaths = ["/tasks/fixed/outputs/value"] };
        Assert.Null(PlanningCompactConstruction.DraftSchema(state)["properties"]!["reselectStep"]);
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
        if (kind != "operation") state.Construction!.Compact!.CurrentStep.OperationIds.Clear();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var taskKinds = Walk(schema).Where(n => n["properties"]?["objective"] is not null)
            .SelectMany(n => n["properties"]!["kind"]?["enum"]?.AsArray() ?? []).Select(n => n!.ToString()).Distinct().ToArray();
        Assert.Equal(kind is "group" or "choice" or "close" ? [] : new[] { kind }, taskKinds);
        if (kind != "operation") Assert.DoesNotContain("\"read\"", schema.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(schema["properties"]!["discoveryRequests"]);
        Assert.Null(schema["properties"]!["requirements"]);
    }

    [Theory]
    [InlineData("remaining","close")]
    [InlineData("closed","close")]
    [InlineData("remaining","value")]
    public void EmptyExtensionsCannotClaimProgressWithoutANewHostIssuedRequirementClosure(string status,string kind)
    {
        var state = State();
        state.Construction!.OpenScopes = ["/root"];
        state.Construction!.Requirements = [new() { Id = "result", Status = status }];
        state.Construction!.Compact!.CurrentStep!.Kind = kind;
        state.Construction!.Compact!.CurrentStep.CloseRequirementIds = ["result"];
        if (kind == "close" && status == "remaining")
        {
            var closure = PlanningConstruction.Apply(state, new() { RequirementIds = ["result"] });
            Assert.Empty(closure.Plan.Root.Tasks);
            Assert.Empty(closure.TaskIds);
            return;
        }
        var rejected = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state, new() { RequirementIds = ["result"] }));
        Assert.Contains(rejected.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
    }

    [Fact]
    public void CompleteForeachDraftRequestRetainsIterationAndDependencySemanticsAfterProjection()
    {
        var state = State();
        state.Construction!.Requirements = [new() { Id = "result" }];
        var step = state.Construction!.Compact!.CurrentStep!;
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
    [InlineData("transform")]
    [InlineData("value")]
    public void CompactDraftDistinguishesTaskPresenceFromBusinessValueNullability(string kind)
    {
        const string current = "always contains cleanup on completion or failure; preserve available payloads before nested cleanup. present tests preceding local or lexical-ancestor task completion, not caller inputs or field nullability. For a null guard, compare the exact input, output or field with null.";
        var state = State();

        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = kind; step.OperationIds.Clear();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        Assert.Contains(current, schema["description"]!.ToString(), StringComparison.Ordinal);

        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));

        if (kind != "value") return;

        var plan = new TaskPlan
        {
            Inputs = [new() { Name = "caller_text", Type = new() { Kind = "string", Nullable = true } }],
            Root = new() { Tasks =
            [
                new() { Id = "producer", Kind = "value", Objective = "Retain the supplied value.",
                    Outputs = [new("text", new() { Kind = "input", Source = "caller_text" })] },
                new() { Id = "consumer", Kind = "value", Objective = "Use the supplied value under the assertion.",
                    Requires = new() { Kind = "present", Source = "caller_text" },
                    Outputs = [new("text", new() { Kind = "input", Source = "caller_text" })] }
            ] }
        };
        var open = new HashSet<string>(StringComparer.Ordinal) { "/root" };
        Assert.NotEmpty(TaskPlanCompiler.ValidateConstruction(plan, state.Catalog!, state.Request, open).Diagnostics);
        plan.Root.Tasks[1].Requires = new() { Kind = "predicate", Predicate = "not_equal",
            Items = [new() { Kind = "input", Source = "caller_text" }, new() { Kind = "null" }] };
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(plan, state.Catalog!, state.Request, open).Diagnostics);
        plan.Root.Tasks[1].Requires = new() { Kind = "present", Source = "producer" };
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(plan, state.Catalog!, state.Request, open).Diagnostics);
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
        response["baseHash"] = state.Construction!.Compact!.CurrentStep.BaseHash;
        response = PlanningCorpus.Transport(response, schema, schema)!;
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

    private static PlanningSession State(int navigationCeiling = 6_000)
    {
        var contract = Capability("read", "Read only the caller-authorized value.");
        var state = new PlanningSession
        {
            Request = new() { TenantId = "compact-contract-test", Prompt = "Read the exact requested value.", Generation = new("medium", 32000, 8192) },
            Requirements = new() { Summary = "Read the exact requested value", Inputs = [], Outputs = [], Outcomes = [new("result", "Read the exact requested value")] },
            Construction = new() { Limits = new() { PhasePolicy = new() { Navigation = new() { TargetInputTokens = 3_000, MaxInputTokens = navigationCeiling } } }, Requirements = [new() { Id = "result" }], Compact = new() { CurrentStep = new() { Id = "s1", Scope = "/root", Kind = "operation",
                Objective = "Read the exact requested value", OperationIds = ["read"], RequirementIds = ["result"] } } },
            Catalog = new() { AllowedStepTypes = ["mcp.call"] },
            Discovery = new() { Resolved = [contract], Sources = [new("source", "Available operations")] }
        };
        PlanningSessionCreation.Capture(state);
        state.Construction!.Compact!.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan);
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
