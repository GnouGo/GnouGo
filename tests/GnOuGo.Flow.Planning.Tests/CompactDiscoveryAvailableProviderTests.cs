using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ProviderDecisionOverviewKeepsLaterChoicesVisibleWithoutCrossingCursorAuthority(int presentationVersion)
    {
        var (state, _) = await Ready(presentationVersion: presentationVersion);
        AddOperations(state, 1);
        state.Discovery.Sources.AddRange([new("beta", "A remaining provider."), new("gamma", "An exhausted provider."),
            new("delta", "A provider with another exact page."), new("epsilon", "A later relevant provider.")]);
        state.Discovery.Pages.Add(new("gamma", null, [], null, Query: Objective));
        state.Discovery.Pages.Add(new("delta", null, [], "delta-next", Query: Objective));
        PlanningConstruction.LoadEvidence(state, [DescriptionAddress(state)]);
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var request = DiscoveryFollowup(state);
        var page = Context(request)["providerRequests"]!;
        Assert.Equal(1, page["offset"]!.GetValue<int>());
        Assert.Equal(5, page["total"]!.GetValue<int>());
        Assert.Equal(presentationVersion == 3 ? new[] { "beta", "gamma", "delta", "epsilon" } : ["beta", "gamma"],
            page["items"]!.AsArray().Select(item => item!["sourceId"]!.ToString()));
        foreach (var (source, cursor, valid) in new (string, string?, bool)[]
        {
            ("beta", null, true), ("delta", "delta-next", presentationVersion == 3), ("epsilon", null, presentationVersion == 3),
            ("alpha", null, false), ("gamma", null, false), ("delta", null, false), ("epsilon", "delta-next", false), ("unknown", null, false)
        })
        {
            var response = Discover(source, cursor);
            if (presentationVersion == 3) { response.Remove("operationIds"); response["binding"] = null; }
            Assert.Equal(valid, PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!).Count == 0);
        }
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Empty(state.Discovery.Resolved);
    }

    [Fact]
    public async Task ProviderOverviewIsBoundedAndItsContinuationLoadsTheUnchangedCanonicalPage()
    {
        var (state, _) = await Ready(presentationVersion: 3);
        var description = new string('界', 10_000);
        state.Discovery.Sources.AddRange(Enumerable.Range(1, 9).Select(index => new CapabilitySource("provider_" + index, description)));
        var canonical = PlanningCompactConstruction.ProviderRequestPage(state, 0).ToJsonString();
        var address = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var request = DiscoveryFollowup(state);
        var overview = Context(request)["providerRequests"]!;
        Assert.Equal(8, overview["items"]!.AsArray().Count);
        Assert.Equal(10, overview["total"]!.GetValue<int>());
        Assert.DoesNotContain(description, request.Prompt, StringComparison.Ordinal);
        foreach (var item in overview["items"]!.AsArray().Skip(1))
        {
            Assert.False(item!["descriptionComplete"]!.GetValue<bool>());
            Assert.StartsWith("source-description:" + item["sourceId"] + "@", item["descriptionEvidence"]!.ToString(), StringComparison.Ordinal);
        }
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.DiscoveryPurpose).MaxInputTokens;
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, limit).EstimatedInputTokens, 1, limit);
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var historical = PlannerFixture.Clone(state);
        historical.Construction!.Compact!.PresentationVersion = 2;
        Assert.Equal(canonical, PlanningCompactConstruction.ProviderRequestPage(historical, 0).ToJsonString());
        Assert.Equal(address, PlanningCompactConstruction.ProviderRequestAddress(historical, 0));
        Assert.Equal(2, PlanningCompactConstruction.DiscoveryContext(historical)["providerRequests"]!["items"]!.AsArray().Count);

        var next = overview["next"]!.ToString();
        Assert.Equal(PlanningCompactConstruction.ProviderRequestAddress(state, 8), next);
        var expected = PlanningCompactConstruction.ProviderRequestPage(state, 8).ToJsonString();
        PlanningConstruction.LoadEvidence(state, [next]);
        request = DiscoveryFollowup(state);
        Assert.Null(Context(request)["providerRequests"]);
        Assert.Equal(expected, Context(request)["evidence"]![next]!.ToJsonString());
        foreach (var provider in new[] { "provider_8", "provider_9" })
            Assert.Empty(PlanningContractValidation.ValidateInstance(Wire(request, Discover(provider, null)).Json!, request.StructuredOutputSchema!));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Wire(request, Discover("alpha", null)).Json!, request.StructuredOutputSchema!));
    }

    [Fact]
    public async Task SavedTwoProviderDecisionResumesItsExactRequestWhenTheFreshOverviewWouldBeWider()
    {
        var (state, runtime) = await Ready(presentationVersion: 3);
        state.Discovery.Sources.AddRange([new("beta", "Second provider."), new("gamma", "Third provider."),
            new("delta", "Fourth provider."), new("epsilon", "Fifth provider.")]);
        var historical = PlannerFixture.Clone(state);
        PlanningConstruction.LoadEvidence(historical, [PlanningCompactConstruction.ProviderRequestAddress(historical, 0)]);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.Equal(5, context["providerRequests"]!["items"]!.AsArray().Count);
        context["providerRequests"] = PlanningCompactConstruction.ProviderRequestPage(state, 0);
        var request = Complete(state, PlanningCompactConstruction.Request("Retained two-provider instructions.", context,
            PlanningCompactConstruction.DiscoverySchema(historical), PlanningCompactConstruction.DiscoveryPurpose));
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = request };
        state.ModelCalls++;
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var calls = state.ModelCalls;
        runtime.Respond = (actual, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(actual, PlanningJsonContext.Default.LLMRequest));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Wire(actual, Discover("epsilon", null)).Json!, actual.StructuredOutputSchema!));
            return Wire(actual, Evidence("page:sources:0"));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AvailableProvidersRemainActionableAlongsideInspectedOperationEvidence(bool description)
    {
        var (state, _) = await Ready();
        AddOperations(state, 1);
        state.Discovery.Sources.AddRange([new("beta", "Another provider."), new("gamma", "Next provider."),
            new("delta", "Later provider.")]);
        state.Discovery.Pages.Add(new("beta", null, [], "beta-next", Query: Objective));
        if (description) PlanningConstruction.LoadEvidence(state, [DescriptionAddress(state)]);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var schema = PlanningCompactConstruction.DiscoverySchema(state);

        Assert.Equal(new[] { "beta", "gamma" }, context["providerRequests"]!["items"]!.AsArray()
            .Select(item => item!["sourceId"]!.ToString()));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("beta", "beta-next"), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("gamma", null), schema));
        foreach (var invalid in new[] { Discover("alpha", null), Discover("beta", null), Discover("gamma", "beta-next"),
            Discover("delta", null), Discover("beta", "page:sources:0") })
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, schema));
        Assert.True(JsonNode.DeepEquals(state.Construction!.Evidence, context["evidence"]));
        if (description)
        {
            var evidence = context["evidence"]![DescriptionAddress(state)]!;
            Assert.Equal(state.Discovery.Pages[0].Capabilities[0].Operation!.Description, evidence["text"]!.ToString());
            Assert.Null(evidence["next"]);
        }
        else Assert.NotNull(context["descriptors"]);
        Assert.Empty(state.Discovery.Resolved);
        Assert.Empty(state.Construction.Compact!.CurrentStep!.OperationIds);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        state.Discovery.Pages.Add(new("beta", "beta-next", [], null, Query: Objective));
        var nextContext = PlanningCompactConstruction.DiscoveryContext(state);
        var nextSchema = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.Equal(new[] { "gamma", "delta" }, nextContext["providerRequests"]!["items"]!.AsArray()
            .Select(item => item!["sourceId"]!.ToString()));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("beta", "beta-next"), nextSchema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("gamma", null), nextSchema));
        Assert.True(JsonNode.DeepEquals(context["evidence"], nextContext["evidence"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedPolicyKeepsProviderActionsSuppressedAfterOperationInspection(bool description)
    {
        var (state, _) = await Ready(retainedPolicy: true);
        AddOperations(state, 1);
        state.Discovery.Sources.Add(new("beta", "Another provider."));
        if (description) PlanningConstruction.LoadEvidence(state, [DescriptionAddress(state)]);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["providerRequests"]);
        Assert.Equal("null", PlanningCompactConstruction.DiscoverySchema(state)["properties"]!["discoveryRequests"]!["type"]!.ToString());
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task ExplicitProviderEvidenceKeepsItsExactActionSelection(bool description, int presentationVersion)
    {
        var (state, _) = await Ready(presentationVersion: presentationVersion);
        AddOperations(state, 1);
        state.Discovery.Sources.AddRange([new("beta", "Another provider."), new("gamma", "Selected provider."),
            new("delta", "Later provider.")]);
        var page = PlanningCompactConstruction.ProviderRequestPage(state, 2);
        var address = description ? page["items"]![0]!["descriptionEvidence"]!.ToString()
            : PlanningCompactConstruction.ProviderRequestAddress(state, 2);
        PlanningConstruction.LoadEvidence(state, [address]);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        var request = DiscoveryFollowup(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Wire(request, Discover("gamma", null)).Json!, schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Wire(request, Discover("beta", null)).Json!, schema));
        Assert.Equal(description, PlanningContractValidation.ValidateInstance(Wire(request, Discover("delta", null)).Json!, schema).Count > 0);
        Assert.True(JsonNode.DeepEquals(state.Construction!.Evidence, context["evidence"]));
        if (description) Assert.Single(context["providerRequests"]!["items"]!.AsArray());
        else Assert.Null(context["providerRequests"]);
    }
}
