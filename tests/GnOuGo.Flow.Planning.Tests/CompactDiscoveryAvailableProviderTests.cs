using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitProviderEvidenceKeepsItsExactActionSelection(bool description)
    {
        var (state, _) = await Ready();
        AddOperations(state, 1);
        state.Discovery.Sources.AddRange([new("beta", "Another provider."), new("gamma", "Selected provider."),
            new("delta", "Later provider.")]);
        var page = PlanningCompactConstruction.ProviderRequestPage(state, 2);
        var address = description ? page["items"]![0]!["descriptionEvidence"]!.ToString()
            : PlanningCompactConstruction.ProviderRequestAddress(state, 2);
        PlanningConstruction.LoadEvidence(state, [address]);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("gamma", null), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("beta", null), schema));
        Assert.Equal(description, PlanningContractValidation.ValidateInstance(Discover("delta", null), schema).Count > 0);
        Assert.True(JsonNode.DeepEquals(state.Construction!.Evidence, context["evidence"]));
        if (description) Assert.Single(context["providerRequests"]!["items"]!.AsArray());
        else Assert.Null(context["providerRequests"]);
    }
}
