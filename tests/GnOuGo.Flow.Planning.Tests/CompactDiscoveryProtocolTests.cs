using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
    [Fact]
    public async Task BoundOperationKeepsCompanionRetrievalNavigationWithoutGrantingContractAuthority()
    {
        var (state, _) = await BoundOperations(2);
        var selected = state.Construction!.Compact!.CurrentStep!.OperationIds.Single();
        var canonical = PlanningCompactConstruction.ContractDescriptorPage(state);
        var companion = canonical["items"]!.AsArray().Single(item => item!["id"]!.ToString() != selected)!;
        var address = companion["descriptionEvidence"]!.ToString();
        var original = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
        state.Construction!.Evidence = new() { [address] = original.DeepClone() };
        state.Construction!.EvidenceRequests.Add(PlanningConstruction.EvidenceBatchFingerprint(state, [address]));
        PlanningCompactConstruction.RetainPrerequisiteEvidence(state);
        Assert.Null(state.Construction!.Evidence);
        Assert.Empty(state.Construction!.EvidenceRequests);
        var discovery = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var draft = PlanningConstruction.DraftRequest(state, repair: false);
        var draftContext = Context(draft);
        Assert.True(draftContext.ContainsKey("companionOperations"));
        Assert.Contains(address, draft.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        var extension = new PlanningConstructionExtension { RequirementIds = ["req_0"], Tasks = [new()
            { Id = "read", Kind = "operation", Objective = Objective, Operation = selected }] };
        PlanningCompactConstruction.StageCommitments(state, null);
        state.Construction!.Candidate = PlanningConstruction.Apply(state, extension);
        Assert.Empty(state.Construction!.Candidate.Diagnostics);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Construction!.Candidate.Plan, state.Catalog!, state.Request,
            state.Construction!.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        foreach (var intermediate in new[] { false, true })
        {
            var (context, review) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate);
            Assert.False(context.ContainsKey("companionOperations"));
            var action = context["actions"]!.AsArray().Single(item => item!["id"]!.ToString() == "read")!;
            Assert.Equal(selected, action["operation"]!.ToString());
            Assert.DoesNotContain("/companionOperations", review.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        }
        Assert.Equal(discovery, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.True(JsonNode.DeepEquals(canonical, PlanningCompactConstruction.ContractDescriptorPage(state)));
        Assert.Null(state.Construction!.Evidence);
        Assert.Empty(state.Construction!.EvidenceRequests);
        var retrieved = PlannerFixture.Clone(state);
        PlanningConstruction.LoadEvidence(retrieved, [address]);
        Assert.True(JsonNode.DeepEquals(original, retrieved.Construction!.Evidence![address]));
        Assert.Single(retrieved.Discovery.Resolved);
        Assert.Equal(new[] { selected }, retrieved.Construction!.Compact!.CurrentStep!.OperationIds);
        Assert.Equal(discovery, JsonSerializer.Serialize(retrieved.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(retrieved, [address]));
    }

    [Fact]
    public async Task CompanionNavigationDoesNotAutofillRegisteredOrEarlierOperationsAndKeepsCanonicalPaging()
    {
        var (state, _) = await BoundOperations(9);
        var selected = state.Construction!.Compact!.CurrentStep!.OperationIds.Single();
        var page = state.Discovery.Pages.Single();
        var first = PlanningCompactConstruction.ContractDescriptorPage(state);
        var current = Context(PlanningConstruction.DraftRequest(state, false))["companionOperations"]!;
        Assert.InRange(current["items"]!.AsArray().Count, 1, 4);
        Assert.DoesNotContain(current["items"]!.AsArray(), item => item!["id"]!.ToString() == selected);
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, current["directory"]!.ToString(), out var fullPage));
        Assert.True(JsonNode.DeepEquals(first, fullPage));
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, current["next"]!.ToString(), out var next));
        Assert.Equal(4, next!["offset"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(first, PlanningCompactConstruction.ContractDescriptorPage(state)));
        var before = PlanningConstruction.DraftRequest(state, false);
        for (var index = 0; index < 50; index++)
            state.Discovery.Pages.Add(page with { Query = "Earlier unrelated action " + index,
                Capabilities = [page.Capabilities[0] with { Id = "previous_" + index,
                    Operation = new() { Id = "previous_operation_" + index, Version = "v", Description = new string('x', 20000) } }] });
        var after = PlanningConstruction.DraftRequest(state, false);
        Assert.Equal(before.Prompt, after.Prompt);
        Assert.True(JsonNode.DeepEquals(before.StructuredOutputSchema, after.StructuredOutputSchema));
        state.Discovery.Pages = [page with { Capabilities = page.Capabilities.Take(1).ToList() }];
        for (var index = 1; index <= 5; index++)
            state.Catalog!.Capabilities.Add(new() { Id = "registered_" + index, Version = "v", Kind = "registered", StepType = "mcp.call",
                Server = "runtime", Method = "registered_" + index, Description = "Read exact original records with declared restrictions.",
                InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
                OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } });
        Assert.Null(Context(PlanningConstruction.DraftRequest(state, false))["companionOperations"]);
        // Four canonical registered rows can precede the only current-query companion.
        var tail = page.Capabilities[1] with { Id = "zzzz", Name = "zzzz", Description = "Unrelated ranking words.", Operation = new() { Id = "zzzz", Version = "v", Description = "Unrelated ranking words." } };
        state.Discovery.Pages[0] = state.Discovery.Pages[0] with { Capabilities = [page.Capabilities[0], tail] };
        var canonical = PlanningCompactConstruction.ContractDescriptorPage(state);
        Assert.DoesNotContain(canonical["items"]!.AsArray(), item => item!["id"]!.ToString() == "zzzz");
        var navigation = Context(PlanningConstruction.DraftRequest(state, false))["companionOperations"]!;
        Assert.Empty(navigation["items"]!.AsArray());
        Assert.NotNull(navigation["directory"]);
        Assert.NotNull(navigation["next"]);
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, navigation["next"]!.ToString(), out var remaining));
        Assert.Contains(remaining!["items"]!.AsArray(), item => item!["id"]!.ToString() == "zzzz");
        Assert.True(JsonNode.DeepEquals(canonical, PlanningCompactConstruction.ContractDescriptorPage(state)));
    }

    [Fact]
    public async Task OversizedCompanionRowsAreOmittedWholeBehindTheExactCanonicalDirectory()
    {
        var (state, _) = await BoundOperations(2);
        var page = state.Discovery.Pages.Single();
        state.Discovery.Pages[0] = page with { Capabilities = [page.Capabilities[0], page.Capabilities[1] with { Name = new string('x', 5000) }] };
        var canonical = PlanningCompactConstruction.ContractDescriptorPage(state);
        var navigation = Context(PlanningConstruction.DraftRequest(state, false))["companionOperations"]!;
        Assert.Empty(navigation["items"]!.AsArray());
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(navigation.ToJsonString()), 1, 2048);
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, navigation["directory"]!.ToString(), out var original));
        Assert.True(JsonNode.DeepEquals(canonical, original));
        Assert.Contains(original!["items"]!.AsArray(), item => item!["name"]!.ToString().Length == 5000);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> BoundOperations(int count)
    {
        var (state, runtime) = await Ready();
        AddOperations(state, count);
        var summary = state.Discovery.Pages[0].Capabilities[0];
        state.Discovery.Resolved = [new() { Id = summary.Id, Version = summary.Version, Kind = "tool", StepType = summary.StepType,
            Server = summary.SourceId, Method = summary.Name, Description = summary.Description, Operation = summary.Operation,
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } }];
        state.Catalog!.Capabilities.Add(state.Discovery.Resolved[0]);
        state.Construction!.Compact!.CurrentStep!.Kind = "operation";
        state.Construction!.Compact!.CurrentStep.OperationIds = [summary.Operation!.Id];
        state.Construction!.Compact!.CurrentStep.DiscoveryComplete = true;
        state.Construction!.Compact!.CurrentStep.ExpectedOutputs = ["An assumed implementation handle."];
        return (state, runtime);
    }

    [Fact]
    public async Task AutomaticOperationDirectoriesUseOnlyTheCurrentQueryWithoutDiscardingOriginals()
    {
        var (state, _) = await Ready();
        AddOperations(state, 3);
        var page = state.Discovery.Pages.Single();
        var current = page.Capabilities[0];
        var previous = page.Capabilities[1];
        var artifact = page.Capabilities[2];
        state.Discovery.Pages = [page with { Capabilities = [current] },
            page with { Query = "A different immediate result.", Capabilities = [previous] },
            page with { ProducedArtifactKind = "document", Capabilities = [artifact] }];
        state.Discovery.Resolved.Add(new() { Id = previous.Id, Version = previous.Version, Kind = "tool", StepType = previous.StepType,
            Server = previous.SourceId, Method = previous.Name, Description = previous.Description, Operation = previous.Operation,
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } });
        var originals = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var items = PlanningCompactConstruction.ContractDescriptorPage(state)["items"]!.AsArray();
        Assert.Single(items);
        Assert.Contains(items, item => item!["id"]!.ToString() == current.Operation!.Id);
        var address = "description:" + Uri.EscapeDataString(previous.Operation!.Id) + "@" + Uri.EscapeDataString(previous.Operation.Version) + ":0";
        Assert.Equal(previous.Operation.Description, PlanningCompactConstruction.LoadContractEvidence(state, address)!["text"]!.ToString());
        Assert.Equal(previous.Operation.Id, PlanningCompactConstruction.LoadContractEvidence(state, "contract:" + address[12..^2])!["id"]!.ToString());
        Assert.Equal(originals, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        // Rediscovery for the selected query can legitimately contain a previously cached operation.
        state.Discovery.Pages.Add(page with { Cursor = "current-next", Capabilities = [previous] });
        Assert.Contains(PlanningCompactConstruction.ContractDescriptorPage(state)["items"]!.AsArray(),
            item => item!["id"]!.ToString() == previous.Operation.Id);
    }

    [Fact]
    public async Task UnrelatedQueryHistoryOnlyAddsBoundedRetrievableCacheNavigation()
    {
        var (state, _) = await Ready();
        AddOperations(state, 1);
        var before = Measure(state);
        var page = state.Discovery.Pages.Single();
        var capability = page.Capabilities.Single();
        for (var index = 0; index < 250; index++)
            state.Discovery.Pages.Add(page with { Query = "Earlier query " + index,
                Capabilities = [capability with { Id = "earlier_" + index,
                    Operation = new() { Id = "earlier_operation_" + index, Version = capability.Version, Description = new string('x', 20000) } }] });
        var after = Measure(state);
        Assert.InRange(after.CompleteRequestBytes - before.CompleteRequestBytes, 1, 4096);
        Assert.InRange(after.EstimatedInputTokens, 1, 6000);
        var cached = PlanningCompactConstruction.DiscoveryContext(state)["describedOperations"]!;
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(cached.ToJsonString()), 1, 2048);
        Assert.InRange(cached["items"]!.AsArray().Count, 1, 4);
        Assert.NotNull(cached["next"]);
        Assert.DoesNotContain(new string('x', 20000), DiscoveryFollowup(state).Prompt, StringComparison.Ordinal);
        Assert.Equal(251, state.Discovery.Pages.Count);
        Assert.Single(PlanningCompactConstruction.ContractDescriptorPage(state)["items"]!.AsArray());
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedProviderViewsAuthorizeEveryVisibleExactPairWithoutCrossProducts(bool descriptions)
    {
        var (state, _) = await Ready();
        state.Discovery.Sources = Enumerable.Range(0, 5).Select(index => new CapabilitySource("source_" + index, "Original provider restrictions.")).ToList();
        foreach (var source in state.Discovery.Sources)
            state.Discovery.Pages.Add(new(source.Id, null, [], source.Id + "_next", Query: Objective));
        var calls = state.ModelCalls;
        foreach (var offset in new[] { 0, 2 })
        {
            var address = descriptions
                ? PlanningCompactConstruction.ProviderRequestPage(state, offset)["items"]![0]!["descriptionEvidence"]!.ToString()
                : PlanningCompactConstruction.ProviderRequestAddress(state, offset);
            PlanningConstruction.LoadEvidence(state, [address], followup: DiscoveryFollowup, purpose: PlanningCompactConstruction.DiscoveryPurpose);
        }
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.Equal(2, state.Construction!.Evidence!.Count);
        var visible = descriptions ? new[] { 0, 2 } : new[] { 0, 1, 2, 3 };
        foreach (var index in visible)
        {
            Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("source_" + index, "source_" + index + "_next"), schema));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("source_" + index, "source_4_next"), schema));
        }
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("source_4", "source_4_next"), schema));
        Assert.InRange(PlanningRequestMeasurements.Measure(DiscoveryFollowup(state), PlanningCompactConstruction.DiscoveryPurpose, 6000).EstimatedInputTokens, 1, 6000);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

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

        Assert.Contains("declared arithmetic", PlanningCompactConstruction.ImplementationGuidance(state), StringComparison.Ordinal);

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
        var request = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state), context, schema, PlanningCompactConstruction.DiscoveryPurpose));
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, 3000).EstimatedInputTokens, 1, 6000);
        Assert.Equal(original, state.Discovery.Sources[0].Description);
        state.Discovery.Pages.Add(new("source_2", null, [], "next", Query: Objective));
        state.Construction!.Evidence = null; state.Construction!.EvidenceRequests.Clear();
        var consumed = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address]));
        Assert.Contains(consumed.Diagnostics, d => d.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
        var next = PlanningCompactConstruction.ProviderRequestAddress(state, 2);
        state.Discovery.Sources[2] = state.Discovery.Sources[2] with { Description = "Revised source restrictions." };
        var changed = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [next]));
        Assert.Contains(changed.Diagnostics, d => d.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
    }


    [Fact]
    public async Task ConsumedProviderPagesAdvanceWithoutGrantingOldVersionRereads()
    {
        var (state, _) = await Ready();
        state.Discovery.Sources.AddRange([new("beta", "Another provider."), new("gamma", "Last provider.")]);
        var first = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        var last = PlanningCompactConstruction.ProviderRequestAddress(state, 2);
        PlanningConstruction.LoadEvidence(state, [first]);
        Assert.Equal(last, PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["providers"]!.ToString());
        PlanningConstruction.LoadEvidence(state, [last]);
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["providers"]);
        state.Discovery.Pages.Add(new("alpha", null, [], "more", Query: Objective));
        var changed = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        Assert.NotEqual(first, changed);
        Assert.Equal(changed, PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["providers"]!.ToString());
        var repeated = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [first]));
        Assert.Contains(repeated.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [first]), state.Construction!.EvidenceRequests);
        Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [last]), state.Construction!.EvidenceRequests);
        Assert.InRange(Measure(state).EstimatedInputTokens, 1, 6000);
    }

    [Fact]
    public async Task RetainedMultiAddressEvidenceDoesNotReofferItsCurrentDirectoryPages()
    {
        var (state, _) = await Ready();
        state.Discovery.Sources.AddRange([new("beta", "Another provider."), new("gamma", "Last provider.")]);
        var provider = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        PlanningConstruction.LoadEvidence(state, ["page:sources:0", provider]);
        Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, ["page:sources:0"]), state.Construction!.EvidenceRequests);
        Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [provider]), state.Construction!.EvidenceRequests);
        var evidence = state.Construction!.Evidence!.ToJsonString();
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.Equal("page:sources:2", context["directoryEvidence"]!["sources"]!.ToString());
        Assert.Equal(PlanningCompactConstruction.ProviderRequestAddress(state, 2), context["directoryEvidence"]!["providers"]!.ToString());
        Assert.Equal(evidence, context["evidence"]!.ToJsonString());
        Assert.Equal(evidence, state.Construction!.Evidence.ToJsonString());
    }

    [Fact]
    public async Task FreshProviderPageKeepsBoundedDecisionIdentitiesWithoutReopeningConsumedEvidence()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 8);
        state.Discovery.Pages[0] = state.Discovery.Pages[0] with { NextCursor = "more" };
        var initial = PlanningCompactConstruction.ContractDescriptorPage(state).ToJsonString();
        var firstAddress = PlanningCompactConstruction.OperationPageAddress(state, 0);
        var secondAddress = PlanningCompactConstruction.OperationPageAddress(state, 4);
        var description = DescriptionAddress(state);
        var accepted = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, _) => Wire(request, Evidence(secondAddress));
        state = await Advance(state, runtime);
        Assert.NotNull(PlanningCompactConstruction.DiscoveryContext(state)["descriptors"]);
        foreach (var address in new[] { "page:sources:0", PlanningCompactConstruction.ProviderRequestAddress(state, 0) })
        {
            runtime.Respond = (request, _) => Wire(request, Evidence(address));
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
            Assert.NotNull(PlanningCompactConstruction.DiscoveryContext(state)["descriptors"]);
            Assert.True(state.Construction!.Evidence!.ContainsKey(secondAddress));
            Assert.True(state.Construction!.Evidence.ContainsKey(address));
        }
        runtime.Capabilities = new ContinuationCatalog([AdditionalOperation(state)]);
        runtime.Respond = (request, _) => Wire(request, Discover("alpha", "more"));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, state.Construction!.Evidence!.Count);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.NotNull(context["descriptors"]);
        Assert.Equal(4, context["evidence"]![secondAddress]!["items"]!.AsArray().Count);
        Assert.Equal(initial, PlanningCompactConstruction.ContractDescriptorPage(state).ToJsonString());
        Assert.Null(context["directoryEvidence"]!["sources"]);
        Assert.Equal(PlanningCompactConstruction.OperationPageAddress(state, 8), context["directoryEvidence"]!["operations"]!.ToString());
        Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [firstAddress]), state.Construction!.EvidenceRequests);
        Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [secondAddress]), state.Construction!.EvidenceRequests);
        Assert.DoesNotContain("Read exact original records", context["evidence"]![secondAddress]!.ToJsonString(), StringComparison.Ordinal);
        runtime.Respond = (request, _) => Wire(request, Evidence(description));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("Read exact original records with declared restrictions.", state.Construction!.Evidence![description]!["text"]!.ToString());
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(0, state.ReplanAttempts);
        Assert.All(runtime.Calls.Where(call => call.StructuredOutputSchema?["title"]?.ToString() == PlanningCompactConstruction.DiscoveryPurpose),
            call => Assert.InRange(PlanningRequestMeasurements.Measure(call, PlanningCompactConstruction.DiscoveryPurpose, 3000).EstimatedInputTokens, 1, 6000));
        var beforeRepeatedRead = state.Construction!.Evidence!.ToJsonString();
        var callsBeforeRepeatedRead = state.ModelCalls;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Evidence(description), PlanningCompactConstruction.DiscoverySchema(state)));
        var repeated = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [description]));
        Assert.Contains(repeated.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(beforeRepeatedRead, state.Construction.Evidence.ToJsonString());
        Assert.Equal(callsBeforeRepeatedRead, state.ModelCalls);
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task ProviderPageThenExactDescriptionCanDiscoverWithoutRepeatingEvidence()
    {
        var (state, runtime) = await Ready();
        const string selected = "source:two/@ exact";
        state.Discovery.Sources = [new("first", "First provider."), new("second", "Second provider."),
            new(selected, new string('x', 3000) + " Preserve complete observations."), new("last", "Last provider.")];
        state.Discovery.Pages.Add(new(selected, null, [], "selected-next", Query: Objective));
        state.Discovery.Pages.Add(new(selected, null, [], "unrelated-next", Query: "A different business substep."));
        var plan = PlanningConstruction.Hash(state.Plan);
        var calls = state.ModelCalls;
        var pageAddress = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        runtime.Respond = (request, _) => Wire(request, Evidence(pageAddress));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var descriptionAddress = PlanningCompactConstruction.ProviderRequestPage(state, 2)["items"]![0]!["descriptionEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [descriptionAddress]);
        var exactDescription = state.Construction!.Evidence![descriptionAddress]!.ToJsonString();
        var catalog = new CountingContinuationCatalog(); runtime.Capabilities = catalog;
        runtime.Respond = (request, _) => Wire(request, Discover(selected, "selected-next"));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(Objective, Assert.Single(state.Discovery.Pages, page => page.Cursor == "selected-next").Query);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan)); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(exactDescription, state.Construction!.Evidence![descriptionAddress]!.ToJsonString());
        return;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderReceiptPreservesOriginalDescriptionAndExpiresOnlyItsOldActionDirectory(bool interrupted)
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 1);
        state.Discovery.Pages[0] = state.Discovery.Pages[0] with { NextCursor = "more" };
        var description = PlanningCompactConstruction.ContractDescriptorPage(state)["items"]![0]!["descriptionEvidence"]!.ToString();
        var provider = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        runtime.Respond = (request, _) => Wire(request, Evidence(provider, description));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var original = state.Construction!.Evidence![description]!.ToJsonString();
        var consumed = state.Construction!.EvidenceRequests.ToArray();
        var plan = PlanningConstruction.Hash(state.Plan);
        var calls = state.ModelCalls;
        var catalog = new CountingContinuationCatalog();
        runtime.Capabilities = catalog;
        string? issued = null;
        if (interrupted)
        {
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(PlannerFixture.Ct);
            runtime.Respond = (_, _) => { interruption.Cancel(); throw new OperationCanceledException(interruption.Token); };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
                new() { ExpectedRevision = state.Revision }, runtime, interruption.Token));
            state = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
            issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        }
        runtime.Respond = (request, _) =>
        {
            if (issued is not null) Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(JsonNode.Parse(original)!["text"]!.ToString(), Context(request)["evidence"]![runtime.EvidenceId(request, description)]!["text"]!.ToString());
            return Wire(request, Discover("alpha", "more"));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(1, catalog.ListCalls);
        Assert.Equal(original, state.Construction!.Evidence![description]!.ToJsonString());
        Assert.False(state.Construction!.Evidence.ContainsKey(provider));
        Assert.All(consumed, fingerprint => Assert.Contains(fingerprint, state.Construction!.EvidenceRequests));
        Assert.Equal(2, state.Discovery.Pages.Count);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(0, state.ReplanAttempts);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.Equal(JsonNode.Parse(original)!["text"]!.ToString(), context["evidence"]![description]!["text"]!.ToString());
        Assert.NotNull(context["describedOperations"]);
        Assert.False(Assert.Single(context["providerRequests"]!["items"]!.AsArray())!["canRequestPage"]!.GetValue<bool>());
        Assert.Contains(state.RequestMeasurements!, measurement => measurement.Phase == PlanningCompactConstruction.DiscoveryPurpose &&
            measurement.Admission == "prepared" && measurement.EstimatedInputTokens <= 6000);
        runtime.Respond = (request, _) => Wire(request, Evidence(description));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.NotEqual(PlanningStatus.FinalReview, state.Status);
        Assert.Contains(state.Diagnostics, finding => finding.Code is "REPLAN_NO_PROGRESS" or "PLANNING_RESPONSE_INVALID");
        Assert.Equal(1, catalog.ListCalls);
        Assert.Equal(original, state.Construction!.Evidence![description]!.ToJsonString());
    }

    [Fact]
    public async Task ProviderAdmissionFailureKeepsOriginalEvidenceAndTheCompletedMetadataRead()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 1);
        state.Discovery.Pages[0] = state.Discovery.Pages[0] with { NextCursor = "more" };
        var description = PlanningCompactConstruction.ContractDescriptorPage(state)["items"]![0]!["descriptionEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [description]);
        var original = state.Construction!.Evidence!.ToJsonString();
        var catalog = new CountingContinuationCatalog(new string('c', 25000));
        runtime.Capabilities = catalog;
        runtime.Respond = (request, _) => Wire(request, Discover("alpha", "more"));
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, finding => finding.Code == "DISCOVERY_NO_PROGRESS");
        Assert.InRange(catalog.ListCalls, 1, 4);
        Assert.InRange(state.Discovery.Pages.Count, 1, 4);
        Assert.Equal(original, state.Construction!.Evidence!.ToJsonString());
        Assert.Null(state.PendingCall);
        Assert.Empty(state.Construction!.Checkpoints);
        var calls = runtime.Calls.Count;
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(calls, runtime.Calls.Count);
        Assert.InRange(catalog.ListCalls, 1, 4);
        Assert.InRange(state.Discovery.Pages.Count, 1, 4);
        Assert.Equal(original, state.Construction!.Evidence!.ToJsonString());
    }

    [Fact]
    public async Task DescribedProviderAlternativesRemainPagedAndConsumedCursorsDisappear()
    {
        var (state, _) = await Ready();
        state.Discovery.Pages = [new("alpha", null, [], "first", Query: Objective),
            new("alpha", "seed-2", [], "second", Query: Objective), new("alpha", "seed-3", [], "third", Query: Objective)];
        var address = PlanningCompactConstruction.ProviderRequestPage(state, 0)["items"]![0]!["descriptionEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        var described = PlanningCompactConstruction.DiscoveryContext(state)["providerRequests"]!;
        Assert.Equal(3, described["total"]!.GetValue<int>());
        Assert.Equal(2, described["items"]!.AsArray().Count);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("alpha", "first"), PlanningCompactConstruction.DiscoverySchema(state)));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("alpha", "third"), PlanningCompactConstruction.DiscoverySchema(state)));
        var next = described["next"]!.ToString();
        var originalEvidence = state.Construction!.Evidence!.DeepClone().AsObject();
        PlanningConstruction.LoadEvidence(state, [next]);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("alpha", "third"), PlanningCompactConstruction.DiscoverySchema(state)));
        state.Construction!.Evidence = originalEvidence;
        state.Discovery.Pages.Add(new("alpha", "first", [], null, Query: Objective));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("alpha", "first"), PlanningCompactConstruction.DiscoverySchema(state)));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("alpha", "third"), PlanningCompactConstruction.DiscoverySchema(state)));
        state.Discovery.Pages.Add(new("alpha", "second", [], null, Query: Objective));
        state.Discovery.Pages.Add(new("alpha", "third", [], null, Query: Objective));
        Assert.False(PlanningCompactConstruction.DiscoveryContext(state)["providerRequests"]!["items"]![0]!["canRequestPage"]!.GetValue<bool>());
        Assert.Equal("null", PlanningCompactConstruction.DiscoverySchema(state)["properties"]!["discoveryRequests"]!["type"]!.ToString());
    }


    [Theory]
    [InlineData("a")]
    [InlineData("界")]
    [InlineData("😀")]
    public async Task DescribedProviderCompleteRequestsKeepOriginalPagesAndCapturedInputLimit(string scalar)
    {
        var (state, _) = await Ready();
        var original = string.Concat(Enumerable.Repeat(scalar, 2100));
        state.Discovery.Sources = [new("alpha", original)];
        var address = PlanningCompactConstruction.ProviderRequestPage(state, 0)["items"]![0]!["descriptionEvidence"]!.ToString();
        var before = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        PlanningConstruction.LoadEvidence(state, [address], followup: DiscoveryFollowup, purpose: PlanningCompactConstruction.DiscoveryPurpose);
        var pages = state.Construction!.Evidence!.Select(pair => pair.Value).OfType<JsonObject>().Where(page => page["text"] is not null).OrderBy(page => page["offset"]!.GetValue<int>()).ToArray();
        Assert.NotEmpty(pages); var text = string.Concat(pages.Select(page => page["text"]!.ToString()));
        Assert.StartsWith(text, original, StringComparison.Ordinal);
        Assert.InRange(Measure(state).EstimatedInputTokens, 1, 6000);
        Assert.Equal(before, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Empty(state.Discovery.Resolved); Assert.Empty(state.Construction.Checkpoints);
    }


    [Fact]
    public async Task OperationNavigationKeepsProgressAcrossDescriptionDrilldownAndStopsRepeatedPages()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 12);
        var plan = PlanningConstruction.Hash(state.Plan);
        var initial = PlanningCompactConstruction.DiscoveryContext(state);
        var expected = Enumerable.Range(0, 3).SelectMany(index =>
            PlanningCompactConstruction.ContractDescriptorPage(state, index * 4)["items"]!.AsArray()
                .Select(item => item!["id"]!.ToString() + "@" + item["version"]!.ToString())).ToArray();
        var presented = initial["descriptors"]!["items"]!.AsArray()
            .Select(item => item!["id"]!.ToString() + "@" + item["version"]!.ToString()).ToList();
        var secondAddress = PlanningCompactConstruction.OperationPageAddress(state, 4);
        var thirdAddress = PlanningCompactConstruction.OperationPageAddress(state, 8);
        Assert.Equal(secondAddress, initial["directoryEvidence"]!["operations"]!.ToString());
        runtime.Respond = (request, _) => Wire(request, Evidence(Context(request)["directoryEvidence"]!["operations"]!.ToString()));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var page = state.Construction!.Evidence![secondAddress]!;
        presented.AddRange(page["items"]!.AsArray().Select(item => item!["id"]!.ToString() + "@" + item["version"]!.ToString()));
        var description = page["items"]![0]!["descriptionEvidence"]!.ToString();
        runtime.Respond = (request, _) => Wire(request, Evidence(description));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, state.Construction!.Evidence!.Count);
        Assert.Equal(thirdAddress, PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]!.ToString());
        runtime.Respond = (request, _) => Wire(request, Evidence(Context(request)["directoryEvidence"]!["operations"]!.ToString()));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        page = state.Construction!.Evidence![thirdAddress]!;
        presented.AddRange(page["items"]!.AsArray().Select(item => item!["id"]!.ToString() + "@" + item["version"]!.ToString()));
        Assert.Equal(expected, presented);
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]);
        {
            var firstPage = PlanningCompactConstruction.DiscoveryContext(state)["descriptors"]!;
            Assert.True(JsonNode.DeepEquals(initial["descriptors"], firstPage));
            Assert.Equal(4, firstPage["items"]!.AsArray().Count);
            Assert.All(firstPage["items"]!.AsArray(), item => Assert.Null(item!["description"]));
            var firstAddress = PlanningCompactConstruction.OperationPageAddress(state, 0);
            Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [firstAddress]), state.Construction!.EvidenceRequests);
            var duplicate = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(PlannerFixture.Clone(state), [firstAddress]));
            Assert.Contains(duplicate.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
            var exactDescription = firstPage["items"]![0]!["descriptionEvidence"]!.ToString();
            runtime.Respond = (request, _) => Wire(request, Evidence(exactDescription));
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
            Assert.Equal("Read exact original records with declared restrictions.", state.Construction!.Evidence![exactDescription]!["text"]!.ToString());
            var measurement = PlanningRequestMeasurements.Measure(DiscoveryFollowup(state), PlanningCompactConstruction.DiscoveryPurpose, 6000);
            Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(component => component.SerializedBytes));
            Assert.InRange(measurement.EstimatedInputTokens, 1, 6000);
        }

        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.All(runtime.Calls.Where(call => call.StructuredOutputSchema?["title"]?.ToString() == PlanningCompactConstruction.DiscoveryPurpose),
            call => Assert.InRange(PlanningRequestMeasurements.Measure(call, PlanningCompactConstruction.DiscoveryPurpose, 6000).EstimatedInputTokens, 1, 6000));
        runtime.Respond = (request, _) => Wire(request, Evidence(secondAddress));
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code ==
            "PLANNING_RESPONSE_INVALID");
        Assert.Empty(state.Construction!.Checkpoints);
    }

    [Fact]
    public async Task RetainedFirstOperationPageIsNotDuplicatedInContext()
    {
        var (state, _) = await Ready();
        AddOperations(state, 8);
        var first = PlanningCompactConstruction.OperationPageAddress(state, 0);
        var page = PlanningCompactConstruction.ContractDescriptorPage(state).ToJsonString();
        PlanningConstruction.LoadEvidence(state, [first], followup: DiscoveryFollowup, purpose: PlanningCompactConstruction.DiscoveryPurpose);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.Null(context["descriptors"]);
        Assert.Equal(4, context["evidence"]![first]!["items"]!.AsArray().Count);
        Assert.Equal(first, PlanningCompactConstruction.OperationPageAddress(state, 0));
    }

    [Fact]
    public async Task IssuedDiscoveryWithOmittedFirstPageResumesExactBytesAndAccounting()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 8);
        runtime.Respond = (request, _) => Wire(request, Evidence(PlanningCompactConstruction.OperationPageAddress(state, 4)));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var description = DescriptionAddress(state);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        context["descriptors"] = null;
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state), context,
            PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose);
        request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject(), null, null, out var evidenceAddresses);
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = request, EvidenceAddresses = evidenceAddresses };
        state.ModelCalls++;
        var calls = state.ModelCalls;
        var measurements = state.RequestMeasurements!.ToDictionary(measurement => measurement.RequestHash,
            measurement => JsonSerializer.Serialize(measurement, PlanningJsonContext.Default.PlanningRequestMeasurement));
        var receipt = Wire(request, Evidence(description), evidenceAddresses);
        var recovery = new TestRuntime { Respond = (issued, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            return receipt;
        } };
        recovery.CaptureIssued(state.PendingCall);
        state = await Advance(PlannerFixture.Clone(state), recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Single(recovery.Calls);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        foreach (var (hash, measurement) in measurements)
            Assert.Equal(measurement, JsonSerializer.Serialize(Assert.Single(state.RequestMeasurements!, current => current.RequestHash == hash),
                PlanningJsonContext.Default.PlanningRequestMeasurement));
        Assert.Null(state.PendingCall);
        Assert.Equal("Read exact original records with declared restrictions.", state.Construction!.Evidence![description]!["text"]!.ToString());
        Assert.NotNull(PlanningCompactConstruction.DiscoveryContext(state)["descriptors"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OperationNavigationDoesNotInventAnUnseenFirstPageOrAContinuation(int count)
    {
        var (state, _) = await Ready();
        AddOperations(state, count);
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]);
        PlanningConstruction.LoadEvidence(state, ["source:request:0:clause:0"]);
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]);
        Assert.DoesNotContain(PlanningConstruction.EvidenceBatchFingerprint(state, [PlanningCompactConstruction.OperationPageAddress(state, 0)]), state.Construction!.EvidenceRequests);
    }

    [Theory]
    [InlineData("CONSTRUCTION_EVIDENCE_UNKNOWN")]
    [InlineData("CONSTRUCTION_EVIDENCE_INVALID")]
    public async Task CorrectedEvidenceReceiptResumesExactlyWithItsCapturedCorrectionAccounting(string code)
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 8);
        state.Request.MaxReplanAttempts = 1;
        state.Diagnostics = [new(code, "/evidenceRequests", "Correct the rejected navigation address.")];
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint =>
            checkpoint.Status == PlanningStatus.Generating &&
            checkpoint.PendingCall?.Purpose == PlanningCompactConstruction.DiscoveryPurpose));
        Assert.Equal(0, saved.ReplanAttempts);
        var issued = saved.PendingCall!.Request;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls;
        var pageKey = PlanningConstruction.EvidenceBatchFingerprint(saved, [PlanningCompactConstruction.OperationPageAddress(saved, 0)]);
        Assert.DoesNotContain(pageKey, saved.Construction!.EvidenceRequests);
        var description = Context(issued)["descriptors"]!["items"]![0]!["descriptionEvidence"]!.ToString();
        var receipt = Wire(issued, Evidence(description));
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return receipt;
        } };
        saved.Status = PlanningStatus.Generating;
        state = await Advance(saved, recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(state.Construction!.EvidenceRequests, key => key == pageKey);
        var nextAddress = PlanningCompactConstruction.OperationPageAddress(state, 4);
        Assert.Equal(nextAddress, PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]!.ToString());
        recovery.Respond = (request, _) => Wire(request, Evidence(nextAddress));
        state = await Advance(PlannerFixture.Clone(state), recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]);
    }

    [Fact]
    public async Task MalformedDescriptionAddressRecoversThroughEvidenceWithoutChargingCorrection()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 4);
        state.Discovery.Pages[0] = state.Discovery.Pages[0] with { NextCursor = "more" };
        state.Request.MaxReplanAttempts = 1;
        var plan = PlanningConstruction.Hash(state.Plan);
        var calls = state.ModelCalls;
        var description = DescriptionAddress(state);
        runtime.Respond = (request, _) => Wire(request, Evidence(description + " Additional explanation."));
        state = await Advance(state, runtime);
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal("PLANNING_RESPONSE_INVALID", diagnostic.Code);
        Assert.Equal("/evidenceRequests/0", diagnostic.Location);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(state.Discovery.Resolved);
        Assert.DoesNotContain(PlanningConstruction.EvidenceBatchFingerprint(state, [description]), state.Construction!.EvidenceRequests);

        runtime.Respond = (request, _) => Wire(request, Evidence(description));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal("Read exact original records with declared restrictions.", state.Construction!.Evidence![description]!["text"]!.ToString());

        var providerPage = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        runtime.Respond = (request, _) => Wire(request, Evidence(providerPage));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(0, state.ReplanAttempts);
        runtime.Respond = (request, _) =>
        {
            Assert.Null(Context(request)["diagnostics"]);
            Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, 3000).EstimatedInputTokens, 1, 6000);
            return Wire(request, Discover("alpha", "more"));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(calls + 4, state.ModelCalls);
        Assert.Single(state.Discovery.Pages, page => page.Cursor == "more" && page.Query == Objective);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Discovery.Resolved);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Null(state.Construction!.Candidate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("0:extra")]
    public async Task InvalidDescriptionOffsetFailsBeforeUnresolvedContractLookup(string offset)
    {
        var (state, _) = await Ready();
        AddOperations(state, 1);
        var address = DescriptionAddress(state);
        var invalid = Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.LoadContractEvidence(state, address[..(address.LastIndexOf(':') + 1)] + offset));
        var diagnostic = Assert.Single(invalid.Diagnostics);
        Assert.Equal("CONSTRUCTION_EVIDENCE_INVALID", diagnostic.Code);
        Assert.Equal("/evidenceRequests", diagnostic.Location);
        var unresolved = Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.LoadContractEvidence(state, "contract:" + address["description:".Length..address.LastIndexOf(':')]));
        Assert.Contains(unresolved.Diagnostics, finding => finding.Code == "CONSTRUCTION_CONTRACT_UNAVAILABLE" &&
            finding.Location == "/construction/compact/currentStep/operationIds");
    }

    [Fact]
    public async Task SuccessfulEvidenceClearsResolvedNavigationDiagnostics()
    {
        var (state, runtime) = await Ready();
        state.Diagnostics = [new("CONSTRUCTION_EVIDENCE_UNKNOWN", "/evidenceRequests", "Resolved address."),
            new("CONSTRUCTION_EVIDENCE_LIMIT", "/evidenceRequests", "Previous batch was too large.")];
        runtime.Respond = (request, _) => Wire(request, Evidence("source:request:0:clause:0"));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(0, state.ReplanAttempts); Assert.Empty(state.Construction!.Checkpoints);
    }


    [Theory]
    [InlineData("version")]
    [InlineData("next")]
    public async Task NoncanonicalDisplayedOperationPagesCannotEstablishNavigationProgress(string change)
    {
        var (state, _) = await Ready(); AddOperations(state, 8);
        var first = PlanningCompactConstruction.OperationPageAddress(state, 0);
        var before = PlanningCompactConstruction.DiscoverySchema(state).ToJsonString();
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, first, out var page));
        if (change == "version") page!["version"] = "forged"; else page!["next"] = "page:operations:forged:8";
        state.Construction!.Evidence = new() { [first] = page };
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, ["page:operations:forged:8"]));
        Assert.False(PlanningCompactConstruction.OperationPageAddress(state, 4).Contains("forged", StringComparison.Ordinal));
    }


    [Fact]
    public async Task ProviderExpansionPreservesConsumedContentAndLinksOnlyTheNewOperationPage()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 8);
        state.Discovery.Pages[0] = state.Discovery.Pages[0] with { NextCursor = "more" };
        var firstAddress = PlanningCompactConstruction.OperationPageAddress(state, 0);
        var secondAddress = PlanningCompactConstruction.OperationPageAddress(state, 4);
        var originalSecond = PlanningCompactConstruction.ContractDescriptorPage(state, 4).ToJsonString();
        var plan = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, _) => Wire(request, Evidence(secondAddress));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]);
        var provider = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        runtime.Respond = (request, _) => Wire(request, Evidence(provider));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        var expanded = PlannerFixture.Clone(state);
        AddOperations(expanded, 9);
        var additional = expanded.Discovery.Pages[0].Capabilities.Single(capability => capability.Id == "operation_8");
        runtime.Capabilities = new ContinuationCatalog([additional]);
        runtime.Respond = (request, _) => Wire(request, Discover("alpha", "more"));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, state.Discovery.Pages.Count);
        Assert.Equal(firstAddress, PlanningCompactConstruction.OperationPageAddress(state, 0));
        Assert.Equal(secondAddress, PlanningCompactConstruction.OperationPageAddress(state, 4));
        Assert.Equal(originalSecond, PlanningCompactConstruction.ContractDescriptorPage(state, 4).ToJsonString());
        Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [firstAddress]), state.Construction!.EvidenceRequests);
        Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [secondAddress]), state.Construction!.EvidenceRequests);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.NotNull(context["descriptors"]);
        Assert.Equal(JsonNode.Parse(originalSecond)!["items"]!.AsArray().Select(item => item!["id"]!.ToString()), context["evidence"]![secondAddress]!["items"]!.AsArray().Select(item => item!["id"]!.ToString()));
        Assert.False(state.Construction!.Evidence!.ContainsKey(provider));
        var next = PlanningCompactConstruction.OperationPageAddress(state, 8);
        Assert.Equal(next, context["directoryEvidence"]!["operations"]!.ToString());
        runtime.Respond = (request, _) => Wire(request, Evidence(next));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.NotNull(context["descriptors"]);
        var page = context["evidence"]![next]!;
        var shown = Assert.Single(page["items"]!.AsArray())!;
        Assert.Equal(additional.Id, shown["id"]!.ToString());
        Assert.Equal(8, page["offset"]!.GetValue<int>());
        Assert.Null(page["next"]);
        Assert.Null(context["directoryEvidence"]!["operations"]);
        Assert.Null(shown["description"]);
        Assert.True(page["descriptionsOmitted"]!.GetValue<bool>());
        runtime.Respond = (request, _) => Wire(request, Evidence(shown["descriptionEvidence"]!.ToString()));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.All(runtime.Calls.Where(call => call.StructuredOutputSchema?["title"]?.ToString() == PlanningCompactConstruction.DiscoveryPurpose),
            call => Assert.InRange(PlanningRequestMeasurements.Measure(call, PlanningCompactConstruction.DiscoveryPurpose, 3000).EstimatedInputTokens, 1, 6000));
        var calls = state.ModelCalls;
        var retained = state.Construction!.Evidence!.ToJsonString();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Evidence(secondAddress), PlanningCompactConstruction.DiscoverySchema(state)));
        var repeated = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [secondAddress]));
        Assert.Contains(repeated.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(retained, state.Construction.Evidence.ToJsonString());
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task ChangedOperationContentGetsANewAddressAndStaleViewsRemainRejected()
    {
        var (state, _) = await Ready();
        AddOperations(state, 8);
        var original = PlanningCompactConstruction.OperationPageAddress(state, 4);
        var changed = state.Discovery.Pages[0].Capabilities[4];
        state.Discovery.Pages[0].Capabilities[4] = changed with { Name = "changed_operation_name" };
        var current = PlanningCompactConstruction.OperationPageAddress(state, 4);
        Assert.NotEqual(original, current);
        var before = state.Construction!.EvidenceRequests.ToArray();
        var rejected = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [original]));
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code is "CONSTRUCTION_EVIDENCE_UNKNOWN" or "PLANNING_RESPONSE_INVALID");
        Assert.Equal(before, state.Construction!.EvidenceRequests);
        Assert.Null(state.Construction!.Evidence);
        PlanningConstruction.LoadEvidence(state, [current]);
        Assert.True(JsonNode.DeepEquals(PlanningCompactConstruction.ContractDescriptorPage(state, 4), state.Construction!.Evidence![current]));
        Assert.Contains(Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [current])).Diagnostics,
            diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedProviderRemainsExplicitAndCannotBeFetchedAgain(bool unavailable)
    {
        var (state, runtime) = await Ready();
        if (unavailable)
            state.Discovery.Pages.Add(new("alpha", null, [], null, UnavailableReason: "The configured provider is unavailable.", Query: Objective));
        else AddOperations(state, 1);
        var plan = PlanningConstruction.Hash(state.Plan);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var provider = Assert.Single(context["providerRequests"]!["items"]!.AsArray())!;
        Assert.False(provider["canRequestPage"]!.GetValue<bool>());
        Assert.Null(provider["available"]);
        Assert.Equal(unavailable ? "The configured provider is unavailable." : null, provider["unavailable"]?.ToString());
        Assert.Equal(unavailable ? 0 : 1, provider["resultCount"]!.GetValue<int>());
        Assert.Equal("null", PlanningCompactConstruction.DiscoverySchema(state)["properties"]!["discoveryRequests"]!["type"]!.ToString());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("alpha", null), PlanningCompactConstruction.DiscoverySchema(state)));
        Assert.True(PlanningCompactConstruction.CanReselectDiscovery(state));
        if (!unavailable)
        {
            var address = context["descriptors"]!["items"]![0]!["descriptionEvidence"]!.ToString();
            runtime.Respond = (request, _) => Wire(request, Evidence(address));
            state = await Advance(state, runtime);
            Assert.Empty(state.Diagnostics);
            Assert.Equal(state.Discovery.Pages[0].Capabilities[0].Operation!.Description,
                state.Construction!.Evidence![address]!["text"]!.ToString());
            Assert.Single(state.Discovery.Pages);
            Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
            Assert.Empty(state.Construction!.Checkpoints);
            Assert.Equal(0, state.ReplanAttempts);
        }
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
        Assert.Null(state.Construction!.Compact!.CurrentStep); Assert.Null(state.Construction!.Candidate);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        var rejected = Assert.Single(state.Construction!.Compact!.RejectedSteps);
        Assert.NotNull(rejected["evidence"]![evidenceAddress]);
        Assert.Equal("/root", state.Construction!.Compact!.ReplacementScope);
        Assert.Equal(["req_0"], state.Construction!.Compact!.ReplacementRequirements);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(Reason, Context(request)["reselection"]!["reason"]!.ToString());
            return Wire(request, Implement(request));
        };
        state = await Advance(state, runtime);
        Assert.Equal(1, state.ReplanAttempts); Assert.Equal(1, state.Construction!.DraftCorrections);
        Assert.Equal("value", state.Construction!.Compact!.CurrentStep!.Kind);
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
        Assert.Single(resumed.Construction!.Compact!.RejectedSteps);
        var durable = PlannerFixture.Clone(resumed);
        recovery.Respond = (issued, _) => Wire(issued, Implement(issued));
        durable = await Advance(durable, recovery);
        Assert.Equal(1, durable.ReplanAttempts); Assert.Equal(1, durable.Construction!.DraftCorrections);
        Assert.Single(durable.Construction!.Compact!.RejectedSteps);
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
            case "draft": state.Construction!.DraftCorrections = state.Construction!.Limits.MaxCorrectionsPerDraft; break;
            case "policy": state.Request.Options["automatic_correction"] = false; break;
            case "revision": state.EditablePaths = ["/root/outputs/message"]; break;
        }
        Assert.False(PlanningCompactConstruction.CanReselectDiscovery(state));
        Assert.Equal("null", PlanningCompactConstruction.DiscoverySchema(state)["properties"]!["reselectStep"]!["type"]!.ToString());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Reselect(), PlanningCompactConstruction.DiscoverySchema(state)));
    }


    [Fact]
    public async Task SeparateDiscoveryEvidenceRequestsPreserveOriginalPagesWithinTheCompleteRequestLimit()
    {
        var (state, _) = await Ready(); AddOperations(state, 6);
        var directory = PlanningCompactConstruction.ContractDescriptorPage(state);
        var addresses = new[] { PlanningCompactConstruction.OperationPageAddress(state, 4), directory["items"]![0]!["descriptionEvidence"]!.ToString(), directory["items"]![1]!["descriptionEvidence"]!.ToString() };
        var originals = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        PlanningConstruction.LoadEvidence(state, [.. addresses], followup: DiscoveryFollowup, purpose: PlanningCompactConstruction.DiscoveryPurpose);
        Assert.All(addresses, address => Assert.NotNull(state.Construction!.Evidence![address]));
        Assert.InRange(Measure(state).EstimatedInputTokens, 1, 6000);
        Assert.Equal(originals, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [addresses[0]]));
        Assert.Empty(state.Discovery.Resolved); Assert.Empty(state.Construction!.Checkpoints);
    }


    [Fact]
    public async Task OneIndivisibleRequiredSchemaStillFailsAdmissionWithoutTruncationOrDispatch()
    {
        var (state, runtime) = await Ready();
        var contract = new PlanningCapability
        {
            Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "alpha", Method = "read",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
                { ["value"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enumerable.Range(0, 500)
                    .Select(index => (JsonNode?)JsonValue.Create(index + new string('x', 96))).ToArray()) } },
                ["required"] = new JsonArray("value"), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
        };
        state.Discovery.Resolved.Add(contract);
        state.Construction!.Compact!.CurrentStep!.OperationIds = [contract.Id];
        state.Construction.Compact.CurrentStep.Kind = null;
        runtime.Respond = (request, _) => Wire(request, new() { ["binding"] = new JsonObject { ["kind"] = "operation", ["operationIds"] = new JsonArray("read"), ["groupId"] = null, ["closeScopes"] = null },
            ["discoveryRequests"] = null, ["evidenceRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null });
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, finding => finding.Code is "CONSTRUCTION_EVIDENCE_LIMIT" or "MODEL_INPUT_LIMIT" or "CONSTRUCTION_CONTRACT_LIMIT");
        Assert.Null(state.Construction!.Candidate); Assert.Empty(state.Construction.Checkpoints); Assert.Null(state.PendingCall);
        Assert.Equal(500, state.Discovery.Resolved[0].InputSchema["properties"]!["value"]!["enum"]!.AsArray().Count);
    }

    [Fact]
    public async Task ExactDescriptionCanBeReadOnceMoreAfterNewProviderKnowledgeWithoutReopeningOperationPages()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 1);
        state.Discovery.Pages[0] = state.Discovery.Pages[0] with { NextCursor = "more" };
        var address = DescriptionAddress(state);
        var plan = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var original = state.Construction!.Evidence![address]!.ToJsonString();
        var firstRead = Assert.Single(state.Construction!.EvidenceRequests, key => key.StartsWith("description-read:", StringComparison.Ordinal));
        var firstPageKey = PlanningConstruction.EvidenceBatchFingerprint(state, [PlanningCompactConstruction.OperationPageAddress(state, 0)]);
        Assert.Contains(firstPageKey, state.Construction!.EvidenceRequests);
        runtime.Respond = (request, _) => Wire(request, Evidence(PlanningCompactConstruction.ProviderRequestAddress(state, 0)));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        runtime.Capabilities = new ContinuationCatalog([AdditionalOperation(state)]);
        runtime.Respond = (request, _) => Wire(request, Discover("alpha", "more"));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, state.Discovery.Pages.Count);
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(original, state.Construction!.Evidence![address]!.ToJsonString());
        Assert.Contains(firstRead, state.Construction!.EvidenceRequests);
        Assert.Contains(firstPageKey, state.Construction!.EvidenceRequests);
        Assert.Equal(2, state.Construction!.EvidenceRequests.Count(key => key.StartsWith("description-read:", StringComparison.Ordinal)));
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.All(runtime.Calls.Where(call => call.StructuredOutputSchema?["title"]?.ToString() == PlanningCompactConstruction.DiscoveryPurpose),
            call => Assert.InRange(PlanningRequestMeasurements.Measure(call, PlanningCompactConstruction.DiscoveryPurpose, 3000).EstimatedInputTokens, 1, 6000));
        var calls = state.ModelCalls;
        state = await Advance(state, runtime);
        Assert.NotEqual(PlanningStatus.FinalReview, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code is "REPLAN_NO_PROGRESS" or "PLANNING_RESPONSE_INVALID");
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(2, state.Construction!.EvidenceRequests.Count(key => key.StartsWith("description-read:", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("order")]
    [InlineData("cursor")]
    [InlineData("diagnostic")]
    [InlineData("denied")]
    [InlineData("source")]
    [InlineData("missing-operation")]
    [InlineData("registered-denied")]
    public async Task NavigationAndUnusableMetadataCannotAuthorizeAnotherDescriptionRead(string change)
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 2);
        var address = DescriptionAddress(state);
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var originalSchema = PlanningCompactConstruction.DiscoverySchema(state)["description"]!.ToString();
        switch (change)
        {
            case "duplicate": state.Discovery.Pages.Add(state.Discovery.Pages[0] with { Cursor = "duplicate" }); break;
            case "empty": state.Discovery.Pages.Add(new("alpha", "empty", [], "later", Query: Objective)); break;
            case "order": state.Discovery.Pages[0].Capabilities.Reverse(); break;
            case "cursor": state.Discovery.Pages[0] = state.Discovery.Pages[0] with { NextCursor = "different" }; break;
            case "diagnostic": state.Diagnostics.Add(new("INFORMATION", "/", "Retained explanatory evidence.", Required: false)); break;
            case "source":
                state.Discovery.Pages.Add(new("beta", null, [state.Discovery.Pages[0].Capabilities[0] with { SourceId = "beta" }], null, Query: Objective));
                break;
            case "missing-operation":
                state.Discovery.Pages.Add(new("alpha", "unusable", [AdditionalOperation(state) with { Operation = null }], null, Query: Objective));
                break;
            case "registered-denied":
                state.Catalog!.Policy.DeniedCapabilityIds.Add("denied_registered");
                state.Catalog.Capabilities.Add(new() { Id = "denied_registered", Kind = "registered", Version = "new",
                    StepType = "mcp.call", InputSchema = new() { ["type"] = "object" }, OutputSchema = new() { ["type"] = "object" } });
                break;
            case "denied":
                var denied = AdditionalOperation(state);
                state.Catalog!.Policy.DeniedCapabilityIds.Add(denied.Id);
                state.Discovery.Pages.Add(new("alpha", "denied", [denied], null, Query: Objective));
                break;
        }
        Assert.Equal(originalSchema, PlanningCompactConstruction.DiscoverySchema(state)["description"]!.ToString());
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.False(state.Status == PlanningStatus.FinalReview);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code is "REPLAN_NO_PROGRESS" or "PLANNING_RESPONSE_INVALID");
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(state.Construction!.EvidenceRequests, key => key.StartsWith("description-read:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConflictingOperationVersionCannotEstablishSelectableKnowledgeGrowth()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 1);
        var address = DescriptionAddress(state);
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var conflicting = PlannerFixture.Clone(state).Discovery.Pages[0].Capabilities[0] with { Version = "conflicting" };
        conflicting.Operation!.Version = "conflicting";
        state.Discovery.Pages.Add(new("alpha", "conflicting", [conflicting], null, Query: Objective));
        Assert.Empty(PlanningCompactConstruction.ContractDescriptors(state));
        Assert.Null(PlanningCompactConstruction.DescriptionReadKey(state, PlanningCompactConstruction.DiscoverySchema(state), Evidence(address)));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.False(state.Status == PlanningStatus.FinalReview);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code is "REPLAN_NO_PROGRESS" or "PLANNING_RESPONSE_INVALID");
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(state.Construction!.EvidenceRequests, key => key.StartsWith("description-read:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DescriptionRereadResumesTheExactIssuedKnowledgeAndReceiptWithoutAnotherCharge()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 1);
        var address = DescriptionAddress(state);
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(state, runtime);
        state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.Status == PlanningStatus.Generating &&
            checkpoint.PendingCall?.Purpose == PlanningCompactConstruction.DiscoveryPurpose));
        var exact = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls;
        var recovery = new TestRuntime { Respond = (request, _) => Wire(request, Evidence(address)) };
        recovery.CaptureIssued(saved.PendingCall!);
        var resumed = await Advance(saved, recovery);
        Assert.Empty(resumed.Diagnostics);
        Assert.Equal(exact, JsonSerializer.Serialize(Assert.Single(recovery.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, resumed.ModelCalls);
        Assert.Equal(0, resumed.ReplanAttempts);
        Assert.Null(resumed.PendingCall);
        Assert.Equal(2, resumed.Construction!.EvidenceRequests.Count(key => key.StartsWith("description-read:", StringComparison.Ordinal)));
        Assert.NotNull(resumed.Construction!.Evidence![address]);
    }


    [Theory]
    [InlineData("knowledge")]
    [InlineData("hash")]
    [InlineData("version")]
    public async Task StaleIssuedKnowledgeOrDescriptionVersionCannotGrantAnotherRead(string change)
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 1);
        var address = DescriptionAddress(state);
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        if (change == "hash") schema["description"] = schema["description"]!.ToString()[..^1] + "Z";
        var issued = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
            PlanningCompactConstruction.DiscoveryContext(state), schema, PlanningCompactConstruction.DiscoveryPurpose), out var evidenceAddresses);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued, EvidenceAddresses = evidenceAddresses };
        runtime.CaptureIssued(state.PendingCall);
        state.ModelCalls++;
        if (change == "knowledge") state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        if (change == "version") address = address.Replace("@version_0:", "@stale:", StringComparison.Ordinal);
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == (change == "version" ? "PLANNING_RESPONSE_INVALID" : "CONSTRUCTION_DISCOVERY_STALE"));
        Assert.Null(state.Construction!.Evidence);
        Assert.DoesNotContain(state.Construction!.EvidenceRequests, key => key.StartsWith("description-read:", StringComparison.Ordinal));
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task UnknownDescriptionBecomesRetrievableOnlyAfterProviderKnowledgeSuppliesIt()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 1);
        var additional = AdditionalOperation(state);
        var expanded = PlannerFixture.Clone(state);
        expanded.Discovery.Pages.Add(new("alpha", "more", [additional], null, Query: Objective));
        var address = PlanningCompactConstruction.ContractDescriptorPage(expanded)["items"]!.AsArray()
            .Single(item => item!["id"]!.ToString() == additional.Operation!.Id)!["descriptionEvidence"]!.ToString();
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code is "CONSTRUCTION_EVIDENCE_INVALID" or "PLANNING_RESPONSE_INVALID");
        Assert.Null(state.Construction!.Evidence);
        state.Discovery.Pages.Add(new("alpha", "more", [additional], null, Query: Objective));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.NotNull(state.Construction!.Evidence![address]);
    }

    private static string DescriptionAddress(PlanningSession state) =>
        PlanningCompactConstruction.ContractDescriptorPage(state)["items"]![0]!["descriptionEvidence"]!.ToString();

    private static CapabilitySummary AdditionalOperation(PlanningSession state)
    {
        var expanded = PlannerFixture.Clone(state);
        var index = state.Discovery.Pages.SelectMany(page => page.Capabilities).Select(capability => capability.Id).Distinct(StringComparer.Ordinal).Count();
        AddOperations(expanded, index + 1);
        return expanded.Discovery.Pages[0].Capabilities.Single(capability => capability.Id == "operation_" + index);
    }

    private static JsonObject Evidence(params string[] addresses) => new()
    {
        ["binding"] = null, ["discoveryRequests"] = null, ["clarifications"] = null, ["reselectStep"] = null,
        ["evidenceRequests"] = new JsonArray(addresses.Select(address => (JsonNode?)JsonValue.Create(address)).ToArray())
    };

    private static PlanningRequestMeasurement Measure(PlanningSession state) => PlanningRequestMeasurements.Measure(
        DiscoveryFollowup(state), PlanningCompactConstruction.DiscoveryPurpose, 6000);

    private static LLMRequest DiscoveryFollowup(PlanningSession state) => DiscoveryFollowup(state, out _);

    private static LLMRequest DiscoveryFollowup(PlanningSession state, out PlanningEvidenceAddresses? evidenceAddresses)
    {
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
            PlanningCompactConstruction.DiscoveryContext(state), PlanningCompactConstruction.DiscoverySchema(state),
            PlanningCompactConstruction.DiscoveryPurpose);
        return PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose,
            request.Prompt, request.StructuredOutputSchema!.AsObject(), state.Construction!.Limits.DraftOutputTokens, null, out evidenceAddresses);
    }

    private static void AddOperations(PlanningSession state, int count)
    {
        var capabilities = Enumerable.Range(0, count).Select(index => new PlanningCapability
        {
            Id = "operation_" + index, Version = "version_" + index, Kind = "tool", StepType = "mcp.call",
            Server = "alpha", Method = "read_" + index, Description = "Read exact original records with declared restrictions.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
        });
        state.Discovery.Pages = [new("alpha", null, capabilities.Select(capability => new CapabilitySummary(capability.Id,
            "alpha", capability.Method!, capability.Description, capability.StepType, capability.EffectKind, capability.Version,
            Operation: TaskOperations.Describe(capability))).ToList(), null, Query: Objective)];
    }

    private const string Objective = "Prepare the exact greeting from the supplied data.";
    private const string Reason = "The supplied literal can be assembled with deterministic bindings; no suitable operation is needed.";
    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready(bool inspected = false)
    {
        var runtime = new TestRuntime { Capabilities = new EmptyCatalog(), Respond = (request, _) =>
        {
            var source = Context(request)["sources"]![0]!;
            return Wire(request, new() { ["evidenceRequests"] = null, ["clarifications"] = null, ["clauses"] = new JsonArray(new JsonObject { ["text"] = "Return exactly Hello. ", ["kind"] = "requirement", ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray() },
                    new JsonObject { ["text"] = "No remote publication.", ["kind"] = "global", ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray() }) });
        } };
        var initial = new PlanningSession { Request = new() { TenantId = "compact-discovery", Prompt = "Return exactly Hello. No remote publication.",
            Options = new JsonObject { ["automatic_correction"] = true } } };
        PlanningSessionCreation.Capture(initial);

        var state = await Advance(PlannerFixture.Clone(initial), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(PlanningCompactConstruction.IntentDeclarationsComplete(state.Construction!.Compact!));
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        state.Request.Policy.Instructions = "Never expand permissions.";
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct); state.Catalog.Capabilities.Clear();
        state.Discovery.Sources = [new("alpha", "Declared metadata for business operations.")];
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "accepted", Kind = "value", Objective = "Preserve this accepted fragment.",
            Outputs = [new("retained", new() { Kind = "string", Text = "Unchanged" })] }] } };
        PlanningCompactConstruction.SelectBlock(state, new JsonObject
        {
            ["objective"] = Objective, ["requirementIds"] = new JsonArray("req_0"),
            ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("The exact requested greeting without remote publication."),
            ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray("Preserve all original constraints and exact result meaning.")
        });
        state.Construction!.Compact!.CurrentStep = new() { BlockId = state.Construction!.Compact!.CurrentBlockId, Id = "tentative", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root", Kind = "operation",
            Objective = Objective, RequirementIds = ["req_0"] };
        if (inspected) state.Discovery.Pages.Add(new("alpha", null, [], null, Query: Objective));
        return (state, runtime);
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response, PlanningEvidenceAddresses? evidenceAddresses = null) => new()
    { Json = PlanningCorpus.Transport(TestRuntime.WireEvidence(response, evidenceAddresses), request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static LLMRequest HostView(LLMRequest request, PlanningEvidenceAddresses? evidenceAddresses) =>
        PlanningModelCalls.CanonicalEvidenceRequest(new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose,
            Request = request, EvidenceAddresses = evidenceAddresses });
    private static JsonObject Discover(string source, string? cursor, string? query = null) => new()
    {
        ["binding"] = null, ["evidenceRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null,
        ["discoveryRequests"] = new JsonArray(new JsonObject { ["sourceId"] = source, ["cursor"] = cursor, ["query"] = query, ["operationIds"] = null })
    };
    private static JsonObject Reselect() => new() { ["binding"] = null, ["discoveryRequests"] = null, ["evidenceRequests"] = null, ["clarifications"] = null, ["reselectStep"] = new JsonObject { ["reason"] = Reason, ["disposition"] = "abandon", ["prerequisite"] = null } };
    private static JsonObject Implement(LLMRequest request) => new()
    {
        ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.ToString(), ["evidenceRequests"] = null, ["clarifications"] = null,
        ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = "value", ["operationIds"] = new JsonArray(), ["inputs"] = new JsonArray(), ["dependencyTaskIds"] = new JsonArray(),
            ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray() }
    };
    private static LLMRequest Complete(PlanningSession state, LLMRequest request) => PlanningGenerationPolicy.Apply(new LLMRequest
    {
        Prompt = request.Prompt, StructuredOutputSchema = request.StructuredOutputSchema, StructuredOutputStrict = true,
        Provider = "provider", Model = "pinned-model", UseBackgroundMode = true, MaxTokens = 8192,
        ClientRequestId = new string('s', 64) + ":123:" + new string('h', 64)
    }, state.Request.Generation);

    private static LLMRequest Complete(PlanningSession state, LLMRequest request, out PlanningEvidenceAddresses? evidenceAddresses) =>
        PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, request.Prompt,
            request.StructuredOutputSchema!.AsObject(), 8192, null, out evidenceAddresses);
    private sealed class EmptyCatalog : ICapabilityCatalog
    {
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([new("alpha", "Declared metadata for business operations.")]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => Task.FromResult(new CapabilityPage(sourceId, cursor, [], null, Query: query));
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException("No tool may be invented by this metadata-only fixture.");
    }

    private sealed class ContinuationCatalog(List<CapabilitySummary> capabilities) : ICapabilityCatalog
    {
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) =>
            Task.FromResult(new CapabilityPage(sourceId, cursor, capabilities, null, Query: query));
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) =>
            throw new InvalidOperationException("Navigation evidence cannot execute or invent a contract.");
    }

    private sealed class CountingContinuationCatalog(string? next = null) : ICapabilityCatalog
    {
        internal int ListCalls { get; private set; }
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CapabilitySource>>([]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        {
            ListCalls++;
            return Task.FromResult(new CapabilityPage(sourceId, cursor, [], next, Query: query));
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) =>
            throw new InvalidOperationException("Navigation evidence cannot execute a contract.");
    }
}
