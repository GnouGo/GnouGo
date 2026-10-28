using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactContractEvidenceFlowTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DraftAdmissionPagesOnlyOptionalPortsAndPreservesSelectedOriginalClosures(bool correction, bool producerImport)
    {
        var (state, _) = await Ready();

        var capability = state.Discovery.Resolved[0];
        for (var index = 12; index < 40; index++)
            capability.OutputSchema["properties"]!["result" + index] = new JsonObject { ["type"] = "string" };
        capability.OutputSchema["properties"]!["result9"] = new JsonObject
        {
            ["type"] = new JsonArray("array", "null"), ["description"] = "Only records returned by this exact authorized read, or null when unavailable.",
            ["items"] = new JsonObject { ["$ref"] = "#/$defs/record" }
        };
        capability.OutputSchema["$defs"] = new JsonObject { ["record"] = new JsonObject
            { ["type"] = "object", ["properties"] = new JsonObject { ["identity"] = new JsonObject { ["type"] = "string", ["pattern"] = "^EXACT$" } },
                ["required"] = new JsonArray("identity"), ["additionalProperties"] = false } };
        capability.OutputSchema["required"] = new JsonArray("result0", "result9");
        foreach (var port in new[] { "result0", "result9" })
            state.Construction!.Compact!.CurrentStep!.SchemaPointers.Add("schema:read@v1:/output/properties/" + port);
        if (producerImport)
        {
            state.Catalog!.Capabilities.Add(capability);
            state.Catalog.AllowedStepTypes.Add("set");
            state.Plan = new() { Root = new() { Tasks = [new() { Id = "producer", Kind = "operation", Operation = "read",
                Objective = "Read the exact authorized original.", Inputs = [new("target", new() { Kind = "string", Text = "EXACT" })] }] } };
            var step = state.Construction!.Compact!.CurrentStep!;
            step.Kind = "value"; step.OperationIds.Clear(); step.BaseHash = PlanningConstruction.Hash(state.Plan);
            step.Inputs = [new() { Kind = "output", Source = "producer", Port = "result0" }, new() { Kind = "output", Source = "producer", Port = "result9" }];
        }
        var originals = state.Construction!.Compact!.CurrentStep!.SchemaPointers.ToDictionary(address => address,
            address => PlanningCompactConstruction.LoadContractEvidence(state, address, recordSchemaSelection: false)!.ToJsonString());
        var purpose = correction ? "replan" : PlanningConstruction.DraftPurpose;
        SetLimit(32000);
        var full = PlanningConstruction.DraftRequest(state, correction);
        var fullPrepared = Prepare(full);
        var maximum = checked((int)PlanningRequestMeasurements.Measure(fullPrepared, purpose, 32000).EstimatedInputTokens - 1);
        Assert.Equal(40, Contract(Context(full))["outputs"]!.AsArray().Count);
        SetLimit(maximum);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var paged = PlanningConstruction.DraftRequest(state, correction);
        var context = Context(paged);
        var operation = Contract(context);
        Assert.InRange(PlanningRequestMeasurements.Measure(Prepare(paged), purpose, maximum).EstimatedInputTokens, 1, maximum);
        Assert.InRange(operation["outputs"]!.AsArray().Count, 8, 9);
        Assert.Equal(40, operation["outputCount"]!.GetValue<int>());
        Assert.Contains(operation["outputs"]!.AsArray(), row => row!["name"]!.ToString() == "result9");
        Assert.Equal(capability.Description, operation["description"]!.ToString());
        if (producerImport) Assert.Equal("EXACT", context["interfaces"]![0]!["producers"]![0]!["task"]!["inputs"]![0]!["value"]!["text"]!.ToString());
        var firstOmitted = Contract(Context(full))["outputs"]!.AsArray().Select((row, index) => (row, index))
            .First(item => !operation["outputs"]!.AsArray().Any(row => row!["name"]!.ToString() == item.row!["name"]!.ToString())).index;
        Assert.Equal("port-page:read@v1:/output:" + firstOmitted, operation["nextPorts"]!.ToString());
        var addresses = PlanningCompactConstruction.NavigationEvidence([operation]);
        Assert.Contains(operation["nextPorts"]!.ToString(), addresses);
        Assert.Contains(operation["nextPorts"]!.ToString(), paged.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        var directory = PlanningCompactConstruction.LoadContractEvidence(state, operation["nextPorts"]!.ToString())!;
        Assert.Equal(firstOmitted, directory["offset"]!.GetValue<int>());
        foreach (var view in operation["selectedOutputs"]!.AsArray().OfType<JsonObject>())
        {
            var expanded = view.DeepClone().AsObject();
            foreach (var key in new[] { "id", "version", "hash" }) expanded[key] = operation[key]!.DeepClone();
            if (operation["selectedSchemaView"] is { } description) expanded["view"] = description.DeepClone();
            foreach (var pair in operation["selectedOutputsSharedSchemaMetadata"]!.AsObject()) expanded[pair.Key] = pair.Value!.DeepClone();
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(originals["schema:read@v1:" + view["pointer"]]), expanded));
        }
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(paged.Prompt, PlanningConstruction.DraftRequest(PlannerFixture.Clone(state), correction).Prompt);
        SetLimit(512);
        var rejected = PlanningConstruction.DraftRequest(state, correction);
        Assert.True(PlanningRequestMeasurements.Measure(Prepare(rejected), purpose, 512).EstimatedInputTokens > 512);
        Assert.Equal(operation["selectedOutputs"]!.ToJsonString(), Contract(Context(rejected))["selectedOutputs"]!.ToJsonString());

        JsonObject Contract(JsonObject view) => (producerImport ? view["interfaces"]![0]!["producers"]![0]!["contract"] : view["operations"]![0])!.AsObject();
        LLMRequest Prepare(LLMRequest request) => PlanningModelCalls.PrepareRequest(state, purpose,
            request.Prompt, request.StructuredOutputSchema!.AsObject(), state.Construction!.Limits.DraftOutputTokens);
        void SetLimit(int limit)
        {
            state.Request.Generation.MaxInputTokensPerRequest = limit;
            state.Request.Options["construction_limits"]!["phasePolicy"]!["draft"]!["maxInputTokens"] = limit;
            state.Request.Options["construction_limits"]!["phasePolicy"]!["draft"]!["targetInputTokens"] = Math.Min(1000, limit);
        }
    }


    [Fact]
    public async Task DraftSharesOnlyIdenticalSelectedContractMetadataAndKeepsConsumedClosures()
    {
        var (state, _) = await Ready();

        state.Catalog!.Capabilities.Add(state.Discovery.Resolved[0]);
        state.Catalog.AllowedStepTypes.Add("set"); // Construction probes use the existing presence-aware binding path.
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "producer", Kind = "operation", Operation = "read",
            Objective = "Read the caller-authorized original.", Inputs = [new("target", new() { Kind = "string", Text = "EXACT" })] }] } };
        var step = state.Construction!.Compact!.CurrentStep!;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        step.Inputs = [new() { Kind = "output", Source = "producer", Port = "result0" }];
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var draft = PlanningConstruction.DraftRequest(state, false);
        var context = Context(draft);
        var contract = context["interfaces"]![0]!["producers"]![0]!["contract"]!.AsObject();
        var shared = contract["sharedContractMetadata"] as JsonObject;
        Assert.NotNull(shared);
        Assert.Equal("EXACT", context["interfaces"]![0]!["producers"]![0]!["task"]!["inputs"]![0]!["value"]!["text"]!.ToString());
        var selected = context["operations"]![0]!.AsObject();
        var expanded = context.DeepClone().AsObject();
        if (shared is not null)
        {
            Assert.Equal("/operations/0", shared["detailsPointer"]!.ToString());
            var expandedContract = expanded["interfaces"]![0]!["producers"]![0]!["contract"]!.AsObject();
            foreach (var field in shared["fields"]!.AsArray().Select(value => value!.ToString()))
            {
                Assert.Null(contract[field]);
                expandedContract[field] = selected[field]!.DeepClone();
            }
            expandedContract.Remove("sharedContractMetadata");
            Assert.True(JsonNode.DeepEquals(context, PlanningCompactConstruction.CompactSelectedProducerContractMetadata(expanded)));
            Assert.True(JsonNode.DeepEquals(context, PlanningCompactConstruction.CompactSelectedProducerContractMetadata(context)));
            var expandedDraft = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose,
                draft.Prompt[..(draft.Prompt.LastIndexOf('\n') + 1)] + PlanningJsonTransport.Prompt(expanded), draft.StructuredOutputSchema!.AsObject());
            var measured = PlanningRequestMeasurements.Measure(PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose,
                draft.Prompt, draft.StructuredOutputSchema.AsObject()), PlanningConstruction.DraftPurpose, 32000);
            var original = PlanningRequestMeasurements.Measure(expandedDraft, PlanningConstruction.DraftPurpose, 32000);
            Assert.True(measured.CompleteRequestBytes < original.CompleteRequestBytes);
            Assert.Equal(original.SchemaHash, measured.SchemaHash);
            Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        }
        foreach (var view in contract["selectedOutputs"]!.AsArray().OfType<JsonObject>())
        {
            var original = PlanningCompactConstruction.LoadContractEvidence(state, "schema:read@v1:" + view["pointer"]!, false)!;
            Assert.Equal(original["schema"]!.ToJsonString(), view["schema"]!.ToJsonString());
            Assert.Equal(original["ancestors"]!.ToJsonString(), view["ancestors"]!.ToJsonString());
        }
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("description")]
    [InlineData("outputs")]
    public void SelectedContractSharingRequiresExactIdentityAndByteIdenticalFields(string change)
    {
        var original = new JsonObject
        {
            ["id"] = "exact", ["version"] = "v1", ["hash"] = new string('a', 64),
            ["description"] = new string('x', 512),
            ["outputs"] = new JsonArray(new JsonObject { ["name"] = "records", ["schemaEvidence"] = "schema:exact@v1:/output/properties/records",
                ["type"] = "array", ["description"] = new string('y', 512) }),
            ["artifacts"] = new JsonArray("local-report"),
            ["selectedOutputs"] = new JsonArray(new JsonObject { ["pointer"] = "/output/properties/records", ["schema"] = new JsonObject
                { ["type"] = new JsonArray("array", "null"), ["items"] = new JsonObject { ["$ref"] = "#/$defs/record" } },
                ["ancestors"] = new JsonArray(new JsonObject { ["constraints"] = new JsonObject { ["required"] = new JsonArray("records", "complete") } }),
                ["referenceClosure"] = new JsonObject { ["#/$defs/record"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("identity") } } })
        };
        var selected = original.DeepClone().AsObject();
        if (change == "outputs") selected["outputs"]![0]!["description"] = "A different normative meaning.";
        else selected[change] = "different";
        var context = new JsonObject { ["operations"] = new JsonArray(selected), ["interfaces"] = new JsonArray(new JsonObject
            { ["producers"] = new JsonArray(new JsonObject { ["contract"] = original.DeepClone(), ["task"] = new JsonObject { ["inputs"] = new JsonArray("exact arguments") } }) }) };
        var before = context.ToJsonString();
        var compact = PlanningCompactConstruction.CompactSelectedProducerContractMetadata(context);
        var actual = compact["interfaces"]![0]!["producers"]![0]!["contract"]!;
        if (change is "id" or "version" or "hash") Assert.Equal(before, compact.ToJsonString());
        else
        {
            Assert.Equal(original[change]!.ToJsonString(), actual[change]!.ToJsonString());
            Assert.DoesNotContain(actual["sharedContractMetadata"]!["fields"]!.AsArray(), item => item!.ToString() == change);
            Assert.Equal(original["selectedOutputs"]!.ToJsonString(), actual["selectedOutputs"]!.ToJsonString());
            Assert.Equal(context["interfaces"]![0]!["producers"]![0]!["task"]!.ToJsonString(), compact["interfaces"]![0]!["producers"]![0]!["task"]!.ToJsonString());
        }
        Assert.Equal(before, context.ToJsonString());
    }

    [Theory]
    [InlineData("ancestors")]
    [InlineData("referenceClosure")]
    [InlineData("identity")]
    [InlineData("ancestors_order")]
    public async Task ProducerSharingKeepsDifferentMetadataAndRequiresExactParentIdentity(string change)
    {
        var (state, _) = await Ready();
        var views = new[] { "result0", "result1", "result2" }.Select(port => PlanningCompactConstruction.LoadContractEvidence(state,
            "schema:read@v1:/output/properties/" + port, recordSchemaSelection: false)!.DeepClone().AsObject()).ToArray();
        if (change == "ancestors") views[1]["ancestors"]![0]!["constraints"]!["description"] = "Different original constraint.";
        if (change == "referenceClosure") views[1]["referenceClosure"]!["#/$defs/original"] = new JsonObject { ["type"] = "number" };
        if (change == "identity") views[1]["hash"] = new string('f', 64);
        if (change == "ancestors_order")
        {
            var ancestor = views[1]["ancestors"]![0]!.AsObject();
            views[1]["ancestors"]![0] = new JsonObject(ancestor.Reverse().Select(item => new KeyValuePair<string, JsonNode?>(item.Key, item.Value?.DeepClone())));
            Assert.True(JsonNode.DeepEquals(views[0]["ancestors"], views[1]["ancestors"]));
            Assert.NotEqual(views[0]["ancestors"]!.ToJsonString(), views[1]["ancestors"]!.ToJsonString());
            change = "ancestors";
        }
        var contract = new JsonObject { ["id"] = views[0]["id"]!.DeepClone(), ["version"] = views[0]["version"]!.DeepClone(),
            ["hash"] = views[0]["hash"]!.DeepClone(), ["selectedInputs"] = new JsonArray(),
            ["selectedOutputs"] = new JsonArray(views.Select(view => (JsonNode)view).ToArray()) };
        var original = contract.ToJsonString();
        var metadata = change == "identity" ? null : views.Select(view => view[change]!.ToJsonString()).ToArray();
        PlanningCompactConstruction.CompactProducerSchemaMetadata(contract, shareConstraints: true);
        if (change == "identity") Assert.Equal(original, contract.ToJsonString());
        else
        {
            Assert.Null(contract["selectedOutputsSharedSchemaMetadata"]?[change]);
            Assert.Equal(metadata, views.Select(view => view[change]!.ToJsonString()));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerationAndCorrectionCanCommitTechnicalWorkBeforeBusinessReview(bool correction)
    {
        var (state, runtime) = await Ready();
        var baseline = PlanningConstruction.Hash(state.Plan);
        var originalContract = JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability);
        if (correction) state.Diagnostics = [new("CONSTRUCTION_STEP_INPUT_INVALID", "/extension", "Retrieve the missing original input contract.")];
        var phase = correction ? "replan" : PlanningConstruction.DraftPurpose;
        var drafts = 0;
        runtime.Respond = (request, purpose) =>
        {
            var context = Context(request);
            var view = context["operations"]![0]!;
            var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, purpose).MaxInputTokens;
            var measurement = PlanningRequestMeasurements.Measure(request, purpose, limit);
            Assert.InRange(measurement.EstimatedInputTokens, 1, limit);
            if (purpose == phase)
            {
                drafts++;
                if (drafts == 1)
                {
                    Assert.Empty(view["editableInputs"]!.AsArray());
                    Assert.Contains("[] does not mean no arguments are needed", request.StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
                    return Wire(request, DraftAction(context, evidence: "schema:read@v1:/input/properties/target"));
                }
                Assert.Equal(2, drafts);
                Assert.Equal("target", Assert.Single(view["editableInputs"]!.AsArray())!["name"]!.ToString());
                Assert.True(JsonNode.DeepEquals(state.Discovery.Resolved[0].InputSchema["properties"]!["target"],
                    context["evidence"]!["schema:read@v1:/input/properties/target"]!["schema"]));
                Assert.Equal(view["outputCount"]!.GetValue<int>(), view["outputs"]!.AsArray().Count);
                Assert.Equal("port-page:read@v1:/output:0", view["outputDirectory"]!.ToString());
                return Wire(request, DraftAction(context, task: new() { Id = "read_exact", Kind = "operation",
                    Objective = "Read the caller-authorized record EXACT.", Operation = "read",
                    Inputs = [new("target", new() { Kind = "string", Text = "EXACT" })] }));
            }
            throw new InvalidOperationException("This partial technical checkpoint must not dispatch an isolated Judge.");
        };
        for (var i = 0; i < 9 && state.Construction!.Checkpoints.Count == 0; i++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.DoesNotContain(state.Diagnostics, finding => finding.Required && finding.Code != "CONSTRUCTION_STEP_INPUT_INVALID");
            Assert.NotNull(state.Construction);
            if (state.Construction!.Checkpoints.Count == 0) Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        }
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(2, drafts);
        Assert.DoesNotContain(runtime.Calls, request => request.StructuredOutputSchema?["title"]?.ToString() is PlanningConstruction.LocalPurpose or PlanningConstruction.GlobalPurpose);
        Assert.True(state.Construction!.Checkpoints[0].Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Equal("EXACT", Assert.Single(Assert.Single(state.Plan!.Root.Tasks).Inputs).Value.Text);
        Assert.Equal(correction ? 1 : 0, state.ReplanAttempts);
        Assert.Equal(originalContract, JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability));
    }

    [Fact]
    public async Task EvidencePresentationKeepsSelectedOutputsAndEveryOriginalPortRetrievable()
    {
        var (state, _) = await Ready();

        var contract = state.Discovery.Resolved[0];
        var before = JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability);
        PlanningConstruction.LoadEvidence(state, ["schema:read@v1:/input/properties/target", "schema:read@v1:/output/properties/result11"]);
        var exactEvidence = state.Construction!.Evidence!.DeepClone();
        var view = Assert.Single(PlanningCompactConstruction.ContractViews(state))!;
        Assert.True(JsonNode.DeepEquals(contract.InputSchema["properties"]!["target"], view["inputSchema"]!["properties"]!["target"]));
        Assert.NotNull(view["selectedOutputs"]);
        Assert.Equal(view["outputCount"]!.GetValue<int>(), view["outputs"]!.AsArray().Count);
        Assert.False(view["omittedOptionalInputs"] is JsonArray);
        var names = new List<string>();
        string? address = view["outputDirectory"]!.ToString();
        while (address is not null)
        {
            var page = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
            names.AddRange(page["items"]!.AsArray().Select(port => port!["name"]!.ToString()));
            address = page["next"]?.ToString();
        }
        Assert.Equal(12, names.Distinct(StringComparer.Ordinal).Count());
        Assert.True(JsonNode.DeepEquals(exactEvidence, state.Construction!.Evidence));
        Assert.Equal(before, JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability));
    }

    [Fact]
    public async Task CoherentEvidenceBatchRetainsEverySelectedInputBeforeTechnicalCheckpoint()
    {
        var (state, runtime) = await Ready();
        var capability = state.Discovery.Resolved[0];
        capability.InputSchema["properties"]!["mode"] = new JsonObject
            { ["type"] = "string", ["enum"] = new JsonArray("exact"), ["description"] = "Preserve every original observation." };
        var names = new[] { "target", "unrelated", "mode" };
        var addresses = names.Select(name => "schema:read@v1:/input/properties/" + name).ToArray();
        var original = JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var drafts = 0;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            var context = Context(request); var view = context["operations"]![0]!;
            if (drafts++ == 0)
            {
                var response = DraftAction(context);
                response["evidenceRequests"] = new JsonArray(addresses.Select(address => (JsonNode?)JsonValue.Create(address)).ToArray());
                Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
                return Wire(request, response);
            }
            Assert.Equal(names.Order(StringComparer.Ordinal), view["editableInputs"]!.AsArray().Select(input => input!["name"]!.ToString()).Order(StringComparer.Ordinal));
            foreach (var name in names)
                Assert.True(JsonNode.DeepEquals(capability.InputSchema["properties"]![name], view["inputSchema"]!["properties"]![name]));
            return Wire(request, DraftAction(context, task: new() { Id = "read_exact", Kind = "operation", Operation = "read",
                Objective = "Read the exact authorized record.", Inputs = [new("target", new() { Kind = "string", Text = "EXACT" }),
                    new("unrelated", new() { Kind = "string", Text = "original" }), new("mode", new() { Kind = "string", Text = "exact" })] }));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        for (var count = 0; count < 4 && state.Construction!.Checkpoints.Count == 0; count++) state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics); Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(2, drafts); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(original, JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability));
        Assert.Equal(names, Assert.Single(state.Plan!.Root.Tasks).Inputs.Select(input => input.Name));
    }

    [Fact]
    public async Task InitialOutputNavigationRetainsExactDirectoryAndSelectedSchemas()
    {
        var (state, _) = await Ready();

        var original = JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability);
        state.Construction!.Compact!.CurrentStep!.SchemaPointers.Add("schema:read@v1:/output/properties/result11");
        Assert.Null(state.Construction!.Evidence);
        var view = Assert.Single(PlanningCompactConstruction.ContractViews(state))!;
        Assert.Equal(view["outputCount"]!.GetValue<int>(), view["outputs"]!.AsArray().Count);
        Assert.Equal(12, view["outputCount"]!.GetValue<int>());
        Assert.Equal("/output/properties/result11", Assert.Single(view["selectedOutputs"]!.AsArray())!["pointer"]!.ToString());
        Assert.NotEmpty(view["omittedOptionalInputs"]!.AsArray());
        var names = new List<string>();
        string? address = view["outputDirectory"]!.ToString();
        while (address is not null)
        {
            var page = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
            names.AddRange(page["items"]!.AsArray().Select(port => port!["name"]!.ToString()));
            address = page["next"]?.ToString();
        }
        Assert.Equal(Enumerable.Range(0, 12).Select(index => "result" + index).Order(StringComparer.Ordinal), names);
        Assert.Null(state.Construction!.Evidence);
        Assert.Equal(original, JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability));
        {
            var empty = PlannerFixture.Clone(state);
            empty.Construction!.Compact!.CurrentStep!.SchemaPointers.Clear();
            empty.Discovery.Resolved[0].OutputSchema["properties"]!.AsObject().Clear();
            var emptyView = Assert.Single(PlanningCompactConstruction.ContractViews(empty))!;
            Assert.Equal(0, emptyView["outputCount"]!.GetValue<int>());
            Assert.Empty(emptyView["outputs"]!.AsArray());
        }
    }

    [Fact]
    public async Task TypedCorrectionEvidenceUsesCapturedPresentationCardinality()
    {
        var (state, _) = await Ready();

        var authority = PlannerFixture.Clone(state);
        authority.Plan = new() { Root = new() { Tasks = [new() { Id = "read_exact", Kind = "operation",
            Objective = "Read the exact authorized record.", Operation = "read",
            Inputs = [new("target", new() { Kind = "string", Text = "EXACT" })] }] } };
        authority.Catalog!.Capabilities.Add(authority.Discovery.Resolved[0]);
        authority.EditablePaths = ["/tasks/read_exact/inputs/target"];
        authority.RevisionScope = ["/tasks/read_exact/inputs/target"];
        var request = PlanningConstruction.RepairRequest(state, authority);
        Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
        var response = new JsonObject { ["patch"] = null, ["discoveryRequests"] = null,
            ["clarifications"] = null, ["reselectStep"] = null,
            ["evidenceRequests"] = new JsonArray("schema:read@v1:/input/properties/target") };
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema));
        Assert.Equal(8, request.StructuredOutputSchema!["properties"]!["evidenceRequests"]!["anyOf"]!.AsArray().Single(branch => branch?["type"]?.ToString() == "array")!["maxItems"]!.GetValue<int>());
        for (var index = 0; index < 8; index++) response["evidenceRequests"]!.AsArray().Add("schema:read@v1:/input/properties/target");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema));
    }


    [Fact]
    public async Task SavedGenerationRequestReplaysItsCapturedPresentationBeforeExpandingEvidence()
    {
        var (state, runtime) = await Ready();
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint =>
            checkpoint.Status == PlanningStatus.Generating && checkpoint.PendingCall?.Purpose == PlanningConstruction.DraftPurpose));
        var issued = saved.PendingCall!.Request;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls;
        var baseline = PlanningConstruction.Hash(saved.Plan);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Wire(request, DraftAction(Context(request), evidence: "schema:read@v1:/input/properties/target"));
        };
        var resumed = await Advance(saved, runtime);
        Assert.Equal(calls, resumed.ModelCalls);
        Assert.Equal(0, resumed.ReplanAttempts);
        Assert.Equal(baseline, PlanningConstruction.Hash(resumed.Plan));
        Assert.Null(resumed.PendingCall);
        Assert.Contains("schema:read@v1:/input/properties/target", resumed.Construction!.Compact!.CurrentStep!.SchemaPointers);
        Assert.NotNull(resumed.Construction!.Evidence!["schema:read@v1:/input/properties/target"]);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready()
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var source = Context(request)["sources"]![0]!;
            return Wire(request, new() { ["sourceId"] = source["id"]!.ToString(),
                ["clauses"] = new JsonArray(new JsonObject { ["text"] = source["text"]!.ToString(), ["kind"] = "requirement", ["interfaces"] = "none" }) });
        } };
        var state = await Advance(PlannerFixture.Capture(new() { Request = new() { TenantId = "contract-evidence-flow",
            Prompt = "Read the caller-authorized record EXACT.", Options = new JsonObject { ["automatic_correction"] = true } } }), runtime);
        Assert.Empty(state.Diagnostics);
        var capability = new PlanningCapability
        {
            Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = "read", EffectKind = "read",
            Description = "Read only authorized original records. " + new string('x', 2100) + " If target is supplied, read that exact record without replacing missing observations.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
                { ["target"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Z]+$", ["description"] = "Optional exact authorized record identity." },
                  ["unrelated"] = new JsonObject { ["type"] = "string" } }, ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
        };
        for (var i = 0; i < 12; i++) capability.OutputSchema["properties"]!["result" + i] = new JsonObject { ["type"] = "string" };
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"] };
        state.Discovery.Resolved = [capability];
        state.Construction!.Compact!.CurrentStep = new() { Id = "s1", Scope = "/root", Kind = "operation",
            Objective = state.Request.Prompt, OperationIds = ["read"], RequirementIds = ["req_0"],
            BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        return (state, runtime);
    }
    private static JsonObject DraftAction(JsonObject context, string? evidence = null, PlanTask? task = null) => new()
    {
        ["baseHash"] = context["baseHash"]!.ToString(), ["requirements"] = null, ["commitments"] = null,
        ["extension"] = task is null ? null : JsonSerializer.SerializeToNode(new PlanningConstructionExtension
            { Tasks = [task], RequirementIds = ["req_0"] }, PlanningJsonContext.Default.PlanningConstructionExtension),
        ["evidenceRequests"] = evidence is null ? null : new JsonArray(evidence), ["discoveryRequests"] = null,
        ["clarifications"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    };
    private static JsonObject Review(string? evidence = null) => new()
    {
        ["decision"] = evidence is null
            ? new JsonObject { ["verdict"] = "ok", ["reason"] = "The exact original read and its selected data perform this fragment's obligation." }
            : new JsonObject { ["verdict"] = "needs_context", ["reason"] = "The exact original evidence is necessary.", ["contextRequests"] = new JsonArray(evidence) }
    };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response)
    {
        var transported = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject());
        Assert.NotNull(transported);
        return new() { Json = transported, Text = transported.ToJsonString() };
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
