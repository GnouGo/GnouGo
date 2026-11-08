using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
    [Fact]
    public async Task DiscoveryOffersExactContractsWithoutAnExecutableBinding()
    {
        var (state, _) = await Ready();
        AddOperations(state, 2);
        var action = Inspect("alpha", "operation_0", "operation_1");
        Assert.Empty(PlanningContractValidation.ValidateInstance(action, PlanningCompactConstruction.DiscoverySchema(state)));
    }

    [Fact]
    public async Task SeparateContractReadsFromTheSameSourceAreOneLegalMetadataBatch()
    {
        var (state, runtime) = await Ready(); AddOperations(state, 2);
        var catalog = new InspectionCatalog(); runtime.Capabilities = catalog;
        var response = Inspect("alpha", "operation_0");
        response["discoveryRequests"]!.AsArray().Add(Inspect("alpha", "operation_1")["discoveryRequests"]![0]!.DeepClone());
        runtime.Respond = (request, _) => Wire(request, response);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, catalog.Reads.Count);
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.OperationIds);
        Assert.False(state.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Null(state.Construction.Candidate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContractInspectionJournalsEachReadWithoutBindingOrChangingAcceptedWork(bool interrupt)
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 5);
        PlanningConstruction.LoadEvidence(state, [PlanningCompactConstruction.OperationPageAddress(state, 4)]);
        state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var catalog = new InspectionCatalog(); runtime.Capabilities = catalog;
        var plan = PlanningConstruction.Hash(state.Plan);
        var step = state.Construction!.Compact!.CurrentStep!;
        var kind = step.Kind;
        var current = state.Discovery.Pages[0];
        state.Discovery.ActiveSourceActions = new(StringComparer.Ordinal)
            { [PlanningDiscoveryFocus.SourceKey("alpha", Objective)] = "original" };
        state.Discovery.Pages[0] = current with { ActionId = "original" };
        runtime.Respond = (request, _) => Wire(request, Inspect("alpha", Enumerable.Range(0, 5).Select(i => "operation_" + i).ToArray()));
        if (interrupt)
        {
            runtime.Checkpointed = checkpoint =>
            {
                if (checkpoint.Discovery.Resolved.Count == 1) throw new PlanningConflictException("Simulated interruption after durable metadata publication.");
            };
            await Assert.ThrowsAsync<PlanningConflictException>(() => Advance(state, runtime));
            state = PlannerFixture.Clone(runtime.Checkpoints[^1]);
            runtime.Checkpointed = null;
        }
        else state = await Advance(state, runtime);
        var calls = state.ModelCalls;
        var cold = new TestRuntime { Capabilities = catalog,
            Respond = (_, _) => throw new InvalidOperationException("Retained metadata work cannot request inference.") };
        for (var continuation = 0; continuation < 3 && state.Construction!.Compact!.CurrentStep!.PendingDiscovery is not null; continuation++)
            state = await Advance(PlannerFixture.Clone(state), cold);
        Assert.Empty(cold.Calls);
        Assert.True(state.Construction!.Compact!.CurrentStep!.PendingDiscovery is null, string.Join(";", state.Diagnostics.Select(d => d.Code + ":" + d.Message).Concat(PlanningContractValidation.ValidateSchema(PlanningCompactConstruction.DiscoverySchema(state), strict: true))));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(5, catalog.Reads.Count);
        Assert.Equal(5, catalog.Reads.Distinct().Count());
        Assert.Equal(5, state.Discovery.Resolved.Count);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(kind, state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.Empty(state.Construction.Compact.CurrentStep.OperationIds);
        Assert.False(state.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Empty(state.Construction.Checkpoints); Assert.Null(state.Construction.Candidate);
        Assert.Equal("original", state.Discovery.ActiveSourceActions![PlanningDiscoveryFocus.SourceKey("alpha", Objective)]);
        Assert.Equal(5, state.Construction.Evidence!.Count(pair => pair.Key.StartsWith("contract:", StringComparison.Ordinal)));
        Assert.Empty(state.Diagnostics);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("version")]
    [InlineData("source")]
    [InlineData("unknown")]
    [InlineData("cursor")]
    [InlineData("duplicate")]
    public async Task InvalidInspectionCannotReadAnyContract(string change)
    {
        var (state, runtime) = await Ready(); AddOperations(state, 1);
        var catalog = new InspectionCatalog(); runtime.Capabilities = catalog;
        var response = Inspect("alpha", "operation_0");
        if (change == "denied") state.Catalog!.Policy.DeniedCapabilityIds.Add("operation_0");
        if (change == "version") state.Discovery.Pages[0].Capabilities.Add(state.Discovery.Pages[0].Capabilities[0] with { Version = "other" });
        if (change == "source") response["discoveryRequests"]![0]!["sourceId"] = "foreign";
        if (change == "unknown") response["discoveryRequests"]![0]!["operationIds"] = new JsonArray("unknown");
        if (change == "cursor") response["discoveryRequests"]![0]!["cursor"] = "foreign";
        if (change == "duplicate") response["discoveryRequests"]![0]!["operationIds"] = new JsonArray("operation_0", "operation_0");
        runtime.Respond = (request, _) => Wire(request, response);
        state = await Advance(state, runtime);
        Assert.Empty(catalog.Reads);
        Assert.Empty(state.Discovery.Resolved);
        Assert.NotEmpty(state.Diagnostics);
        Assert.Null(state.Construction!.Candidate);
    }

    [Fact]
    public async Task InspectedOptionalSchemaIsReadableWithoutSelectingATask()
    {
        var (state, runtime) = await Ready(); AddOperations(state, 1);
        var catalog = new InspectionCatalog { OptionalInput = true }; runtime.Capabilities = catalog;
        // Keep the discovered editable description bound to this exact fixture contract.
        state.Discovery.Pages[0].Capabilities[0] = state.Discovery.Pages[0].Capabilities[0] with
            { Operation = TaskOperations.Describe(catalog.Contract(state.Discovery.Pages[0].Capabilities[0])) };
        runtime.Respond = (request, _) => Wire(request, Inspect("alpha", "operation_0"));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var view = state.Construction!.Evidence!.Single(pair => pair.Key.StartsWith("contract:", StringComparison.Ordinal)).Value!;
        var address = view["omittedOptionalInputs"]![0]!["evidence"]!.ToString();
        var original = PlanningCompactConstruction.LoadContractEvidence(state, address);
        Assert.Equal("string", original!["schema"]!["type"]!.ToString());
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.OperationIds);
        Assert.False(state.Construction.Compact.CurrentStep.DiscoveryComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedPendingInspectionCannotResolveAnotherContract(bool corruptEvidence)
    {
        var (state, runtime) = await Ready(); AddOperations(state, 2);
        var catalog = new InspectionCatalog(); runtime.Capabilities = catalog;
        runtime.Respond = (request, _) => Wire(request, Inspect("alpha", "operation_0", "operation_1"));
        runtime.Checkpointed = saved =>
        {
            if (saved.Discovery.Resolved.Count == 1) throw new PlanningConflictException("Simulated cold interruption.");
        };
        await Assert.ThrowsAsync<PlanningConflictException>(() => Advance(state, runtime));
        state = PlannerFixture.Clone(runtime.Checkpoints[^1]);
        if (corruptEvidence) state.Construction!.Evidence!.First().Value!["inputSchema"] = new JsonObject { ["type"] = "string" };
        else state.Construction!.Compact!.CurrentStep!.PendingDiscovery!.Pending[0] =
            state.Construction.Compact.CurrentStep.PendingDiscovery.Pending[0] with { OperationIds = ["operation_1"] };
        var cold = new TestRuntime { Capabilities = catalog,
            Respond = (_, _) => throw new InvalidOperationException("Retained inspection cannot infer.") };
        await Assert.ThrowsAsync<PlanningConflictException>(() => Advance(state, cold));
        Assert.Single(catalog.Reads);
        Assert.Empty(cold.Calls);
    }

    [Fact]
    public async Task FailedMetadataInspectionRequiresProtectedRecoveryWithoutAcceptingABinding()
    {
        var (state, runtime) = await Ready(); AddOperations(state, 1);
        state.Construction!.Compact!.CurrentStep!.Kind = null;
        state.Construction.Compact.CurrentStep.DiscoveryProtocol = 2;
        var catalog = new InspectionCatalog { Failure = "access_denied", Page = state.Discovery.Pages[0] }; runtime.Capabilities = catalog;
        var focus = PlanningDiscoveryFocus.Choices(state)[0].Id;
        runtime.Respond = (request, _) =>
        {
            var response = Inspect("alpha", "operation_0");
            response["discoveryRequests"]![0]!["focusId"] = focus;
            response["discoveryRequests"]![0]!["justification"] = "Inspect the owned interface for this immediate objective.";
            return Wire(request, response);
        };
        var baseline = PlanningConstruction.Hash(state.Plan);
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "DISCOVERY_REQUIRED_CONTRACT_UNAVAILABLE");
        Assert.True(PlanningDiscoveryRecoveryActions.Eligible(state));
        Assert.NotNull(state.Construction!.Compact!.CurrentStep!.PendingDiscovery);
        var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state));
        var calls = state.ModelCalls; catalog.Failure = null;
        runtime.Respond = (_, _) => throw new InvalidOperationException("Metadata recovery cannot infer.");
        runtime.DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask;
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new()
        { Kind = "recover_discovery", ExpectedRevision = state.Revision, DiscoveryRecovery = new(failure.Id, "refresh_source") }, runtime, PlannerFixture.Ct);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(PlanningStatus.DiscoveryRecovery, state.Status);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Single(state.Discovery.Resolved);
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.OperationIds);
        Assert.False(state.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Null(state.Construction.Candidate); Assert.Empty(state.Construction.Checkpoints);
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("scope", false)]
    [InlineData("block", false)]
    [InlineData("plan", false)]
    [InlineData("version", false)]
    [InlineData("policy", false)]
    [InlineData("corrupt", false)]
    public async Task ReselectionReusesVerifiedContractEvidenceOnlyForTheSameResponsibility(string change, bool restore)
    {
        var (state, runtime) = await Ready(); AddOperations(state, 1);
        runtime.Capabilities = new InspectionCatalog();
        runtime.Respond = (request, _) => Wire(request, Inspect("alpha", "operation_0"));
        state = await Advance(state, runtime);
        var original = state.Construction!.Evidence!.DeepClone();
        var previous = state.Construction.Compact!.CurrentStep!;
        state.Diagnostics = [new("CONSTRUCTION_RESELECTION_REQUIRED", "/step", "A lifecycle companion is missing.")];
        PlanningCompactConstruction.Reselect(state, "The selected interface needs its lifecycle companions.");
        state.Construction.Compact.CurrentStep = new()
        {
            Id = "replacement", BlockId = previous.BlockId, BaseHash = previous.BaseHash,
            Scope = previous.Scope, Kind = null, Objective = previous.Objective, RequirementIds = previous.RequirementIds.ToList()
        };
        if (change == "scope") state.Construction.Compact.CurrentStep.Scope = "/other";
        if (change == "block") state.Construction.Compact.CurrentStep.BlockId = "another";
        if (change == "plan") state.Plan!.Root.Tasks[0].Objective = "Changed accepted baseline";
        if (change == "policy") state.Catalog!.Policy.DeniedCapabilityIds.Add("operation_0");
        if (change == "version") state.Discovery.Pages[0].Capabilities[0] = state.Discovery.Pages[0].Capabilities[0] with { Version = "changed" };
        if (change == "corrupt") state.Construction.Compact.RejectedSteps[^1]["evidence"]!.AsObject().First().Value!["inputSchema"] = new JsonObject { ["type"] = "string" };
        PlanningCompactConstruction.RetainPrerequisiteEvidence(state);
        Assert.Equal(restore, state.Construction.Evidence is { Count: > 0 });
        if (restore) Assert.True(JsonNode.DeepEquals(original, state.Construction.Evidence));
        Assert.Empty(state.Construction.Compact.CurrentStep.OperationIds);
        Assert.False(state.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Null(state.Construction.Candidate); Assert.Empty(state.Construction.Checkpoints);
    }

    private sealed class InspectionCatalog : ICapabilityCatalog
    {
        internal readonly List<string> Reads = [];
        internal bool OptionalInput;
        internal string? Failure;
        internal CapabilityPage? Page;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => Task.FromResult(Page! with { Query = query, Cursor = cursor });
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
        { Reads.Add(summary.Id); if (Failure is { } category) throw new CapabilityDiscoveryException(category, "Redacted fixture metadata failure."); return Task.FromResult(Contract(summary)); }
        internal PlanningCapability Contract(CapabilitySummary summary) => new()
        {
            Id = summary.Id, Version = summary.Version, Kind = "tool", StepType = summary.StepType,
            Server = summary.SourceId, Method = summary.Name, Description = summary.Description,
            InputSchema = new() { ["type"] = "object", ["properties"] = OptionalInput
                ? new JsonObject { ["optional"] = new JsonObject { ["type"] = "string" } } : new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
        };
    }

    private static JsonObject Inspect(string source, params string[] operations)
    {
        var response = Discover(source, null);
        response["discoveryRequests"]![0]!["operationIds"] = new JsonArray(operations.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
        return response;
    }
}
