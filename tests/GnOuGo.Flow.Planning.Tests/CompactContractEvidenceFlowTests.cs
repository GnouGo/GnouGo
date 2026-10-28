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
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftAdmissionPagesOnlyOptionalPortsAndPreservesSelectedOriginalClosures(bool correction)
    {
        var (state, _) = await Ready();
        state.Construction!.Compact!.PresentationVersion = 11;
        state.Request.Options["construction_presentation_version"] = 11;
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
            state.Construction.Compact.CurrentStep!.SchemaPointers.Add("schema:read@v1:/output/properties/" + port);
        var originals = state.Construction.Compact.CurrentStep!.SchemaPointers.ToDictionary(address => address,
            address => PlanningCompactConstruction.LoadContractEvidence(state, address, recordSchemaSelection: false)!.ToJsonString());
        var purpose = correction ? "replan" : PlanningConstruction.DraftPurpose;
        SetLimit(32000);
        var full = PlanningConstruction.DraftRequest(state, correction);
        var fullPrepared = Prepare(full);
        var maximum = checked((int)PlanningRequestMeasurements.Measure(fullPrepared, purpose, 32000).EstimatedInputTokens - 1);
        Assert.Equal(40, Context(full)["operations"]![0]!["outputs"]!.AsArray().Count);
        SetLimit(maximum);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var paged = PlanningConstruction.DraftRequest(state, correction);
        var context = Context(paged);
        var operation = context["operations"]![0]!.AsObject();
        Assert.InRange(PlanningRequestMeasurements.Measure(Prepare(paged), purpose, maximum).EstimatedInputTokens, 1, maximum);
        Assert.InRange(operation["outputs"]!.AsArray().Count, 8, 9);
        Assert.Equal(40, operation["outputCount"]!.GetValue<int>());
        Assert.Contains(operation["outputs"]!.AsArray(), row => row!["name"]!.ToString() == "result9");
        Assert.Equal(capability.Description, operation["description"]!.ToString());
        var firstOmitted = Context(full)["operations"]![0]!["outputs"]!.AsArray().Select((row, index) => (row, index))
            .First(item => !operation["outputs"]!.AsArray().Any(row => row!["name"]!.ToString() == item.row!["name"]!.ToString())).index;
        Assert.Equal("port-page:read@v1:/output:" + firstOmitted, operation["nextPorts"]!.ToString());
        var addresses = PlanningCompactConstruction.NavigationEvidence([context["operations"]]);
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
        Assert.Equal(operation["selectedOutputs"]!.ToJsonString(), Context(rejected)["operations"]![0]!["selectedOutputs"]!.ToJsonString());

        LLMRequest Prepare(LLMRequest request) => PlanningModelCalls.PrepareRequest(state, purpose,
            request.Prompt, request.StructuredOutputSchema!.AsObject(), state.Construction.Limits.DraftOutputTokens);
        void SetLimit(int limit)
        {
            state.Request.Generation.MaxInputTokensPerRequest = limit;
            state.Request.Options["construction_limits"]!["phasePolicy"]!["draft"]!["maxInputTokens"] = limit;
            state.Request.Options["construction_limits"]!["phasePolicy"]!["draft"]!["targetInputTokens"] = Math.Min(1000, limit);
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task AdmissionDoesNotPageConsumedProducerFactsOrHistoricalDrafts(int version)
    {
        var (state, _) = await Ready();
        state.Construction!.Compact!.PresentationVersion = version;
        state.Request.Options["construction_presentation_version"] = version;
        state.Catalog!.Capabilities.Add(state.Discovery.Resolved[0]);
        state.Catalog.AllowedStepTypes.Add("set");
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "producer", Kind = "operation", Operation = "read",
            Objective = "Read the exact authorized original.", Inputs = [new("target", new() { Kind = "string", Text = "EXACT" })] }] } };
        state.Construction.Compact.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Compact.CurrentStep.Inputs = [new() { Kind = "output", Source = "producer", Port = "result9" }];
        var full = PlanningConstruction.DraftRequest(state, false);
        state.Request.Generation.MaxInputTokensPerRequest = 512;
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var rejected = PlanningConstruction.DraftRequest(state, false);
        Assert.Equal(Context(full)["operations"]![0]!["outputs"]!.ToJsonString(), Context(rejected)["operations"]![0]!["outputs"]!.ToJsonString());
        Assert.Equal(Context(full)["interfaces"]!.ToJsonString(), Context(rejected)["interfaces"]!.ToJsonString());
        Assert.True(PlanningRequestMeasurements.Measure(PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose,
            rejected.Prompt, rejected.StructuredOutputSchema!.AsObject()), PlanningConstruction.DraftPurpose, 512).EstimatedInputTokens > 512);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task DraftSharesOnlyIdenticalSelectedContractMetadataAndKeepsConsumedClosures(int version)
    {
        var (state, _) = await Ready();
        state.Construction!.Compact!.PresentationVersion = version;
        state.Request.Options["construction_presentation_version"] = version;
        state.Catalog!.Capabilities.Add(state.Discovery.Resolved[0]);
        state.Catalog.AllowedStepTypes.Add("set"); // Construction probes use the existing presence-aware binding path.
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "producer", Kind = "operation", Operation = "read",
            Objective = "Read the caller-authorized original.", Inputs = [new("target", new() { Kind = "string", Text = "EXACT" })] }] } };
        var step = state.Construction.Compact.CurrentStep!;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        step.Inputs = [new() { Kind = "output", Source = "producer", Port = "result0" }];
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var draft = PlanningConstruction.DraftRequest(state, false);
        var context = Context(draft);
        var contract = context["interfaces"]![0]!["producers"]![0]!["contract"]!.AsObject();
        var shared = contract["sharedContractMetadata"] as JsonObject;
        Assert.Equal(version == 11, shared is not null);
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
    [InlineData(7)]
    [InlineData(8)]
    public async Task ProducerSchemaSharingPreservesExactClosuresAndReducesTheCompleteRequestOnlyInVersionEight(int version)
    {
        var (state, _) = await Ready();
        state.Construction!.Compact!.PresentationVersion = version;
        state.Request.Options["construction_presentation_version"] = version;
        var capability = state.Discovery.Resolved[0];
        capability.OutputSchema["$defs"] = new JsonObject { ["original"] = new JsonObject { ["type"] = "string", ["pattern"] = "^ORIGINAL$" } };
        foreach (var port in capability.OutputSchema["properties"]!.AsObject())
            port.Value!.AsObject()["$ref"] = "#/$defs/original";
        state.Catalog!.Capabilities.Add(capability);
        state.Catalog.AllowedStepTypes.Add("set"); // Optional output imports retain their ordinary presence checks.
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "producer", Kind = "operation", Operation = "read",
            Objective = "Read the exact authorized original.", Inputs = [new("target", new() { Kind = "string", Text = "EXACT" })] }] } };
        var step = state.Construction.Compact.CurrentStep!;
        step.Kind = "value"; step.OperationIds.Clear(); step.BaseHash = PlanningConstruction.Hash(state.Plan);
        step.Inputs = [new() { Kind = "output", Source = "producer", Port = "result0" }, new() { Kind = "output", Source = "producer", Port = "result1" }];
        var validation = TaskPlanCompiler.ValidateConstruction(state.Plan, state.Catalog, state.Request,
            state.Construction.OpenScopes.ToHashSet(StringComparer.Ordinal), new Dictionary<string, IReadOnlyList<TaskValue>> { [step.Scope] = step.Inputs });
        Assert.True(validation.Diagnostics.Count == 0, string.Join("; ", validation.Diagnostics.Select(item => item.Code + ": " + item.Message)));
        Assert.Equal(2, validation.Interfaces.GetValueOrDefault(step.Scope)?.Count ?? 0);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var proposal = PlanningConstruction.DraftRequest(state, false);
        var context = Context(proposal);
        var contract = context["interfaces"]![0]!["producers"]![0]!["contract"]!.AsObject();
        var views = contract["selectedOutputs"]!.AsArray();
        var shared = contract["selectedOutputsSharedSchemaMetadata"] as JsonObject;
        Assert.Equal(version == 8, shared is not null);
        var expanded = context.DeepClone().AsObject();
        var expandedContract = expanded["interfaces"]![0]!["producers"]![0]!["contract"]!.AsObject();
        if (shared is not null)
        {
            Assert.NotEmpty(shared["referenceClosure"]!.AsObject());
            foreach (var view in expandedContract["selectedOutputs"]!.AsArray().OfType<JsonObject>())
                foreach (var (key, value) in shared) view[key] = value!.DeepClone();
            expandedContract.Remove("selectedOutputsSharedSchemaMetadata");
        }
        foreach (var view in expandedContract["selectedOutputs"]!.AsArray().OfType<JsonObject>())
        {
            var original = PlanningCompactConstruction.LoadContractEvidence(state, "schema:read@v1:" + view["pointer"]!, recordSchemaSelection: false)!;
            foreach (var key in new[] { "schema", "ancestors", "referenceClosure", "pointer" })
                Assert.True(JsonNode.DeepEquals(original[key], view[key]), key);
            Assert.NotNull(original["id"]); Assert.NotNull(original["view"]);
        }
        var request = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose, proposal.Prompt,
            proposal.StructuredOutputSchema!.AsObject(), state.Construction.Limits.DraftOutputTokens);
        var originalPrompt = proposal.Prompt[..(proposal.Prompt.LastIndexOf('\n') + 1)] + PlanningJsonTransport.Prompt(expanded);
        var unshared = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose, originalPrompt,
            proposal.StructuredOutputSchema.AsObject(), state.Construction.Limits.DraftOutputTokens);
        var measured = PlanningRequestMeasurements.Measure(request, PlanningConstruction.DraftPurpose, 32000);
        var previous = PlanningRequestMeasurements.Measure(unshared, PlanningConstruction.DraftPurpose, 32000);
        Assert.Equal(previous.SchemaHash, measured.SchemaHash);
        Assert.Equal(version == 8, measured.CompleteRequestBytes < previous.CompleteRequestBytes);
        Assert.Equal(version == 8, measured.EstimatedInputTokens < previous.EstimatedInputTokens);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
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
    public async Task GenerationAndCorrectionExposeOptionalPortsBeforeReviewingExactOriginals(bool correction)
    {
        var (state, runtime) = await Ready();
        var baseline = PlanningConstruction.Hash(state.Plan);
        var originalContract = JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability);
        if (correction) state.Diagnostics = [new("CONSTRUCTION_STEP_INPUT_INVALID", "/extension", "Retrieve the missing original input contract.")];
        var phase = correction ? "replan" : PlanningConstruction.DraftPurpose;
        var drafts = 0; var reviews = 0; var retrievedText = "";
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
                Assert.Null(view["outputs"]);
                Assert.Null(view["omittedOptionalInputs"]);
                Assert.Equal("port-page:read@v1:/output:0", view["outputDirectory"]!.ToString());
                return Wire(request, DraftAction(context, task: new() { Id = "read_exact", Kind = "operation",
                    Objective = "Read the caller-authorized record EXACT.", Operation = "read",
                    Inputs = [new("target", new() { Kind = "string", Text = "EXACT" })] }));
            }
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            reviews++;
            Assert.False(context["constructionStage"]!["requirementCompletionClaimed"]!.GetValue<bool>());
            Assert.False(context["constructionStage"]!["planCompletionClaimed"]!.GetValue<bool>());
            Assert.Empty(context["step"]!["closeRequirementIds"]!.AsArray());
            Assert.Equal("req_0", Assert.Single(context["requirements"]!.AsArray())!["id"]!.ToString());
            var supplied = context["evidence"]?.AsObject().Where(pair => pair.Key.StartsWith("description:", StringComparison.Ordinal))
                .Select(pair => pair.Value!.AsObject()).OrderBy(page => page["offset"]!.GetValue<int>()).ToArray() ?? [];
            if (supplied.LastOrDefault() is { } original)
            {
                Assert.Null(view["description"]);
                Assert.Null(view["deterministicValidation"]);
                Assert.Equal("target", Assert.Single(view["boundInputs"]!.AsArray())!["name"]!.ToString());
                retrievedText = string.Concat(supplied.Select(page => page["text"]!.ToString()));
                return Wire(request, original["next"] is { } next ? Review(next.ToString()) : Review());
            }
            Assert.False(view["descriptionComplete"]!.GetValue<bool>());
            return Wire(request, Review(view["descriptionEvidence"]!.ToString()));
        };
        for (var i = 0; i < 9 && state.Construction!.Checkpoints.Count == 0; i++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.DoesNotContain(state.Diagnostics, finding => finding.Required && finding.Code != "CONSTRUCTION_STEP_INPUT_INVALID");
            Assert.NotNull(state.Construction);
            if (state.Construction.Checkpoints.Count == 0) Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        }
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(2, drafts);
        Assert.Equal(3, reviews);
        Assert.Equal(state.Discovery.Resolved[0].Description, retrievedText);
        Assert.Equal("EXACT", Assert.Single(Assert.Single(state.Plan!.Root.Tasks).Inputs).Value.Text);
        Assert.Equal(correction ? 1 : 0, state.ReplanAttempts);
        Assert.Equal(originalContract, JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidencePresentationKeepsSelectedOutputsAndEveryOriginalPortRetrievable(bool historical)
    {
        var (state, _) = await Ready();
        if (historical) state.Construction!.Compact!.PresentationVersion = null;
        var contract = state.Discovery.Resolved[0];
        var before = JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability);
        PlanningConstruction.LoadEvidence(state, ["schema:read@v1:/input/properties/target", "schema:read@v1:/output/properties/result11"]);
        var exactEvidence = state.Construction!.Evidence!.DeepClone();
        var view = Assert.Single(PlanningCompactConstruction.ContractViews(state))!;
        Assert.True(JsonNode.DeepEquals(contract.InputSchema["properties"]!["target"], view["inputSchema"]!["properties"]!["target"]));
        Assert.NotNull(view["selectedOutputs"]);
        Assert.Equal(historical, view["outputs"] is JsonArray);
        Assert.Equal(historical, view["omittedOptionalInputs"] is JsonArray);
        var names = historical ? view["outputs"]!.AsArray().Select(port => port!["name"]!.ToString()).ToList() : [];
        string? address = view["outputDirectory"]!.ToString();
        while (address is not null)
        {
            var page = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
            names.AddRange(page["items"]!.AsArray().Select(port => port!["name"]!.ToString()));
            address = page["next"]?.ToString();
        }
        Assert.Equal(12, names.Distinct(StringComparer.Ordinal).Count());
        Assert.True(JsonNode.DeepEquals(exactEvidence, state.Construction.Evidence));
        Assert.Equal(before, JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability));
    }

    [Fact]
    public async Task SequentialDraftEvidenceRetainsEverySelectedInputWithoutAcceptingABatch()
    {
        var (state, runtime) = await Ready(retainedPolicy: true);
        state.Discovery.Resolved[0].InputSchema["properties"]!["mode"] = new JsonObject
            { ["type"] = "string", ["enum"] = new JsonArray("exact"), ["description"] = "Preserve every original observation." };
        var names = new[] { "target", "unrelated", "mode" };
        var addresses = names.Select(name => "schema:read@v1:/input/properties/" + name).ToArray();
        var baseline = PlanningConstruction.Hash(state.Plan);
        var originals = JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var clauses = state.Requirements!.Outcomes.Select(outcome => (outcome.Id, outcome.Description)).ToArray();
        var drafts = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose == PlanningConstruction.LocalPurpose) return Wire(request, Review());
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            var context = Context(request); var view = context["operations"]![0]!;
            Assert.Equal(drafts, view["editableInputs"]!.AsArray().Count);
            Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 8000).EstimatedInputTokens, 1, 8000);
            for (var i = 0; i < drafts; i++)
                Assert.True(JsonNode.DeepEquals(state.Discovery.Resolved[0].InputSchema["properties"]![names[i]],
                    view["inputSchema"]!["properties"]![names[i]]));
            if (drafts > 0)
            {
                Assert.Single(context["evidence"]!.AsObject());
                Assert.True(JsonNode.DeepEquals(state.Discovery.Resolved[0].InputSchema["properties"]![names[drafts - 1]],
                    context["evidence"]![addresses[drafts - 1]]!["schema"]));
            }
            var batch = DraftAction(context, evidence: addresses[0]);
            batch["evidenceRequests"] = new JsonArray(addresses.Select(address => (JsonNode?)JsonValue.Create(address)).ToArray());
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(batch, request.StructuredOutputSchema!));
            if (drafts < addresses.Length) return Wire(request, DraftAction(context, evidence: addresses[drafts++]));
            drafts++;
            return Wire(request, DraftAction(context, task: new() { Id = "read_exact", Kind = "operation",
                Objective = "Read the exact authorized record.", Operation = "read",
                Inputs = [new("target", new() { Kind = "string", Text = "EXACT" }),
                    new("unrelated", new() { Kind = "string", Text = "original" }),
                    new("mode", new() { Kind = "string", Text = "exact" })] }));
        };
        for (var i = 0; i < 7 && state.Construction!.Checkpoints.Count == 0; i++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
            Assert.NotNull(state.Construction);
            if (state.Construction.Checkpoints.Count == 0)
            {
                Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
                Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
            }
        }
        Assert.Equal(4, drafts);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(names, Assert.Single(state.Plan!.Root.Tasks).Inputs.Select(input => input.Name));
        Assert.Equal(originals, JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability));
        Assert.Equal(clauses, state.Requirements!.Outcomes.Select(outcome => (outcome.Id, outcome.Description)));
        Assert.Equal("read", Assert.Single(Assert.Single(state.Requirements.Outcomes).Commitments!).Operation);
        var acceptedRequirements = JsonSerializer.SerializeToNode(state.Requirements, PlanningJsonContext.Default.PlanningRequirements)!;
        foreach (var key in new[] { "inputs", "outputs" })
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(requirements)![key], acceptedRequirements[key]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialOutputNavigationRetainsExactDirectoryAndSelectedSchemas(bool historical)
    {
        var (state, _) = await Ready();
        if (historical) state.Construction!.Compact!.PresentationVersion = null;
        var original = JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability);
        state.Construction!.Compact!.CurrentStep!.SchemaPointers.Add("schema:read@v1:/output/properties/result11");
        Assert.Null(state.Construction.Evidence);
        var view = Assert.Single(PlanningCompactConstruction.ContractViews(state))!;
        Assert.Equal(historical, view["outputs"] is JsonArray);
        Assert.Equal(12, view["outputCount"]!.GetValue<int>());
        Assert.Equal("/output/properties/result11", Assert.Single(view["selectedOutputs"]!.AsArray())!["pointer"]!.ToString());
        Assert.NotEmpty(view["omittedOptionalInputs"]!.AsArray());
        var names = historical ? view["outputs"]!.AsArray().Select(port => port!["name"]!.ToString()).ToList() : [];
        string? address = view["outputDirectory"]!.ToString();
        while (address is not null)
        {
            var page = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
            names.AddRange(page["items"]!.AsArray().Select(port => port!["name"]!.ToString()));
            address = page["next"]?.ToString();
        }
        Assert.Equal(Enumerable.Range(0, 12).Select(index => "result" + index).Order(StringComparer.Ordinal), names);
        Assert.Null(state.Construction.Evidence);
        Assert.Equal(original, JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability));
        if (!historical)
        {
            var empty = PlannerFixture.Clone(state);
            empty.Construction!.Compact!.CurrentStep!.SchemaPointers.Clear();
            empty.Discovery.Resolved[0].OutputSchema["properties"]!.AsObject().Clear();
            var emptyView = Assert.Single(PlanningCompactConstruction.ContractViews(empty))!;
            Assert.Equal(0, emptyView["outputCount"]!.GetValue<int>());
            Assert.False(emptyView["outputsOmitted"]!.GetValue<bool>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedCorrectionEvidenceUsesCapturedPresentationCardinality(bool historical)
    {
        var (state, _) = await Ready(retainedPolicy: true);
        if (historical) state.Construction!.Compact!.PresentationVersion = null;
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
        response["evidenceRequests"]!.AsArray().Add("schema:read@v1:/input/properties/unrelated");
        Assert.Equal(historical, PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema).Count == 0);
    }

    [Fact]
    public async Task SavedHistoricalDraftConsumesItsExactBatchReceiptWithoutNewAccounting()
    {
        var (state, runtime) = await Ready(retainedPolicy: true);
        state.Construction!.Compact!.PresentationVersion = null;
        state.Request.Options.Remove("construction_presentation_version");
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint =>
            checkpoint.Status == PlanningStatus.Generating && checkpoint.PendingCall?.Purpose == PlanningConstruction.DraftPurpose));
        var issued = saved.PendingCall!.Request;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var response = DraftAction(Context(issued), evidence: "schema:read@v1:/input/properties/target");
        response["evidenceRequests"]!.AsArray().Add("schema:read@v1:/input/properties/unrelated");
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, issued.StructuredOutputSchema!));
        saved.Construction!.Compact!.PresentationVersion = 2;
        saved.Request.Options["construction_presentation_version"] = 2;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, PlanningCompactConstruction.DraftSchema(saved)));
        var calls = saved.ModelCalls; var baseline = PlanningConstruction.Hash(saved.Plan);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Wire(request, response);
        };
        var resumed = await Advance(saved, runtime);
        Assert.Empty(resumed.Diagnostics);
        Assert.Equal(calls, resumed.ModelCalls);
        Assert.Equal(0, resumed.ReplanAttempts);
        Assert.Equal(baseline, PlanningConstruction.Hash(resumed.Plan));
        Assert.Null(resumed.PendingCall);
        Assert.Null(resumed.Construction!.Candidate);
        Assert.Equal(2, resumed.Construction.Evidence!.Count);
        Assert.Equal(2, resumed.Construction.Compact!.CurrentStep!.SchemaPointers.Count);
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
        Assert.NotNull(resumed.Construction.Evidence!["schema:read@v1:/input/properties/target"]);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready(bool retainedPolicy = false)
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var source = Context(request)["sources"]![0]!;
            return Wire(request, new() { ["sourceId"] = source["id"]!.ToString(),
                ["clauses"] = new JsonArray(new JsonObject { ["text"] = source["text"]!.ToString(), ["kind"] = "requirement", ["interfaces"] = "none" }) });
        } };
        var state = await Advance(PlannerFixture.CapturedPresentation(new() { Request = new() { TenantId = "contract-evidence-flow",
            Prompt = "Read the caller-authorized record EXACT.", Options = new JsonObject { ["automatic_correction"] = true } } }), runtime);
        Assert.Empty(state.Diagnostics);
        if (retainedPolicy)
        {
            state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
            state.Construction!.Limits.PhasePolicy = null;
        }
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
    { ["findings"] = evidence is null ? new JsonArray() : null, ["uncertainty"] = "Execution remains unverified.",
        ["evidenceRequests"] = evidence is null ? null : new JsonArray(evidence) };
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
