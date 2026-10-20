using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
    [Fact]
    public async Task PreviouslyReadOperationsRemainSelectableAlongsideDifferentOriginalEvidence()
    {
        var (state, _) = await Ready();
        AddOperations(state, 9);
        var shown = PlanningCompactConstruction.ContractDescriptors(state, count: 9);
        var earlier = shown[5]!.AsObject();
        var other = shown[8]!.AsObject();
        foreach (var descriptor in new[] { earlier, other })
            PlanningConstruction.LoadEvidence(state, [descriptor["descriptionEvidence"]!.ToString()]);
        PlanningConstruction.LoadEvidence(state, ["page:sources:0"]);
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);

        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var choices = context["describedOperations"]!.AsObject();
        Assert.Equal(2, choices["total"]!.GetValue<int>());
        Assert.True(choices["complete"]!.GetValue<bool>());
        Assert.Equal(new[] { earlier["id"]!.ToString(), other["id"]!.ToString() },
            choices["items"]!.AsArray().Select(item => item!["id"]!.ToString()));
        foreach (var item in choices["items"]!.AsArray())
            Assert.Equal(new[] { "id", "name", "effect" }, item!.AsObject().Select(property => property.Key));
        Assert.Null(context["descriptors"]);
        Assert.True(JsonNode.DeepEquals(state.Construction!.Evidence, context["evidence"]));
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Empty(state.Discovery.Resolved);
        Assert.Empty(state.Construction.Compact!.CurrentStep!.OperationIds);
        var repeated = Assert.Throws<PlanningResponseException>(() =>
            PlanningConstruction.LoadEvidence(state, [earlier["descriptionEvidence"]!.ToString()]));
        Assert.Contains(repeated.Diagnostics, finding => finding.Code == "REPLAN_NO_PROGRESS");
    }

    [Theory]
    [InlineData("version")]
    [InlineData("baseline")]
    [InlineData("denied")]
    [InlineData("step-type")]
    [InlineData("conflicting")]
    [InlineData("summary-version")]
    [InlineData("historical")]
    [InlineData("no-evidence")]
    public async Task DescriptionChoiceRequiresItsCurrentBaselineVersionAndPolicy(string change)
    {
        var (state, _) = await Ready();
        AddOperations(state, 1);
        PlanningConstruction.LoadEvidence(state, [DescriptionAddress(state)]);
        Assert.NotNull(PlanningCompactConstruction.DescribedOperationChoices(state));
        var capability = state.Discovery.Pages[0].Capabilities[0];
        switch (change)
        {
            case "version":
                capability.Operation!.Version = "new-version";
                state.Discovery.Pages[0].Capabilities[0] = capability with { Version = "new-version" };
                break;
            case "baseline": state.Plan!.Root.Tasks[0].Objective = "A different accepted baseline."; break;
            case "denied": state.Catalog!.Policy.DeniedCapabilityIds.Add(capability.Id); break;
            case "step-type": state.Catalog!.AllowedStepTypes.Clear(); break;
            case "conflicting":
                var conflicting = PlannerFixture.Clone(state).Discovery.Pages[0].Capabilities[0] with { Version = "conflicting" };
                conflicting.Operation!.Version = "conflicting";
                state.Discovery.Pages.Add(new("alpha", "later", [conflicting], null, Query: Objective));
                break;
            case "summary-version": state.Discovery.Pages[0].Capabilities[0] = capability with { Version = "mismatched" }; break;
            case "historical": state.Construction!.Compact!.PresentationVersion = null; break;
            case "no-evidence": state.Construction!.Evidence = null; break;
        }
        Assert.Null(PlanningCompactConstruction.DescribedOperationChoices(state));
        Assert.False(PlanningCompactConstruction.DiscoveryContext(state).ContainsKey("describedOperations"));
    }

    [Fact]
    public async Task DescriptionLookalikesAndFailedActionsCannotCreateSuccessfulReadChoices()
    {
        var (state, _) = await Ready();
        AddOperations(state, 1);
        var address = DescriptionAddress(state);
        state.Construction!.Evidence = new() { [address] = PlanningCompactConstruction.LoadContractEvidence(state, address) };
        state.Construction.Compact!.FailedActions.Add(PlanningConstruction.EvidenceBatchFingerprint(state, [address]));
        Assert.Null(PlanningCompactConstruction.DescribedOperationChoices(state));
        Assert.Empty(state.Construction.EvidenceRequests);
        Assert.False(PlanningCompactConstruction.DiscoveryContext(state).ContainsKey("describedOperations"));
    }

    [Fact]
    public async Task DescriptionChoicesAreBoundedAndDoNotReinjectLargeUnrelatedContractProse()
    {
        var (state, _) = await Ready();
        AddOperations(state, 12);
        var descriptors = PlanningCompactConstruction.ContractDescriptors(state, count: 12);
        foreach (var descriptor in descriptors.Take(7))
            PlanningConstruction.LoadEvidence(state, [descriptor!["descriptionEvidence"]!.ToString()]);
        var unrelated = state.Discovery.Pages[0].Capabilities[^1];
        var huge = new string('界', 100_000);
        unrelated.Operation!.Description = huge;
        state.Discovery.Pages[0].Capabilities[^1] = unrelated with { Description = huge };
        PlanningConstruction.LoadEvidence(state, ["page:sources:0"]);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var choices = context["describedOperations"]!.AsObject();
        Assert.Equal(7, choices["total"]!.GetValue<int>());
        Assert.False(choices["complete"]!.GetValue<bool>());
        Assert.Equal(4, choices["items"]!.AsArray().Count);
        Assert.DoesNotContain(huge, context.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(huge, state.Discovery.Pages[0].Capabilities[^1].Operation!.Description);
        var request = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
            context, PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose));
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.DiscoveryPurpose).MaxInputTokens;
        var measurement = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, limit);
        Assert.InRange(measurement.EstimatedInputTokens, 1, limit);
        Assert.True(JsonNode.DeepEquals(state.Construction!.Evidence, context["evidence"]));
    }

    [Fact]
    public async Task SavedDiscoveryWithoutDecisionChoicesResumesItsExactRequestAndAccounting()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 2);
        PlanningConstruction.LoadEvidence(state, [DescriptionAddress(state)]);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.NotNull(context["describedOperations"]);
        context.Remove("describedOperations");
        var issued = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions,
            context, PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose));
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued };
        state.ModelCalls++;
        var calls = state.ModelCalls;
        var plan = PlanningConstruction.Hash(state.Plan!);
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Wire(request, Evidence("page:sources:0"));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan!));
        Assert.Null(state.PendingCall);
        Assert.NotNull(PlanningCompactConstruction.DiscoveryContext(state)["describedOperations"]);
    }
}
