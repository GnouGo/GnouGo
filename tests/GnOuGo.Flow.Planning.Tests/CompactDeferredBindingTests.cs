using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactDeferredBindingTests
{
    private const string Objective = "Produce the requested exact record without external changes.";

    [Theory]
    [InlineData("operation", 1, null, true)]
    [InlineData("operation", 2, null, true)]
    [InlineData("operation", 0, null, false)]
    [InlineData("operation", 3, null, false)]
    [InlineData("operation", 1, "worker", false)]
    [InlineData("call", 0, "worker", true)]
    [InlineData("call", 0, null, false)]
    [InlineData("call", 1, "worker", false)]
    [InlineData("close", 0, null, true)]
    [InlineData("close", 1, null, false)]
    [InlineData("close", 0, "worker", false)]
    public void FreshDiscoveryBindingSchemaCouplesKindToOperationAndGroupFields(string kind, int operations, string? group, bool valid)
    {
        var (state, _) = Ready();
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
            PlanningCompactConstruction.DiscoveryContext(state), schema, PlanningCompactConstruction.DiscoveryPurpose);
        var response = Binding(kind);
        response["binding"]!["operationIds"] = new JsonArray(Enumerable.Range(0, operations)
            .Select(index => (JsonNode?)JsonValue.Create("operation_" + index)).ToArray());
        response["binding"]!["groupId"] = group;
        Assert.Equal(valid, PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema!).Count == 0);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
        Assert.Empty(TaskKinds(request.StructuredOutputSchema));
        Assert.Null(request.StructuredOutputSchema["$defs"]);
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public void HistoricalFlatDiscoveryBindingBytesAndNonTaskActionsRemainAvailable()
    {
        var (state, _) = Ready();
        var current = PlanningCompactConstruction.DiscoverySchema(state);
        foreach (var kind in new[] { "value", "transform", "sequence", "conditional", "parallel", "foreach", "close", "group", "choice" })
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Binding(kind), current));
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Discover(null), current));
        state.Construction!.Compact!.PresentationVersion = 3;
        state.Request.Options["construction_presentation_version"] = 3;
        var historical = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.Equal(LegacyBindingSchema().ToJsonString(), historical["properties"]!["binding"]!.ToJsonString());
        current["properties"]!["binding"] = LegacyBindingSchema();
        Assert.Equal(historical.ToJsonString(), current.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssuedVersionFourFlatBindingRecoversExactBytesWithoutWeakeningSemanticValidation(bool invalidBinding)
    {
        var (state, runtime) = Ready();
        var baseline = PlanningConstruction.Hash(state.Plan);
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        schema["properties"]!["binding"] = LegacyBindingSchema();
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
            PlanningCompactConstruction.DiscoveryContext(state), schema, PlanningCompactConstruction.DiscoveryPurpose);
        request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, request.Prompt, schema);
        var response = Binding(invalidBinding ? "close" : "value");
        if (invalidBinding) response["binding"]!["operationIds"] = new JsonArray("read");
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema!));
        Assert.Equal(!invalidBinding, PlanningContractValidation.ValidateInstanceFindings(response, PlanningCompactConstruction.DiscoverySchema(state)).Count == 0);
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = request, CorrectionAccounting = 0 };
        state.ModelCalls++;
        runtime.Respond = (issued, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            return Wire(issued, response);
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(exact, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(!invalidBinding, state.Construction.Compact!.CurrentStep!.DiscoveryComplete);
        if (invalidBinding)
        {
            Assert.Null(state.Construction.Compact.CurrentStep.Kind);
            Assert.Equal("DISCOVERY_SELECTION_INVALID", Assert.Single(state.Diagnostics).Code);
        }
        else
        {
            Assert.Equal("value", state.Construction.Compact.CurrentStep.Kind);
            Assert.Empty(state.Diagnostics);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("extract")]
    [InlineData("interpret")]
    public async Task LocalSelectionDraftsDirectlyWithoutDiscoveryOrAnotherModelCall(string? transformMode)
    {
        var (state, runtime) = Ready(selected: false);
        var kind = transformMode is null ? "value" : "transform";
        var pages = new Pages(); runtime.Capabilities = pages;
        if (transformMode is not null) state.Catalog!.AllowedStepTypes = ["mcp.call", "mapping.dynamic", "llm.call", "template.render"];
        state.Request.MaxModelCalls = 3; // selection, draft, mandatory local review; no discovery reserve for local work.
        runtime.Respond = (request, _) => Wire(request, Select(state, kind));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete);
        Assert.Equal(kind, state.Construction.Compact.CurrentStep.Kind);
        Assert.Single(runtime.Calls);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            Assert.DoesNotContain("operation", TaskKinds(request.StructuredOutputSchema!));
            var response = Draft(state);
            if (transformMode is not null)
            {
                var extension = response["extension"]!.Deserialize(PlanningJsonContext.Default.PlanningConstructionExtension)!;
                extension.Tasks = [new() { Id = "transformed", Kind = "transform", Mode = transformMode, Objective = Objective,
                    Inputs = [new("record", new() { Kind = "string", Text = "EXACT" })],
                    ResultType = new() { Kind = "object", Fields = [new() { Name = "record", Type = new() { Kind = "string" } }] } }];
                response["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension);
            }
            return Wire(request, response);
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.NotNull(state.Construction!.Candidate);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(pages.Cursors);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("transform")]
    [InlineData("operation")]
    public async Task UnsetImplementationBindsOnlyAfterDiscoveryWithoutAReselectionCharge(string kind)
    {
        var (state, runtime) = Ready(selected: false);
        var pages = new Pages(); runtime.Capabilities = pages;
        runtime.Respond = (request, _) => Wire(request, Select(state, null));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.False(state.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Throws<PlanningConflictException>(() => PlanningCompactConstruction.DraftSchema(state));
        AddContract(state, Contract());
        var original = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose);
            Assert.Null(request.StructuredOutputSchema!["$defs"]?["task"]);
            return Wire(request, Binding(kind));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(kind, state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.True(state.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(pages.Cursors);
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(new[] { kind }, TaskKinds(PlanningCompactConstruction.DraftSchema(state)));
        Assert.Equal(kind == "operation", state.Construction.Compact.PendingCommitments["req_0"].Count == 1);
    }

    [Theory]
    [InlineData(false, 2, false)]
    [InlineData(false, 3, false)]
    [InlineData(false, 4, false)]
    [InlineData(false, 5, false)]
    [InlineData(true, 3, false)]
    [InlineData(true, 4, false)]
    [InlineData(true, 5, false)]
    [InlineData(true, 5, true)]
    public async Task RetainedLocalTransformSelectionKeepsExactGuidanceAndSchema(bool discovery, int presentation, bool retainTransformGuidance)
    {
        var (state, runtime) = Ready(selected: discovery);
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        var purpose = discovery ? PlanningCompactConstruction.DiscoveryPurpose : PlanningCompactConstruction.SelectPurpose;
        var guidance = discovery ? PlanningCompactConstruction.DiscoveryGuidance(state) : PlanningCompactConstruction.SelectGuidance(state);
        // Reconstruct exact previously issued instructions, not a claim that wording improves model behavior.
        var previous = discovery ? guidance.Replace("offered constraints", "necessary constraints", StringComparison.Ordinal)
            .Replace("Binding tentatively selects implementation; the host resolves its exact contract and admits the required constraints before drafting", "Bind only after its needed inputs and contract constraints are known", StringComparison.Ordinal) : guidance;
        if (!retainTransformGuidance) previous = previous.Replace(PlanningCompactConstruction.TransformSemantics + (discovery ? " " : ""),
            discovery ? "" : "transform extracts/interprets supplied data, never external effects.", StringComparison.Ordinal);
        if (presentation < 5) Assert.Equal(previous, guidance);
        else Assert.NotEqual(previous, guidance);
        var context = discovery ? PlanningCompactConstruction.DiscoveryContext(state) : PlanningCompactConstruction.SelectionContext(state);
        var schema = discovery ? PlanningCompactConstruction.DiscoverySchema(state) : PlanningCompactConstruction.SelectionSchema(state);
        var current = PlanningCompactConstruction.Request(guidance, context, schema, purpose);
        var request = PlanningCompactConstruction.Request(previous, context, schema, purpose);
        Assert.Equal(current.StructuredOutputSchema!.ToJsonString(), request.StructuredOutputSchema!.ToJsonString());
        request = PlanningModelCalls.PrepareRequest(state, purpose, request.Prompt, request.StructuredOutputSchema.AsObject());
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var baseline = PlanningConstruction.Hash(state.Plan);
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = purpose, Request = request };
        state.ModelCalls = 1;
        var pages = new Pages(); runtime.Capabilities = pages;
        runtime.Respond = (issued, actualPurpose) =>
        {
            Assert.Equal(purpose, actualPurpose);
            Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            var response = discovery ? Binding("transform") : Select(state, "transform");
            if (!discovery && presentation == 2) response["step"]!.AsObject().Remove("prerequisites");
            return Wire(issued, response);
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("transform", state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.True(state.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Null(state.PendingCall); Assert.Single(runtime.Calls); Assert.Empty(pages.Cursors);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan)); Assert.Empty(state.Construction.Checkpoints);
    }

    [Theory]
    [InlineData("requirement:req_0", true)]
    [InlineData("source:request:0:clause:0", true)]
    [InlineData("requirement:unrelated", false)]
    [InlineData("task:future_producer", false)]
    [InlineData("schema:read@stale:/input", false)]
    public void PrerequisiteLinksPreserveOriginalAuthorityAndCannotInventFutureBindings(string address, bool valid)
    {
        var (state, _) = Ready();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Prerequisites = [new() { Need = "The original authorized record identity.", EvidenceAddresses = [address] }];
        var original = PlanningConstruction.Hash(state.Plan);
        if (valid) PlanningCompactConstruction.ValidateSelection(state, step);
        else Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, step));
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task RepeatedExactPrerequisiteCitationsNormalizeOnlyFreshNavigation(int presentation)
    {
        var (state, runtime) = Ready(selected: false);
        state.Request.Options["construction_presentation_version"] = presentation;
        state.Construction!.Compact!.PresentationVersion = presentation;
        var response = Select(state, "value");
        response["step"]!["prerequisites"] = new JsonArray(new JsonObject
        {
            ["need"] = "Use the original requested record.",
            ["evidenceAddresses"] = new JsonArray("requirement:req_0", "source:request:0:clause:0", "source:request:0:clause:0")
        });
        var original = response.ToJsonString();
        var baseline = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, _) => Wire(request, response);
        state = await Advance(state, runtime);
        Assert.Equal(original, response.ToJsonString());
        Assert.Single(runtime.Calls);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        if (presentation < 5)
        {
            Assert.Null(state.Construction.Compact!.CurrentStep);
            Assert.Equal("CONSTRUCTION_STEP_INVALID", Assert.Single(state.Diagnostics).Code);
        }
        else
        {
            Assert.Empty(state.Diagnostics);
            var accepted = Assert.Single(state.Construction.Compact!.CurrentStep!.Prerequisites!);
            Assert.Equal(new[] { "requirement:req_0", "source:request:0:clause:0" }, accepted.EvidenceAddresses);
            var recovered = PlannerFixture.Clone(state);
            Assert.Equal(accepted.EvidenceAddresses, Assert.Single(recovered.Construction!.Compact!.CurrentStep!.Prerequisites!).EvidenceAddresses);
            Assert.True(recovered.Construction.Compact.CurrentStep.DiscoveryComplete);
        }
    }

    [Theory]
    [InlineData("requirement:req_0", 5)]
    [InlineData("requirement:missing", 2)]
    [InlineData("task:future_producer", 2)]
    [InlineData("schema:read@stale:/input", 2)]
    public void CitationNormalizationPreservesRawBoundsAndOriginalEvidenceChecks(string address, int count)
    {
        var (state, _) = Ready();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Prerequisites = [new() { Need = "Use the original requested record.", EvidenceAddresses = Enumerable.Repeat(address, count).ToList() }];
        var original = step.Prerequisites[0].EvidenceAddresses.ToArray();
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, step));
        Assert.Equal(original, step.Prerequisites[0].EvidenceAddresses);
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task CallerDeclarationPrerequisitesSurviveSelectionAndBindingWithoutExpandingBusinessCoverage()
    {
        const string declaration = "Accept required numeric caller input sourceValue. ";
        const string work = "Produce the requested result from sourceValue.";
        const string declarationSource = "source:request:0:clause:0";
        var state = new PlanningSession { Request = new() { TenantId = "test", Prompt = declaration + work } };
        PlanningSessionCreation.Capture(state);
        PlanningCompactConstruction.InitializeSources(state);
        var compact = state.Construction!.Compact!;
        compact.Sources[0].Indexed = true;
        foreach (var (text, index) in new[] { declaration, work }.Select((text, index) => (text, index)))
        {
            var source = "request:0:clause:" + index;
            compact.Sources.Add(new() { Id = source, Version = PlanningGraphCompiler.Fingerprint(text), Text = text,
                Kind = "requirement", InterfaceKind = "none", Indexed = true });
            compact.RequirementSources["req_" + index] = [source];
        }
        var input = new TaskInput { Name = "sourceValue", Type = new() { Kind = "number" } };
        state.Requirements = new() { Summary = declaration + work, Inputs = [input], Outputs = [],
            Outcomes = [new("req_0", declaration) { Commitments = [] }, new("req_1", work) { Commitments = [] }] };
        state.Construction.Requirements = [new() { Id = "req_0", Status = "closed" }, new() { Id = "req_1" }];
        state.Plan = new() { Inputs = [input] }; state.Catalog = new(); state.IntentVersion = 3;
        var baseline = PlanningConstruction.Hash(state.Plan);
        // A bounded original page exposes the earlier declaration beside the
        // current obligation. Selection consumes this navigation page afterward.
        PlanningConstruction.LoadEvidence(state, ["page:requirements:0"]);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var response = Select(state, null);
            response["step"]!["requirementIds"] = new JsonArray("req_1");
            response["step"]!["objective"] = work;
            response["step"]!["inputs"] = new JsonArray(new JsonObject { ["kind"] = "input", ["source"] = "sourceValue" });
            response["step"]!["prerequisites"] = new JsonArray(new JsonObject
                { ["need"] = "Use the declared caller input.", ["evidenceAddresses"] = new JsonArray("requirement:req_0", declarationSource) });
            var wire = Wire(request, response);
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(wire.Json!, request.StructuredOutputSchema!));
            return wire;
        } };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(["req_1"], state.Construction!.Compact!.CurrentStep!.RequirementIds);
        Assert.Null(state.Construction.Evidence);
        var originals = PlanningCompactConstruction.SelectionContext(state)["requirements"]!.AsArray();
        Assert.Equal(new[] { "req_0", "req_1" }, originals.Select(row => row!["id"]!.ToString()));
        Assert.Equal(new[] { declaration, work }, originals.Select(row => row!["original"]!.ToString()));
        var discovery = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.Equal(new[] { "requirement:req_0", declarationSource }, discovery["step"]!["prerequisites"]![0]!["evidenceAddresses"]!.AsArray().Select(address => address!.ToString()));
        runtime.Respond = (request, _) =>
        {
            foreach (var address in new[] { "requirement:req_0", declarationSource })
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Wire(request, new JsonObject
                    { ["evidenceRequests"] = new JsonArray(address) }).Json!, request.StructuredOutputSchema!));
            return Wire(request, Binding("value"));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var selected = state.Construction!.Compact!.CurrentStep!;
        Assert.True(selected.DiscoveryComplete);
        Assert.Equal("value", selected.Kind);
        Assert.Equal(["req_1"], selected.RequirementIds);
        Assert.Empty(selected.CloseRequirementIds);
        Assert.Equal("closed", state.Construction.Requirements[0].Status);
        Assert.Equal(0, state.ReplanAttempts);
        var draft = PlanningConstruction.DraftRequest(state, repair: false);
        foreach (var address in new[] { "requirement:req_0", declarationSource })
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Wire(draft, new JsonObject
                { ["baseHash"] = baseline, ["evidenceRequests"] = new JsonArray(address) }).Json!, draft.StructuredOutputSchema!));
        PlanningConstruction.LoadEvidence(state, [declarationSource],
            followup: current => PlanningConstruction.DraftRequest(current, repair: false), purpose: PlanningConstruction.DraftPurpose);
        Assert.Equal(declaration, state.Construction.Evidence![declarationSource]!["text"]!.ToString());
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction.Checkpoints);
        selected.CloseRequirementIds = ["req_0"];
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, selected));
    }

    [Theory]
    [InlineData("description:read@v1:0", "value")]
    [InlineData("description:read@v1:0", "operation")]
    [InlineData("schema:read@v1:/input/properties/target", "value")]
    [InlineData("schema:read@v1:/input/properties/target", "operation")]
    [InlineData("contract:read@v1", "value")]
    [InlineData("contract:read@v1", "operation")]
    public async Task SelectedContractPrerequisitesSurviveBindingWithoutRereadingOrSelectingTheirSchemas(string address, string kind)
    {
        var (state, runtime) = Ready(selected: false);
        AddContract(state, Contract());
        state.Construction!.Compact!.CurrentStep = new() { OperationIds = ["read"] };
        var original = PlanningCompactConstruction.LoadContractEvidence(state, address)!.DeepClone();
        state.Construction.Evidence = new() { [address] = original.DeepClone(), ["requirement:unrelated"] = new JsonObject() };
        state.Construction.Compact.CurrentStep = null;
        runtime.Respond = (request, _) =>
        {
            var response = Select(state, null);
            response["step"]!["prerequisites"] = new JsonArray(new JsonObject
                { ["need"] = "Preserve the inspected original constraint.", ["evidenceAddresses"] = new JsonArray(address) });
            return Wire(request, response);
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(address, Assert.Single(state.Construction!.Evidence!).Key);
        Assert.Empty(state.Construction.Compact!.CurrentStep!.SchemaPointers);
        runtime.Respond = (request, _) => Wire(request, Binding(kind));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete);
        Assert.Empty(state.Construction.Compact.CurrentStep.SchemaPointers);
        Assert.True(JsonNode.DeepEquals(original, state.Construction.Evidence![address]));
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public void UnavailableInputsAndOpenReusableGroupsRemainInvalidBeforeBinding()
    {
        var (state, _) = Ready();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Inputs = [new() { Kind = "output", Source = "future_producer", Port = "record" }];
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, step));
        step.Inputs.Clear(); step.Kind = "call"; step.GroupId = "worker";
        state.Plan!.Groups.Add(new() { Id = "worker" });
        state.Construction.OpenScopes.Add("/groups/worker/body");
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, step));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RequiredContractClosureIsAdmittedBeforeBindingAndOptionalGiantBranchesStayOmitted(bool required, bool unresolved)
    {
        var (state, runtime) = Ready();
        var contract = Contract();
        contract.InputSchema["properties"]!["large"] = new JsonObject { ["type"] = "string", ["description"] = new string('x', 80_000) };
        if (required) contract.InputSchema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("large"));
        var exact = JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability);
        var pages = new Pages(resolved: unresolved ? contract : null); runtime.Capabilities = pages;
        if (unresolved)
            state.Discovery.Pages.Add(new("source", null,
                [new(contract.Id, "source", "Read a record", contract.Description, contract.StepType,
                    contract.EffectKind, contract.Version, Operation: TaskOperations.Describe(contract))], null, Query: Objective));
        else AddContract(state, contract);
        var baseline = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose);
            Assert.Empty(pages.Resolutions);
            if (unresolved)
            {
                Assert.Empty(state.Discovery.Resolved);
                var descriptor = Assert.Single(OriginalContext(request)["descriptors"]!["items"]!.AsArray())!;
                Assert.Equal("read", descriptor["id"]!.ToString());
                Assert.DoesNotContain(Nodes(OriginalContext(request)).OfType<JsonObject>(), node => node.ContainsKey("inputSchema"));
            }
            return Wire(request, Binding("operation"));
        };
        state = await Advance(state, runtime);
        Assert.Equal(unresolved ? 1 : 0, pages.Resolutions.Count);
        Assert.Empty(pages.Cursors); Assert.Single(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts); Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(exact, JsonSerializer.Serialize(state.Discovery.Resolved.Single(), PlanningJsonContext.Default.PlanningCapability));
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        if (required)
        {
            Assert.Equal(PlanningStatus.Stopped, state.Status);
            Assert.Contains(state.Diagnostics, d => d.Code == "CONSTRUCTION_CONTRACT_LIMIT");
            Assert.Null(state.Construction!.Compact!.CurrentStep!.Kind);
            Assert.False(state.Construction.Compact.CurrentStep.DiscoveryComplete);
            Assert.Empty(state.Construction.Compact.PendingCommitments);
            Assert.Contains(state.RequestMeasurements!, m => m.Phase == PlanningConstruction.DraftPurpose && m.Admission == "rejected_before_dispatch");
        }
        else
        {
            Assert.Empty(state.Diagnostics);
            Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete);
            var draft = PlanningConstruction.DraftRequest(state, repair: false);
            var view = Assert.Single(OriginalContext(draft)["operations"]!.AsArray())!;
            Assert.NotNull(view["inputSchema"]!["properties"]!["target"]);
            Assert.Contains("target", view["inputSchema"]!["required"]!.AsArray().Select(value => value!.ToString()));
            Assert.Null(view["inputSchema"]!["properties"]!["large"]);
            Assert.Contains("schema:read@v1:/input/properties/large", view["omittedOptionalInputs"]!.AsArray().Select(n => n!.ToString()));
        }
    }

    [Fact]
    public async Task SelectedProviderPaginationUsesFourReadsAndKeepsRemainingCursorWithoutSelectingOperations()
    {
        var (state, runtime) = Ready();
        var catalog = new Pages(); runtime.Capabilities = catalog;
        runtime.Respond = (request, _) => Wire(request, Discover(null));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(new string?[] { null, "1", "2", "3" }, catalog.Cursors);
        Assert.Equal(4, state.Discovery.Pages.Count);
        Assert.Contains(runtime.Checkpoints, saved => saved.Discovery.Pages.Count == 4);
        Assert.All(state.Discovery.Pages, page => Assert.Equal(Objective, page.Query));
        Assert.Null(state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.Empty(state.Construction.Compact.CurrentStep.OperationIds);
        runtime.Respond = (request, _) => Wire(request, Discover("4"));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(new string?[] { null, "1", "2", "3", "4", "5" }, catalog.Cursors);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task SavedProviderActionReusesCompletedPagesAndItsExactIssuedRequest()
    {
        var (state, runtime) = Ready();
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
            PlanningCompactConstruction.DiscoveryContext(state), PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose);
        request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject());
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = request };
        state.ModelCalls++;
        state.Discovery.Pages.Add(new("source", null, [], "1", Query: Objective));
        state.Discovery.Pages.Add(new("source", "1", [], "2", Query: Objective));
        var catalog = new Pages(); runtime.Capabilities = catalog;
        runtime.Respond = (issued, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            return Wire(issued, Discover(null));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(new string?[] { "2", "3", "4", "5" }, catalog.Cursors);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public async Task InterruptedMetadataActionResumesItsRemainingPagesWithoutAnotherModelCallOrCharge(int completed, bool correcting)
    {
        var (state, runtime) = Ready();
        var catalog = new Pages(); runtime.Capabilities = catalog;
        if (correcting) state.Diagnostics = [new("DISCOVERY_SCOPE_INVALID", "/discoveryRequests", "Select the original provider action.")];
        runtime.Respond = (request, _) => Wire(request, Discover(null));
        var interrupted = new InterruptedRuntime(runtime, completed);
        await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, interrupted, PlannerFixture.Ct);
        var saved = Assert.IsType<PlanningSession>(interrupted.Saved);
        Assert.NotNull(saved.Construction!.Compact!.CurrentStep!.PendingDiscovery);
        Assert.Equal(completed, saved.Discovery.Pages.Count);
        Assert.Equal(1, saved.ModelCalls);
        Assert.Equal(0, saved.ReplanAttempts);
        runtime.Respond = (_, _) => throw new InvalidOperationException("A received metadata action must resume without asking the model again.");
        var resumed = await Advance(saved, runtime);
        Assert.DoesNotContain(resumed.Diagnostics, d => d.Code is "PLANNING_HOST_FAILURE" or "MODEL_DISPATCH_UNVERIFIABLE");
        Assert.Null(resumed.Construction!.Compact!.CurrentStep!.PendingDiscovery);
        Assert.Equal(new string?[] { null, "1", "2", "3" }, catalog.Cursors);
        Assert.Equal(1, resumed.ModelCalls);
        Assert.Equal(0, resumed.ReplanAttempts);
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task InvalidMetadataResponseChargesItsCorrectionWithoutCreatingAContinuation()
    {
        var (state, runtime) = Ready();
        state.Diagnostics = [new("DISCOVERY_SCOPE_INVALID", "/discoveryRequests", "Select the original provider action.")];
        runtime.Respond = (_, _) =>
        {
            var response = Discover(null);
            response["discoveryRequests"]![0]!["sourceId"] = "unavailable_source";
            return new() { Json = response };
        };
        state = await Advance(state, runtime);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, state.ModelCalls);
        Assert.Null(state.Construction!.Compact!.CurrentStep!.PendingDiscovery);
        Assert.Empty(state.Discovery.Pages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableOrCyclicProviderPagesStopAndRetainTheirOutcomes(bool cycle)
    {
        var (state, runtime) = Ready();
        var catalog = new Pages(cycle ? "cycle" : "unavailable"); runtime.Capabilities = catalog;
        runtime.Respond = (request, _) => Wire(request, Discover(null));
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.NotEmpty(state.Discovery.Pages);
        Assert.Contains(state.Diagnostics, d => d.Code == (cycle ? "DISCOVERY_NO_PROGRESS" : "DISCOVERY_INCOMPLETE"));
        Assert.Null(state.Construction!.Compact!.CurrentStep!.Kind);
        var reads = catalog.Cursors.Count;
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        state = await Advance(state, runtime);
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(reads, catalog.Cursors.Count);
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task OversizedSavedDiscoveryFollowupStopsWithoutRepeatedAdmissionOrDispatch()
    {
        var (state, runtime) = Ready();
        var catalog = new Pages("oversized"); runtime.Capabilities = catalog;
        runtime.Respond = (request, _) => Wire(request, Discover(null));
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "CONSTRUCTION_DISCOVERY_LIMIT");
        Assert.NotNull(state.Construction!.Compact!.CurrentStep!.PendingDiscovery);
        Assert.Contains(state.RequestMeasurements!, m => m.Phase == PlanningCompactConstruction.DiscoveryPurpose && m.Admission == "rejected_before_dispatch");
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        state = await Advance(state, runtime);
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Single(catalog.Cursors);
        Assert.Single(runtime.Calls);
    }

    [Theory]
    [InlineData("draft", false)]
    [InlineData("draft", true)]
    [InlineData("discovery", false)]
    [InlineData("discovery", true)]
    [InlineData("judge", false)]
    [InlineData("judge", true)]
    public async Task V5SelectedPhasesRetrieveLaterOriginalsWithoutChangingClaims(string phase, bool recover)
    {
        var (state, runtime) = OriginalPages(phase);
        // This retained protocol unions requested originals into one review;
        // fresh v6 presents all author originals and reviews whole-clause windows.
        state.Construction!.Compact!.PresentationVersion = 5;
        state.Request.Options["construction_presentation_version"] = 5;
        var accepted = PlanningConstruction.Hash(state.Plan);
        var stepBytes = JsonSerializer.Serialize(state.Construction!.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var claims = state.Construction.Requirements.Select(item => (item.Id, item.Status)).ToArray();
        var candidate = state.Construction.Candidate?.Hash;
        var initial = OriginalRequest(state, phase);
        var initialContext = OriginalContext(initial);
        Assert.Equal("page:requirements:0", initialContext["requirementsDirectory"]!.ToString());
        Assert.Equal("page:requirements:8", initialContext["nextRequirements"]!.ToString());
        if (recover)
        {
            // A saved request predates the new navigation; its exact envelope is retained.
            initialContext.Remove("requirementsDirectory"); initialContext.Remove("nextRequirements");
            var schema = initial.StructuredOutputSchema!.DeepClone().AsObject();
            foreach (var node in Nodes(schema).OfType<JsonObject>())
                if (node["enum"] is JsonArray values)
                    foreach (var value in values.Where(value => value?.ToString().StartsWith("page:requirements:", StringComparison.Ordinal) == true).ToArray())
                        values.Remove(value);
            var old = PlanningCompactConstruction.Request(initial.Prompt[..initial.Prompt.LastIndexOf('\n')], initialContext, schema,
                phase == "judge" ? PlanningConstruction.LocalPurpose : phase == "draft" ? PlanningConstruction.DraftPurpose : PlanningCompactConstruction.DiscoveryPurpose);
            initial = PlanningModelCalls.PrepareRequest(state, phase == "judge" ? PlanningConstruction.LocalPurpose : phase == "draft" ? PlanningConstruction.DraftPurpose : PlanningCompactConstruction.DiscoveryPurpose,
                old.Prompt, old.StructuredOutputSchema!.AsObject());
            state.PendingCall = new() { Id = initial.ClientRequestId!, Purpose = initial.StructuredOutputSchema!["title"]!.ToString(), Request = initial, CorrectionAccounting = 0 };
            if (phase == "draft") state.Construction.IssuedBaseHash = accepted;
            if (phase == "judge")
            {
                state.Construction.PendingReviewContext = initialContext;
                state.Construction.PendingReviewHash = PlanningGraphCompiler.Fingerprint(PlanningConstruction.LocalPurpose + "\n" + candidate + "\n" + initialContext.ToJsonString());
            }
            state.ModelCalls++;
            var exact = JsonSerializer.Serialize(initial, PlanningJsonContext.Default.LLMRequest);
            runtime.Respond = (request, _) =>
            {
                Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                return OriginalEvidence(request, phase, "source:request:0:clause:0");
            };
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
            Assert.Equal(1, state.ModelCalls);
        }
        foreach (var address in new[] { "page:requirements:8", "page:requirements:16", "page:requirements:0" })
        {
            runtime.Respond = (request, _) =>
            {
                var context = OriginalContext(request);
                if (address == "page:requirements:16")
                    Assert.Equal(address, context["evidence"]!["page:requirements:8"]!["next"]!.ToString());
                var response = OriginalEvidence(request, phase, address);
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response.Json!, request.StructuredOutputSchema!));
                return response;
            };
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
            var page = state.Construction!.Evidence![address]!;
            var items = page["items"]!.AsArray();
            Assert.InRange(items.Count, 1, 8);
            foreach (var item in items)
                Assert.True(JsonNode.DeepEquals(item, PlanningCompactConstruction.RequirementNodes(state, [item!["id"]!.ToString()])[0]));
            Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
            Assert.Equal(candidate, state.Construction.Candidate?.Hash);
            Assert.Equal(stepBytes, JsonSerializer.Serialize(state.Construction.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
            Assert.Equal(claims, state.Construction.Requirements.Select(item => (item.Id, item.Status)));
            Assert.Empty(state.Construction.Checkpoints);
            Assert.Null(state.EditablePaths); Assert.Null(state.Construction.Candidate?.AuthorizedPaths);
            Assert.Equal(0, state.ReplanAttempts); Assert.Equal(0, state.Construction.DraftCorrections);
        }
        Assert.Equal(recover ? 4 : 3, state.ModelCalls);
        var followup = OriginalRequest(state, phase);
        var displayed = OriginalContext(followup);
        Assert.Null(displayed["requirementsDirectory"]); Assert.Null(displayed["nextRequirements"]);
        if (phase != "judge")
            Assert.DoesNotContain("page:requirements:", followup.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        else
        {
            var requirement = displayed["requirements"]!.AsArray().Select((node, index) => (node, index))
                .Single(item => item.node!["id"]!.ToString() == "req_10");
            var finding = new JsonObject { ["kind"] = "inconsistency", ["location"] = "/draft/tasks/0",
                ["obligation"] = "/requirements/" + requirement.index, ["implementation"] = "/draft/tasks/0",
                ["explanation"] = "The current value does not fulfill this retrieved original obligation.",
                ["uncertainty"] = "Advisory business interpretation.", ["fact"] = null, ["requiredBinding"] = null };
            runtime.Respond = (request, _) => Wire(request, new JsonObject
            { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray(finding.DeepClone()) }, ["uncertainty"] = "No execution observed." });
            state = await Advance(state, runtime);
            Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
            Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan)); Assert.Empty(state.Construction!.Checkpoints);
            Assert.Equal(stepBytes, JsonSerializer.Serialize(state.Construction.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
            Assert.Null(state.Construction.Candidate!.AuthorizedPaths); Assert.Null(state.EditablePaths);
            Assert.Equal(0, state.ReplanAttempts); Assert.Equal(0, state.Construction.DraftCorrections);
        }
        var replay = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, ["page:requirements:8"]));
        Assert.Contains(replay.Diagnostics, item => item.Code == "REPLAN_NO_PROGRESS");
        var invalid = state.Construction!.Compact!.CurrentStep!;
        invalid.CloseRequirementIds = ["req_10"];
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, invalid));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HistoricalSelectedPhasesKeepTheirOriginalRequirementProjection(int version)
    {
        foreach (var phase in new[] { "draft", "discovery", "judge" })
        {
            var (state, _) = OriginalPages(phase);
            state.Construction!.Compact!.PresentationVersion = version;
            state.Request.Options["construction_presentation_version"] = version;
            var request = OriginalRequest(state, phase);
            var context = OriginalContext(request);
            Assert.Null(context["requirementsDirectory"]); Assert.Null(context["nextRequirements"]);
            Assert.Single(context["requirements"]!.AsArray());
            PlanningConstruction.LoadEvidence(state, ["page:requirements:8"]);
            var after = OriginalContext(OriginalRequest(state, phase));
            Assert.Single(after["requirements"]!.AsArray());
            Assert.Null(after["requirementsDirectory"]); Assert.Null(after["nextRequirements"]);
        }
    }

    [Fact]
    public async Task RetrievedOriginalCannotGrantAnAcceptedTaskRepairLocation()
    {
        var (state, runtime) = OriginalPages("judge");
        state.Plan!.Root.Tasks.Add(new() { Id = "accepted", Kind = "value", Objective = "Keep accepted work.",
            Outputs = [new("value", new() { Kind = "string", Text = "UNCHANGED" })] });
        var construction = state.Construction!;
        var accepted = PlanningConstruction.Hash(state.Plan);
        construction.Compact!.CurrentStep!.BaseHash = accepted;
        construction.Candidate = PlanningConstruction.Apply(state,
            JsonSerializer.Deserialize(Draft(state)["extension"], PlanningJsonContext.Default.PlanningConstructionExtension)!);
        PlanningConstruction.LoadEvidence(state, ["page:requirements:8", "task:accepted"],
            followup: next => OriginalRequest(next, "judge"), purpose: PlanningConstruction.LocalPurpose);
        runtime.Respond = (request, _) =>
        {
            var context = OriginalContext(request);
            Assert.Equal("UNCHANGED", context["evidence"]!["task:accepted"]!["outputs"]![0]!["value"]!["text"]!.ToString());
            var index = context["requirements"]!.AsArray().Select((item, index) => (item, index))
                .Single(pair => pair.item!["id"]!.ToString() == "req_10").index;
            var finding = new JsonObject { ["kind"] = "inconsistency", ["location"] = "/draft/tasks/0",
                ["obligation"] = "/requirements/" + index, ["implementation"] = "/draft/tasks/0",
                ["explanation"] = "The tentative value conflicts with the retrieved original.",
                ["uncertainty"] = "Advisory review.", ["fact"] = null, ["requiredBinding"] = null };
            var report = new JsonObject { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray(finding) },
                ["uncertainty"] = "No execution observed." };
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(report, request.StructuredOutputSchema!));
            finding["location"] = "/evidence/task:accepted";
            Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(report, request.StructuredOutputSchema!));
            finding["location"] = "/draft/tasks/0";
            return Wire(request, report);
        };
        state = await Advance(state, runtime);
        Assert.Equal("/tasks/value", Assert.Single(state.Diagnostics).Location);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(["value"], state.Construction!.Candidate!.TaskIds);
        Assert.Null(state.EditablePaths); Assert.Null(state.Construction.Candidate.AuthorizedPaths);
        Assert.Empty(state.Construction.Checkpoints); Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public void V5RetrievedOriginalUnionStillRequiresCompleteReviewAdmission()
    {
        var (state, _) = OriginalPages("judge", count: 19);
        // V5's complete union must still stop; v6's bounded review windows are a
        // different captured protocol, exercised by the review coverage fixtures.
        state.Construction!.Compact!.PresentationVersion = 5;
        state.Request.Options["construction_presentation_version"] = 5;
        PlanningConstruction.LoadEvidence(state, ["page:requirements:8", "page:requirements:16"],
            followup: Review, purpose: PlanningConstruction.LocalPurpose);
        var original = state.Construction!.Evidence!.ToJsonString();
        var offset = state.Construction.Compact!.RequirementOffset;
        var reads = state.Construction.EvidenceRequests.ToArray();
        var candidate = state.Construction.Candidate!.Hash; var accepted = PlanningConstruction.Hash(state.Plan);
        var error = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, ["page:requirements:0"],
            followup: Review, purpose: PlanningConstruction.LocalPurpose));
        Assert.Contains(error.Diagnostics, item => item.Code == "CONSTRUCTION_EVIDENCE_LIMIT");
        Assert.Equal(original, state.Construction.Evidence.ToJsonString());
        Assert.Equal(offset, state.Construction.Compact.RequirementOffset);
        Assert.Equal(reads, state.Construction.EvidenceRequests);
        Assert.Equal(candidate, state.Construction.Candidate.Hash); Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction.Checkpoints); Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        LLMRequest Review(PlanningSession current)
        {
            var request = OriginalRequest(current, "judge");
            return PlanningModelCalls.PrepareRequest(current, PlanningConstruction.LocalPurpose, request.Prompt,
                request.StructuredOutputSchema!.AsObject(), current.Construction!.Limits.ReviewOutputTokens);
        }
    }

    private static (PlanningSession, TestRuntime) OriginalPages(string phase, int count = 17)
    {
        var (state, runtime) = Ready(); var construction = state.Construction!; var compact = construction.Compact!;
        for (var index = 1; index < count; index++)
        {
            var id = "req_" + index; var original = "Retain exact label " + index + ". "; var source = "request:0:clause:" + index;
            state.Requirements!.Outcomes.Add(new(id, original)); construction.Requirements.Add(new() { Id = id });
            compact.Sources.Add(new() { Id = source, Text = original, Version = PlanningGraphCompiler.Fingerprint(original), Kind = "requirement", InterfaceKind = "none", Indexed = true });
            compact.RequirementSources[id] = [source];
        }
        var text = string.Concat(state.Requirements!.Outcomes.Select(item => item.Description));
        state.Request.Prompt = text; state.Requirements.Summary = text;
        compact.Sources[0].Text = text; compact.Sources[0].Version = PlanningGraphCompiler.Fingerprint(text);
        if (phase != "discovery")
        {
            compact.CurrentStep!.Kind = "value"; compact.CurrentStep.DiscoveryComplete = true;
            if (phase == "judge")
            {
                construction.CandidateFailureIdentityVersion = 1;
                construction.Candidate = PlanningConstruction.Apply(state, JsonSerializer.Deserialize(Draft(state)["extension"], PlanningJsonContext.Default.PlanningConstructionExtension)!);
            }
        }
        return (state, runtime);
    }

    private static LLMRequest OriginalRequest(PlanningSession state, string phase) => phase switch
    {
        "judge" => HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false).Request,
        "draft" => PlanningConstruction.DraftRequest(state, repair: false),
        _ => PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state), PlanningCompactConstruction.DiscoveryContext(state),
            PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose)
    };
    private static JsonObject OriginalContext(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse OriginalEvidence(LLMRequest request, string phase, string address) => Wire(request, phase == "judge"
        ? new JsonObject { ["action"] = new JsonObject { ["kind"] = "evidenceRequests", ["addresses"] = new JsonArray(address) }, ["uncertainty"] = "Need the exact original obligation." }
        : new JsonObject { ["baseHash"] = request.StructuredOutputSchema!["properties"]?["baseHash"]?["enum"]?[0]?.DeepClone(),
            ["evidenceRequests"] = new JsonArray(address) });

    private static (PlanningSession, TestRuntime) Ready(bool selected = true)
    {
        var state = new PlanningSession { Request = new() { TenantId = "test", Prompt = Objective,
            Options = new JsonObject { ["automatic_correction"] = true } } };
        PlanningSessionCreation.Capture(state);
        var compact = state.Construction!.Compact!;
        PlanningCompactConstruction.InitializeSources(state);
        compact.Sources[0].Indexed = true;
        compact.Sources.Add(new() { Id = "request:0:clause:0", Version = PlanningGraphCompiler.Fingerprint(Objective),
            Text = Objective, Kind = "requirement", InterfaceKind = "none", Indexed = true });
        compact.RequirementSources["req_0"] = ["request:0:clause:0"];
        state.IntentVersion = 3;
        state.Requirements = new() { Summary = Objective, Outcomes = [new("req_0", Objective) { Commitments = [] }], Inputs = [], Outputs = [] };
        state.Construction.Requirements = [new() { Id = "req_0" }];
        state.Plan = new(); state.Catalog = new() { AllowedStepTypes = ["mcp.call"] };
        state.Discovery.Sources = [new("source", "Original record-reading capabilities.")];
        if (selected) compact.CurrentStep = new() { Id = "selected", Kind = null, Scope = "/root", Objective = Objective,
            RequirementIds = ["req_0"], BaseHash = PlanningConstruction.Hash(state.Plan) };
        return (PlannerFixture.Clone(state), new TestRuntime());
    }

    private static JsonObject Select(PlanningSession state, string? kind) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["evidenceRequests"] = null, ["clarifications"] = null,
        ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = kind, ["objective"] = Objective,
            ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(), ["groupId"] = null,
            ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray("The requested record."),
            ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray(), ["prerequisites"] = null }
    };
    private static JsonObject Binding(string kind) => new()
    {
        ["binding"] = new JsonObject { ["kind"] = kind, ["operationIds"] = kind == "operation" ? new JsonArray("read") : new JsonArray(), ["groupId"] = null },
        ["discoveryRequests"] = null, ["evidenceRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null
    };
    private static JsonObject Discover(string? cursor) => new()
    {
        ["binding"] = null, ["evidenceRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null,
        ["discoveryRequests"] = new JsonArray(new JsonObject { ["sourceId"] = "source", ["cursor"] = cursor, ["query"] = null, ["operationIds"] = null })
    };
    private static JsonObject Draft(PlanningSession state) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["requirements"] = null, ["commitments"] = null,
        ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension { RequirementIds = ["req_0"], Tasks = [new()
            { Id = "value", Kind = "value", Objective = Objective, Outputs = [new("record", new() { Kind = "string", Text = "EXACT" })] }] }, PlanningJsonContext.Default.PlanningConstructionExtension),
        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    };
    private static PlanningCapability Contract() => new()
    {
        Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = "read", EffectKind = "read",
        Description = "Read the exact authorized record without changes.",
        InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["target"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("target"), ["additionalProperties"] = false },
        OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
    };
    private static void AddContract(PlanningSession state, PlanningCapability capability) => state.Discovery.Resolved.Add(capability);
    private static JsonObject LegacyBindingSchema() => JsonNode.Parse("""
        {"anyOf":[{"type":"object","properties":{"kind":{"type":"string","enum":["operation","value","transform","sequence","conditional","parallel","foreach","call","close","group","choice"]},"operationIds":{"type":"array","items":{"type":"string","maxLength":256},"minItems":0,"maxItems":2},"groupId":{"anyOf":[{"type":"string","maxLength":96},{"type":"null"}]}},"required":["kind","operationIds","groupId"],"additionalProperties":false},{"type":"null"}]}
        """)!.AsObject();
    private static string[] TaskKinds(JsonNode schema) => Nodes(schema).OfType<JsonObject>()
        .Where(node => node["properties"] is JsonObject properties && properties.ContainsKey("id") && properties.ContainsKey("objective") && properties.ContainsKey("kind"))
        .SelectMany(node => node["properties"]!["kind"]!["enum"]!.AsArray().Select(value => value!.ToString())).Distinct(StringComparer.Ordinal).ToArray();
    private static IEnumerable<JsonNode> Nodes(JsonNode node)
    {
        yield return node;
        var children = node is JsonObject obj ? obj.Select(pair => pair.Value) : node is JsonArray array ? array : [];
        foreach (var child in children.OfType<JsonNode>())
            foreach (var nested in Nodes(child)) yield return nested;
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private sealed class Pages(string mode = "normal", PlanningCapability? resolved = null) : ICapabilityCatalog
    {
        internal readonly List<string?> Cursors = [];
        internal readonly List<CapabilitySummary> Resolutions = [];
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        {
            Cursors.Add(cursor);
            if (mode == "oversized")
            {
                var capability = Contract();
                return Task.FromResult(new CapabilityPage(sourceId, cursor,
                    [new(capability.Id, sourceId, new string('x', 80_000), capability.Description, capability.StepType,
                        capability.EffectKind, capability.Version, Operation: TaskOperations.Describe(capability))], null, Query: query));
            }
            var offset = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(new CapabilityPage(sourceId, cursor, [], mode == "cycle" ? "1" : offset == 5 ? null : (offset + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                mode == "unavailable" ? "Unavailable original provider metadata." : null, query));
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
        {
            if (resolved is null) throw new InvalidOperationException("No operation is chosen by metadata pagination.");
            Assert.Equal(resolved.Id, summary.Id); Assert.Equal(resolved.Version, summary.Version);
            Resolutions.Add(summary); return Task.FromResult(resolved);
        }
    }

    private sealed class InterruptedRuntime(TestRuntime inner, int completed) : IPlanningRuntime
    {
        internal PlanningSession? Saved;
        public ICapabilityCatalog Capabilities => inner.Capabilities;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => inner.DiscoverAsync(request, ct);
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct) => inner.CallAsync(request, purpose, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => inner.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => inner.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct)
        {
            if (Saved is null && session.Construction?.Compact?.CurrentStep?.PendingDiscovery is not null && session.Discovery.Pages.Count == completed)
            {
                Saved = PlannerFixture.Clone(session);
                throw new IOException("Injected interruption after the exact metadata action checkpoint.");
            }
            return inner.CheckpointAsync(session, ct);
        }
    }
}
