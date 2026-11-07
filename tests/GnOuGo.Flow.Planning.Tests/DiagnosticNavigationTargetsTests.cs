using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DiagnosticNavigationTargetsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongOwnedContinuationUsesExactScopeAcrossSerializedAcceptance(bool diagnostic)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready(diagnostic);
        state.Construction!.Candidate = null;
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "sequence"; step.CloseRequirementIds.Clear();
        var taskId = new string('s', 300);
        var path = "/tasks/" + taskId + "/body";
        var extension = new PlanningConstructionExtension
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], ContinueInScope = path,
            Tasks = [new() { Id = taskId, Kind = "sequence", Objective = "Retain the partially computed number.",
                Body = new() { Tasks = [Value("first", 1)] } }]
        };
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var response = Draft(extension, schema);
        Assert.Equal(diagnostic, PlanningContractValidation.ValidateInstance(response, schema).Count == 0);
        if (!diagnostic) return;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            var wire = Draft(extension, request.StructuredOutputSchema!.AsObject());
            Assert.Empty(PlanningContractValidation.ValidateInstance(wire, request.StructuredOutputSchema));
            return new() { Json = wire };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        state = PlannerFixture.Clone(state);
        Assert.Equal(path, state.Construction!.Compact!.CurrentStep!.Scope);
        Assert.Equal(path, Assert.Single(state.Construction.Checkpoints).Fragment["continueInScope"]!.ToString());
        Assert.Contains(path, state.Construction.OpenScopes);
        Assert.Single(runtime.Calls);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Plan!, state.Catalog!, state.Request,
            state.Construction.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        var invalid = new PlanningConstructionExtension { CompleteBlock = false, Scope = path, RequirementIds = ["work"],
            ContinueInScope = path + "_not_owned", Tasks = [Value("second", 2)] };
        var error = Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.ValidateExtension(state, PlanningConstruction.Apply(state, invalid)));
        Assert.Contains(error.Diagnostics, finding => finding.Code == "CONSTRUCTION_BLOCK_CONTINUATION_INVALID");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongAcceptedGroupProducerAndFieldRemainExactTechnicalChoices(bool diagnostic)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready(diagnostic);
        state.Construction!.Candidate = null;
        var groupId = new string('g', 300); var producerId = new string('p', 320); var field = new string('f', 160);
        state.Plan!.Groups.Add(new() { Id = groupId, Body = new() { Tasks = [Value("inside", 1)],
            Outputs = [new("result", new() { Kind = "output", Source = "inside", Port = "number" })] } });
        state.Plan.Root.Tasks.Add(new() { Id = producerId, Kind = "value", Objective = "Preserve the accepted record.",
            Outputs = [new("record", new() { Kind = "object", Members = [new(field, new() { Kind = "number", Number = 7 })] })] });
        var selected = state.Construction.Compact!.CurrentStep!;
        selected.Kind = "call"; selected.GroupId = groupId; selected.BaseHash = PlanningConstruction.Hash(state.Plan);
        selected.CloseRequirementIds.Clear(); selected.DependencyTaskIds = [producerId];
        selected.Inputs = [new() { Kind = "field", Port = field, Items = [new() { Kind = "output", Source = producerId, Port = "record" }] }];
        var schema = PlanningCompactConstruction.ImplementationSchema(state);
        var step = new JsonObject
        {
            ["scope"] = "/root", ["kind"] = "call", ["groupId"] = groupId,
            ["inputs"] = new JsonArray(new JsonObject { ["kind"] = "field", ["port"] = field, ["items"] = new JsonArray(
                new JsonObject { ["kind"] = "output", ["source"] = producerId, ["port"] = "record" }) }),
            ["dependencyTaskIds"] = new JsonArray(producerId), ["closeRequirementIds"] = new JsonArray(),
            ["outputNames"] = new JsonArray(), ["prerequisites"] = null, ["operationIds"] = new JsonArray(),
            ["closeScopes"] = null
        };
        var authored = new JsonObject { ["baseHash"] = selected.BaseHash, ["step"] = step };
        var wire = PlanningCorpus.Transport(authored, schema, schema)!;
        Assert.True(JsonNode.DeepEquals(step, wire["step"]), "Authored: " + step.ToJsonString() + "\nTransported: " + wire["step"]!.ToJsonString());
        Assert.Equal(diagnostic, PlanningContractValidation.ValidateInstance(wire, schema).Count == 0);
        var discoveryState = PlannerFixture.Clone(state);
        discoveryState.Construction!.Compact!.CurrentStep!.Kind = null;
        discoveryState.Construction.Compact.CurrentStep.DiscoveryComplete = false;
        var discoverySchema = PlanningCompactConstruction.DiscoverySchema(discoveryState);
        var discoveryWire = PlanningCorpus.Transport(new JsonObject { ["binding"] = new JsonObject
            { ["kind"] = "call", ["groupId"] = groupId, ["operationIds"] = new JsonArray(), ["closeScopes"] = null } },
            discoverySchema, discoverySchema)!;
        Assert.Equal(diagnostic, PlanningContractValidation.ValidateInstance(discoveryWire, discoverySchema).Count == 0);
        if (!diagnostic) return;
        PlanningCompactConstruction.ValidateSelection(state, selected);
        state = PlannerFixture.Clone(state);
        selected = state.Construction!.Compact!.CurrentStep!;
        Assert.Equal(groupId, selected.GroupId); Assert.Equal(field, selected.Inputs[0].Port);
        var extension = new PlanningConstructionExtension { CompleteBlock = false, Scope = "/root", RequirementIds = ["work"],
            Tasks = [new() { Id = "invoke", Kind = "call", Objective = "Invoke the retained exact group.", Group = groupId },
                new() { Id = "copy", Kind = "value", Objective = "Copy the exact accepted member.", Outputs = [new("number", selected.Inputs[0])] }] };
        var draftSchema = PlanningCompactConstruction.DraftSchema(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Draft(extension, draftSchema), draftSchema));
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        selected.GroupId += "_missing";
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, selected));
        candidate.Plan.Root.Tasks[^1].Outputs[0].Value.Port += "_missing";
        Assert.NotEmpty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
    }

    [Fact]
    public async Task DiscoveryKeepsEveryExactEvidenceAddressThroughIssuedRecoveryAndAtomicLoading()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready(advisoryOptimization: true);
        state.Construction!.Candidate = null;
        var compact = state.Construction.Compact!;
        compact.CurrentStep!.Kind = null; compact.CurrentStep.DiscoveryComplete = false;
        var original = state.Requirements!.Outcomes.Single(requirement => requirement.Id == "work").Description;
        var ids = Enumerable.Range(0, 10).Select(index => index == 0 ? new string('a', 2300) : "exact_original_" + index).ToList();
        for (var index = 0; index < ids.Count; index++)
        {
            var text = original.Substring(index * 2, index == ids.Count - 1 ? original.Length - index * 2 : 2);
            compact.Sources.Add(new() { Id = ids[index], Kind = "requirement", Indexed = true, Text = text,
                Version = PlanningGraphCompiler.Fingerprint(text), Inputs = [], Outputs = [] });
        }
        compact.RequirementSources["work"] = ids;
        var addresses = ids.Select(id => "source:" + id).ToList();
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        var authored = new JsonObject { ["evidenceRequests"] = new JsonArray(addresses.Select(address => (JsonNode?)JsonValue.Create(address)).ToArray()) };
        var response = PlanningCorpus.Transport(authored, schema, schema)!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, schema));
        var selectionSchema = PlanningCompactConstruction.ImplementationSchema(state);
        var selectionAuthored = authored.DeepClone().AsObject();
        selectionAuthored["baseHash"] = PlanningConstruction.Hash(state.Plan);
        var selectionResponse = PlanningCorpus.Transport(selectionAuthored, selectionSchema, selectionSchema)!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(selectionResponse, selectionSchema));
        var drafting = PlannerFixture.Clone(state);
        drafting.Construction!.Compact!.CurrentStep!.Kind = "value";
        drafting.Construction.Compact.CurrentStep.DiscoveryComplete = true;
        var draftSchema = PlanningCompactConstruction.DraftSchema(drafting);
        Assert.Empty(PlanningContractValidation.ValidateInstance(PlanningCorpus.Transport(authored, draftSchema, draftSchema), draftSchema));
        var request = PlanningCompactConstruction.Request(state, "Read the exact original evidence.", context, schema, PlanningCompactConstruction.DiscoveryPurpose);
        runtime.Checkpointed = _ => throw new OperationCanceledException("Stop after durable issuance.");
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningCompactConstruction.DiscoveryPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct));
        Assert.Empty(runtime.Calls);
        state = PlannerFixture.Clone(state);
        var call = state.PendingCall!;
        var issued = JsonSerializer.Serialize(call, PlanningJsonContext.Default.PlanningModelCall);
        Assert.All(addresses, address => Assert.Contains(address, call.EvidenceAddresses!.Addresses.Values));
        var shortResponse = TestRuntime.WireEvidence(response, call.EvidenceAddresses);
        var rawReceipt = shortResponse.ToJsonString();
        Assert.Empty(PlanningContractValidation.ValidateInstance(shortResponse, call.Request.StructuredOutputSchema!));
        var recovery = new TestRuntime { Respond = (actual, _) =>
        {
            Assert.Equal(call.Request.Prompt, actual.Prompt);
            return new() { Json = shortResponse };
        } };
        var normalized = await PlanningModelCalls.CallAsync(state, recovery, PlanningCompactConstruction.DiscoveryPurpose,
            "This newer prompt must not replace the issued request.", new(), PlannerFixture.Ct);
        Assert.Equal(addresses, normalized["evidenceRequests"]!.AsArray().Select(item => item!.ToString()));
        Assert.Equal(issued, JsonSerializer.Serialize(call, PlanningJsonContext.Default.PlanningModelCall));
        Assert.Equal(rawReceipt, shortResponse.ToJsonString());
        PlanningConstruction.LoadEvidence(state, addresses, issuedDiscoverySchema: schema);
        state = PlannerFixture.Clone(state);
        Assert.Equal(original, string.Concat(addresses.Select(address => state.Construction!.Evidence![address]!["text"]!.ToString())));
        Assert.Contains(state.ThresholdWarnings!, warning => warning.Kind == "evidence_requests" && warning.Current == 10);
        Assert.Single(recovery.Calls); Assert.Equal(1, state.ModelCalls);
        var before = state.Construction!.Evidence!.ToJsonString();
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, ["source:not_retained"]));
        Assert.Equal(before, state.Construction.Evidence.ToJsonString());
        var ordinary = PlannerFixture.Clone(state);
        ordinary.Request.Options.Remove(PlanningTokenThresholds.OptimizationOptionName);
        ordinary.Construction!.Evidence = null; ordinary.Construction.EvidenceRequests.Clear();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, PlanningCompactConstruction.DiscoverySchema(ordinary)));
        ordinary.Request.Options[PlanningTokenThresholds.OptionName] = "advisory";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, PlanningCompactConstruction.DiscoverySchema(ordinary)));
    }

    private static JsonNode Draft(PlanningConstructionExtension extension, JsonObject schema) => PlanningCorpus.Transport(
        new JsonObject { ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension) }, schema, schema)!;
    private static PlanTask Value(string id, int number) => new() { Id = id, Kind = "value", Objective = "Retain the number.",
        Outputs = [new("number", new() { Kind = "number", Number = number })] };
}
