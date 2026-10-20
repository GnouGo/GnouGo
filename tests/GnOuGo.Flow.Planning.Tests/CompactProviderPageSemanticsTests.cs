using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderPaginationLabelPreservesExactRetainedPagesAndActionPairs(bool continuation)
    {
        var (state, _) = await Ready();
        if (continuation) state.Discovery.Pages.Add(new("alpha", null, [], "opaque-next", Query: Objective));
        var old = RetainedProviderPage(state);
        var address = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        state.Construction!.Evidence = new() { [address] = old };
        var exact = old.ToJsonString();
        var newPage = PlanningCompactConstruction.ProviderRequestPage(state, 0);
        Assert.Equal(old["version"]!.ToString(), newPage["version"]!.ToString());
        Assert.True(old["items"]![0]!["available"]!.GetValue<bool>());
        Assert.Null(old["items"]![0]!["canRequestPage"]);
        Assert.True(newPage["items"]![0]!["canRequestPage"]!.GetValue<bool>());
        Assert.Null(newPage["items"]![0]!["available"]);

        PlanningCompactConstruction.RemoveStaleDiscoveryDirectories(state);
        Assert.Equal(exact, state.Construction.Evidence[address]!.ToJsonString());
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("alpha", continuation ? "opaque-next" : null), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("alpha", continuation ? null : "invented"), schema));
        Assert.Equal(exact, PlanningCompactConstruction.DiscoveryContext(state)["evidence"]![address]!.ToJsonString());
    }

    [Theory]
    [InlineData("available")]
    [InlineData("cursor")]
    [InlineData("resultCount")]
    [InlineData("canRequestPage")]
    public async Task RetainedProviderLabelCompatibilityDoesNotAcceptChangedActionMetadata(string changed)
    {
        var (state, _) = await Ready();
        AddOperations(state, 1);
        var page = RetainedProviderPage(state);
        var item = page["items"]![0]!;
        switch (changed)
        {
            case "available": item[changed] = true; break;
            case "cursor": item[changed] = "invented"; break;
            case "resultCount": item[changed] = 2; break;
            case "canRequestPage": item[changed] = true; break;
        }
        var address = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        state.Construction!.Evidence = new() { [address] = page };
        Assert.Equal("null", PlanningCompactConstruction.DiscoverySchema(state)["properties"]!["discoveryRequests"]!["type"]!.ToString());
        PlanningCompactConstruction.RemoveStaleDiscoveryDirectories(state);
        Assert.False(state.Construction.Evidence.ContainsKey(address));
        Assert.Single(state.Discovery.Pages[0].Capabilities);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingDiscoveryWithOriginalProviderLabelKeepsExactRequestAndAccounting(bool retainedPolicy)
    {
        var (state, runtime) = await Ready(retainedPolicy: retainedPolicy);
        AddOperations(state, 1);
        var page = RetainedProviderPage(state);
        var provider = PlanningCompactConstruction.ProviderRequestAddress(state, 0);
        state.Construction!.Evidence = new() { [provider] = page };
        var exactPage = page.ToJsonString();
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        var issued = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
            PlanningCompactConstruction.DiscoveryContext(state), schema, PlanningCompactConstruction.DiscoveryPurpose));
        var description = DescriptionAddress(state);
        var receipt = Wire(issued, Evidence(description));
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose,
            Request = issued, CorrectionAccounting = retainedPolicy ? null : 0 };
        state.ModelCalls++;
        var calls = state.ModelCalls;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var plan = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(exactPage, Context(request)["evidence"]![provider]!.ToJsonString());
            return receipt;
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.PendingCall);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(state.Discovery.Pages[0].Capabilities[0].Operation!.Description,
            state.Construction.Evidence![description]!["text"]!.ToString());
        if (!retainedPolicy)
        {
            Assert.Equal(exactPage, state.Construction.Evidence[provider]!.ToJsonString());
            PlanningCompactConstruction.RemoveStaleDiscoveryDirectories(state);
            Assert.Equal(exactPage, state.Construction.Evidence[provider]!.ToJsonString());
        }
        else
        {
            Assert.Null(PlanningCompactConstruction.ProviderRequestPage(state, 0)["items"]![0]!["canRequestPage"]);
            Assert.False(PlanningCompactConstruction.ProviderRequestPage(state, 0)["items"]![0]!["available"]!.GetValue<bool>());
        }
    }

    private static JsonObject RetainedProviderPage(PlanningSession state)
    {
        var retained = PlannerFixture.Clone(state);
        retained.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        retained.Construction!.Limits.PhasePolicy = null;
        return PlanningCompactConstruction.ProviderRequestPage(retained, 0);
    }
}
