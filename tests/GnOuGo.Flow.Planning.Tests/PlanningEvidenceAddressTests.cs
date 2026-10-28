using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningEvidenceAddressTests
{
    private const string First = "description:operation@0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef:0";
    private const string Second = "port-page:operation@0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef:/output:8";

    [Fact]
    public async Task ActualRecursiveDraftGrammarRemainsBoundedAndPreservesValueReferences()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var context = PlanningCompactConstruction.Context(state);
        PlanningCompactConstruction.ConstrainDraftEvidence(state, context, schema);
        var request = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose,
            "Construct the next actual fragment.\n" + context.ToJsonString(), schema);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(request, PlanningJsonContext.Default.LLMRequest).Length, 1, 100_000);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!, strict: true));
        Assert.Contains("$ref", request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("task:compute", request.StructuredOutputSchema.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssuedMappingIsPersistedBeforeDispatchAndRecoversWithoutRebuilding()
    {
        var state = PlannerFixture.Session();
        var runtime = new TestRuntime { Checkpointed = current =>
        {
            Assert.NotNull(current.PendingCall!.EvidenceAddresses);
            throw new OperationCanceledException("interrupted after durable issuance");
        } };
        await Assert.ThrowsAsync<OperationCanceledException>(() => Call(state, runtime));
        Assert.Empty(runtime.Calls);
        Assert.Equal(1, state.ModelCalls);
        var recovered = PlannerFixture.Clone(state);
        var issued = JsonSerializer.Serialize(recovered.PendingCall, PlanningJsonContext.Default.PlanningModelCall);
        var shortId = recovered.PendingCall!.EvidenceAddresses!.Addresses.Single(pair => pair.Value == First).Key;
        var canonicalRequest = PlanningModelCalls.CanonicalEvidenceRequest(recovered.PendingCall);
        var canonicalContext = JsonNode.Parse(canonicalRequest.Prompt[(canonicalRequest.Prompt.LastIndexOf('\n') + 1)..])!;
        Assert.Equal(First, canonicalContext["operations"]![0]!["descriptionEvidence"]!.ToString());
        Assert.Equal(issued, JsonSerializer.Serialize(recovered.PendingCall, PlanningJsonContext.Default.PlanningModelCall));
        var receipt = Response(shortId);
        var rawHash = PlanningGraphCompiler.Fingerprint(receipt.ToJsonString());
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(recovered.PendingCall!.Request.Prompt, request.Prompt);
            return new() { Json = receipt };
        } };
        var result = await PlanningModelCalls.CallAsync(recovered, recovery, PlanningCompactConstruction.DiscoveryPurpose,
            "Newer context must not replace the issued request.", new JsonObject(), PlannerFixture.Ct);
        Assert.Equal(First, result["evidenceRequests"]![0]!.ToString());
        Assert.Equal(rawHash, PlanningGraphCompiler.Fingerprint(receipt.ToJsonString()));
        Assert.Equal(issued, JsonSerializer.Serialize(state.PendingCall, PlanningJsonContext.Default.PlanningModelCall));
        Assert.Single(recovery.Calls);
        Assert.Equal(1, recovered.ModelCalls);
        Assert.Null(recovered.PendingCall);
    }

    [Fact]
    public void MappedRequestRetainsTheSharedProductionJournalFingerprintSuffix()
    {
        var state = PlannerFixture.Session();
        var request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose,
            "Read exact evidence.\n" + Context().ToJsonString(), Schema(), null, null, out var mapping);
        var exactId = request.ClientRequestId!;
        request.ClientRequestId = null;
        var fingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        request.ClientRequestId = exactId;
        Assert.Equal(mapping!.RequestBasisHash, fingerprint);
        Assert.Equal(state.Request.SessionId + ":1:" + mapping.MapHash + ":" + fingerprint, exactId);
        PlanningModelCalls.ValidateEvidenceAddresses(new() { Id = exactId, Request = request, EvidenceAddresses = mapping });
    }

    [Fact]
    public async Task PreparationMeasuresFinalShortIdsAndPreservesOriginalContractAndTaskValues()
    {
        var state = PlannerFixture.Session();
        var raw = Context();
        var contract = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
        {
            ["evidence"] = new JsonObject { ["type"] = "string", ["const"] = First, ["description"] = First }
        }, ["required"] = new JsonArray("evidence") };
        raw["contract"] = new JsonObject { ["schema"] = contract.DeepClone() };
        raw["value"] = new JsonObject { ["kind"] = "output", ["source"] = "producer", ["port"] = "records" };
        raw["literal"] = new JsonObject { ["kind"] = "string", ["text"] = First };
        raw["actions"] = new JsonArray(new JsonObject { ["id"] = "opaque", ["arguments"] = new JsonObject
            { ["next"] = First, ["schema"] = First, ["evidence"] = First, ["inputs"] = new JsonArray(new JsonObject { ["next"] = First }) } });
        var prompt = "Retrieve only necessary evidence.\n" + raw.ToJsonString();
        var prepared = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, prompt, Schema());
        var again = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, prompt, Schema());
        Assert.Equal(JsonSerializer.Serialize(prepared, PlanningJsonContext.Default.LLMRequest), JsonSerializer.Serialize(again, PlanningJsonContext.Default.LLMRequest));
        var shown = JsonNode.Parse(prepared.Prompt[(prepared.Prompt.LastIndexOf('\n') + 1)..])!;
        Assert.StartsWith("e", shown["operations"]![0]!["descriptionEvidence"]!.ToString());
        Assert.True(JsonNode.DeepEquals(contract, shown["contract"]!["schema"]));
        Assert.True(JsonNode.DeepEquals(raw["value"], shown["value"]));
        Assert.True(JsonNode.DeepEquals(raw["literal"], shown["literal"]));
        Assert.True(JsonNode.DeepEquals(raw["actions"], shown["actions"]));
        Assert.DoesNotContain(First, prepared.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(shown["evidenceNavigation"]); // Displayed links already identify these choices.
        var runtime = new TestRuntime { Respond = (request, _) => new() { Json = Response(FirstOffered(request)) } };
        await PlanningModelCalls.CallAsync(state, runtime, PlanningCompactConstruction.DiscoveryPurpose, prompt, Schema(), PlannerFixture.Ct);
        var measured = Assert.Single(state.RequestMeasurements!);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest).Length, measured.CompleteRequestBytes);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
    }

    [Fact]
    public void SharedEnumUsedByOriginalDataIsSpecializedOnlyForNavigation()
    {
        var state = PlannerFixture.Session();
        var schema = PlanningSchemas.Object(("evidenceRequests", PlanningSchemas.Array(new JsonObject { ["$ref"] = "#/$defs/original" }, 1, 1)),
            ("extension", PlanningSchemas.Object(("actualArgument", new JsonObject { ["$ref"] = "#/$defs/original" }))));
        schema["$defs"] = new JsonObject { ["original"] = PlanningSchemas.Enum(First, Second) };
        var request = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose,
            "Preserve the original argument contract.\n" + Context().ToJsonString(), schema);
        var ids = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        var id = ids["operations"]![0]!["descriptionEvidence"]!.ToString();
        var response = Response(id);
        response["extension"] = new JsonObject { ["actualArgument"] = First };
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(response, request.StructuredOutputSchema!));
        response["extension"]!["actualArgument"] = id;
        Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(response, request.StructuredOutputSchema!));
    }

    [Fact]
    public void SchemaOnlyNavigationRetainsBusinessIdentityAndExactPointerWithoutHashes()
    {
        var state = PlannerFixture.Session();
        var context = new JsonObject { ["operations"] = new JsonArray(new JsonObject { ["id"] = "observe", ["name"] = "Read complete records" }) };
        var schema = PlanningSchemas.Object(("evidenceRequests", PlanningSchemas.Array(PlanningSchemas.Enum(
            "requirement:record_identity", "source:request:0:clause:2", "schema:observe@" + new string('a', 64) + ":/output/properties/records"), 1, 3)));
        var request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose,
            "Retrieve necessary originals.\n" + context.ToJsonString(), schema);
        var shown = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        var labels = shown["evidenceNavigation"]!.AsArray().Select(row => row!["label"]!.ToString()).ToArray();
        Assert.Contains(labels, label => label.Contains("record_identity", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("request:0:clause:2", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Read complete records", StringComparison.Ordinal) && label.Contains("/output/properties/records", StringComparison.Ordinal));
        Assert.DoesNotContain(new string('a', 64), request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingHostNavigationLinksDoNotCreateDuplicateSupplementalLabels()
    {
        var state = PlannerFixture.Session();
        var addresses = new[] { "requirement:records", "source:request:0:clause:2", "scope-exports:accepted:%2Froot@" + new string('a', 64) + ":0",
            "page:interfaces:8", "task:producer", "scope:/root", "contract:observe", "page:operations:8", "rejected:0", "group:reusable" };
        var original = new JsonObject
        {
            ["requirements"] = new JsonArray(new JsonObject { ["id"] = "records", ["original"] = First, ["evidence"] = addresses[0],
                ["sources"] = new JsonArray(new JsonObject { ["id"] = "request:0:clause:2", ["text"] = Second, ["evidence"] = addresses[1] }) }),
            ["scopes"] = new JsonArray(new JsonObject { ["path"] = "/root", ["acceptedExports"] = new JsonObject { ["evidence"] = addresses[2] } }),
            ["nextInterfaces"] = addresses[3],
            ["taskEvidence"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["address"] = addresses[4], ["scopes"] = new JsonArray(addresses[5]) }) },
            ["companionOperations"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["contract"] = addresses[6] }) },
            ["directoryEvidence"] = new JsonObject { ["operations"] = addresses[7] },
            ["reselection"] = new JsonObject { ["evidence"] = addresses[8] },
            ["groupEvidence"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["address"] = addresses[9] }) },
            ["findingEvidence"] = new JsonArray(addresses[0]),
            ["actions"] = new JsonArray(new JsonObject { ["arguments"] = new JsonObject { ["bindingEvidence"] = addresses[5],
                ["sources"] = new JsonArray(new JsonObject { ["evidence"] = addresses[1] }) } })
        };
        var schema = PlanningSchemas.Object(("evidenceRequests", PlanningSchemas.Array(PlanningSchemas.Enum(addresses), 1, 8)));
        var request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose,
            "Read only necessary evidence.\n" + original.ToJsonString(), schema, null, null, out var mapping);
        var shown = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        Assert.Null(shown["evidenceNavigation"]);
        Assert.True(JsonNode.DeepEquals(original["actions"], shown["actions"]));
        Assert.Equal(First, shown["requirements"]![0]!["original"]!.ToString());
        Assert.Equal(Second, shown["requirements"]![0]!["sources"]![0]!["text"]!.ToString());
        Assert.StartsWith("e", shown["scopes"]![0]!["acceptedExports"]!["evidence"]!.ToString());
        var canonical = PlanningModelCalls.CanonicalEvidenceRequest(new PlanningModelCall
            { Id = request.ClientRequestId!, Request = request, EvidenceAddresses = mapping });
        var restored = JsonNode.Parse(canonical.Prompt[(canonical.Prompt.LastIndexOf('\n') + 1)..])!;
        Assert.True(JsonNode.DeepEquals(original, restored));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("request")]
    [InlineData("map_hash")]
    [InlineData("swapped")]
    public async Task MissingStaleAndSwappedMapsStopBeforeAnyReplay(string corruption)
    {
        var state = await Pending();
        var mapping = state.PendingCall!.EvidenceAddresses!;
        switch (corruption)
        {
            case "missing": state.PendingCall.EvidenceAddresses = null; break;
            case "request": state.PendingCall.Request.Prompt += " changed"; break;
            case "map_hash": mapping.MapHash = new string('0', 64); break;
            case "swapped":
                var keys = mapping.Addresses.Keys.ToArray();
                (mapping.Addresses[keys[0]], mapping.Addresses[keys[1]]) = (mapping.Addresses[keys[1]], mapping.Addresses[keys[0]]);
                mapping.MapHash = PlanningGraphCompiler.Fingerprint(new JsonObject(mapping.Addresses.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))).ToJsonString());
                break;
        }
        var runtime = new TestRuntime { Respond = (_, _) => throw new InvalidOperationException("No replay is permitted.") };
        await Assert.ThrowsAsync<PlanningConflictException>(() => Call(state, runtime));
        Assert.Empty(runtime.Calls);
        Assert.Equal(1, state.ModelCalls);
    }

    [Theory]
    [InlineData("e999")]
    [InlineData(First)]
    public async Task UnknownOrCanonicalProviderIdsFailTheExactIssuedSchema(string supplied)
    {
        var state = await Pending();
        var runtime = new TestRuntime { EncodeCanonicalEvidence = false, Respond = (_, _) => new() { Json = Response(supplied) } };
        var error = await Assert.ThrowsAsync<PlanningResponseException>(() => Call(state, runtime));
        Assert.Contains(error.Diagnostics, item => item.Code == "PLANNING_RESPONSE_INVALID");
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task DifferentRequestLocalIdsResolveToTheSameCanonicalRepeatIdentity()
    {
        var first = await Pending();
        var second = PlannerFixture.Session();
        var context = Context();
        context["operations"]!.AsArray().Add(new JsonObject { ["name"] = "An earlier inspected contract", ["address"] = "contract:another" });
        var runtime = new TestRuntime { Checkpointed = _ => throw new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(second, runtime,
            PlanningCompactConstruction.DiscoveryPurpose, "Read evidence.\n" + context.ToJsonString(), Schema(), PlannerFixture.Ct));
        var firstMap = first.PendingCall!.EvidenceAddresses!;
        var secondMap = second.PendingCall!.EvidenceAddresses!;
        var firstId = firstMap.Addresses.Single(pair => pair.Value == First).Key;
        var secondId = secondMap.Addresses.Single(pair => pair.Value == First).Key;
        Assert.NotEqual(firstId, secondId);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        Assert.True(identities.Add(PlanningModelCalls.NormalizeEvidenceResponse(Response(firstId), firstMap)["evidenceRequests"]![0]!.ToString()));
        Assert.False(identities.Add(PlanningModelCalls.NormalizeEvidenceResponse(Response(secondId), secondMap)["evidenceRequests"]![0]!.ToString()));
    }

    [Fact]
    public async Task CorrectionIdentityUsesCanonicalResponseAfterIssuedSchemaValidation()
    {
        var state = PlannerFixture.Session();
        state.Construction!.LastProposalHash = "proposal:" + PlanningGraphCompiler.Fingerprint(Response(First).ToJsonString());
        var runtime = new TestRuntime { Respond = (request, _) => new() { Json = Response(FirstOffered(request)) } };
        var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            "replan", "Read exact evidence.\n" + Context().ToJsonString(), Schema(), PlannerFixture.Ct));
        Assert.Equal("REPLAN_NO_PROGRESS", error.Code);
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task AnIdFromAnotherRequestFailsTheNewRawSchemaWithoutCanonicalFallback()
    {
        var previous = await Pending();
        var oldId = previous.PendingCall!.EvidenceAddresses!.Addresses.Single(pair => pair.Value == First).Key;
        var next = PlannerFixture.Session();
        var runtime = new TestRuntime { EncodeCanonicalEvidence = false, Respond = (request, _) =>
        {
            Assert.DoesNotContain(oldId, request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
            return new() { Json = Response(oldId) };
        } };
        var error = await Assert.ThrowsAsync<PlanningResponseException>(() => Call(next, runtime));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "PLANNING_RESPONSE_INVALID");
        Assert.Equal(0, next.ReplanAttempts);
    }

    [Fact]
    public async Task ReviewKeepsRawIdsAndCanonicalRequestsWithoutSpendingReinterpretation()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        runtime.Respond = (_, _) => new() { Json = new JsonObject { ["decision"] = new JsonObject
            { ["verdict"] = "needs_context", ["reason"] = "Read the exact calculation.", ["contextRequests"] = new JsonArray("task:compute") } } };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        var row = Assert.Single(state.Construction!.Candidate!.Reviews);
        Assert.StartsWith("e", row["rawResponse"]!["decision"]!["contextRequests"]![0]!.ToString());
        Assert.Equal("task:compute", row["response"]!["decision"]!["contextRequests"]![0]!.ToString());
        Assert.True(PlanningModelCalls.ReviewEvidenceBindingValid(row));
        Assert.NotNull(state.Construction.Evidence!["task:compute"]);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.DoesNotContain(state.Construction.Candidate.Reviews, item => item["outcome"]?.ToString()?.StartsWith("reconsideration", StringComparison.Ordinal) == true);
        row["evidenceAddresses"]!["requestHash"] = new string('0', 64);
        Assert.False(PlanningModelCalls.ReviewEvidenceBindingValid(row));
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("null")]
    [InlineData("malformed")]
    [InlineData("request_hash")]
    [InlineData("null_basis")]
    [InlineData("null_addresses")]
    public async Task CleanGlobalReviewStillRequiresItsExactCapturedMappingForApproval(string corruption)
    {
        var state = await CurrentPlanningFixture.LiteralAsync("review-map", null, null, PlannerFixture.Ct);
        PlanningArtifactApproval.Verify(state);
        var row = state.Construction!.Checkpoints[^1].Reviews.Single(review => review["outcome"]?.ToString() == "ok");
        var raw = row["rawResponse"]!.ToJsonString();
        Assert.True(PlanningModelCalls.ReviewEvidenceBindingValid(row));
        switch (corruption)
        {
            case "removed": row.Remove("evidenceAddresses"); break;
            case "null": row["evidenceAddresses"] = null; break;
            case "malformed": row["evidenceAddresses"] = "not a captured mapping"; break;
            case "request_hash": row["evidenceAddresses"]!["requestHash"] = new string('0', 64); break;
            case "null_basis": row["evidenceAddresses"]!["requestBasisHash"] = null; break;
            case "null_addresses": row["evidenceAddresses"]!["addresses"] = null; break;
        }
        Assert.Equal(raw, row["rawResponse"]!.ToJsonString());
        Assert.False(PlanningModelCalls.ReviewEvidenceBindingValid(row));
        Assert.False(HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    private static async Task<PlanningSession> Pending()
    {
        var state = PlannerFixture.Session();
        var runtime = new TestRuntime { Checkpointed = _ => throw new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => Call(state, runtime));
        return PlannerFixture.Clone(state);
    }
    private static Task<JsonNode> Call(PlanningSession state, TestRuntime runtime) => PlanningModelCalls.CallAsync(state, runtime,
        PlanningCompactConstruction.DiscoveryPurpose, "Read exact evidence.\n" + Context().ToJsonString(), Schema(), PlannerFixture.Ct);
    private static JsonObject Context() => new() { ["operations"] = new JsonArray(new JsonObject
        { ["name"] = "Read structured observation", ["descriptionEvidence"] = First, ["nextPorts"] = Second }) };
    private static JsonObject Schema() => PlanningSchemas.Object(("evidenceRequests", PlanningSchemas.Array(PlanningSchemas.Enum(First, Second), 1, 2)));
    private static JsonObject Response(string address) => new() { ["evidenceRequests"] = new JsonArray(address) };
    private static string FirstOffered(LLMRequest request) => request.StructuredOutputSchema!["properties"]!["evidenceRequests"]!["items"]!["enum"]![0]!.ToString();
}
