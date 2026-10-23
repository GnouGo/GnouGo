using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactDeferredBindingTests
{
    private const string Objective = "Produce the requested exact record without external changes.";

    [Fact]
    public async Task LocalSelectionDraftsDirectlyWithoutDiscoveryOrAnotherModelCall()
    {
        var (state, runtime) = Ready(selected: false);
        state.Request.MaxModelCalls = 3; // selection, draft, mandatory local review; no discovery reserve for local work.
        runtime.Respond = (request, _) => Wire(request, Select(state, "value"));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete);
        Assert.Equal("value", state.Construction.Compact.CurrentStep.Kind);
        Assert.Single(runtime.Calls);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            Assert.DoesNotContain("operation", TaskKinds(request.StructuredOutputSchema!));
            return Wire(request, Draft(state));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.NotNull(state.Construction!.Candidate);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("operation")]
    public async Task UnsetImplementationBindsOnlyAfterDiscoveryWithoutAReselectionCharge(string kind)
    {
        var (state, runtime) = Ready(selected: false);
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
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(new[] { kind }, TaskKinds(PlanningCompactConstruction.DraftSchema(state)));
        Assert.Equal(kind == "operation", state.Construction.Compact.PendingCommitments["req_0"].Count == 1);
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
        Assert.Equal("req_1", Assert.Single(PlanningCompactConstruction.SelectionContext(state)["requirements"]!.AsArray())!["id"]!.ToString());
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredContractClosureIsAdmittedBeforeBindingAndOptionalGiantBranchesStayOmitted(bool required)
    {
        var (state, runtime) = Ready();
        var contract = Contract();
        contract.InputSchema["properties"]!["large"] = new JsonObject { ["type"] = "string", ["description"] = new string('x', 80_000) };
        if (required) contract.InputSchema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("large"));
        var exact = JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability);
        AddContract(state, contract);
        var baseline = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, _) => Wire(request, Binding("operation"));
        state = await Advance(state, runtime);
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
            var view = Assert.Single(PlanningCompactConstruction.ContractViews(state))!;
            Assert.NotNull(view["inputSchema"]!["properties"]!["target"]);
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
    private sealed class Pages(string mode = "normal") : ICapabilityCatalog
    {
        internal readonly List<string?> Cursors = [];
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
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException("No operation is chosen by metadata pagination.");
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
