using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactDiscoveryProtocolTests
{
    [Fact]
    public async Task ProviderSchemaBindsSourceCursorAndHostOwnedQueryWithoutAcceptingEvidenceAddresses()
    {
        var (state, _) = await Ready();
        state.Discovery.Sources.Add(new("beta", "A separate provider."));
        state.Discovery.Pages.Add(new("alpha", null, [], "alpha-next", Query: Objective));
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("alpha", "alpha-next"), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("beta", null), schema));
        foreach (var invalid in new[] { Discover("beta", "alpha-next"), Discover("alpha", "page:operations:2"),
            Discover("alpha", "alpha-next", Objective), Discover("alpha", null), Discover("unknown", null) })
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, schema));
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.Equal("No remote publication.", context["globalConstraints"]![0]!["text"]!.ToString());
        Assert.Equal("Never expand permissions.", context["instructions"]!.ToString());
        Assert.Contains("addresses go in evidenceRequests, never cursor", PlanningCompactConstruction.DiscoveryInstructions, StringComparison.Ordinal);
        Assert.Contains("declared arithmetic", PlanningCompactConstruction.SelectInstructions, StringComparison.Ordinal);
        Assert.Contains("operation calls an inspected capability", PlanningCompactConstruction.SelectInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderPagesRemainBoundedAndRejectConsumedOrChangedVersions()
    {
        var (state, _) = await Ready();
        state.Discovery.Sources = Enumerable.Range(0, 500).Select(i => new CapabilitySource("source_" + i, "Exact source restrictions " + new string('x', 10000))).ToList();
        var original = state.Discovery.Sources[0].Description;
        var page = PlanningCompactConstruction.ProviderRequestPage(state, 0);
        Assert.Equal(2, page["items"]!.AsArray().Count);
        Assert.Equal(500, page["total"]!.GetValue<int>());
        Assert.False(page["items"]![0]!["descriptionComplete"]!.GetValue<bool>());
        var address = page["next"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.Null(context["providerRequests"]);
        Assert.Equal("source_2", context["evidence"]![address]!["items"]![0]!["sourceId"]!.ToString());
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("source_2", null), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("source_0", null), schema));
        var request = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions, context, schema, PlanningCompactConstruction.DiscoveryPurpose));
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, 3000).EstimatedInputTokens, 1, 3000);
        Assert.Equal(original, state.Discovery.Sources[0].Description);
        state.Discovery.Pages.Add(new("source_2", null, [], "next", Query: Objective));
        state.Construction!.Evidence = null; state.Construction.EvidenceRequests.Clear();
        var consumed = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address]));
        Assert.Contains(consumed.Diagnostics, d => d.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
        var next = PlanningCompactConstruction.ProviderRequestAddress(state, 2);
        state.Discovery.Sources[2] = state.Discovery.Sources[2] with { Description = "Revised source restrictions." };
        var changed = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [next]));
        Assert.Contains(changed.Diagnostics, d => d.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
    }

    [Fact]
    public async Task ReusedProviderCursorIsResolvedOnlyWithinTheSelectedSubstepQuery()
    {
        var (state, runtime) = await Ready();
        state.Discovery.Pages.Add(new("alpha", null, [], "next", Query: "A different accepted business objective."));
        state.Discovery.Pages.Add(new("alpha", null, [], "next", Query: Objective));
        runtime.Respond = (request, _) => Wire(request, Discover("alpha", "next"));
        var resumed = await Advance(state, runtime);
        Assert.Empty(resumed.Diagnostics);
        var continuation = Assert.Single(resumed.Discovery.Pages, page => page.Cursor == "next");
        Assert.Equal(Objective, continuation.Query);
        Assert.Equal(3, resumed.Discovery.Pages.Count);
        Assert.Equal(0, resumed.ReplanAttempts);
    }

    [Fact]
    public async Task ExhaustedProviderRemainsExplicitAndCannotBeFetchedAgain()
    {
        var (state, _) = await Ready();
        state.Discovery.Pages.Add(new("alpha", null, [], null, UnavailableReason: "The configured provider is unavailable.", Query: Objective));
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.False(context["providerRequests"]!["items"]![0]!["available"]!.GetValue<bool>());
        Assert.Equal("The configured provider is unavailable.", context["providerRequests"]!["items"]![0]!["unavailable"]!.ToString());
        Assert.Equal("null", PlanningCompactConstruction.DiscoverySchema(state)["properties"]!["discoveryRequests"]!["type"]!.ToString());
        Assert.True(PlanningCompactConstruction.CanReselectDiscovery(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryReselectionConsumesOneCorrectionAndKeepsAcceptedWorkAndAuthority(bool previouslyDiagnosed)
    {
        var (state, runtime) = await Ready(inspected: true);
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var evidenceAddress = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        PlanningConstruction.LoadEvidence(state, [evidenceAddress]);
        if (previouslyDiagnosed) state.Diagnostics = [new("DISCOVERY_SELECTION_INVALID", "/operationIds", "The tentative operation is unavailable.")];
        runtime.Respond = (request, _) => Wire(request, Reselect());
        state = await Advance(state, runtime);
        Assert.Equal(1, state.ReplanAttempts); Assert.Equal(1, state.Construction!.DraftCorrections);
        Assert.Null(state.Construction.Compact!.CurrentStep); Assert.Null(state.Construction.Candidate);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        var rejected = Assert.Single(state.Construction.Compact.RejectedSteps);
        Assert.NotNull(rejected["evidence"]![evidenceAddress]);
        Assert.Equal("/root", state.Construction.Compact.ReplacementScope);
        Assert.Equal(["req_0"], state.Construction.Compact.ReplacementRequirements);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(Reason, Context(request)["reselection"]!["reason"]!.ToString());
            return Wire(request, Select(request));
        };
        state = await Advance(state, runtime);
        Assert.Equal(1, state.ReplanAttempts); Assert.Equal(1, state.Construction!.DraftCorrections);
        Assert.Equal("value", state.Construction.Compact!.CurrentStep!.Kind);
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingDiscoveryReselectionResumesExactRequestAndChargesOnce(bool previouslyDiagnosed)
    {
        var (state, runtime) = await Ready(inspected: true);
        if (previouslyDiagnosed) state.Diagnostics = [new("DISCOVERY_SELECTION_INVALID", "/operationIds", "Inspect a suitable operation.")];
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall?.Purpose == PlanningCompactConstruction.DiscoveryPurpose));
        saved.Status = PlanningStatus.Generating;
        var request = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls;
        var recovery = new TestRuntime { Respond = (issued, _) => Wire(issued, Reselect()) };
        var resumed = await Advance(saved, recovery);
        Assert.Equal(request, JsonSerializer.Serialize(Assert.Single(recovery.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, resumed.ModelCalls);
        Assert.Equal(1, resumed.ReplanAttempts); Assert.Equal(1, resumed.Construction!.DraftCorrections);
        Assert.Single(resumed.Construction.Compact!.RejectedSteps);
        var durable = PlannerFixture.Clone(resumed);
        recovery.Respond = (issued, _) => Wire(issued, Select(issued));
        durable = await Advance(durable, recovery);
        Assert.Equal(1, durable.ReplanAttempts); Assert.Equal(1, durable.Construction!.DraftCorrections);
        Assert.Single(durable.Construction.Compact!.RejectedSteps);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("draft")]
    [InlineData("policy")]
    [InlineData("revision")]
    public async Task ReselectionCannotExpandCapturedAllowancesOrExplicitRevisionAuthority(string restriction)
    {
        var (state, _) = await Ready(inspected: true);
        switch (restriction)
        {
            case "total": state.ReplanAttempts = state.Request.MaxReplanAttempts; break;
            case "draft": state.Construction!.DraftCorrections = state.Construction.Limits.MaxCorrectionsPerDraft; break;
            case "policy": state.Request.Options["automatic_correction"] = false; break;
            case "revision": state.EditablePaths = ["/root/outputs/message"]; break;
        }
        Assert.False(PlanningCompactConstruction.CanReselectDiscovery(state));
        Assert.Equal("null", PlanningCompactConstruction.DiscoverySchema(state)["properties"]!["reselectStep"]!["type"]!.ToString());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Reselect(), PlanningCompactConstruction.DiscoverySchema(state)));
    }

    [Fact]
    public async Task PreviouslyIssuedDiscoverySchemaKeepsItsExactQueryAndReceiptContract()
    {
        var (state, runtime) = await Ready();
        var schema = PlanningSchemas.Object(("operationIds", PlanningSchemas.Type("null")),
            ("discoveryRequests", PlanningSchemas.Array(PlanningSchemas.Object(("sourceId", PlanningSchemas.String()),
                ("cursor", PlanningSchemas.Nullable(PlanningSchemas.String())), ("query", PlanningSchemas.Nullable(PlanningSchemas.String())),
                ("operationIds", PlanningSchemas.Type("null"))), 1, 2)),
            ("evidenceRequests", PlanningSchemas.Type("null")), ("clarifications", PlanningSchemas.Type("null")));
        schema["title"] = PlanningCompactConstruction.DiscoveryPurpose;
        var issued = Complete(state, PlanningCompactConstruction.Request("Retained exact discovery instructions.", new(), schema, PlanningCompactConstruction.DiscoveryPurpose));
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued };
        state.ModelCalls++;
        var before = state.ModelCalls;
        runtime.Respond = (request, _) => { var response = Discover("alpha", null, Objective); response.Remove("reselectStep"); return new() { Json = response }; };
        var resumed = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(before, resumed.ModelCalls);
        Assert.Equal(JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest), JsonSerializer.Serialize(runtime.Calls[^1], PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(Objective, Assert.Single(resumed.Discovery.Pages).Query);
        Assert.Equal(0, resumed.ReplanAttempts);
    }

    private const string Objective = "Prepare the exact greeting from the supplied data.";
    private const string Reason = "The supplied literal can be assembled with deterministic bindings; no suitable operation is needed.";
    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready(bool inspected = false)
    {
        var runtime = new TestRuntime { Capabilities = new EmptyCatalog(), Respond = (request, _) =>
        {
            var source = Context(request)["sources"]![0]!;
            return Wire(request, new() { ["sourceId"] = source["id"]!.ToString(),
                ["clauses"] = new JsonArray(new JsonObject { ["text"] = "Return exactly Hello. ", ["kind"] = "requirement" },
                    new JsonObject { ["text"] = "No remote publication.", ["kind"] = "global" }),
                ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray() });
        } };
        var state = await Advance(new() { Request = new() { TenantId = "compact-discovery", Prompt = "Return exactly Hello. No remote publication.",
            Options = new JsonObject { ["automatic_correction"] = true } } }, runtime);
        state.Request.Policy.Instructions = "Never expand permissions.";
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct); state.Catalog.Capabilities.Clear();
        state.Discovery.Sources = [new("alpha", "Declared metadata for business operations.")];
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "accepted", Kind = "value", Objective = "Preserve this accepted fragment.",
            Outputs = [new("retained", new() { Kind = "string", Text = "Unchanged" })] }] } };
        state.Construction!.Compact!.CurrentStep = new() { Id = "tentative", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root", Kind = "operation",
            Objective = Objective, RequirementIds = ["req_0"] };
        if (inspected) state.Discovery.Pages.Add(new("alpha", null, [], null, Query: Objective));
        return (state, runtime);
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static JsonObject Discover(string source, string? cursor, string? query = null) => new()
    {
        ["operationIds"] = null, ["evidenceRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null,
        ["discoveryRequests"] = new JsonArray(new JsonObject { ["sourceId"] = source, ["cursor"] = cursor, ["query"] = query, ["operationIds"] = null })
    };
    private static JsonObject Reselect() => new() { ["operationIds"] = null, ["discoveryRequests"] = null, ["evidenceRequests"] = null, ["clarifications"] = null, ["reselectStep"] = Reason };
    private static JsonObject Select(LLMRequest request) => new()
    {
        ["baseHash"] = Context(request)["baseHash"]!.ToString(), ["evidenceRequests"] = null, ["clarifications"] = null,
        ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = "value", ["objective"] = "Assemble the exact literal greeting.",
            ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(), ["dependencyTaskIds"] = new JsonArray(),
            ["expectedOutputs"] = new JsonArray("The exact greeting."), ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray() }
    };
    private static LLMRequest Complete(PlanningSession state, LLMRequest request) => PlanningGenerationPolicy.Apply(new LLMRequest
    {
        Prompt = request.Prompt, StructuredOutputSchema = request.StructuredOutputSchema, StructuredOutputStrict = true,
        Provider = "provider", Model = "pinned-model", UseBackgroundMode = true, MaxTokens = 8192,
        ClientRequestId = new string('s', 64) + ":123:" + new string('h', 64)
    }, state.Request.Generation);
    private sealed class EmptyCatalog : ICapabilityCatalog
    {
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([new("alpha", "Declared metadata for business operations.")]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => Task.FromResult(new CapabilityPage(sourceId, cursor, [], null, Query: query));
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException("No tool may be invented by this metadata-only fixture.");
    }
}
