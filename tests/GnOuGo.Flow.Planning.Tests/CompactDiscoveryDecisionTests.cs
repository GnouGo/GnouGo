using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
    [Fact]
    public async Task ExactEvidenceAddressesFactorOnlyRedundantVersionsWithoutChangingOriginalsOrRecovery()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 4);
        for (var index = 0; index < 4; index++)
        {
            var capability = state.Discovery.Pages[0].Capabilities[index];
            capability.Operation!.Version = new string((char)('a' + index), 64);
            capability.Operation.Description = new string('x', 2500) + " Preserve exact records and permissions.";
            state.Discovery.Pages[0].Capabilities[index] = capability with { Version = capability.Operation.Version };
        }
        var directory = PlanningCompactConstruction.OperationPageAddress(state, 0);
        var description = DescriptionAddress(state);
        var source = PlanningCompactConstruction.ProviderRequestPage(state, 0)["items"]![0]!["descriptionEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [directory, description, source]);
        var original = state.Construction!.Evidence!.DeepClone().AsObject();
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var request = DiscoveryFollowup(state);
        var view = Context(request)["evidence"]!.AsObject();
        var expected = original.DeepClone().AsObject();
        expected[description]!.AsObject().Remove("version");
        expected[source]!.AsObject().Remove("version");
        foreach (var item in expected[directory]!["items"]!.AsArray()) item!.AsObject().Remove("version");
        Assert.Equal(expected.ToJsonString(), view.ToJsonString());
        Assert.Equal(saved, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var control = PlannerFixture.Clone(state);

        var unprojected = DiscoveryFollowup(control);
        var unprojectedContext = Context(unprojected);
        unprojectedContext["evidence"] = original.DeepClone();
        unprojected.Prompt = unprojected.Prompt[..(unprojected.Prompt.LastIndexOf('\n') + 1)] + unprojectedContext.ToJsonString();
        unprojected = PlanningModelCalls.PrepareRequest(control, PlanningCompactConstruction.DiscoveryPurpose,
            unprojected.Prompt, unprojected.StructuredOutputSchema!.AsObject());
        Assert.Equal(original.ToJsonString(), Context(unprojected)["evidence"]!.ToJsonString());
        Assert.Equal(unprojected.StructuredOutputSchema!.ToJsonString(), request.StructuredOutputSchema!.ToJsonString());
        // This comparison isolates metadata. Keep both grammar and instructions
        // fixed, without changing the original issued request used below.
        var measuredControl = PlanningModelCalls.PrepareRequest(control, PlanningCompactConstruction.DiscoveryPurpose,
            request.Prompt[..(request.Prompt.LastIndexOf('\n') + 1)] +
                unprojected.Prompt[(unprojected.Prompt.LastIndexOf('\n') + 1)..], unprojected.StructuredOutputSchema.AsObject());
        var before = PlanningRequestMeasurements.Measure(measuredControl, PlanningCompactConstruction.DiscoveryPurpose, 6000);
        var after = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, 6000);
        Assert.Equal(after.CompleteRequestBytes, after.Components.Sum(component => component.SerializedBytes));
        Assert.True(after.CompleteRequestBytes < before.CompleteRequestBytes - 400);

        // An already-issued full-page request remains authoritative after the
        // presentation improvement; recovery must not rebuild its prompt.
        var next = original[description]!["next"]!.ToString();
        var exact = JsonSerializer.Serialize(unprojected, PlanningJsonContext.Default.LLMRequest);
        state.PendingCall = new() { Id = unprojected.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose,
            Request = unprojected, CorrectionAccounting = 0 };
        state.ModelCalls++;
        var calls = state.ModelCalls;
        runtime.Respond = (issued, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            return Wire(issued, Evidence(next));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        foreach (var (address, page) in original)
            Assert.Equal(page!.ToJsonString(), state.Construction!.Evidence![address]!.ToJsonString());
        Assert.NotNull(state.Construction!.Evidence![next]!["version"]);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("directory-hash")]
    [InlineData("directory-link")]
    public async Task UnverifiedEvidenceKeepsItsVersionPresentation(string changed)
    {
        var (state, _) = await Ready();
        AddOperations(state, 1);
        var address = changed == "description" ? DescriptionAddress(state) : PlanningCompactConstruction.OperationPageAddress(state, 0);
        PlanningConstruction.LoadEvidence(state, [address]);
        var page = state.Construction!.Evidence![address]!.AsObject();
        if (changed == "description") page["version"] = "different";
        else if (changed == "directory-hash") page["items"]![0]!["name"] = "Changed original row";
        else
        {
            page["items"]![0]!["descriptionEvidence"] = "description:unrelated@version:0";
            state.Construction.Evidence.Remove(address);
            address = "page:operations:" + PlanningGraphCompiler.Fingerprint(page.ToJsonString()) + ":0";
            state.Construction.Evidence[address] = page;
        }
        var original = page.ToJsonString();
        Assert.Equal(original, PlanningCompactConstruction.DiscoveryContext(state)["evidence"]![address]!.ToJsonString());
        Assert.Equal(original, state.Construction.Evidence[address]!.ToJsonString());
    }

    [Theory]
    [InlineData(true, "/evidenceRequests", true)]
    [InlineData(false, "/evidenceRequests", false)]
    [InlineData(true, "/operations", false)]
    public async Task OnlyCurrentRequiredDiscoveryAdmissionFailureNarrowsUnseenEvidence(bool required, string location, bool singleton)
    {
        var (state, _) = await Ready();
        AddOperations(state, 2);
        var addresses = PlanningCompactConstruction.ContractDescriptors(state, count: 2)
            .Select(item => item!["descriptionEvidence"]!.ToString()).ToArray();
        var original = PlanningCompactConstruction.DiscoverySchema(state);
        state.Diagnostics = [new("CONSTRUCTION_EVIDENCE_LIMIT", location, "The complete prospective request exceeded its captured ceiling.", required)];
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        var request = DiscoveryFollowup(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Wire(request, Evidence(addresses[0])).Json!, schema));
        Assert.Equal(singleton, PlanningContractValidation.ValidateInstance(Wire(request, Evidence(addresses)).Json!, schema).Count != 0);
        if (!singleton) Assert.Equal(original.ToJsonString(), schema.ToJsonString());
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedDiscoveryBatchRecoversThroughAdmittedSingletonWithoutLosingEvidenceOrAccounting(bool recover)
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 3);
        foreach (var capability in state.Discovery.Pages[0].Capabilities)
            capability.Operation!.Description = new string('x', 1800) + " Preserve the complete observation.";
        var addresses = PlanningCompactConstruction.ContractDescriptors(state, count: 3)
            .Select(item => item!["descriptionEvidence"]!.ToString()).ToArray();
        var single = PlannerFixture.Clone(state);
        PlanningConstruction.LoadEvidence(single, [addresses[0]]);
        var batch = PlannerFixture.Clone(state);
        PlanningConstruction.LoadEvidence(batch, [.. addresses]);
        var singleSize = PlanningRequestMeasurements.Measure(DiscoveryFollowup(single), PlanningCompactConstruction.DiscoveryPurpose, 6000).EstimatedInputTokens;
        var batchSize = PlanningRequestMeasurements.Measure(DiscoveryFollowup(batch), PlanningCompactConstruction.DiscoveryPurpose, 6000).EstimatedInputTokens;
        Assert.True(batchSize > singleSize + 400);
        // Freeze a fixture ceiling that admits the exact singleton, but not the
        // complete three-page follow-up, before either evidence response is issued.
        var policy = state.Construction!.Limits.PhasePolicy!;
        policy.Navigation.MaxInputTokens = checked((int)((singleSize + batchSize) / 2));
        policy.Navigation.TargetInputTokens = Math.Min(policy.Navigation.TargetInputTokens, policy.Navigation.MaxInputTokens);
        state.Request.Options["construction_limits"]!["phasePolicy"] = JsonSerializer.SerializeToNode(policy, PlanningJsonContext.Default.PlanningConstructionPhasePolicy);
        var settings = state.Request.Options["construction_limits"]!.ToJsonString();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var originals = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var reads = state.Construction.EvidenceRequests.ToArray();
        runtime.Respond = (request, _) => Wire(request, Evidence(addresses));
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_LIMIT" && diagnostic.Required);
        Assert.Null(state.Construction!.Evidence);
        Assert.All(reads, read => Assert.Contains(read, state.Construction.EvidenceRequests));
        foreach (var address in addresses)
            Assert.DoesNotContain(PlanningConstruction.EvidenceBatchFingerprint(state, [address]), state.Construction.EvidenceRequests);
        Assert.Contains(state.RequestMeasurements!, measurement => measurement.Admission == "rejected_before_dispatch" && measurement.EstimatedInputTokens > measurement.InputLimit);
        var calls = state.ModelCalls;
        var corrections = state.ReplanAttempts;
        var draftCorrections = state.Construction.DraftCorrections;
        runtime.Respond = (request, _) =>
        {
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Wire(request, Evidence(addresses)).Json!, request.StructuredOutputSchema!));
            return Wire(request, Evidence(addresses[0]));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        if (recover)
        {
            var issued = runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall is { Purpose: PlanningCompactConstruction.DiscoveryPurpose } &&
                checkpoint.Diagnostics.Any(diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_LIMIT"));
            var exact = JsonSerializer.Serialize(issued.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
            var receipt = Wire(issued.PendingCall.Request, Evidence(addresses[0]));
            runtime.Respond = (request, _) =>
            {
                Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                return receipt;
            };
            state = await Advance(PlannerFixture.Clone(issued), runtime);
        }
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(corrections, state.ReplanAttempts);
        Assert.Equal(draftCorrections, state.Construction!.DraftCorrections);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(originals, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Equal(settings, state.Request.Options["construction_limits"]!.ToJsonString());
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Null(state.Construction.Candidate);
        Assert.Single(state.Construction.Evidence!);
        Assert.Contains("Preserve the complete observation.", state.Construction.Evidence![addresses[0]]!["text"]!.ToString(), StringComparison.Ordinal);
        Assert.Contains(PlanningConstruction.EvidenceBatchFingerprint(state, [addresses[0]]), state.Construction.EvidenceRequests);
        var next = DiscoveryFollowup(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Wire(next, Evidence(addresses[1..])).Json!, next.StructuredOutputSchema!));
        var measurement = PlanningRequestMeasurements.Measure(next, PlanningCompactConstruction.DiscoveryPurpose, policy.Navigation.MaxInputTokens);
        Assert.InRange(measurement.EstimatedInputTokens, 1, measurement.InputLimit);
        Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(component => component.SerializedBytes));
    }

    [Fact]
    public async Task AlreadyIssuedBatchKeepsItsExactBytesWhenAdmissionDiagnosticArrivesAfterIssuance()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 2);
        var addresses = PlanningCompactConstruction.ContractDescriptors(state, count: 2)
            .Select(item => item!["descriptionEvidence"]!.ToString()).ToArray();
        var issued = DiscoveryFollowup(state);
        var receipt = Wire(issued, Evidence(addresses));
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued, CorrectionAccounting = 0 };
        state.ModelCalls++;
        var calls = state.ModelCalls;
        var accepted = PlanningConstruction.Hash(state.Plan);
        state.Diagnostics = [new("CONSTRUCTION_EVIDENCE_LIMIT", "/evidenceRequests", "A later diagnostic cannot replace the issued schema.")];
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(receipt.Json!, PlanningCompactConstruction.DiscoverySchema(state)));
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return receipt;
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, state.Construction!.DraftCorrections);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(2, state.Construction.Evidence!.Count);
        Assert.Null(state.PendingCall);
    }

    [Fact]
    public async Task DiscoverySeparatesUnseenContinuationBatchesFromExactSingleDescriptionRereads()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 2);
        state.Discovery.Pages[0].Capabilities[0].Operation!.Description = new string('a', 3000) + " Preserve the complete observation.";
        var addresses = PlanningCompactConstruction.ContractDescriptors(state, count: 2)
            .Select(item => item!["descriptionEvidence"]!.ToString()).ToArray();
        runtime.Respond = (request, _) => Wire(request, Evidence(addresses));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var originals = state.Construction!.Evidence!.ToJsonString();
        var continuations = addresses.Select(address => state.Construction.Evidence[address]!["next"]?.ToString()).OfType<string>().ToArray();
        state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        var request = DiscoveryFollowup(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!, strict: true));
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.DiscoveryPurpose).MaxInputTokens;
        var measurement = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, limit);
        Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(component => component.SerializedBytes));
        Assert.InRange(measurement.EstimatedInputTokens, 1, limit);
        Assert.All(continuations, address => Assert.Contains(address, request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal));
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
        var (state, runtime) = await Ready();
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

    [Fact]
    public async Task EarlierDescriptionsAreNavigationOnlyWhenTheirExactRereadIsAvailable()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 1);
        var address = DescriptionAddress(state);
        runtime.Respond = (request, _) => Wire(request, Evidence(address));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var original = state.Construction!.Evidence![address]!.ToJsonString();
        var presentedOriginal = JsonNode.Parse(original)!.AsObject(); presentedOriginal.Remove("version");
        Assert.Equal(presentedOriginal.ToJsonString(), PlanningCompactConstruction.DiscoveryContext(state)["evidence"]![address]!.ToJsonString());
        state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var presented = context["evidence"]![address]!;
        Assert.Null(presented["text"]);
        Assert.True(presented["textOmitted"]!.GetValue<bool>());
        Assert.Equal(address, presented["evidence"]!.ToString());
        Assert.NotNull(context["describedOperations"]);
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var followup = DiscoveryFollowup(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Wire(followup, Evidence(address)).Json!, followup.StructuredOutputSchema!));

        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(original, state.Construction!.Evidence![address]!.ToJsonString());
        Assert.Equal(presentedOriginal.ToJsonString(), PlanningCompactConstruction.DiscoveryContext(state)["evidence"]![address]!.ToJsonString());
        Assert.Equal(2, state.Construction.EvidenceRequests.Count(key => key.StartsWith("description-read:", StringComparison.Ordinal)));
        Assert.Equal(0, state.ReplanAttempts);
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "PLANNING_RESPONSE_INVALID");
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("cursor")]
    [InlineData("prerequisite")]
    [InlineData("resuming-prerequisite")]
    [InlineData("rejected-reread")]
    public async Task DiscoveryKeepsOriginalTextWhenItCannotSafelyOfferAnotherRead(string condition)
    {
        var (state, runtime) = await Ready();
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
            if (condition == "rejected-reread")
            {
                var key = PlanningCompactConstruction.DescriptionReadKey(state, PlanningCompactConstruction.DiscoverySchema(state), Evidence(address));
                Assert.NotNull(key);
                state.Construction.Compact!.FailedActions.Add("rejected:" + key);
            }
            else if (condition == "resuming-prerequisite")
            {
                // New provider knowledge would otherwise replace this exact read
                // with navigation. The restored consumer keeps its typed need on
                // the continuation, separately from its bounded original notes.
                Assert.True(PlanningCompactConstruction.DiscoveryContext(state)["evidence"]![address]!["textOmitted"]!.GetValue<bool>());
                var compact = state.Construction.Compact!;
                var consumer = compact.CurrentStep!;
                Assert.Empty(consumer.Prerequisites ?? []);
                compact.ResumingStepId = "continuation_1";
                compact.SuspendedSteps = [new()
                {
                    Id = "continuation_1", Status = "resuming", Consumer = consumer,
                    Prerequisite = new() { Need = "Retain the inspected restriction.", ExpectedType = new() { Kind = "string" }, EvidenceAddresses = [address] }
                }];
                var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
                var schema = PlanningCompactConstruction.DiscoverySchema(state);
                Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonArray(address), schema["properties"]!["evidenceRequests"]!));
                Assert.Equal(schema.ToJsonString(), PlanningCompactConstruction.DiscoverySchema(PlannerFixture.Clone(state)).ToJsonString());
                Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
            }
            else state.Construction.Compact!.CurrentStep!.Prerequisites = [new() { Need = "Retain the inspected restriction.", EvidenceAddresses = [address] }];
        }
        var expected = JsonNode.Parse(original)!.AsObject();
        expected.Remove("version"); // The exact current address carries the contract version.
        Assert.Equal(expected.ToJsonString(), PlanningCompactConstruction.DiscoveryContext(state)["evidence"]![address]!.ToJsonString());
        Assert.Equal(original, state.Construction.Evidence![address]!.ToJsonString());
    }

    [Fact]
    public async Task DiscoveryPresentationReducesPriorKnowledgeWithoutChangingMandatoryContractClosure()
    {
        var (state, _) = await Ready();
        AddOperations(state, 4);
        for (var index = 0; index < state.Discovery.Pages[0].Capabilities.Count; index++)
        {
            var capability = state.Discovery.Pages[0].Capabilities[index];
            var text = new string((char)('a' + index), 3800) + " Keep exact records; no remote publication.";
            capability.Operation!.Description = text;
            state.Discovery.Pages[0].Capabilities[index] = capability with { Description = text };
        }
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        foreach (var descriptor in PlanningCompactConstruction.ContractDescriptors(state, count: 4))
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
        var unprojected = DiscoveryFollowup(state);
        var unprojectedContext = Context(unprojected); unprojectedContext["evidence"] = state.Construction.Evidence.DeepClone();
        unprojected.Prompt = unprojected.Prompt[..(unprojected.Prompt.LastIndexOf('\n') + 1)] + unprojectedContext.ToJsonString();
        var before = PlanningRequestMeasurements.Measure(unprojected, PlanningCompactConstruction.DiscoveryPurpose, limit);
        var after = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, limit);
        Assert.True(after.CompleteRequestBytes < before.CompleteRequestBytes);
        Assert.InRange(after.EstimatedInputTokens, 1, limit);
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
        Assert.NotNull(context["descriptors"]);
        Assert.Equal(state.Construction!.Evidence!.Count, context["evidence"]!.AsObject().Count);
        foreach (var (address, originalEvidence) in state.Construction.Evidence)
        {
            var expectedEvidence = originalEvidence!.DeepClone().AsObject();
            if (address.StartsWith("description:", StringComparison.Ordinal)) expectedEvidence.Remove("version");
            Assert.True(JsonNode.DeepEquals(expectedEvidence, context["evidence"]![address]));
        }
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
        var inspectedIds = descriptors.Take(7).Select(descriptor => descriptor!["id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        var unrelatedIndex = state.Discovery.Pages[0].Capabilities.FindIndex(capability => !inspectedIds.Contains(capability.Operation!.Id));
        var unrelated = state.Discovery.Pages[0].Capabilities[unrelatedIndex];
        var huge = new string('界', 100_000);
        unrelated.Operation!.Description = huge;
        state.Discovery.Pages[0].Capabilities[unrelatedIndex] = unrelated with { Description = huge };
        PlanningConstruction.LoadEvidence(state, ["page:sources:0"]);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var choices = context["describedOperations"]!.AsObject();
        Assert.Equal(7, choices["total"]!.GetValue<int>());
        Assert.False(choices["complete"]!.GetValue<bool>());
        Assert.Equal(4, choices["items"]!.AsArray().Count);
        Assert.DoesNotContain(huge, context.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(huge, state.Discovery.Pages[0].Capabilities[unrelatedIndex].Operation!.Description);
        var request = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
            context, PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose));
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.DiscoveryPurpose).MaxInputTokens;
        var measurement = PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.DiscoveryPurpose, limit);
        Assert.InRange(measurement.EstimatedInputTokens, 1, limit);
        Assert.Equal(state.Construction!.Evidence!.Count, context["evidence"]!.AsObject().Count);
        foreach (var (address, evidence) in state.Construction.Evidence)
        {
            var expected = evidence!.DeepClone().AsObject();
            if (address.StartsWith("description:", StringComparison.Ordinal) || address.StartsWith("source-description:", StringComparison.Ordinal)) expected.Remove("version");
            Assert.True(JsonNode.DeepEquals(expected, context["evidence"]![address]), address + ": " + expected.ToJsonString() + " != " + context["evidence"]![address]!.ToJsonString());
        }
    }

    [Fact]
    public async Task SavedDiscoveryWithoutDecisionChoicesResumesItsExactRequestAndAccounting()
    {
        var (state, runtime) = await Ready();
        AddOperations(state, 2);
        var address = DescriptionAddress(state);
        var descriptionKey = PlanningCompactConstruction.DescriptionReadKey(state, PlanningCompactConstruction.DiscoverySchema(state), Evidence(address));
        PlanningConstruction.LoadEvidence(state, [address], descriptionKey);
        state.Discovery.Pages.Add(new("alpha", "more", [AdditionalOperation(state)], null, Query: Objective));
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.NotNull(context["describedOperations"]);
        Assert.True(context["evidence"]![address]!["textOmitted"]!.GetValue<bool>());
        // A historical issued view includes full original prose even when a
        // freshly constructed request would now offer its exact retrieval link.
        context["evidence"] = state.Construction!.Evidence!.DeepClone();
        context.Remove("describedOperations");
        var issued = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
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
