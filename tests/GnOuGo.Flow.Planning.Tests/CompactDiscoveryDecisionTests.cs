using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
    [Fact]
    public async Task DiscoverySeparatesUnseenContinuationBatchesFromExactSingleDescriptionRereads()
    {
        var (state, runtime) = await Ready(presentationVersion: 3);
        AddOperations(state, 3);
        foreach (var capability in state.Discovery.Pages[0].Capabilities)
            capability.Operation!.Description = new string('a', 3000) + " Preserve the complete observation.";
        var addresses = PlanningCompactConstruction.ContractDescriptors(state, count: 3)
            .Select(item => item!["descriptionEvidence"]!.ToString()).ToArray();
        runtime.Respond = (request, _) => Wire(request, Evidence(addresses));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var originals = state.Construction!.Evidence!.ToJsonString();
        var continuations = addresses.Select(address => state.Construction.Evidence[address]!["next"]!.ToString()).ToArray();
        state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        var request = DiscoveryFollowup(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!, strict: true));
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.DiscoveryPurpose).MaxInputTokens;
        var measurement = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, limit);
        Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(component => component.SerializedBytes));
        Assert.InRange(measurement.EstimatedInputTokens, 1, limit);
        Assert.Contains("partial descriptions do not establish unavailable behavior", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Follow necessary original description continuations", request.Prompt, StringComparison.Ordinal);
        foreach (var address in addresses)
        {
            Assert.True(Context(request)["evidence"]![address]!["textOmitted"]!.GetValue<bool>());
            Assert.Empty(PlanningContractValidation.ValidateInstance(Wire(request, Evidence(address)).Json!, request.StructuredOutputSchema!));
        }
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Wire(request, Evidence(addresses)).Json!, request.StructuredOutputSchema!));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Wire(request, Evidence(addresses[0], continuations[0])).Json!, request.StructuredOutputSchema!));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Wire(request, Evidence(continuations)).Json!, request.StructuredOutputSchema!));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Wire(request, Evidence("description:invented@version:0")).Json!, request.StructuredOutputSchema!));
        Assert.Equal(originals, state.Construction.Evidence.ToJsonString());
        var accepted = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (actual, _) => Wire(actual, Evidence(continuations));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        foreach (var address in continuations) Assert.Contains("Preserve the complete observation.", state.Construction!.Evidence![address]!["text"]!.ToString(), StringComparison.Ordinal);
        runtime.Respond = (actual, _) => Wire(actual, Evidence(addresses[0]));
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.NotNull(PlanningCompactConstruction.DiscoveryContext(state)["evidence"]![addresses[0]]!["text"]);
        request = DiscoveryFollowup(state);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Wire(request, Evidence(addresses[0])).Json!, request.StructuredOutputSchema!));
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(state.Construction!.Checkpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviouslyIssuedDiscoveryBatchSchemaKeepsExactRecoveryAndSingleRereadAuthority(bool batch)
    {
        var (state, runtime) = await Ready(presentationVersion: 3);
        AddOperations(state, 2);
        var addresses = PlanningCompactConstruction.ContractDescriptors(state, count: 2)
            .Select(item => item!["descriptionEvidence"]!.ToString()).ToArray();
        runtime.Respond = (request, _) => Wire(request, Evidence(addresses));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        schema["properties"]!["evidenceRequests"] = PlanningSchemas.Nullable(PlanningSchemas.Array(new() { ["type"] = "string", ["maxLength"] = 2048 }, 1, 8));
        var issued = Complete(state, PlanningCompactConstruction.Request("Retained discovery instructions.",
            PlanningCompactConstruction.DiscoveryContext(state), schema, PlanningCompactConstruction.DiscoveryPurpose));
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued };
        state.ModelCalls++;
        var calls = state.ModelCalls;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var reads = state.Construction!.EvidenceRequests.ToArray();
        var evidence = state.Construction.Evidence!.ToJsonString();
        var accepted = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Wire(request, Evidence(batch ? addresses : [addresses[0]]));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        if (batch)
        {
            Assert.Equal(PlanningStatus.Stopped, state.Status);
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
            Assert.Equal(reads.Where(key => key.StartsWith("description-read:", StringComparison.Ordinal)),
                state.Construction!.EvidenceRequests.Where(key => key.StartsWith("description-read:", StringComparison.Ordinal)));
        }
        else
        {
            Assert.Empty(state.Diagnostics);
            Assert.Equal(reads.Count(key => key.StartsWith("description-read:", StringComparison.Ordinal)) + 1,
                state.Construction!.EvidenceRequests.Count(key => key.StartsWith("description-read:", StringComparison.Ordinal)));
        }
        Assert.All(reads, read => Assert.Contains(read, state.Construction!.EvidenceRequests));
        Assert.Equal(evidence, state.Construction!.Evidence!.ToJsonString());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EarlierDescriptionsAreNavigationOnlyWhenTheirExactRereadIsAvailable(int presentationVersion)
    {
        var (state, runtime) = await Ready(presentationVersion: presentationVersion);
        AddOperations(state, 1);
        var address = DescriptionAddress(state);
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var original = state.Construction!.Evidence![address]!.ToJsonString();
        Assert.Equal(original, PlanningCompactConstruction.DiscoveryContext(state)["evidence"]![address]!.ToJsonString());
        state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var presented = context["evidence"]![address]!;
        if (presentationVersion == 3)
        {
            Assert.Null(presented["text"]);
            Assert.True(presented["textOmitted"]!.GetValue<bool>());
            Assert.Equal(address, presented["evidence"]!.ToString());
            Assert.NotNull(context["describedOperations"]);
        }
        else Assert.Equal(original, presented.ToJsonString());
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var followup = DiscoveryFollowup(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Wire(followup, Evidence(address)).Json!, followup.StructuredOutputSchema!));

        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(original, state.Construction!.Evidence![address]!.ToJsonString());
        Assert.Equal(original, PlanningCompactConstruction.DiscoveryContext(state)["evidence"]![address]!.ToJsonString());
        Assert.Equal(2, state.Construction.EvidenceRequests.Count(key => key.StartsWith("description-read:", StringComparison.Ordinal)));
        Assert.Equal(0, state.ReplanAttempts);
        state = await Advance(state, runtime);
        if (presentationVersion == 2) Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code ==
            (presentationVersion == 3 ? "PLANNING_RESPONSE_INVALID" : "REPLAN_NO_PROGRESS"));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("cursor")]
    [InlineData("historical-read")]
    [InlineData("prerequisite")]
    [InlineData("rejected-reread")]
    public async Task DiscoveryKeepsOriginalTextWhenItCannotSafelyOfferAnotherRead(string condition)
    {
        var (state, runtime) = await Ready(presentationVersion: 3);
        AddOperations(state, 1);
        var address = DescriptionAddress(state);
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var original = state.Construction!.Evidence![address]!.ToJsonString();
        if (condition == "duplicate") state.Discovery.Pages.Add(state.Discovery.Pages[0] with { Cursor = "duplicate" });
        else if (condition == "cursor") state.Discovery.Pages[0] = state.Discovery.Pages[0] with { NextCursor = "different" };
        else
        {
            state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
            if (condition == "historical-read") state.Construction.EvidenceRequests.RemoveAll(key => key.StartsWith("description-read:", StringComparison.Ordinal));
            else if (condition == "rejected-reread")
            {
                var key = PlanningCompactConstruction.DescriptionReadKey(state, PlanningCompactConstruction.DiscoverySchema(state), Evidence(address));
                Assert.NotNull(key);
                state.Construction.Compact!.FailedActions.Add("rejected:" + key);
            }
            else state.Construction.Compact!.CurrentStep!.Prerequisites = [new() { Need = "Retain the inspected restriction.", EvidenceAddresses = [address] }];
        }
        Assert.Equal(original, PlanningCompactConstruction.DiscoveryContext(state)["evidence"]![address]!.ToJsonString());
    }

    [Fact]
    public async Task DiscoveryPresentationReducesPriorKnowledgeWithoutChangingMandatoryContractClosure()
    {
        var (state, _) = await Ready(presentationVersion: 3);
        AddOperations(state, 8);
        for (var index = 0; index < state.Discovery.Pages[0].Capabilities.Count; index++)
        {
            var capability = state.Discovery.Pages[0].Capabilities[index];
            var text = new string((char)('a' + index), 3800) + " Keep exact records; no remote publication.";
            capability.Operation!.Description = text;
            state.Discovery.Pages[0].Capabilities[index] = capability with { Description = text };
        }
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        foreach (var descriptor in PlanningCompactConstruction.ContractDescriptors(state, count: 8))
        {
            var address = descriptor!["descriptionEvidence"]!.ToString();
            while (true)
            {
                var key = PlanningCompactConstruction.DescriptionReadKey(state, schema, Evidence(address));
                PlanningConstruction.LoadEvidence(state, [address], key);
                if (state.Construction!.Evidence![address]!["next"] is not { } next) break;
                address = next.ToString();
            }
        }
        var originals = state.Construction!.Evidence!.ToJsonString();
        var reads = state.Construction.EvidenceRequests.ToArray();
        state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        var request = DiscoveryFollowup(state);
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.DiscoveryPurpose).MaxInputTokens;
        PlanningConstruction.AdmitEvidenceFollowup(state, request, PlanningCompactConstruction.DiscoveryPurpose);
        var legacy = PlannerFixture.Clone(state);
        legacy.Construction!.Compact!.PresentationVersion = 2;
        var unprojected = DiscoveryFollowup(legacy);
        Assert.True(PlanningRequestMeasurements.Measure(unprojected, PlanningCompactConstruction.DiscoveryPurpose, limit).EstimatedInputTokens > limit);
        Assert.True(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, limit).EstimatedInputTokens < limit);
        Assert.Equal(originals, state.Construction.Evidence.ToJsonString());
        Assert.Equal(reads, state.Construction.EvidenceRequests);

        var selected = state.Discovery.Pages[0].Capabilities[0];
        var contract = new PlanningCapability { Id = selected.Id, Version = selected.Version, Kind = "tool", StepType = "mcp.call",
            Server = "alpha", Method = selected.Name, Description = selected.Description,
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("exact") } },
                ["required"] = new JsonArray("mode"), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } };
        state.Discovery.Resolved.Add(contract);
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "operation"; step.OperationIds = [TaskOperations.Describe(contract).Id];
        var view = Assert.Single(PlanningCompactConstruction.ContractViews(state))!;
        Assert.Equal(selected.Description, view["description"]!.ToString());
        Assert.True(JsonNode.DeepEquals(contract.InputSchema, view["inputSchema"]));
        Assert.Equal(originals, state.Construction.Evidence.ToJsonString());
    }

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

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DescriptionChoicesAreBoundedAndDoNotReinjectLargeUnrelatedContractProse(int presentationVersion)
    {
        var (state, _) = await Ready(presentationVersion: presentationVersion);
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

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SavedDiscoveryWithoutDecisionChoicesResumesItsExactRequestAndAccounting(int presentationVersion)
    {
        var (state, runtime) = await Ready(presentationVersion: presentationVersion);
        AddOperations(state, 2);
        var address = DescriptionAddress(state);
        var descriptionKey = PlanningCompactConstruction.DescriptionReadKey(state, PlanningCompactConstruction.DiscoverySchema(state), Evidence(address));
        PlanningConstruction.LoadEvidence(state, [address], descriptionKey);
        state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.NotNull(context["describedOperations"]);
        if (presentationVersion == 3) Assert.True(context["evidence"]![address]!["textOmitted"]!.GetValue<bool>());
        // A historical issued view includes full original prose even when a
        // freshly constructed request would now offer its exact retrieval link.
        context["evidence"] = state.Construction!.Evidence!.DeepClone();
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
