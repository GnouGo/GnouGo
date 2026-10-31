using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedChoicesBeyondTheFirstWindowRemainExactlyRetrievable(bool withDiscoveryDirectory)
    {
        var (state, _) = await AcceptedDiscovery(true, 7);
        if (withDiscoveryDirectory)
            state.Discovery.Pages[0] = state.Discovery.Pages[0] with { Query = state.Construction!.Compact!.CurrentStep!.Objective };
        else state.Discovery.Pages.Clear();
        var expected = state.Plan!.Root.Tasks.Where(task => task.Kind == "operation").Select(task => task.Operation!).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var traversed = new HashSet<string>(StringComparer.Ordinal);
        var plan = PlanningConstruction.Hash(state.Plan);
        while (seen.Count != expected.Count)
        {
            var choices = PlanningCompactConstruction.DescribedOperationChoices(state)!;
            seen.UnionWith(choices["items"]!.AsArray().Select(item => item!["id"]!.ToString()));
            if (seen.Count == expected.Count) break;
            var next = Assert.IsAssignableFrom<JsonValue>(choices["next"]).ToString();
            Assert.True(traversed.Add(next), "Every omitted choice must expose an unconsumed exact continuation.");
            var request = PlanningCompactConstruction.Request(state, PlanningCompactConstruction.DiscoveryGuidance(state),
                PlanningCompactConstruction.DiscoveryContext(state), PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose);
            Assert.Contains(next, request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
            PlanningConstruction.LoadEvidence(state, [next], issuedDiscoverySchema: request.StructuredOutputSchema);
            state = PlannerFixture.Clone(state);
            var retrieved = state.Construction!.Evidence![next]!;
            if (retrieved["items"] is JsonArray rows) seen.UnionWith(rows.Select(row => row!["id"]!.ToString()));
            else seen.Add(retrieved["id"]!.ToString());
        }
        Assert.True(expected.SetEquals(seen));
        Assert.NotEmpty(traversed);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.OperationIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedAcceptedOperationCanBindWithoutRepeatingProviderOrDescriptionReads(bool dependency)
    {
        var (state, runtime) = await AcceptedDiscovery(dependency);
        var provider = new CountingContinuationCatalog();
        runtime.Capabilities = provider;
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var canonical = PlanningCompactConstruction.OperationPageAddress(state, 0);
        var request = DiscoveryFollowup(state, out var evidenceAddresses);
        var host = HostView(request, evidenceAddresses);
        var context = Context(host);
        Assert.Null(context["descriptors"]);
        var choice = Assert.Single(context["describedOperations"]!["items"]!.AsArray())!;
        Assert.Equal("operation_0", choice["id"]!.ToString());
        Assert.Equal("version_0", choice["version"]!.ToString());
        Assert.Equal("description:operation_0@version_0:0", choice["descriptionEvidence"]!.ToString());
        Assert.Contains(choice["descriptionEvidence"]!.ToString(), host.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(canonical, PlanningCompactConstruction.OperationPageAddress(state, 0));
        var baseline = PlanningConstruction.Hash(state.Plan);
        var calls = runtime.Calls.Count;
        runtime.Respond = (issued, _) => Wire(issued, CachedBinding());
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, runtime.Calls.Count);
        Assert.Equal(0, provider.ListCalls);
        Assert.Equal(new[] { "operation_0" }, state.Construction!.Compact!.CurrentStep!.OperationIds);
        Assert.True(state.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Equal("operation", state.Construction.Compact.CurrentStep.Kind);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task CachedProducerDescriptionRetrievalUsesIssuedExactLinkWithoutSelectingAnOperation()
    {
        var (state, runtime) = await AcceptedDiscovery(false);
        var provider = new CountingContinuationCatalog();
        runtime.Capabilities = provider;
        var retained = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var step = JsonSerializer.Serialize(state.Construction!.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var canonical = PlanningCompactConstruction.OperationPageAddress(state, 0);
        runtime.Respond = (issued, _) =>
        {
            var address = Context(issued)["describedOperations"]!["items"]![0]!["descriptionEvidence"]!.ToString();
            var response = new JsonObject { ["binding"] = null, ["discoveryRequests"] = null, ["evidenceRequests"] = new JsonArray(address),
                ["reselectStep"] = null, ["clarifications"] = null };
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, issued.StructuredOutputSchema!));
            return Wire(issued, response);
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("Read exact original records with declared restrictions.", state.Construction!.Evidence!["description:operation_0@version_0:0"]!["text"]!.ToString());
        Assert.Equal(0, provider.ListCalls);
        Assert.Equal(step, JsonSerializer.Serialize(state.Construction.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
        Assert.Equal(retained, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Equal(canonical, PlanningCompactConstruction.OperationPageAddress(state, 0));
        Assert.Empty(state.Construction.Compact.PendingCommitments);
    }

    [Fact]
    public async Task CachedMetadataDoesNotAddUnselectedAcceptedSiblingsToTheConsumersBindings()
    {
        var (state, _) = await AcceptedDiscovery(true, 3);
        var step = state.Construction!.Compact!.CurrentStep!;
        step.DependencyTaskIds = ["producer_0"];
        PlanningCompactConstruction.ValidateSelection(state, step);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.Equal(new[] { "operation_0", "operation_1", "operation_2" },
            context["describedOperations"]!["items"]!.AsArray().Select(item => item!["id"]!.ToString()));
        Assert.Equal(new[] { "producer_0" }, step.InputTaskIds);
        Assert.Empty(step.OperationIds);
        var schema = HostView(DiscoveryFollowup(state, out var evidenceAddresses), evidenceAddresses).StructuredOutputSchema!.ToJsonString();
        Assert.Contains("description:operation_1@version_1:0", schema, StringComparison.Ordinal);
        Assert.Contains("description:operation_2@version_2:0", schema, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("step-type")]
    [InlineData("ambiguous")]
    [InlineData("future")]
    public async Task CachedChoicesExcludeUnavailableOrUnselectedAcceptedOperations(string change)
    {
        var (state, _) = await AcceptedDiscovery(true);
        var compact = state.Construction!.Compact!;
        var capability = state.Catalog!.Capabilities.Single();
        switch (change)
        {
            case "denied": state.Catalog.Policy.DeniedCapabilityIds.Add(capability.Id); break;
            case "step-type": state.Catalog.AllowedStepTypes.Clear(); break;
            case "ambiguous":
                var second = JsonSerializer.Deserialize(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability), PlanningJsonContext.Default.PlanningCapability)!;
                second.Version = "conflicting";
                state.Discovery.Resolved.Add(second);
                break;
            case "future": state.Plan!.Root.Tasks.RemoveAll(task => task.Id == "producer_0"); break;
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Null(PlanningCompactConstruction.DescribedOperationChoices(state));
        Assert.False(PlanningCompactConstruction.DiscoveryContext(state).ContainsKey("describedOperations"));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedMetadataRemainsRetrievableWithoutAResolvedOrSelectedProducer(bool unresolved)
    {
        var (state, _) = await AcceptedDiscovery(true);
        if (unresolved) state.Catalog!.Capabilities.Clear();
        else
        {
            state.Construction!.Compact!.CurrentStep!.DependencyTaskIds.Clear();
            state.Construction.Compact.CurrentStep.InputTaskIds.Clear();
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var choice = Assert.Single(PlanningCompactConstruction.DescribedOperationChoices(state)!["items"]!.AsArray())!;
        Assert.Equal("operation_0", choice["id"]!.ToString());
        Assert.Equal("description:operation_0@version_0:0", choice["descriptionEvidence"]!.ToString());
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.OperationIds);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task CachedChoicesDeduplicateSelectedProducersAndBoundWholeRowsWithoutChangingCanonicalPages()
    {
        var (state, _) = await AcceptedDiscovery(true, 7);
        var step = state.Construction!.Compact!.CurrentStep!;
        state.Plan!.Root.Tasks.Add(new() { Id = "same_operation", Kind = "operation", Operation = "operation_0", Objective = Objective });
        step.DependencyTaskIds.Add("same_operation");
        PlanningCompactConstruction.ValidateSelection(state, step);
        var page = PlanningCompactConstruction.ContractDescriptorPage(state).ToJsonString();
        var address = PlanningCompactConstruction.OperationPageAddress(state, 0);
        var choices = PlanningCompactConstruction.DescribedOperationChoices(state)!;
        Assert.Equal(7, choices["total"]!.GetValue<int>());
        Assert.False(choices["complete"]!.GetValue<bool>());
        Assert.Equal(4, choices["items"]!.AsArray().Count);
        Assert.Equal(4, choices["items"]!.AsArray().Select(item => item!["id"]!.ToString()).Distinct().Count());
        state.Catalog!.Capabilities[0].Method = new string('x', 5000);
        choices = PlanningCompactConstruction.DescribedOperationChoices(state)!;
        Assert.Equal(7, choices["total"]!.GetValue<int>());
        Assert.InRange(Encoding.UTF8.GetByteCount(choices.ToJsonString()), 1, 2048);
        Assert.DoesNotContain(choices["items"]!.AsArray(), item => item!["name"]!.ToString().Length >= 5000);
        Assert.Equal(page, PlanningCompactConstruction.ContractDescriptorPage(state).ToJsonString());
        Assert.Equal(address, PlanningCompactConstruction.OperationPageAddress(state, 0));
    }


    [Fact]
    public async Task PendingDiscoveryKeepsItsExactIssuedBytesAfterContractAvailabilityChanges()
    {
        var (state, runtime) = await AcceptedDiscovery(true);
        var capability = state.Catalog!.Capabilities.Single();
        state.Catalog.Capabilities.Clear();
        var issued = DiscoveryFollowup(state, out var evidenceAddresses);
        state.Catalog.Capabilities.Add(capability);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued, EvidenceAddresses = evidenceAddresses };
        runtime.CaptureIssued(state.PendingCall);
        state.ModelCalls++;
        var calls = state.ModelCalls;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal("operation_0", Assert.Single(Context(request)["describedOperations"]!["items"]!.AsArray())!["id"]!.ToString());
            return Wire(request, CachedBinding());
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Null(state.PendingCall);
        Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete);
    }

    private static JsonObject CachedBinding() => new()
    {
        ["binding"] = new JsonObject { ["kind"] = "operation", ["operationIds"] = new JsonArray("operation_0"), ["groupId"] = null },
        ["discoveryRequests"] = null, ["evidenceRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null
    };

    private static async Task<(PlanningSession State, TestRuntime Runtime)> AcceptedDiscovery(bool dependency, int count = 1)
    {
        var (state, runtime) = await Ready();
        AddOperations(state, count);
        foreach (var summary in state.Discovery.Pages[0].Capabilities)
        {
            state.Catalog!.Capabilities.Add(new() { Id = summary.Id, Version = summary.Version, Kind = "tool", StepType = summary.StepType,
                Server = summary.SourceId, Method = summary.Name, Description = summary.Description,
                InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
                OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } });
            state.Plan!.Root.Tasks.Add(new() { Id = "producer_" + state.Catalog.Capabilities.IndexOf(state.Catalog.Capabilities[^1]),
                Kind = "operation", Objective = "Earlier accepted business action.", Operation = summary.Operation!.Id });
        }
        state.Discovery.Pages[0] = state.Discovery.Pages[0] with { Query = "Earlier accepted business action." };
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = null;
        if (dependency) step.DependencyTaskIds = Enumerable.Range(0, count).Select(index => "producer_" + index).ToList();
        else step.Inputs = [new() { Kind = "output", Source = "producer_0" }];
        PlanningCompactConstruction.ValidateSelection(state, step);
        state.Construction.Evidence = null;
        state.Construction.EvidenceRequests.Clear();
        return (state, runtime);
    }
}
