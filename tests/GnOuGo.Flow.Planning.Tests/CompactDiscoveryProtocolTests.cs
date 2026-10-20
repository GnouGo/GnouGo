using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
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
        Assert.Contains("operation executes external work", PlanningCompactConstruction.SelectInstructions, StringComparison.Ordinal);
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
        var pageAddress = PlanningCompactConstruction.ProviderRequestAddress(state, 2);
        runtime.Respond = (request, _) => Wire(request, Evidence(pageAddress));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var descriptionAddress = state.Construction!.Evidence![pageAddress]!["items"]![0]!["descriptionEvidence"]!.ToString();
        runtime.Respond = (request, _) => Wire(request, Evidence(descriptionAddress));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        var exactDescription = state.Construction!.Evidence![descriptionAddress]!.ToJsonString();
        runtime.Respond = (request, _) =>
        {
            var context = Context(request);
            Assert.Single(context["evidence"]!.AsObject());
            Assert.Equal(exactDescription, context["evidence"]![descriptionAddress]!.ToJsonString());
            var option = Assert.Single(context["providerRequests"]!["items"]!.AsArray())!;
            Assert.Equal(selected, option["sourceId"]!.ToString());
            Assert.Equal("selected-next", option["cursor"]!.ToString());
            Assert.Null(option["description"]);
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover(selected, "unrelated-next"), request.StructuredOutputSchema!));
            Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, 3000).EstimatedInputTokens, 1, 3000);
            return Wire(request, Discover(selected, "selected-next"));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(Objective, Assert.Single(state.Discovery.Pages, page => page.Cursor == "selected-next").Query);
        Assert.Equal(calls + 3, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Null(state.Construction.Candidate);
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
        state.Construction.Evidence = originalEvidence;
        state.Discovery.Pages.Add(new("alpha", "first", [], null, Query: Objective));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("alpha", "first"), PlanningCompactConstruction.DiscoverySchema(state)));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("alpha", "third"), PlanningCompactConstruction.DiscoverySchema(state)));
        state.Discovery.Pages.Add(new("alpha", "second", [], null, Query: Objective));
        state.Discovery.Pages.Add(new("alpha", "third", [], null, Query: Objective));
        Assert.False(PlanningCompactConstruction.DiscoveryContext(state)["providerRequests"]!["items"]![0]!["available"]!.GetValue<bool>());
        Assert.Equal("null", PlanningCompactConstruction.DiscoverySchema(state)["properties"]!["discoveryRequests"]!["type"]!.ToString());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("address")]
    [InlineData("version")]
    [InlineData("ambiguous")]
    [InlineData("historical")]
    public async Task OnlyCanonicalCurrentDescriptionEvidenceExposesProviderActions(string change)
    {
        var (state, _) = await Ready();
        var address = PlanningCompactConstruction.ProviderRequestPage(state, 0)["items"]![0]!["descriptionEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        switch (change)
        {
            case "text": state.Construction!.Evidence![address]!["text"] = "Invented source description."; break;
            case "address":
                var value = state.Construction!.Evidence![address]!.DeepClone();
                state.Construction.Evidence = new() { [address[..^1] + "1"] = value };
                break;
            case "version": state.Discovery.Sources[0] = new("alpha", "Changed normative restrictions."); break;
            case "ambiguous": state.Discovery.Sources.Add(state.Discovery.Sources[0]); break;
            case "historical": state.Construction!.Compact!.PresentationVersion = null; break;
        }
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
        state.Discovery.Sources = Enumerable.Range(0, 500).Select(index => new CapabilitySource("source_" + index, original)).ToList();
        var address = PlanningCompactConstruction.ProviderRequestPage(state, 2)["items"]![0]!["descriptionEvidence"]!.ToString();
        var text = new System.Text.StringBuilder();
        while (true)
        {
            PlanningConstruction.LoadEvidence(state, [address]);
            var context = PlanningCompactConstruction.DiscoveryContext(state);
            var page = context["evidence"]![address]!;
            Assert.Single(context["evidence"]!.AsObject());
            Assert.Single(context["providerRequests"]!["items"]!.AsArray());
            Assert.Equal("source_2", context["providerRequests"]!["items"]![0]!["sourceId"]!.ToString());
            Assert.Equal(text.Length, page["offset"]!.GetValue<int>());
            text.Append(page["text"]!.ToString());
            Assert.InRange(Measure(state).EstimatedInputTokens, 1, 3000);
            if (page["next"]?.ToString() is not { } next) break;
            address = next;
        }
        Assert.Equal(original, text.ToString());
        Assert.All(state.Discovery.Sources, source => Assert.Equal(original, source.Description));
        var repeated = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address]));
        Assert.Contains(repeated.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
    }

    [Fact]
    public async Task IssuedHistoricalDescriptionRequestResumesBeforeNewProviderActionsArePresented()
    {
        var (state, runtime) = await Ready();
        state.Discovery.Sources[0] = new("alpha", new string('x', 3000));
        var address = PlanningCompactConstruction.ProviderRequestPage(state, 0)["items"]![0]!["descriptionEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        var next = state.Construction!.Evidence![address]!["next"]!.ToString();
        state.Construction.Compact!.PresentationVersion = null;
        var oldSchema = PlanningCompactConstruction.DiscoverySchema(state);
        var issued = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions,
            PlanningCompactConstruction.DiscoveryContext(state), oldSchema, PlanningCompactConstruction.DiscoveryPurpose));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("alpha", null), oldSchema));
        state.Construction.Compact.PresentationVersion = 2;
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("alpha", null), PlanningCompactConstruction.DiscoverySchema(state)));
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued };
        state.ModelCalls++;
        var calls = state.ModelCalls;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var receipt = Wire(issued, Evidence(next));
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return receipt;
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        var retainedEvidence = Assert.IsType<JsonObject>(state.Construction!.Evidence);
        Assert.Single(retainedEvidence);
        Assert.NotNull(retainedEvidence[next]);
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
        Assert.Single(state.Construction!.Evidence!);
        Assert.Equal(thirdAddress, PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]!.ToString());
        runtime.Respond = (request, _) => Wire(request, Evidence(Context(request)["directoryEvidence"]!["operations"]!.ToString()));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        page = state.Construction!.Evidence![thirdAddress]!;
        presented.AddRange(page["items"]!.AsArray().Select(item => item!["id"]!.ToString() + "@" + item["version"]!.ToString()));
        Assert.Equal(expected, presented);
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.All(runtime.Calls.Where(call => call.StructuredOutputSchema?["title"]?.ToString() == PlanningCompactConstruction.DiscoveryPurpose),
            call => Assert.InRange(PlanningRequestMeasurements.Measure(call, PlanningCompactConstruction.DiscoveryPurpose, 3000).EstimatedInputTokens, 1, 3000));
        runtime.Respond = (request, _) => Wire(request, Evidence(secondAddress));
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Empty(state.Construction!.Checkpoints);
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
        PlanningConstruction.LoadEvidence(state, ["source:" + state.Construction!.Compact!.Sources[0].Id]);
        Assert.Equal(count == 0 ? null : PlanningCompactConstruction.OperationPageAddress(state, 0),
            PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]?.ToString());
        Assert.DoesNotContain(PlanningConstruction.EvidenceBatchFingerprint(state, [PlanningCompactConstruction.OperationPageAddress(state, 0)]),
            state.Construction!.EvidenceRequests);
        state.Construction.Compact!.PresentationVersion = null;
        Assert.Equal("page:operations:0", PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]!.ToString());
    }

    [Theory]
    [InlineData("CONSTRUCTION_EVIDENCE_UNKNOWN")]
    [InlineData("CONSTRUCTION_EVIDENCE_INVALID")]
    public async Task CorrectedEvidenceReceiptResumesExactlyAndChargesOnlyOneCorrection(string code)
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
        Assert.Equal(1, saved.ReplanAttempts);
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
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Single(state.Construction!.EvidenceRequests, key => key == pageKey);
        var nextAddress = PlanningCompactConstruction.OperationPageAddress(state, 4);
        Assert.Equal(nextAddress, PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]!.ToString());
        recovery.Respond = (request, _) => Wire(request, Evidence(nextAddress));
        state = await Advance(PlannerFixture.Clone(state), recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]);
    }

    [Fact]
    public async Task SuccessfulEvidenceClearsOnlyItsResolvedNavigationDiagnostics()
    {
        var (state, runtime) = await Ready();
        state.Diagnostics = [new("CONSTRUCTION_EVIDENCE_UNKNOWN", "/evidenceRequests", "Resolved address."),
            new("CONSTRUCTION_EVIDENCE_INVALID", "/elsewhere", "Unrelated location."),
            new("CONSTRUCTION_STEP_INPUT_INVALID", "/step/inputs/0", "Unrelated semantic finding.")];
        runtime.Respond = (request, _) => Wire(request, Evidence("source:" + state.Construction!.Compact!.Sources[0].Id));
        state = await Advance(state, runtime);
        Assert.Equal(2, state.Diagnostics.Count);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Location == "/elsewhere");
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_STEP_INPUT_INVALID");
        Assert.Equal(1, state.ReplanAttempts);
    }

    [Fact]
    public async Task HistoricalIssuedRestartLinkDoesNotRetroactivelyConsumeItsRequestedPage()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 4);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        context["descriptors"] = PlanningCompactConstruction.ContractDescriptorPage(state, legacy: true);
        context["directoryEvidence"]!["operations"] = "page:operations:0";
        var originalSchema = PlanningCompactConstruction.DiscoverySchema(state);
        originalSchema.Remove("description");
        var issued = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions,
            context, originalSchema, PlanningCompactConstruction.DiscoveryPurpose));
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued };
        state.ModelCalls++;
        var calls = state.ModelCalls;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Wire(request, Evidence("page:operations:0"));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.NotNull(state.Construction!.Evidence!["page:operations:0"]);
        Assert.Single(state.Construction!.EvidenceRequests);
        Assert.Equal(2, state.Construction.Evidence!["page:operations:0"]!["items"]!.AsArray().Count);
        Assert.Equal("page:operations:2", state.Construction.Evidence["page:operations:0"]!["next"]!.ToString());
        Assert.Equal(PlanningCompactConstruction.OperationPageAddress(state, 0),
            PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]!.ToString());
    }

    [Fact]
    public async Task FreshIssuedSchemaRejectsLegacyAliasesWithoutLoadingOrResettingPresentedEvidence()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 4);
        var address = PlanningCompactConstruction.OperationPageAddress(state, 0);
        runtime.Respond = (request, _) => Wire(request, Evidence("page:operations:0"));
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_INVALID");
        Assert.Null(state.Construction!.Evidence);
        Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [address]), state.Construction.EvidenceRequests);
        Assert.DoesNotContain(PlanningConstruction.EvidenceBatchFingerprint(state, ["page:operations:0"]), state.Construction.EvidenceRequests);
        Assert.Equal(0, state.ReplanAttempts);
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(1, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("next")]
    public async Task NoncanonicalDisplayedOperationPagesCannotEstablishNavigationProgress(string change)
    {
        var (state, _) = await Ready();
        AddOperations(state, 4);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        if (change == "version") context["descriptors"]!["items"]![0]!["version"] = "stale-version";
        else context["descriptors"]!["next"] = "page:operations:999";
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions,
            context, PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose);
        PlanningCompactConstruction.RecordPresentedOperations(state, request.Prompt);
        Assert.Empty(state.Construction!.EvidenceRequests);
        state.Construction.Evidence = new() { ["page:operations:0"] = context["descriptors"]!.DeepClone() };
        Assert.Equal(PlanningCompactConstruction.OperationPageAddress(state, 0), PlanningCompactConstruction.DiscoveryContext(state)["directoryEvidence"]!["operations"]!.ToString());
    }

    [Fact]
    public async Task ProviderExpansionPreservesConsumedContentAndShowsOnlyTheNewOperationPage()
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
        Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [secondAddress]), state.Construction.EvidenceRequests);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var shown = Assert.Single(context["descriptors"]!["items"]!.AsArray())!;
        Assert.Equal(additional.Id, shown["id"]!.ToString());
        Assert.Equal(8, context["descriptors"]!["offset"]!.GetValue<int>());
        Assert.Null(context["descriptors"]!["next"]);
        Assert.Null(context["directoryEvidence"]!["operations"]);
        Assert.Null(shown["description"]);
        Assert.True(context["descriptors"]!["descriptionsOmitted"]!.GetValue<bool>());
        runtime.Respond = (request, _) => Wire(request, Evidence(shown["descriptionEvidence"]!.ToString()));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.All(runtime.Calls.Where(call => call.StructuredOutputSchema?["title"]?.ToString() == PlanningCompactConstruction.DiscoveryPurpose),
            call => Assert.InRange(PlanningRequestMeasurements.Measure(call, PlanningCompactConstruction.DiscoveryPurpose, 3000).EstimatedInputTokens, 1, 3000));
        var calls = state.ModelCalls;
        runtime.Respond = (request, _) => Wire(request, Evidence(secondAddress));
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Same(state, await Advance(state, runtime));
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
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
        Assert.Equal(before, state.Construction.EvidenceRequests);
        Assert.Null(state.Construction.Evidence);
        PlanningConstruction.LoadEvidence(state, [current]);
        Assert.True(JsonNode.DeepEquals(PlanningCompactConstruction.ContractDescriptorPage(state, 4), state.Construction.Evidence![current]));
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

    [Fact]
    public async Task SeparateDiscoveryEvidenceRequestsPreserveOriginalPagesWithinTheCompleteRequestLimit()
    {
        var (state, _) = await Ready();
        state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with
        { Description = string.Concat(Enumerable.Repeat("Preserve complete original observations, identities, order and required cleanup. ", 8)) };
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Objective = string.Concat(Enumerable.Repeat("Inspect only the authorized resource and preserve its original records. ", 4));
        step.ExpectedOutputs = [string.Concat(Enumerable.Repeat("Complete observed records with exact identities and supplied ordering. ", 4))];
        var operations = Enumerable.Range(0, 6).Select(index => new PlanningCapability
        {
            Id = "operation_" + index.ToString("d24"), Version = new string((char)('a' + index), 64),
            Kind = "tool", StepType = "mcp.call", Server = "alpha", Method = "read_" + index,
            Description = new string((char)('A' + index), 2048) + " Preserve the exact final normative restriction.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
        }).ToArray();
        state.Discovery.Pages = [new("alpha", null, operations.Select(operation => new CapabilitySummary(operation.Id, "alpha",
            operation.Method!, operation.Description, operation.StepType, operation.EffectKind, operation.Version,
            Operation: TaskOperations.Describe(operation))).ToList(), null, Query: step.Objective)];
        var originals = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var directory = PlanningCompactConstruction.ContractDescriptorPage(state);
        var addresses = new[] { PlanningCompactConstruction.OperationPageAddress(state, 4),
            directory["items"]![0]!["descriptionEvidence"]!.ToString(), directory["items"]![1]!["descriptionEvidence"]!.ToString() };
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Evidence(addresses), schema));
        foreach (var address in addresses)
            Assert.Empty(PlanningContractValidation.ValidateInstance(Evidence(address), schema));

        // The historical batch is still readable; only fresh response grammar
        // prevents combining independently bounded pages into one oversized call.
        PlanningConstruction.LoadEvidence(state, [.. addresses]);
        Assert.True(Measure(state).EstimatedInputTokens > 3000);
        foreach (var address in addresses)
        {
            PlanningConstruction.LoadEvidence(state, [address]);
            var page = state.Construction.Evidence![address]!;
            Assert.InRange(Measure(state).EstimatedInputTokens, 1, 3000);
            if (address.StartsWith("page:operations:", StringComparison.Ordinal))
                Assert.Equal(operations.Skip(4).Select(operation => operation.Id), page["items"]!.AsArray().Select(item => item!["id"]!.ToString()));
            else
            {
                var original = operations.Single(operation => address.Contains(operation.Id, StringComparison.Ordinal)).Description;
                var text = new System.Text.StringBuilder();
                while (true)
                {
                    Assert.Equal(text.Length, page["offset"]!.GetValue<int>());
                    text.Append(page["text"]!.ToString());
                    Assert.InRange(Measure(state).EstimatedInputTokens, 1, 3000);
                    if (page["next"]?.ToString() is not { } next) break;
                    PlanningConstruction.LoadEvidence(state, [next]);
                    page = state.Construction.Evidence![next]!;
                }
                Assert.Equal(original, text.ToString());
            }
        }
        Assert.Equal(originals, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
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
        const string address = "schema:read@v1:/input/properties/value";
        Assert.Empty(PlanningContractValidation.ValidateInstance(Evidence(address), PlanningCompactConstruction.DiscoverySchema(state)));
        PlanningConstruction.LoadEvidence(state, [address]);
        Assert.True(JsonNode.DeepEquals(contract.InputSchema["properties"]!["value"]!["enum"],
            state.Construction.Evidence![address]!["schema"]!["enum"]));
        var request = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions,
            PlanningCompactConstruction.DiscoveryContext(state), PlanningCompactConstruction.DiscoverySchema(state),
            PlanningCompactConstruction.DiscoveryPurpose));
        var calls = state.ModelCalls; var dispatched = runtime.Calls.Count;
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningCompactConstruction.DiscoveryPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, 8192));
        Assert.Equal("MODEL_INPUT_LIMIT", error.Code);
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(dispatched, runtime.Calls.Count);
        Assert.Null(state.PendingCall); Assert.Null(state.Construction.Candidate);
        Assert.True(JsonNode.DeepEquals(contract.InputSchema["properties"]!["value"]!["enum"],
            state.Construction.Evidence[address]!["schema"]!["enum"]));
    }

    [Fact]
    public async Task PendingHistoricalDiscoveryEvidenceBatchKeepsItsIssuedSchemaAndSavedReceipt()
    {
        var (state, runtime) = await Ready();
        state.Construction!.Compact!.PresentationVersion = null;
        var originalSchema = PlanningCompactConstruction.DiscoverySchema(state);
        var response = Evidence("source:" + state.Construction.Compact.Sources[0].Id,
            "source:" + state.Construction.Compact.Sources[1].Id);
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, originalSchema));
        state.Construction.Compact.PresentationVersion = 2;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, PlanningCompactConstruction.DiscoverySchema(state)));
        var issued = Complete(state, PlanningCompactConstruction.Request("Retained discovery evidence request.",
            PlanningCompactConstruction.DiscoveryContext(state), originalSchema, PlanningCompactConstruction.DiscoveryPurpose));
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued };
        state.ModelCalls++;
        var calls = state.ModelCalls;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var receipt = Wire(issued, response);
        var physicalCalls = 0;
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            if (request.ClientRequestId == issued.ClientRequestId) return receipt;
            physicalCalls++;
            throw new InvalidOperationException("A saved evidence receipt cannot dispatch a replacement request.");
        };
        var resumed = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(resumed.Diagnostics);
        Assert.Equal(0, physicalCalls);
        Assert.Equal(calls, resumed.ModelCalls); Assert.Equal(0, resumed.ReplanAttempts);
        Assert.Null(resumed.PendingCall);
        Assert.Equal(2, resumed.Construction!.Evidence!.Count);
        foreach (var address in response["evidenceRequests"]!.AsArray().Select(value => value!.ToString()))
            Assert.Equal(state.Construction.Compact.Sources.Single(source => "source:" + source.Id == address).Text,
                resumed.Construction.Evidence[address]!["text"]!.ToString());
        Assert.Null(resumed.Construction.Candidate);
        Assert.False(resumed.Construction.Compact!.CurrentStep!.DiscoveryComplete);
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
        var firstRead = Assert.Single(state.Construction.EvidenceRequests, key => key.StartsWith("description-read:", StringComparison.Ordinal));
        var firstPageKey = PlanningConstruction.EvidenceBatchFingerprint(state, [PlanningCompactConstruction.OperationPageAddress(state, 0)]);
        Assert.Contains(firstPageKey, state.Construction.EvidenceRequests);
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
        Assert.Contains(firstRead, state.Construction.EvidenceRequests);
        Assert.Contains(firstPageKey, state.Construction.EvidenceRequests);
        Assert.Equal(2, state.Construction.EvidenceRequests.Count(key => key.StartsWith("description-read:", StringComparison.Ordinal)));
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.All(runtime.Calls.Where(call => call.StructuredOutputSchema?["title"]?.ToString() == PlanningCompactConstruction.DiscoveryPurpose),
            call => Assert.InRange(PlanningRequestMeasurements.Measure(call, PlanningCompactConstruction.DiscoveryPurpose, 3000).EstimatedInputTokens, 1, 3000));
        var calls = state.ModelCalls;
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
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
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
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
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
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
        var resumed = await Advance(saved, recovery);
        Assert.Empty(resumed.Diagnostics);
        Assert.Equal(exact, JsonSerializer.Serialize(Assert.Single(recovery.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, resumed.ModelCalls);
        Assert.Equal(0, resumed.ReplanAttempts);
        Assert.Null(resumed.PendingCall);
        Assert.Equal(2, resumed.Construction!.EvidenceRequests.Count(key => key.StartsWith("description-read:", StringComparison.Ordinal)));
        Assert.NotNull(resumed.Construction.Evidence![address]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalIssuedDescriptionRequestsKeepTheirOriginalRepeatGuard(bool markerOnly)
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 1);
        var address = DescriptionAddress(state);
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(state, runtime);
        state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        if (markerOnly) schema["description"] = "Content-addressed operation pages only.";
        else schema.Remove("description");
        var issued = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions,
            PlanningCompactConstruction.DiscoveryContext(state), schema, PlanningCompactConstruction.DiscoveryPurpose));
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued };
        state.ModelCalls++;
        var calls = state.ModelCalls;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(exact, JsonSerializer.Serialize(runtime.Calls[^1], PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Single(state.Construction!.EvidenceRequests, key => key.StartsWith("description-read:", StringComparison.Ordinal));
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
        var issued = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions,
            PlanningCompactConstruction.DiscoveryContext(state), schema, PlanningCompactConstruction.DiscoveryPurpose));
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued };
        state.ModelCalls++;
        if (change == "knowledge") state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        if (change == "version") address = address.Replace("@version_0:", "@stale:", StringComparison.Ordinal);
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == (change == "version" ? "CONSTRUCTION_EVIDENCE_INVALID" : "CONSTRUCTION_DISCOVERY_STALE"));
        Assert.Null(state.Construction!.Evidence);
        Assert.DoesNotContain(state.Construction.EvidenceRequests, key => key.StartsWith("description-read:", StringComparison.Ordinal));
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task FailedUnknownDescriptionCannotBecomeANewActionAfterProviderGrowth()
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
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_INVALID");
        Assert.Null(state.Construction!.Evidence);
        state.Discovery.Pages.Add(new("alpha", "more", [additional], null, Query: Objective));
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(1, state.ReplanAttempts);
        Assert.DoesNotContain(state.Construction!.EvidenceRequests, key => key.StartsWith("description-read:", StringComparison.Ordinal));
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
        ["operationIds"] = null, ["discoveryRequests"] = null, ["clarifications"] = null, ["reselectStep"] = null,
        ["evidenceRequests"] = new JsonArray(addresses.Select(address => (JsonNode?)JsonValue.Create(address)).ToArray())
    };

    private static PlanningRequestMeasurement Measure(PlanningSession state) => PlanningRequestMeasurements.Measure(
        Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions,
            PlanningCompactConstruction.DiscoveryContext(state), PlanningCompactConstruction.DiscoverySchema(state),
            PlanningCompactConstruction.DiscoveryPurpose)), PlanningCompactConstruction.DiscoveryPurpose, 3000);

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
            return Wire(request, new() { ["sourceId"] = source["id"]!.ToString(),
                ["clauses"] = new JsonArray(new JsonObject { ["text"] = "Return exactly Hello. ", ["kind"] = "requirement", ["interfaces"] = "none" },
                    new JsonObject { ["text"] = "No remote publication.", ["kind"] = "global", ["interfaces"] = "none" }) });
        } };
        var state = await Advance(new() { Request = new() { TenantId = "compact-discovery", Prompt = "Return exactly Hello. No remote publication.",
            Options = new JsonObject { ["automatic_correction"] = true } } }, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, state.Construction!.Compact!.PresentationVersion);
        Assert.Empty(state.Construction.Compact.InterfaceExtractions!);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
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

    private sealed class ContinuationCatalog(List<CapabilitySummary> capabilities) : ICapabilityCatalog
    {
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) =>
            Task.FromResult(new CapabilityPage(sourceId, cursor, capabilities, null, Query: query));
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) =>
            throw new InvalidOperationException("Navigation evidence cannot execute or invent a contract.");
    }
}
