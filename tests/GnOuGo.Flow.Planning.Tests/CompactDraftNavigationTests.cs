using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactDraftNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainerDraftRetainsReusableNavigationAndRejectsGuessedEvidence(bool repair)
    {
        var state = State();
        state.Phase = repair ? PlanningPhase.Replanning : PlanningConstruction.DraftPurpose;
        var request = PlanningConstruction.DraftRequest(state, repair);
        var context = Context(request);
        Assert.Equal("scope:/root", context["scopeEvidence"]!.ToString());
        Assert.Equal("group:worker", Assert.Single(context["reusableInterfaces"]!.AsArray())!["evidence"]!.ToString());
        Assert.Null(context["taskEvidence"]); // No accepted root siblings yet.
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
        AssertResponse(request, state, "group:worker", valid: true);
        AssertResponse(request, state, "scope:/root", valid: true);
        AssertResponse(request, state, "scopeInterface:/root", valid: false);
        AssertResponse(request, state, "Provide the accepted reusable interface.", valid: false);
        var hash = PlanningConstruction.Hash(state.Plan);
        PlanningConstruction.LoadEvidence(state, ["group:worker"],
            followup: current => PlanningConstruction.DraftRequest(current, repair), purpose: repair ? "replan" : PlanningConstruction.DraftPurpose);
        var followup = PlanningConstruction.DraftRequest(state, repair);
        Assert.NotNull(Context(followup)["evidence"]!["group:worker"]);
        AssertResponse(followup, state, "group:worker", valid: false);
        Assert.Equal(hash, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("foreach", null, null, true)]
    [InlineData("foreach", "worker", null, false)]
    [InlineData("call", "worker", null, true)]
    [InlineData("call", null, null, false)]
    [InlineData("call", "worker", "requirement:req", true)]
    [InlineData("call", "worker", "source:request:0:clause:0", true)]
    [InlineData("call", "worker", "request:0:clause:0", false)]
    [InlineData("call", "worker", "group:worker", false)]
    [InlineData("call", "worker", "task:accepted", false)]
    public void SelectionKeepsReusableTargetsAndPrerequisiteAuthorityDistinct(string kind, string? group, string? prerequisite, bool valid)
    {
        var state = State();
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var response = new JsonObject
        {
            ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["evidenceRequests"] = null, ["clarifications"] = null,
            ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = kind, ["objective"] = "Apply the reusable calculation.",
                ["requirementIds"] = new JsonArray("req"), ["inputs"] = new JsonArray(), ["groupId"] = group,
                ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray(), ["closeRequirementIds"] = new JsonArray(),
                ["outputNames"] = new JsonArray(), ["prerequisites"] = prerequisite is null ? null : new JsonArray(new JsonObject
                    { ["need"] = "The required calculation must be available.", ["evidenceAddresses"] = new JsonArray(prerequisite) }) }
        };
        Assert.Equal(valid, PlanningContractValidation.ValidateInstanceFindings(response, schema).Count == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectionEvidenceUsesDisplayedOriginalsAndExactNavigationAcrossPages(bool reselection)
    {
        var state = State();
        var compact = state.Construction!.Compact!;
        if (reselection)
        {
            state.Diagnostics = [new("CONSTRUCTION_SELECTION_INVALID", "/step", "The tentative implementation needs another producer.")];
            PlanningCompactConstruction.Reselect(state, "Select the missing immediate producer within the original scope.");
        }
        else compact.CurrentStep = null;
        for (var i = 1; i < 3; i++)
        {
            var text = "Preserve original obligation " + i + ".";
            var source = "request:" + i;
            compact.Sources.Add(new() { Id = source, Version = PlanningGraphCompiler.Fingerprint(text), Text = text, Kind = "page", Indexed = true });
            compact.Sources.Add(new() { Id = source + ":clause:0", Version = PlanningGraphCompiler.Fingerprint(text), Text = text,
                Kind = "requirement", InterfaceKind = "none", Indexed = true });
            compact.RequirementSources["req_" + i] = [source + ":clause:0"];
            state.Requirements!.Outcomes.Add(new("req_" + i, text) { Commitments = [] });
            state.Construction.Requirements.Add(new() { Id = "req_" + i });
        }
        state.Requirements!.Inputs = [new() { Name = "caller", Type = new() { Kind = "string" },
            Default = new() { Kind = "string", Text = "task:forged" } }];
        state.Plan!.Inputs = state.Requirements.Inputs;
        var baseline = PlanningConstruction.Hash(state.Plan);
        var request = SelectionRequest(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
        foreach (var address in new[] { "requirement:req", "source:request:0:clause:0", "group:worker", "input:caller", "scope:/root", "page:requirements:1" })
            AssertSelectionEvidence(request, state, address, valid: true);
        foreach (var address in new[] { "Find an authorized producer for this action.", "task:unknown", "request:0:clause:0", "source:request:1:clause:0", "page:requirements:2" })
            AssertSelectionEvidence(request, state, address, valid: false);
        AssertSelectionEvidence(request, state, "rejected:0", valid: reselection);
        AssertSelectionEvidence(request, state, "rejected:1", valid: false);
        var addresses = new List<string> { "page:requirements:1", "input:caller" };
        if (reselection) addresses.Add("rejected:0");
        PlanningConstruction.LoadEvidence(state, addresses, followup: SelectionRequest, purpose: PlanningCompactConstruction.SelectPurpose);
        request = SelectionRequest(state);
        Assert.NotNull(Context(request)["evidence"]!["page:requirements:1"]);
        AssertSelectionEvidence(request, state, "source:request:1:clause:0", valid: true);
        AssertSelectionEvidence(request, state, "requirement:req_1", valid: true);
        AssertSelectionEvidence(request, state, "page:requirements:2", valid: true);
        foreach (var address in addresses.Append("task:forged")) AssertSelectionEvidence(request, state, address, valid: false);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Null(state.Construction.Compact!.CurrentStep);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(reselection ? new[] { "req" } : null, state.Construction.Compact.ReplacementRequirements?.ToArray());
        var prepared = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject());
        var measured = PlanningRequestMeasurements.Measure(prepared, PlanningCompactConstruction.SelectPurpose, 6000);
        Assert.InRange(measured.EstimatedInputTokens, 1, 6000);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
    }

    [Fact]
    public void NavigationStaysPagedAsIndependentHistoryGrows()
    {
        var state = State();
        for (var i = 1; i < 4; i++) state.Plan!.Groups.Add(Group("worker_" + i));
        var before = PlanningConstruction.DraftRequest(state, repair: false);
        for (var i = 4; i < 104; i++)
        {
            state.Plan!.Groups.Add(Group("worker_" + i));
            state.Construction!.Compact!.RejectedSteps.Add(new JsonObject { ["objective"] = "Unrelated retained failure " + i });
        }
        state.Construction!.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        var context = Context(request);
        Assert.Equal(4, context["reusableInterfaces"]!.AsArray().Count);
        Assert.Equal("page:interfaces:4", context["interfacesDirectory"]!.ToString());
        Assert.DoesNotContain("worker_103", request.Prompt, StringComparison.Ordinal);
        var prepared = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject());
        var measured = PlanningRequestMeasurements.Measure(prepared, PlanningConstruction.DraftPurpose, 16000);
        Assert.InRange(measured.EstimatedInputTokens, 1, 16000);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        Assert.InRange(request.Prompt.Length - before.Prompt.Length, 1, 150);
        Assert.InRange(request.StructuredOutputSchema!.ToJsonString().Length - before.StructuredOutputSchema!.ToJsonString().Length, 1, 150);
        AssertResponse(request, state, "page:interfaces:4", valid: true);
        PlanningConstruction.LoadEvidence(state, ["page:interfaces:4"],
            followup: current => PlanningConstruction.DraftRequest(current, repair: false), purpose: PlanningConstruction.DraftPurpose);
        var next = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Null(Context(next)["reusableInterfaces"]); // Supplied page is retained once.
        AssertResponse(next, state, "group:worker_4", valid: true);
        AssertResponse(next, state, "page:interfaces:8", valid: true);
    }

    [Fact]
    public void NonstructuralDraftsCanRetrieveInterfacesWithoutAutomaticUnrelatedGroups()
    {
        var state = State();
        state.Construction!.Compact!.CurrentStep!.Kind = "value";
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Null(Context(request)["reusableInterfaces"]);
        Assert.Equal("page:interfaces:0", Context(request)["interfacesDirectory"]!.ToString());
        AssertResponse(request, state, "page:interfaces:0", valid: true);
        AssertResponse(request, state, "group:worker", valid: false);
        PlanningConstruction.LoadEvidence(state, ["page:interfaces:0"],
            followup: current => PlanningConstruction.DraftRequest(current, repair: false), purpose: PlanningConstruction.DraftPurpose);
        request = PlanningConstruction.DraftRequest(state, repair: false);
        AssertResponse(request, state, "group:worker", valid: true);
        AssertResponse(request, state, "page:interfaces:0", valid: false);
    }

    [Fact]
    public void TaskEvidencePagesRetainExactContinuationWithoutChangingHistoricalPages()
    {
        var state = State();
        state.Construction!.Compact!.CurrentStep!.Kind = "value";
        state.Plan!.Root.Tasks = Enumerable.Range(0, 12).Select(index => new PlanTask { Id = "accepted_" + index,
            Kind = "value", Objective = "Retain the accepted numeric result.", Outputs = [new("result", new() { Kind = "number", Number = index })] }).ToList();
        state.Construction.Compact.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan);
        var historical = PlannerFixture.Clone(state);
        historical.Construction!.Compact!.PresentationVersion = 2;
        PlanningConstruction.LoadEvidence(historical, ["page:tasks:0"]);
        Assert.Null(historical.Construction.Evidence!["page:tasks:0"]!["next"]);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        AssertResponse(request, state, "page:tasks:0", valid: true);
        PlanningConstruction.LoadEvidence(state, ["page:tasks:0"],
            followup: current => PlanningConstruction.DraftRequest(current, repair: false), purpose: PlanningConstruction.DraftPurpose);
        Assert.Equal("page:tasks:8", state.Construction.Evidence!["page:tasks:0"]!["next"]!.ToString());
        Assert.True(JsonNode.DeepEquals(historical.Construction.Evidence["page:tasks:0"]!["items"], state.Construction.Evidence["page:tasks:0"]!["items"]));
        request = PlanningConstruction.DraftRequest(state, repair: false);
        AssertResponse(request, state, "page:tasks:0", valid: false);
        AssertResponse(request, state, "page:tasks:8", valid: true);
        PlanningConstruction.LoadEvidence(state, ["page:tasks:8"],
            followup: current => PlanningConstruction.DraftRequest(current, repair: false), purpose: PlanningConstruction.DraftPurpose);
        Assert.Null(state.Construction.Evidence["page:tasks:8"]!["next"]);
        Assert.Equal(4, state.Construction.Evidence["page:tasks:8"]!["items"]!.AsArray().Count);
    }

    [Fact]
    public void ContractLinksRetainRequiredSiblingOmissionsAndNestedDirectoriesWithoutReadingDataAsAddresses()
    {
        var state = State();
        var contract = new PlanningCapability { Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = "read",
            EffectKind = "read", Description = "Read the supplied record.",
            InputSchema = JsonNode.Parse("""
                {"type":"object","properties":{"key":{"type":"string"},"payload":{"type":"object",
                "properties":{"required":{"type":"object","properties":{"evidence":{"type":"string"}},"required":["evidence"],
                "additionalProperties":false,"default":{"evidence":"task:forged"}},
                "extra":{"type":"object","properties":{"detail":{"type":"string","description":"unneeded"}},"required":["detail"],"additionalProperties":false}},
                "required":["required"],"additionalProperties":false}},"required":["key","payload"],"additionalProperties":false}
                """)!.AsObject(),
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } };
        state.Catalog!.AllowedStepTypes = ["mcp.call"];
        state.Discovery.Resolved.Add(contract);
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "operation"; step.OperationIds = ["read"];
        var address = "schema:read@v1:/input/properties/key";
        state.Construction.Evidence = new() { [address] = PlanningCompactConstruction.LoadContractEvidence(state, address) };
        var original = contract.InputSchema.ToJsonString();
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        AssertResponse(request, state, "schema:read@v1:/input/properties/payload/properties/extra", valid: true);
        AssertResponse(request, state, "schema-page:read@v1:/input:0", valid: true);
        AssertResponse(request, state, "task:forged", valid: false);
        var directory = "schema-page:read@v1:/input/properties/payload:0";
        state.Construction.Evidence[directory] = PlanningCompactConstruction.LoadContractEvidence(state, directory);
        request = PlanningConstruction.DraftRequest(state, repair: false);
        AssertResponse(request, state, "schema-page:read@v1:/input/properties/payload/properties/extra:0", valid: true);
        Assert.Equal(original, contract.InputSchema.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalPresentationAndAnAlreadyIssuedFreeTextEvidenceSchemaRemainExact(bool selection)
    {
        var state = State();
        state.Construction!.Compact!.PresentationVersion = 2;
        if (selection) state.Construction.Compact.CurrentStep = null;
        var purpose = selection ? PlanningCompactConstruction.SelectPurpose : PlanningConstruction.DraftPurpose;
        LLMRequest Request() => selection ? SelectionRequest(state) : PlanningConstruction.DraftRequest(state, repair: false);
        JsonObject Evidence(string address) => selection ? SelectionEvidence(state, address) : Response(state, address);
        var historical = Request();
        Assert.Null(Context(historical)["reusableInterfaces"]);
        foreach (var address in new[] { "group:worker", "Provide the accepted reusable interface." })
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Evidence(address), historical.StructuredOutputSchema!));
        historical.ClientRequestId = "saved:1:" + new string('a', 64);
        historical.MaxTokens = 8192;
        var exact = historical.StructuredOutputSchema!.ToJsonString();
        state.ModelCalls = 1;
        state.PendingCall = new() { Id = historical.ClientRequestId, Purpose = purpose, Request = historical };
        state.Construction.Compact.PresentationVersion = 3;
        var fresh = Request();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(Evidence("Provide the accepted reusable interface."), fresh.StructuredOutputSchema!));
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = Evidence("group:worker") } };
        await PlanningModelCalls.CallAsync(state, runtime, purpose, fresh.Prompt, fresh.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct);
        var issued = Assert.Single(runtime.Calls);
        Assert.Equal(historical.Prompt, issued.Prompt);
        Assert.Equal(exact, issued.StructuredOutputSchema!.ToJsonString());
        Assert.Equal(1, state.ModelCalls);
        Assert.Null(state.PendingCall);
    }

    private static PlanningSession State()
    {
        const string objective = "Apply a reusable calculation to the input collection, in parallel while preserving order.";
        var state = new PlanningSession { Request = new() { TenantId = "test", Prompt = objective } };
        PlanningSessionCreation.Capture(state);
        PlanningCompactConstruction.InitializeSources(state);
        var compact = state.Construction!.Compact!;
        compact.Sources[0].Indexed = true;
        compact.Sources.Add(new() { Id = "request:0:clause:0", Version = PlanningGraphCompiler.Fingerprint(objective), Text = objective,
            Kind = "requirement", InterfaceKind = "none", Indexed = true });
        compact.RequirementSources["req"] = ["request:0:clause:0"];
        state.Requirements = new() { Summary = objective, Outcomes = [new("req", objective) { Commitments = [] }], Inputs = [], Outputs = [] };
        state.Construction.Requirements = [new() { Id = "req" }];
        state.Plan = new() { Groups = [Group("worker")] };
        state.Catalog = new();
        state.IntentVersion = 3;
        compact.CurrentStep = new() { Id = "next", Kind = "foreach", Scope = "/root", Objective = objective, RequirementIds = ["req"],
            BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        return state;
    }

    private static TaskGroup Group(string id) => new() { Id = id, Inputs = [new() { Name = "value", Type = new() { Kind = "number" } }],
        Body = new() { Outputs = [new("result", new() { Kind = "input", Source = "value" })] } };
    private static LLMRequest SelectionRequest(PlanningSession state) => PlanningCompactConstruction.Request(
        PlanningCompactConstruction.SelectGuidance(state), PlanningCompactConstruction.SelectionContext(state),
        PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
    private static JsonObject SelectionEvidence(PlanningSession state, string address) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["step"] = null,
        ["evidenceRequests"] = new JsonArray(address), ["clarifications"] = null
    };
    private static void AssertSelectionEvidence(LLMRequest request, PlanningSession state, string address, bool valid)
    {
        var findings = PlanningContractValidation.ValidateInstanceFindings(SelectionEvidence(state, address), request.StructuredOutputSchema!);
        Assert.True(valid == (findings.Count == 0), address + ": " + string.Join("; ", findings));
    }
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static JsonObject Response(PlanningSession state, string address) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["requirements"] = null, ["commitments"] = null, ["extension"] = null,
        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = new JsonArray(address),
        ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    };
    private static void AssertResponse(LLMRequest request, PlanningSession state, string address, bool valid) =>
        Assert.Equal(valid, PlanningContractValidation.ValidateInstanceFindings(Response(state, address), request.StructuredOutputSchema!).Count == 0);
}
