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
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownKnownSelectionAdmitsTheExactContractAndDraftsWithoutDiscovery(bool catalogContract)
    {
        var (state, runtime) = ReadyKnown();
        var capability = Contract();
        if (catalogContract) state.Catalog!.Capabilities.Add(capability); else AddContract(state, capability);
        var pages = new Pages(); runtime.Capabilities = pages;
        var baseline = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            var descriptor = Assert.Single(OriginalContext(request)["knownOperations"]!["items"]!.AsArray())!;
            Assert.Equal("read", descriptor["id"]!.ToString());
            Assert.Equal(capability.Description, descriptor["description"]!.ToString());
            Assert.NotNull(descriptor["hash"]);
            return new LLMResponse { Json = SelectKnown(state, "operation", "read") };
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var step = state.Construction!.Compact!.CurrentStep!;
        Assert.True(step.DiscoveryComplete); Assert.Equal(new[] { "read" }, step.OperationIds);
        Assert.Equal("read", Assert.Single(state.Construction!.Compact!.PendingCommitments["req_0"]).Operation);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan)); Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            var view = Assert.Single(OriginalContext(request)["operations"]!.AsArray())!;
            Assert.Contains("target", view["inputSchema"]!["required"]!.AsArray().Select(value => value!.ToString()));
            var response = Draft(state);
            var extension = new PlanningConstructionExtension { RequirementIds = ["req_0"], Tasks = [new()
                { Id = "read_exact", Kind = "operation", Operation = "read", Objective = Objective,
                    Inputs = [new("target", new() { Kind = "string", Text = "EXACT" })] }] };
            response["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension);
            return Wire(request, response);
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.NotNull(state.Construction!.Candidate);
        Assert.Equal(2, state.ModelCalls); Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(pages.Cursors); Assert.Empty(pages.Resolutions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("value")]
    public async Task KnownUnknownAndLocalSelectionsKeepTheirExistingPaths(string? kind)
    {
        var (state, runtime) = ReadyKnown(); AddContract(state, Contract());
        var pages = new Pages(); runtime.Capabilities = pages;
        runtime.Respond = (request, _) => Wire(request, SelectKnown(state, kind));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(kind, state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.Equal(kind is not null, state.Construction!.Compact!.CurrentStep.DiscoveryComplete);
        Assert.Equal(1, state.ModelCalls); Assert.Empty(pages.Cursors); Assert.Empty(pages.Resolutions);
        if (kind is null)
        {
            runtime.Respond = (request, purpose) =>
            { Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose); return Wire(request, Binding("operation")); };
            state = await Advance(state, runtime);
            Assert.Empty(state.Diagnostics); Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete);
            Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        }
    }

    [Fact]
    public void KnownKnownOperationsHaveBoundedExactPagesAndRejectStalePages()
    {
        var (state, _) = ReadyKnown();
        for (var index = 0; index < 9; index++)
        { var contract = Contract(); contract.Id = contract.Method = "read_" + index; AddContract(state, contract); }
        var first = PlanningCompactConstruction.SelectionContext(state)["knownOperations"]!;
        Assert.Equal(4, first["items"]!.AsArray().Count);
        Assert.Equal(new[] { "read_0", "read_1", "read_2", "read_3" }, PlanningCompactConstruction.SelectionOperationIds(state));
        var next = first["next"]!.ToString();
        Assert.Contains(next, Nodes(PlanningCompactConstruction.SelectionSchema(state)).OfType<JsonValue>().Select(value => value.ToString()));
        PlanningConstruction.LoadEvidence(state, [next]);
        var retained = state.Construction!.Evidence![next]!;
        Assert.Equal(4, retained["items"]!.AsArray().Count);
        Assert.Equal(8, PlanningCompactConstruction.SelectionOperationIds(state).Length);
        Assert.DoesNotContain("read_8", PlanningCompactConstruction.SelectionOperationIds(state));
        var stale = retained["next"]!.ToString();
        state.Discovery.Resolved[8].Description += " Changed exact original contract.";
        var error = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [stale]));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code is "CONSTRUCTION_EVIDENCE_UNKNOWN" or "CONSTRUCTION_EVIDENCE_INVALID");
        Assert.Equal(4, PlanningCompactConstruction.SelectionOperationIds(state).Length);
        Assert.Equal(0, state.ModelCalls);
    }

    [Fact]
    public async Task KnownSelectsAnExactCachedOperationFromItsRetrievedPage()
    {
        var (state, runtime) = ReadyKnown();
        for (var index = 0; index < 9; index++)
        { var contract = Contract(); contract.Id = contract.Method = "read_" + index; AddContract(state, contract); }
        var next = PlanningCompactConstruction.SelectionContext(state)["knownOperations"]!["next"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [next]);
        var pages = new Pages(); runtime.Capabilities = pages;
        runtime.Respond = (_, _) => new() { Json = SelectKnown(state, "operation", "read_5") };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete);
        Assert.Equal(new[] { "read_5" }, state.Construction!.Compact!.CurrentStep.OperationIds);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(state.Construction!.Checkpoints); Assert.Empty(pages.Cursors); Assert.Empty(pages.Resolutions);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("step_type")]
    [InlineData("version")]
    [InlineData("content")]
    [InlineData("descriptor")]
    [InlineData("invalid")]
    public void KnownDoesNotOfferUnqualifiedCachedContracts(string reason)
    {
        var (state, _) = ReadyKnown(); var contract = Contract();
        if (reason == "descriptor") state.Discovery.Pages.Add(new("source", null,
            [new(contract.Id, "source", "Read", contract.Description, contract.StepType, contract.EffectKind, contract.Version,
                Operation: TaskOperations.Describe(contract))], null, Query: Objective));
        else AddContract(state, contract);
        if (reason == "denied") state.Catalog!.Policy.DeniedCapabilityIds.Add("read");
        if (reason == "step_type") state.Catalog!.AllowedStepTypes.Clear();
        if (reason is "version" or "content")
        { var conflict = Contract(); if (reason == "version") conflict.Version = "v2"; else conflict.Description += " Changed."; AddContract(state, conflict); }
        if (reason == "invalid")
        {
            contract.Operation = TaskOperations.Describe(contract);
            contract.Operation.Version = "stale_mapping";
            Assert.NotEmpty(TaskOperations.Validate(contract));
        }
        Assert.Empty(PlanningCompactConstruction.SelectionOperationIds(state));
        Assert.Null(PlanningCompactConstruction.SelectionContext(state)["knownOperations"]);
        Assert.DoesNotContain(Nodes(PlanningCompactConstruction.SelectionSchema(state)).OfType<JsonObject>(), node =>
            node["properties"]?["kind"]?["enum"] is JsonArray kinds && kinds.Any(kind => kind?.ToString() == "operation"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownKnownSelectionPreservesStateWhenMandatoryDraftCannotFit(bool required)
    {
        var (state, runtime) = ReadyKnown(); var contract = Contract();
        contract.InputSchema["properties"]!["large"] = new JsonObject { ["type"] = "string", ["description"] = new string('x', 80_000) };
        if (required) contract.InputSchema["required"]!.AsArray().Add("large");
        AddContract(state, contract); var pages = new Pages(); runtime.Capabilities = pages;
        var baseline = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, _) => new LLMResponse { Json = SelectKnown(state, "operation", "read") };
        state = await Advance(state, runtime);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan)); Assert.Empty(state.Construction!.Checkpoints);
        Assert.Empty(pages.Cursors); Assert.Empty(pages.Resolutions); Assert.Equal(1, state.ModelCalls);
        if (required)
        {
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_CONTRACT_LIMIT");
            Assert.Null(state.Construction!.Compact!.CurrentStep); Assert.Equal(0, state.Construction!.Compact!.NextStep);
            Assert.Empty(state.Construction!.Compact!.PendingCommitments);
            Assert.Contains(state.RequestMeasurements!, measurement => measurement.Phase == PlanningConstruction.DraftPurpose && measurement.Admission == "rejected_before_dispatch");
        }
        else { Assert.Empty(state.Diagnostics); Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownIssuedKnownSelectionRequiresItsOriginalContractHashOnRecovery(bool changed)
    {
        var (state, runtime) = ReadyKnown(); AddContract(state, Contract());
        var request = PlanningCompactConstruction.Request(state, PlanningCompactConstruction.SelectGuidance(state), PlanningCompactConstruction.SelectionContext(state),
            PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
        request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject());
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.SelectPurpose, Request = request };
        state.ModelCalls = 1;
        if (changed) state.Discovery.Resolved[0].Description += " Changed after issuance.";
        runtime.Respond = (issued, _) =>
        { Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest)); return new LLMResponse { Json = SelectKnown(state, "operation", "read") }; };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(1, state.ModelCalls); Assert.Single(runtime.Calls); Assert.Empty(state.Construction!.Checkpoints);
        if (changed)
        { Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "DISCOVERY_SELECTION_INVALID"); Assert.Null(state.Construction!.Compact!.CurrentStep); }
        else { Assert.Empty(state.Diagnostics); Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete); }
    }

    private static (PlanningSession, TestRuntime) ReadyKnown()
    {
        var (state, runtime) = Ready(selected: false);

        return (state, runtime);
    }


    [Theory]
    [InlineData("value", false, true)]
    [InlineData("value", true, false)]
    [InlineData("close", false, false)]
    [InlineData("close", true, true)]
    public void KnownSelectionRequiresExplicitClosureOnlyForClose(string kind, bool explicitTarget, bool valid)
    {
        var (state, _) = ReadyKnown();
        var response = SelectKnown(state, kind);
        response["step"]!["closeScopes"] = explicitTarget ? new JsonArray() : null;
        Assert.Equal(valid, PlanningContractValidation.ValidateInstanceFindings(response, PlanningCompactConstruction.SelectionSchema(state)).Count == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownDeferredCloseSelectsItsExactTargetWithoutReselection(bool closeScope)
    {
        var (state, runtime) = ReadyKnown();
        runtime.Respond = (request, _) =>
        {
            var response = SelectKnown(state, null);
            response["step"]!["closeRequirementIds"] = new JsonArray("req_0");
            return Wire(request, response);
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Null(state.Construction!.Compact!.CurrentStep!.Kind);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose);
            var response = Binding("close");
            response["binding"]!["closeScopes"] = closeScope ? new JsonArray("/root") : new JsonArray();
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema!));
            return Wire(request, response);
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var step = state.Construction!.Compact!.CurrentStep!;
        Assert.Equal("close", step.Kind); Assert.True(step.DiscoveryComplete);
        Assert.Equal(closeScope ? new[] { "/root" } : [], step.CloseScopes);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(state.Construction!.Checkpoints); Assert.Null(state.Construction!.Candidate);
    }

    private static JsonObject SelectKnown(PlanningSession state, string? kind, params string[] operations)
    {
        var response = Select(state, kind);
        response["step"]!["operationIds"] = new JsonArray(operations.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        response["step"]!["closeScopes"] = kind == "close" ? new JsonArray() : null;
        return response;
    }

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
        state.Construction!.Compact!.CurrentStep!.CloseRequirementIds = ["req_0"];
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
    public void DiscoveryCloseRequiresAnExistingProgressRoute()
    {
        var (state, _) = Ready();
        var compact = state.Construction!.Compact!;

        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(Binding("close"), schema));
        foreach (var kind in new[] { "value", "transform", "sequence", "conditional", "parallel", "foreach", "group", "choice" })
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Binding(kind), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Discover(null), schema));
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        compact.CurrentStep!.Kind = "close";
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state,
            new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["req_0"] }));
        Assert.Contains(failure.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
    }

    [Theory]
    [InlineData("requirement")]
    [InlineData("root_export")]
    [InlineData("nested_close")]
    [InlineData("nested_export")]
    [InlineData("complete")]
    public void DiscoveryClosePreservesRequirementExportAndScopeProgress(string progress)
    {
        var (state, _) = Ready();
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        var extension = new PlanningConstructionExtension { RequirementIds = ["req_0"] };
        if (progress == "requirement") step.CloseRequirementIds = ["req_0"];
        if (progress == "root_export")
        {
            state.Requirements!.Outputs = [new() { Name = "result", Type = new() { Kind = "string" }, Required = true }];
            step.OutputNames = ["result"];
            extension.Outputs = [new("result", new() { Kind = "string", Text = "EXACT" })];
        }
        if (progress.StartsWith("nested", StringComparison.Ordinal))
        {
            state.Plan!.Groups.Add(new() { Id = "worker" });
            step.Scope = extension.Scope = "/groups/worker/body";
            construction.OpenScopes.Add(step.Scope);
            if (progress == "nested_close") extension.CloseScopes = [step.Scope];
            else
            {
                state.Plan.Groups[0].Body.Tasks.Add(new() { Id = "child", Kind = "sequence", Objective = "Produce the nested result", Body = new() });
                construction.OpenScopes.Add("/tasks/child/body");
                extension.Outputs = [new("result", new() { Kind = "string", Text = "EXACT" })];
            }
        }
        if (progress == "complete")
        {
            construction.Requirements[0].Status = "closed";
            extension.CloseScopes = ["/root"]; extension.Complete = true;
        }
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Binding("close"), PlanningCompactConstruction.DiscoverySchema(state)));
        step.Kind = "close"; step.DiscoveryComplete = true;
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.DraftGuidance(state),
            PlanningCompactConstruction.Context(state, useCandidate: false), schema, PlanningConstruction.DraftPurpose);
        var response = Draft(state);
        response["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Wire(request, response).Json!, request.StructuredOutputSchema!));
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(candidate.TaskIds);
        Assert.False(candidate.BusinessReviewed);
    }

    [Fact]
    public async Task SavedLocalBindingRecoversUnderItsIssuedSchema()
    {
        var (state, runtime) = Ready();
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
            PlanningCompactConstruction.DiscoveryContext(state), schema, PlanningCompactConstruction.DiscoveryPurpose);
        request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject());
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var baseline = PlanningConstruction.Hash(state.Plan);
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = request };
        state.ModelCalls = 1;
        runtime.Respond = (issued, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            return Wire(issued, Binding("value"));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(exact, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal("value", state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.True(state.Construction!.Compact!.CurrentStep.DiscoveryComplete);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
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
        Assert.Equal(kind, state.Construction!.Compact!.CurrentStep.Kind);
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
        Assert.False(state.Construction!.Compact!.CurrentStep.DiscoveryComplete);
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
        Assert.True(state.Construction!.Compact!.CurrentStep.DiscoveryComplete);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(pages.Cursors);
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(new[] { kind }, TaskKinds(PlanningCompactConstruction.DraftSchema(state)));
        Assert.Equal(kind == "operation", state.Construction!.Compact!.PendingCommitments["req_0"].Count == 1);
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
        Assert.Empty(state.Construction!.Checkpoints);
    }

    [Fact]
    public async Task RepeatedExactPrerequisiteCitationsNormalizeOnlyFreshNavigation()
    {
        var (state, runtime) = Ready(selected: false);

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

        {
            Assert.Empty(state.Diagnostics);
            var accepted = Assert.Single(state.Construction!.Compact!.CurrentStep!.Prerequisites!);
            Assert.Equal(new[] { "requirement:req_0", "source:request:0:clause:0" }, accepted.EvidenceAddresses);
            var recovered = PlannerFixture.Clone(state);
            Assert.Equal(accepted.EvidenceAddresses, Assert.Single(recovered.Construction!.Compact!.CurrentStep!.Prerequisites!).EvidenceAddresses);
            Assert.True(recovered.Construction!.Compact!.CurrentStep.DiscoveryComplete);
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
        Assert.Empty(state.Construction!.Checkpoints);
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
        state.Construction!.Requirements = [new() { Id = "req_0", Status = "closed" }, new() { Id = "req_1" }];
        state.Plan = new() { Inputs = [input] }; state.Catalog = new();
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
        Assert.Null(state.Construction!.Evidence);
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
        Assert.Equal("closed", state.Construction!.Requirements[0].Status);
        Assert.Equal(0, state.ReplanAttempts);
        var draft = PlanningConstruction.DraftRequest(state, repair: false);
        foreach (var address in new[] { "requirement:req_0", declarationSource })
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Wire(draft, new JsonObject
                { ["baseHash"] = baseline, ["evidenceRequests"] = new JsonArray(address) }).Json!, draft.StructuredOutputSchema!));
        PlanningConstruction.LoadEvidence(state, [declarationSource],
            followup: current => PlanningConstruction.DraftRequest(current, repair: false), purpose: PlanningConstruction.DraftPurpose);
        Assert.Equal(declaration, state.Construction!.Evidence![declarationSource]!["text"]!.ToString());
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
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
        state.Construction!.Evidence = new() { [address] = original.DeepClone(), ["requirement:unrelated"] = new JsonObject() };
        state.Construction!.Compact!.CurrentStep = null;
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
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.SchemaPointers);
        runtime.Respond = (request, _) => Wire(request, Binding(kind));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete);
        Assert.Empty(state.Construction!.Compact!.CurrentStep.SchemaPointers);
        Assert.True(JsonNode.DeepEquals(original, state.Construction!.Evidence![address]));
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
        state.Construction!.OpenScopes.Add("/groups/worker/body");
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
            Assert.False(state.Construction!.Compact!.CurrentStep.DiscoveryComplete);
            Assert.Empty(state.Construction!.Compact!.PendingCommitments);
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
        Assert.Empty(state.Construction!.Compact!.CurrentStep.OperationIds);
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
    public async Task SelectedPhasesRetrieveLaterOriginalsWithoutChangingClaims(string phase, bool recover)
    {
        var (state, runtime) = OriginalPages(phase);

        var accepted = PlanningConstruction.Hash(state.Plan);
        var stepBytes = JsonSerializer.Serialize(state.Construction!.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var claims = state.Construction!.Requirements.Select(item => (item.Id, item.Status)).ToArray();
        var candidate = state.Construction!.Candidate?.Hash;
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
            if (phase == "draft") state.Construction!.IssuedBaseHash = accepted;
            if (phase == "judge")
            {
                state.Construction!.PendingReviewContext = initialContext;
                state.Construction!.PendingReviewHash = PlanningGraphCompiler.Fingerprint(PlanningConstruction.LocalPurpose + "\n" + candidate + "\n" + initialContext.ToJsonString());
            }
            state.ModelCalls++;
            var exact = JsonSerializer.Serialize(initial, PlanningJsonContext.Default.LLMRequest);
            runtime.Respond = (request, _) =>
            {
                Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                return OriginalEvidence(request, phase, "source:request:0:clause:0");
            };
            state = await Advance(PlannerFixture.Clone(state), runtime);
            if (phase == "discovery" && state.Diagnostics.Any(d => d.Code == "CONSTRUCTION_EVIDENCE_LIMIT"))
            { Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan)); Assert.Empty(state.Construction!.Checkpoints); return; }
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
            if (phase == "discovery" && state.Diagnostics.Any(d => d.Code == "CONSTRUCTION_EVIDENCE_LIMIT"))
            { Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan)); Assert.Empty(state.Construction!.Checkpoints); return; }
            Assert.Empty(state.Diagnostics);
            var page = state.Construction!.Evidence![address]!;
            var items = page["items"]!.AsArray();
            Assert.InRange(items.Count, 1, 8);
            foreach (var item in items)
                Assert.True(JsonNode.DeepEquals(item, PlanningCompactConstruction.RequirementNodes(state, [item!["id"]!.ToString()])[0]));
            Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
            Assert.Equal(candidate, state.Construction!.Candidate?.Hash);
            Assert.Equal(stepBytes, JsonSerializer.Serialize(state.Construction!.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
            Assert.Equal(claims, state.Construction!.Requirements.Select(item => (item.Id, item.Status)));
            Assert.Empty(state.Construction!.Checkpoints);
            Assert.Null(state.EditablePaths); Assert.Null(state.Construction!.Candidate?.AuthorizedPaths);
            Assert.Equal(0, state.ReplanAttempts); Assert.Equal(0, state.Construction!.DraftCorrections);
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
            Assert.Equal(stepBytes, JsonSerializer.Serialize(state.Construction!.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
            Assert.Null(state.Construction!.Candidate!.AuthorizedPaths); Assert.Null(state.EditablePaths);
            Assert.Equal(0, state.ReplanAttempts); Assert.Equal(0, state.Construction!.DraftCorrections);
        }
        var replay = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, ["page:requirements:8"]));
        Assert.Contains(replay.Diagnostics, item => item.Code == "REPLAN_NO_PROGRESS");
        var invalid = state.Construction!.Compact!.CurrentStep!;
        invalid.CloseRequirementIds = ["req_10"];
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, invalid));
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

        state.Requirements = new() { Summary = Objective, Outcomes = [new("req_0", Objective) { Commitments = [] }], Inputs = [], Outputs = [] };
        state.Construction!.Requirements = [new() { Id = "req_0" }];
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
            ["requirementIds"] = new JsonArray("req_0"), ["operationIds"] = new JsonArray(), ["inputs"] = new JsonArray(), ["groupId"] = null,
            ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray("The requested record."),
            ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray(), ["prerequisites"] = null }
    };
    private static JsonObject Binding(string kind) => new()
    {
        ["binding"] = new JsonObject { ["kind"] = kind, ["operationIds"] = kind == "operation" ? new JsonArray("read") : new JsonArray(), ["groupId"] = null, ["closeScopes"] = kind == "close" ? new JsonArray() : null },
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
