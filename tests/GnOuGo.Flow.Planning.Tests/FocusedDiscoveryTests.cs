using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FocusedDiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourPagesThenRecreatedRuntimeUsesPinnedSnapshotDespiteChangedRemoteCatalog(bool diagnostic)
    {
        var source = new Source("arbitrary", 48);
        var (state, runtime) = Ready(source);
        if (diagnostic) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        runtime.Respond = (request, _) => Discover(request, state, source.Id);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(4, state.Discovery.Pages.Count);
        Assert.NotNull(state.Construction!.Compact!.CurrentStep!.PendingDiscovery!.InlineSnapshots);
        Assert.Equal(1, source.Reads); Assert.Equal(1, state.ModelCalls);
        var version = Assert.Single(state.Discovery.Pages.Select(p => p.SnapshotVersion).Distinct());
        source.Tools[0].Description = "A changed remote contract after the fourth page";
        var resumedRuntime = new TestRuntime { Capabilities = new CapabilityDiscovery(new() { McpClientFactory = source }),
            Respond = (_, _) => throw new InvalidOperationException("Pagination must not infer again.") };
        state = await Advance(PlannerFixture.Clone(state), resumedRuntime);
        Assert.Empty(state.Diagnostics); Assert.Equal(6, state.Discovery.Pages.Count);
        Assert.Null(state.Construction!.Compact!.CurrentStep!.PendingDiscovery); Assert.Empty(resumedRuntime.Calls);
        Assert.Equal(1, source.Reads); Assert.All(state.Discovery.Pages, p => Assert.Equal(version, p.SnapshotVersion));
        Assert.Equal(48, state.Discovery.Pages.SelectMany(p => p.Capabilities).Select(c => c.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("unrelated", "document")]
    [InlineData("alpha", "beta")]
    public async Task FailedExploratorySourceWarnsAndHealthyAlternativeRemainsAvailable(string failedName, string healthyName)
    {
        var source = new Source(healthyName, 2);
        var (state, runtime) = Ready(source);
        var failedId = CapabilityDiscovery.SourceId(failedName);
        state.Discovery.Sources.Insert(0, new(failedId, "Exploratory metadata source"));
        runtime.Capabilities = new Mixed(source, failedId);
        runtime.Respond = (request, _) => Discover(request, state, failedId, source.Id);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.False(PlanningStatus.IsTerminal(state.Status));
        Assert.NotNull(state.Discovery.Failures); Assert.Single(state.Discovery.Failures);
        Assert.Contains(state.InspectionEvents!, e => e.State == "warning" && e.Discovery?.SourceId == failedId);
        Assert.Contains(state.Discovery.Pages, p => p.SourceId == source.Id && p.Capabilities.Count == 2);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(state.InspectionEvents, PlanningJsonContext.Default.ListPlanningInspectionEvent));
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.Contains("transport_unavailable", context.ToJsonString()); Assert.Null(state.Construction!.Compact!.CurrentStep!.Kind);
        var failures = state.Discovery.Failures.Count;
        await runtime.Capabilities.ListAsync(source.Id, null, Ct); // independent healthy discovery grants no binding authority
        Assert.Equal(failures, state.Discovery.Failures.Count); Assert.Empty(state.Catalog!.Capabilities);
    }

    [Fact]
    public async Task SelectedPrerequisiteIsTheExactQueryAndNoOtherSourceIsContacted()
    {
        var source = new Source("renamed", 1); var (state, runtime) = Ready(source);
        state.Construction!.Compact!.CurrentStep!.Prerequisites = [new() { Need = "Present the already prepared result to the operator", EvidenceAddresses = ["requirement:req_0"] }];
        var focus = PlanningDiscoveryFocus.Choices(state).Single(f => f.Basis == "retained_prerequisite");
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        runtime.Respond = (request, _) => Discover(request, state, [source.Id], focus.Id);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(focus.Query, Assert.Single(state.Discovery.Pages).Query);
        Assert.Equal(focus.Id, state.Discovery.Pages[0].FocusId); Assert.Equal(1, source.Reads);
        var invalid = JsonNode.Parse("{\"sourceId\":\"" + source.Id + "\",\"cursor\":null,\"query\":null,\"operationIds\":null,\"focusId\":\"another-step\",\"justification\":\"invented\"}")!;
        var response = new JsonObject { ["binding"] = null, ["discoveryRequests"] = new JsonArray(invalid), ["evidenceRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null };
        Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(response, schema));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task SelectedContractFailureRecoversWithoutLosingIndependentWork(bool diagnostic, bool changed)
    {
        var source = new Source("arbitrary-contracts", 1); var (state, runtime) = Ready(source);
        if (diagnostic) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        state.Plan!.Root.Tasks.Add(new() { Id = "independent", Kind = "value", Objective = "Keep the original supplied value",
            Outputs = [new("retained", new() { Kind = "string", Text = "Original value" })] });
        state.Construction!.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        var baseline = PlanningConstruction.Hash(state.Plan); var block = state.Construction.Compact.CurrentBlockId;
        runtime.Respond = (request, _) => Discover(request, state, source.Id);
        state = await Advance(state, runtime);
        var original = Assert.Single(state.Discovery.Pages).Capabilities[0];
        if (changed) source.Tools[0].OutputSchema!["type"] = "integer";
        else source.OpenFailure = new IOException("PRIVATE_SOURCE_FAILURE");
        runtime.Capabilities = new CapabilityDiscovery(new() { McpClientFactory = source });
        runtime.Respond = (request, _) => new() { Json = PlanningCorpus.Transport(new JsonObject
        {
            ["binding"] = new JsonObject { ["kind"] = "operation", ["operationIds"] = new JsonArray(original.Operation!.Id), ["groupId"] = null, ["closeScopes"] = null },
            ["discoveryRequests"] = null, ["evidenceRequests"] = null, ["clarifications"] = null, ["reselectStep"] = null
        }, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.PendingCall);
        Assert.Contains(state.Diagnostics, d => d.Required && d.Code == (changed ? "DISCOVERY_REQUIRED_CONTRACT_CHANGED" : "DISCOVERY_REQUIRED_CONTRACT_UNAVAILABLE"));
        var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state)); Assert.Equal(changed ? "catalog_changed" : "transport_unavailable", failure.Category);
        Assert.Equal(original.Id, failure.CapabilityId); Assert.Equal(original.Version, failure.ContractVersion);
        Assert.True(PlanningDiscoveryRecoveryActions.Eligible(state)); Assert.Equal(2, state.ModelCalls);
        source.OpenFailure = null;
        runtime.DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "recover_discovery", ExpectedRevision = state.Revision,
            DiscoveryRecovery = new(failure.Id, "refresh_source") }, runtime, Ct);
        Assert.Equal(PlanningStatus.DiscoveryRecovery, state.Status); Assert.Equal(2, state.ModelCalls);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan)); Assert.Equal(block, state.Construction!.Compact!.CurrentBlockId);
        Assert.Empty(state.Diagnostics); Assert.Equal(changed ? 0 : 1, state.Discovery.Resolved.Count); Assert.Equal(2, state.Discovery.Pages.Count);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "resume_discovery", ExpectedRevision = state.Revision }, runtime, Ct);
        var repeatProbe = PlannerFixture.Clone(state);
        var sameBinding = new JsonObject { ["binding"] = new JsonObject { ["kind"] = "operation", ["operationIds"] = new JsonArray(original.Operation!.Id), ["groupId"] = null, ["closeScopes"] = null } };
        PlanningProgress.AssessAttempt(repeatProbe, PlanningCompactConstruction.DiscoveryPurpose, sameBinding, "fresh-contract-binding");
        var repeated = Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() =>
            PlanningProgress.AssessAttempt(repeatProbe, PlanningCompactConstruction.DiscoveryPurpose, sameBinding, "same-contract-binding"));
        Assert.Equal("REPLAN_NO_PROGRESS", repeated.Code);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete);
        Assert.Equal(changed, original.Version != Assert.Single(state.Discovery.Resolved).Version);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan)); Assert.Equal(block, state.Construction.Compact.CurrentBlockId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InspectedSourceDescriptionsRetainIssuedFocusBeforeAndAfterLegacyRecovery(bool recover, bool diagnostic)
    {
        var source = new Source("renamed-source", 1); var (state, runtime) = Ready(source);
        if (diagnostic) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Prerequisites = [new() { Need = "Use the retained prerequisite result", EvidenceAddresses = ["requirement:req_0"] }];
        if (recover) step.DiscoveryProtocol = null;
        var description = PlanningCompactConstruction.DiscoveryContext(state)["providerRequests"]!["items"]![0]!["descriptionEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [description]);
        var original = state.Construction.Evidence![description]!.ToJsonString();
        if (recover)
        {
            const string unavailable = "unavailable-source";
            state.Discovery.Sources.Add(new(unavailable, "Exploratory unavailable source"));
            state.Discovery.Pages.Add(new(unavailable, "old-cursor", [], null, "Historical source failure", step.Objective));
            step.PendingDiscovery = new() { Requests = [new(unavailable)], Pending = [new(unavailable, "old-cursor", step.Objective)] };
            state.Status = PlanningStatus.Stopped; state.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "Historical source failure")];
            var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state));
            runtime.DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask;
            state = await new HybridWorkflowPlanner().AdvanceAsync(state,
                new() { Kind = "recover_discovery", ExpectedRevision = state.Revision, DiscoveryRecovery = new(failure.Id, "continue_available") }, runtime, Ct);
            state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "resume_discovery", ExpectedRevision = state.Revision }, runtime, Ct);
        }
        var choices = PlanningDiscoveryFocus.Choices(state).Select(f => f.Id).ToArray();
        var items = PlanningCompactConstruction.DiscoveryContext(state)["providerRequests"]!["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Contains(item!["focusId"]?.ToString(), choices));
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        var request = new LLMRequest { StructuredOutputSchema = schema };
        var response = Discover(request, state, [source.Id], choices[0]).Json!;
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(response, schema));
        response["discoveryRequests"]![0]!["focusId"] = null;
        Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(response, schema));
        response["discoveryRequests"]![0]!["focusId"] = "another-session-focus";
        Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(response, schema));
        Assert.Equal(original, state.Construction!.Evidence![description]!.ToJsonString());
        Assert.Empty(runtime.Calls); Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task SourceJustificationUsesTheIssuedUnicodeCodePointLimit()
    {
        var source = new Source("arbitrary", 1); var (state, runtime) = Ready(source);
        var justification = string.Concat(Enumerable.Repeat("😀", 256));
        runtime.Respond = (request, _) => Discover(request, state, [source.Id],
            PlanningDiscoveryFocus.Choices(state)[0].Id, justification);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(1, source.Reads); Assert.Single(state.Discovery.Pages);
    }

    [Fact]
    public async Task HistoricalFailureRecoveryPreservesWorkAndAccountingAndRequiresSeparateResume()
    {
        var source = new Source("arbitrary", 1); var (state, runtime) = Ready(source);
        state.Construction!.Compact!.CurrentStep!.DiscoveryProtocol = null;
        state.Status = PlanningStatus.Stopped; state.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "Historical failure")];
        state.ModelCalls = 70; state.Usage = new() { Calls = 70, InputTokens = 100, OutputTokens = 50 };
        state.Discovery.Pages = [new(source.Id, "old-cursor", [], null, "Cause discarded", state.Construction.Compact.CurrentStep.Objective)];
        state.Construction.Compact.CurrentStep.PendingDiscovery = new() { Requests = [new(source.Id)], Pending = [new(source.Id, "old-cursor")] };
        var request = JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest);
        var plan = PlanningConstruction.Hash(state.Plan); var usage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state));
        runtime.DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "recover_discovery", ExpectedRevision = state.Revision,
            DiscoveryRecovery = new(failure.Id, "continue_available") }, runtime, Ct);
        Assert.Equal(PlanningStatus.DiscoveryRecovery, state.Status); Assert.True(PlanningStatus.IsWaiting(state.Status));
        Assert.Empty(runtime.Calls); Assert.Equal(0, source.Reads); Assert.Equal(70, state.ModelCalls);
        Assert.Equal(request, JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest));
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan)); Assert.Equal(usage, JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Single(state.Discovery.Pages); Assert.Contains(state.Discovery.Failures!, f => f.Category == "historical_unavailable");
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { Kind = "resume_discovery", ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task RefreshStartsAtFirstPageAndKeepsOldFailureWithoutInference()
    {
        var source = new Source("arbitrary", 1); var (state, runtime) = Ready(source);
        state.Status = PlanningStatus.Stopped; state.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "Historical")];
        state.Discovery.Pages = [new(source.Id, "stale", [], null, "Historical", state.Construction!.Compact!.CurrentStep!.Objective)];
        var id = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state)).Id;
        runtime.DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "recover_discovery", ExpectedRevision = state.Revision,
            DiscoveryRecovery = new(id, "refresh_source") }, runtime, Ct);
        Assert.Equal(PlanningStatus.DiscoveryRecovery, state.Status); Assert.Empty(runtime.Calls); Assert.Equal(1, source.Reads);
        Assert.Equal(2, state.Discovery.Pages.Count); Assert.Null(state.Discovery.Pages[1].Cursor);
        Assert.Contains(id, state.Discovery.SupersededFailures!); Assert.Equal("stale", state.Discovery.Pages[0].Cursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshOffersOnlyTheNewActionVersionWhileKeepingOldPages(bool diagnostic)
    {
        var source = new Source("renamed-records", 1); var (state, runtime) = Ready(source);
        if (diagnostic) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        runtime.Respond = (request, _) => Discover(request, state, source.Id);
        state = await Advance(state, runtime);
        var originalPage = Assert.Single(state.Discovery.Pages);
        var originalVersion = Assert.Single(originalPage.Capabilities).Version;
        var step = state.Construction!.Compact!.CurrentStep!; var focus = PlanningDiscoveryFocus.Choices(state)[0];
        var failure = new CapabilityDiscoveryFailure("changed-source", source.Id, focus.Id, focus.Query, null,
            "catalog_changed", "Explicit metadata refresh required", originalPage.SnapshotVersion, null, null, DateTimeOffset.UtcNow) { StepId = step.Id };
        state.Discovery.Failures = [failure];
        source.Tools[0].OutputSchema!["type"] = "integer";
        runtime.Capabilities = new CapabilityDiscovery(new() { McpClientFactory = source });
        runtime.DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "recover_discovery", ExpectedRevision = state.Revision,
            DiscoveryRecovery = new(failure.Id, "refresh_source") }, runtime, Ct);
        Assert.Equal(2, state.Discovery.Pages.Count); Assert.Single(runtime.Calls);
        Assert.Equal(originalVersion, state.Discovery.Pages[0].Capabilities[0].Version);
        var current = state.Discovery.Pages[1].Capabilities[0]; Assert.NotEqual(originalVersion, current.Version);
        var descriptor = Assert.Single(PlanningCompactConstruction.ContractDescriptors(state));
        Assert.Equal(current.Version, descriptor!["version"]!.ToString());
        Assert.Equal(current.Version, (await runtime.Capabilities.ResolveAsync(current, Ct)).Version);
        await Assert.ThrowsAsync<PlanningConflictException>(() => runtime.Capabilities.ResolveAsync(originalPage.Capabilities[0], Ct));
        Assert.Equal("block_1", state.Construction!.Compact!.CurrentBlockId);
    }

    [Fact]
    public async Task IdleExploratoryWarningCanBeExplicitlyRefreshedWithoutNewInference()
    {
        var source = new Source("arbitrary", 1); var (state, runtime) = Ready(source);
        runtime.Capabilities = new Faulty(source.Id, "unavailable");
        runtime.Respond = (request, _) => Discover(request, state, source.Id);
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.Empty(state.Diagnostics);
        Assert.True(PlanningDiscoveryRecoveryActions.Eligible(state));
        var selected = PlannerFixture.Clone(state); selected.Construction!.Compact!.CurrentStep!.Kind = "mcp";
        Assert.False(PlanningDiscoveryRecoveryActions.Eligible(selected));
        var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state));
        runtime.Capabilities = new CapabilityDiscovery(new() { McpClientFactory = source });
        runtime.DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "recover_discovery", ExpectedRevision = state.Revision, DiscoveryRecovery = new(failure.Id, "refresh_source") }, runtime, Ct);
        Assert.Equal(PlanningStatus.DiscoveryRecovery, state.Status); Assert.Single(runtime.Calls);
        Assert.Equal(1, source.Reads); Assert.Equal(2, state.Discovery.Pages.Count);
        Assert.Single(state.Discovery.Pages[^1].Capabilities); Assert.Null(state.Construction!.Compact!.CurrentStep!.Kind);
    }

    [Fact]
    public async Task RefreshKeepsTheExactPrerequisiteFocusWhenItsTextEqualsTheObjective()
    {
        var source = new Source("arbitrary", 1); var (state, runtime) = Ready(source);
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Prerequisites = [new() { Need = step.Objective, EvidenceAddresses = ["requirement:req_0"] }];
        var focus = PlanningDiscoveryFocus.Choices(state).Single(f => f.Basis == "retained_prerequisite");
        state.Status = PlanningStatus.Stopped; state.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "Source unavailable")];
        var failure = new CapabilityDiscoveryFailure("retained-focus", source.Id, focus.Id, focus.Query, null,
            "transport_unavailable", "Source unavailable", null, null, "Retained prerequisite metadata", DateTimeOffset.UtcNow) { StepId = step.Id };
        state.Discovery.Failures = [failure]; runtime.DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "recover_discovery", ExpectedRevision = state.Revision, DiscoveryRecovery = new(failure.Id, "refresh_source") }, runtime, Ct);
        var page = Assert.Single(state.Discovery.Pages);
        Assert.Equal(focus.Id, page.FocusId); Assert.Equal(focus.Query, page.Query);
        Assert.Empty(runtime.Calls); Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task InterruptedRecoveryRetainsItsExactMetadataActionAndSnapshotForExplicitContinuation()
    {
        var source = new Source("arbitrary", 48); var (state, runtime) = Ready(source);
        state.Status = PlanningStatus.Stopped; state.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "Historical failure")];
        state.Discovery.Pages = [new(source.Id, "stale", [], null, "Historical", state.Construction!.Compact!.CurrentStep!.Objective)];
        var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state));
        runtime.DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask;
        runtime.Checkpointed = saved =>
        {
            if (saved.Discovery.Pages.Count == 5)
                throw new PlanningConflictException("Simulated process interruption after the fourth metadata page.");
        };
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "recover_discovery", ExpectedRevision = state.Revision, DiscoveryRecovery = new(failure.Id, "refresh_source") }, runtime, Ct));
        var retained = runtime.Checkpoints[^1];
        Assert.Equal(PlanningStatus.DiscoveryRecovery, retained.Status);
        Assert.Contains(PlanningDiscoveryRecoveryActions.Failures(retained), item => item.Id == failure.Id);
        var actionId = retained.Construction!.Compact!.CurrentStep!.PendingDiscovery!.Id;
        var version = retained.Discovery.Pages[^1].SnapshotVersion;
        source.Tools[0].Description = "Changed remote metadata after the interrupted recovery";
        var resumed = new TestRuntime { Capabilities = new CapabilityDiscovery(new() { McpClientFactory = source }),
            DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask,
            Respond = (_, _) => throw new InvalidOperationException("Metadata recovery must not infer.") };
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(retained,
            new() { Kind = "recover_discovery", ExpectedRevision = retained.Revision, DiscoveryRecovery = new(failure.Id, "continue_available") }, resumed, Ct));
        state = await new HybridWorkflowPlanner().AdvanceAsync(retained,
            new() { Kind = "recover_discovery", ExpectedRevision = retained.Revision, DiscoveryRecovery = new(failure.Id, "refresh_source") }, resumed, Ct);
        Assert.Equal(PlanningStatus.DiscoveryRecovery, state.Status); Assert.Null(state.Construction!.Compact!.CurrentStep!.PendingDiscovery);
        Assert.Equal(1, source.Reads); Assert.Empty(runtime.Calls); Assert.Empty(resumed.Calls);
        Assert.Equal(7, state.Discovery.Pages.Count);
        Assert.All(state.Discovery.Pages.Skip(1), page => { Assert.Equal(version, page.SnapshotVersion); Assert.Equal(actionId, page.ActionId); });
        Assert.Contains(failure.Id, state.Discovery.SupersededFailures!);
    }

    [Theory]
    [InlineData("unavailable", false)]
    [InlineData("timeout", false)]
    [InlineData("changed", false)]
    [InlineData("invalid", false)]
    [InlineData("cycle", true)]
    [InlineData("ownership", true)]
    public async Task SourceFailuresRemainDistinctFromHardProtocolFailures(string outcome, bool hard)
    {
        var source = new Source("arbitrary", 0); var (state, runtime) = Ready(source);
        runtime.Capabilities = new Faulty(source.Id, outcome);
        runtime.Respond = (request, _) => Discover(request, state, source.Id);
        state = await Advance(state, runtime);
        Assert.Single(runtime.Calls); Assert.Empty(state.Catalog!.Capabilities); Assert.Null(state.ApprovedHash);
        if (hard)
        {
            Assert.True(PlanningStatus.IsTerminal(state.Status));
            Assert.True(state.Discovery.Failures is null or { Count: 0 });
        }
        else
        {
            Assert.False(PlanningStatus.IsTerminal(state.Status)); Assert.Empty(state.Diagnostics);
            Assert.Single(state.Discovery.Failures!); Assert.Null(state.Construction!.Compact!.CurrentStep!.Kind);
            Assert.Contains(state.InspectionEvents!, e => e.State == "warning");
            // All sources unavailable is unresolved context, never permission to invent an operation.
            Assert.Contains("canRequestPage\":false", PlanningCompactConstruction.DiscoveryContext(state).ToJsonString());
        }
    }

    [Fact]
    public async Task CancellationNeverBecomesAnUnavailableWarning()
    {
        var source = new Source("arbitrary", 0); var (state, runtime) = Ready(source);
        using var cancellation = new CancellationTokenSource();
        runtime.Capabilities = new Cancelling(cancellation);
        runtime.Respond = (request, _) => Discover(request, state, source.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = state.Revision }, runtime, cancellation.Token));
        Assert.DoesNotContain(runtime.Checkpoints.SelectMany(s => s.InspectionEvents ?? []), e => e.State == "warning");
    }

    [Fact]
    public async Task RecoveryRejectsFailuresOwnedByAnotherRetainedStepOrFocus()
    {
        var source = new Source("arbitrary", 1); var (state, runtime) = Ready(source);
        state.Status = PlanningStatus.Stopped;
        state.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "Retained discovery failure")];
        var step = state.Construction!.Compact!.CurrentStep!;
        var focus = PlanningDiscoveryFocus.Choices(state)[0];
        state.Discovery.Failures = [
            new("other-step", source.Id, focus.Id, focus.Query, null, "transport_unavailable", "Unavailable", null, null, null, DateTimeOffset.UtcNow) { StepId = "another-step" },
            new("other-focus", source.Id, "another-focus", focus.Query, null, "transport_unavailable", "Unavailable", null, null, null, DateTimeOffset.UtcNow) { StepId = step.Id }
        ];
        state.Discovery.Pages = [new(source.Id, null, [], null, "Historical failure", "Another retained objective")];
        runtime.DiscoveryRecoveryGuard = (_, _) => throw new InvalidOperationException("Foreign failures must be rejected before authorization.");
        Assert.Empty(PlanningDiscoveryRecoveryActions.Failures(state));
        foreach (var failure in state.Discovery.Failures)
            await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
                new() { Kind = "recover_discovery", ExpectedRevision = state.Revision, DiscoveryRecovery = new(failure.Id, "continue_available") }, runtime, Ct));
        Assert.Equal(2, state.Discovery.Failures.Count); Assert.Single(state.Discovery.Pages);
        Assert.Empty(runtime.Calls); Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task RuntimeWithoutRecoveryAuthorityCannotChangeAStoppedSession()
    {
        var source = new Source("arbitrary", 1); var (state, runtime) = Ready(source);
        state.Status = PlanningStatus.Stopped; state.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "Historical")];
        state.Discovery.Pages = [new(source.Id, null, [], null, "Unavailable", state.Construction!.Compact!.CurrentStep!.Objective)];
        var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state));
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "recover_discovery", ExpectedRevision = state.Revision, DiscoveryRecovery = new(failure.Id, "refresh_source") }, runtime, Ct));
        Assert.Empty(runtime.Calls); Assert.Equal(0, source.Reads); Assert.Single(state.Discovery.Pages);
    }

    internal static (PlanningSession State, TestRuntime Runtime) Ready(Source source)
    {
        var (state, runtime) = CompactDeferredBindingTests.Ready();
        state.Construction!.Compact!.CurrentStep!.DiscoveryProtocol = 2;
        state.Discovery.Sources = [new(source.Id, "Metadata for the requested business capability")];
        runtime.Capabilities = new CapabilityDiscovery(new() { McpClientFactory = source });
        return (state, runtime);
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
    private static LLMResponse Discover(LLMRequest request, PlanningSession state, params string[] sources) => Discover(request, state, sources, PlanningDiscoveryFocus.Choices(state)[0].Id);
    private static LLMResponse Discover(LLMRequest request, PlanningSession state, string[] sources, string focus,
        string justification = "Its described metadata may establish the required operation.") => new()
    {
        Json = PlanningCorpus.Transport(new JsonObject { ["binding"] = null, ["evidenceRequests"] = null, ["clarifications"] = null, ["reselectStep"] = null,
            ["discoveryRequests"] = new JsonArray(sources.Select(source => (JsonNode)new JsonObject { ["sourceId"] = source, ["cursor"] = null,
                ["query"] = null, ["operationIds"] = null, ["focusId"] = focus, ["justification"] = justification }).ToArray()) },
            request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())
    };
    internal sealed class Source(string name, int count) : IMcpClientFactory
    {
        internal string Id => CapabilityDiscovery.SourceId(name);
        internal int Reads;
        internal Exception? OpenFailure;
        internal List<McpToolInfo> Tools = Enumerable.Range(0, count).Select(i => new McpToolInfo { Name = "operation_" + i,
            Description = "Declared operation", EffectKind = "read", InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            OutputSchema = new JsonObject { ["type"] = "string" } }).ToList();
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [new() { Name = name, Description = "Business operations" }];
        public Task<IMcpSession> GetClientAsync(string server, CancellationToken ct)
        { Reads++; if (OpenFailure is not null) throw OpenFailure; var inner = new InMemoryMcpClientFactory(); inner.RegisterServer(name, new() { Tools = Tools }); return inner.GetClientAsync(server, ct); }
    }
    private sealed class Mixed(Source healthy, string failed) : ICapabilityCatalog
    {
        private readonly CapabilityDiscovery _inner = new(new() { McpClientFactory = healthy });
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => _inner.ListSourcesAsync(ct);
        public Task<CapabilityPage> ListAsync(string source, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) =>
            source == failed ? throw new IOException("PRIVATE_PROVIDER_DETAIL") : _inner.ListAsync(source, cursor, ct, query, producedArtifactKind);
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => _inner.ResolveAsync(summary, ct);
    }
    private sealed class Faulty(string id, string outcome) : ICapabilityCatalog
    {
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([new(id, "Source")]);
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException();
        public Task<CapabilityPage> ListAsync(string source, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => outcome switch
        {
            "unavailable" => throw new IOException("PRIVATE"), "timeout" => throw new OperationCanceledException("PRIVATE"),
            "changed" => throw new ArgumentException("PRIVATE"), "invalid" => throw new System.Text.Json.JsonException("PRIVATE"),
            "ownership" => Task.FromResult(new CapabilityPage("another-source", cursor, [], null)),
            _ => Task.FromResult(new CapabilityPage(source, cursor, [], cursor is null ? "next" : cursor, Query: query))
        };
    }
    private sealed class Cancelling(CancellationTokenSource cancellation) : ICapabilityCatalog
    {
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException();
        public Task<CapabilityPage> ListAsync(string source, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }
    }
}
