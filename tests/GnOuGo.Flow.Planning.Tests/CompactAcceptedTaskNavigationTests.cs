using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactAcceptedTaskNavigationTests(ITestOutputHelper output)
{
    private const string NestedScope = "/tasks/container/body";

    [Theory]
    [InlineData("draft")]
    [InlineData("review")]
    [InlineData("discovery")]
    public async Task DecisionViewCarriesExactConsumedProducerMeaningWithoutChangingItsBinding(string phase)
    {
        const string port = "zz_snapshot";
        var state = await ArtifactProducerState(6);
        var capability = state.Catalog!.Capabilities.Single(value => value.Id == "allocate");
        capability.ArtifactContract = null;
        capability.Description = string.Concat(Enumerable.Repeat("Display is only a label; structured mode returns the complete nullable snapshot. ", 45));
        capability.InputSchema = JsonNode.Parse("""
            {"type":"object","properties":{"mode":{"type":"string","enum":["label","structured"]}},"required":["mode"],"additionalProperties":false}
            """)!.AsObject();
        state.Plan!.Root.Tasks.Single(task => task.Id == "make").Inputs = [new("mode", new() { Kind = "string", Text = "structured" })];
        for (var index = 0; index < 7; index++)
        {
            var name = "extra_" + index;
            capability.OutputSchema["properties"]![name] = new JsonObject { ["type"] = "string" };
            capability.OutputSchema["required"]!.AsArray().Add((JsonNode)JsonValue.Create(name)!);
        }
        var snapshot = JsonNode.Parse("""
            {"type":["object","null"],"properties":{"pages":{"type":"array","items":{"type":"string"}}},"required":["pages"],"additionalProperties":false}
            """)!.AsObject();
        capability.OutputSchema["properties"]![port] = snapshot.DeepClone();
        capability.OutputSchema["required"]!.AsArray().Add((JsonNode)JsonValue.Create(port)!);
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "transform"; step.OperationIds.Clear();
        step.Inputs = [TaskArtifactBindingTests.Reference("make", port)];
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        if (phase == "review")
            state.Construction.Candidate = PlanningConstruction.Apply(state, new()
            {
                Scope = step.Scope, RequirementIds = ["req_0"], Tasks = [new()
                {
                    Id = "consume", Kind = "transform", Mode = "interpret", Objective = "Interpret the supplied snapshot without changing its null state.",
                    Inputs = [new("observed", TaskArtifactBindingTests.Reference("make", port))],
                    ResultType = new() { Kind = "object", Fields = [new() { Name = "result", Type = new() { Kind = "string" } }] }
                }]
            });
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = phase == "discovery" ? PlanningCompactConstruction.DiscoveryContext(state) : Context(state, phase == "review");
        var imported = Assert.Single(context["interfaces"]!.AsArray())!;
        Assert.True(JsonNode.DeepEquals(snapshot, imported["schema"]));
        var producer = Assert.Single(imported["producers"]!.AsArray())!;
        Assert.Equal("structured", producer["task"]!["inputs"]![0]!["value"]!["text"]!.ToString());
        Assert.True(JsonNode.DeepEquals(capability.InputSchema["properties"]!["mode"],
            Assert.Single(producer["contract"]!["selectedInputs"]!.AsArray())!["schema"]));
        Assert.Equal(capability.Description, producer["contract"]!["description"]!.ToString());
        Assert.Equal("v1", producer["contract"]!["version"]!.ToString());
        Assert.Equal(PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability)),
            producer["contract"]!["hash"]!.ToString());
        Assert.Equal(10, producer["contract"]!["outputs"]!.AsArray().Count);
        var selected = Assert.Single(producer["contract"]!["selectedOutputs"]!.AsArray())!;
        Assert.Equal("/output/properties/zz_snapshot", selected["pointer"]!.ToString());
        if (phase == "discovery")
        {
            Assert.Null(selected["schema"]); Assert.Null(selected["ancestors"]);
            Assert.Equal("schema:allocate@v1:/output/properties/zz_snapshot", selected["schemaEvidence"]!.ToString());
            Assert.True(producer["contract"]!["selectedOutputSchemasOmitted"]!.GetValue<bool>());
        }
        else
        {
            Assert.True(JsonNode.DeepEquals(snapshot, selected["schema"]));
            Assert.Contains(port, selected["ancestors"]![0]!["constraints"]!["required"]!.AsArray().Select(value => value!.ToString()));
            Assert.Null(producer["contract"]!["selectedOutputSchemasOmitted"]);
        }
        Assert.NotNull(producer["contract"]!["selectedSchemaView"]);
        foreach (var view in producer["contract"]!["selectedInputs"]!.AsArray().Concat(
                     producer["contract"]!["selectedOutputs"]!.AsArray()).Where(view => view?["schema"] is not null))
        {
            Assert.Null(view!["id"]); Assert.Null(view["version"]); Assert.Null(view["hash"]); Assert.Null(view["view"]);
            var retrievedState = PlannerFixture.Clone(state);
            var address = "schema:allocate@v1:" + view["pointer"]!.ToString();
            PlanningConstruction.LoadEvidence(retrievedState, [address]);
            var retrieved = retrievedState.Construction!.Evidence![address]!;
            Assert.Equal("allocate", retrieved["id"]!.ToString()); Assert.Equal("v1", retrieved["version"]!.ToString());
            Assert.Equal(producer["contract"]!["hash"]!.ToString(), retrieved["hash"]!.ToString());
            Assert.Equal(producer["contract"]!["selectedSchemaView"]!.ToString(), retrieved["view"]!.ToString());
            foreach (var field in new[] { "pointer", "schema", "ancestors", "referenceClosure" })
                Assert.True(JsonNode.DeepEquals(view[field], retrieved[field]), field);
        }
        var links = PlanningCompactConstruction.NavigationEvidence([context["interfaces"]]);
        Assert.Contains("schema:allocate@v1:/output/properties/zz_snapshot", links);
        Assert.Contains("description:allocate@v1:0", links);
        Assert.True(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/interfaces/0/producers/0/contract", true));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task DiscoveryDefersOnlyAutomaticOutputClosuresAndRetainsExactRetrieval()
    {
        var state = await ArtifactProducerState(6);
        var capability = state.Catalog!.Capabilities.Single(value => value.Id == "allocate");
        capability.ArtifactContract = null;
        capability.Description = "Structured mode returns the nullable observation; a label is not that observation.";
        capability.InputSchema = JsonNode.Parse("""
            {"type":"object","properties":{"mode":{"type":"string","enum":["label","structured"],"default":"label",
            "description":"The exact representation selected by the producing task."}},"required":["mode"],"additionalProperties":false}
            """)!.AsObject();
        state.Plan!.Root.Tasks.Single(task => task.Id == "make").Inputs = [new("mode", new() { Kind = "string", Text = "structured" })];
        var description = string.Concat(Enumerable.Repeat("The nullable structured observation preserves its exact records. ", 8));
        capability.OutputSchema["properties"]!["display"] = JsonNode.Parse("""
            {"type":["object","null"],"properties":{"record":{"type":"string","pattern":"^exact-[0-9]+$"}},
            "required":["record"],"additionalProperties":false}
            """);
        capability.OutputSchema["properties"]!["display"]!["description"] = description;
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = null; step.OperationIds.Clear(); step.DiscoveryComplete = false;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        AuthorAllowance(state, 128000);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var full = context.DeepClone().AsObject();
        PlanningCompactConstruction.AddDecisionContext(state, full, review: false);
        var producer = context["interfaces"]![0]!["producers"]![0]!;
        var originalProducer = full["interfaces"]![0]!["producers"]![0]!;
        Assert.True(JsonNode.DeepEquals(context["interfaces"]![0]!["schema"], full["interfaces"]![0]!["schema"]));
        Assert.True(JsonNode.DeepEquals(producer["task"], originalProducer["task"]));
        Assert.True(JsonNode.DeepEquals(producer["contract"]!["selectedInputs"], originalProducer["contract"]!["selectedInputs"]));
        Assert.Equal(capability.Description, producer["contract"]!["description"]!.ToString());
        var port = Assert.Single(producer["contract"]!["outputs"]!.AsArray(), row => row!["name"]!.ToString() == "display")!;
        Assert.Equal(description, port["description"]!.ToString());
        Assert.True(JsonNode.DeepEquals(capability.OutputSchema["properties"]!["display"]!["type"], port["type"]));
        var selected = Assert.Single(producer["contract"]!["selectedOutputs"]!.AsArray())!;
        Assert.Equal("/output/properties/display", selected["pointer"]!.ToString());
        Assert.Null(selected["schema"]); Assert.Null(selected["ancestors"]); Assert.Null(selected["referenceClosure"]);
        var address = selected["schemaEvidence"]!.ToString();
        Assert.Equal("schema:allocate@v1:/output/properties/display", address);
        Assert.True(PlanningCompactConstruction.NavigationEvidence([full["interfaces"]])
            .SetEquals(PlanningCompactConstruction.NavigationEvidence([context["interfaces"]])));
        var request = AuthorRequest(state, PlanningCompactConstruction.DiscoveryPurpose, context);
        var originalRequest = AuthorRequest(state, PlanningCompactConstruction.DiscoveryPurpose, full);
        Assert.True(JsonNode.DeepEquals(request.StructuredOutputSchema, originalRequest.StructuredOutputSchema));
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(new JsonArray(address),
            request.StructuredOutputSchema!["properties"]!["evidenceRequests"]!));
        var prepared = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, request.Prompt,
            request.StructuredOutputSchema.AsObject(), state.Construction.Limits.DraftOutputTokens);
        var originalPrepared = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, originalRequest.Prompt,
            originalRequest.StructuredOutputSchema!.AsObject(), state.Construction.Limits.DraftOutputTokens);
        var measured = PlanningRequestMeasurements.Measure(prepared, PlanningCompactConstruction.DiscoveryPurpose, 128000);
        var originalMeasured = PlanningRequestMeasurements.Measure(originalPrepared, PlanningCompactConstruction.DiscoveryPurpose, 128000);
        Assert.True(measured.CompleteRequestBytes < originalMeasured.CompleteRequestBytes);
        Assert.True(measured.EstimatedInputTokens < originalMeasured.EstimatedInputTokens);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        PlanningConstruction.LoadEvidence(state, [address]);
        var retained = state.Construction.Evidence![address]!.DeepClone();
        var retrieved = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.True(JsonNode.DeepEquals(retained, retrieved["evidence"]![address]));
        Assert.True(JsonNode.DeepEquals(capability.OutputSchema["properties"]!["display"], retained["schema"]));
        Assert.NotEmpty(retained["ancestors"]!.AsArray());
        Assert.Empty(step.OperationIds); Assert.Empty(step.SchemaPointers);
        Assert.Null(state.Construction.Candidate); Assert.Equal(step.BaseHash, PlanningConstruction.Hash(state.Plan));
        var draft = Context(state, review: false);
        Assert.True(JsonNode.DeepEquals(capability.OutputSchema["properties"]!["display"],
            draft["interfaces"]![0]!["producers"]![0]!["contract"]!["selectedOutputs"]![0]!["schema"]));
    }

    [Theory]
    [InlineData("whole")]
    [InlineData("output")]
    [InlineData("present")]
    [InlineData("field")]
    public async Task DiscoveryProjectionKeepsWholeAndNestedHintPointersAcrossProducers(string kind)
    {
        var state = await ArtifactProducerState(6);
        var second = PlanningConstruction.Clone(state.Plan!).Root.Tasks.Single(task => task.Id == "make");
        second.Id = "second_make"; state.Plan!.Root.Tasks.Add(second);
        TaskValue Hint(string id)
        {
            var value = TaskArtifactBindingTests.Reference(id, "display");
            if (kind == "whole") value.Port = null;
            if (kind == "present") value.Kind = "present";
            return kind == "field" ? TaskArtifactBindingTests.Field(value, "record") : value;
        }
        var context = new JsonObject { ["interfaces"] = new JsonArray(new JsonObject
            { ["value"] = PlanningConstruction.Node(Hint("make"), PlanningJsonContext.Default.TaskValue) },
            new JsonObject { ["value"] = PlanningConstruction.Node(Hint("second_make"), PlanningJsonContext.Default.TaskValue) }) };
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        PlanningCompactConstruction.AddDecisionContext(state, context, review: false, discovery: true);
        var producers = context["interfaces"]!.AsArray().SelectMany(row => row!["producers"]!.AsArray()).ToArray();
        Assert.Equal(new[] { "make", "second_make" }, producers.Select(producer => producer!["id"]!.ToString()));
        var pointer = kind == "whole" ? "/output" : "/output/properties/display";
        foreach (var producer in producers)
        {
            var selected = Assert.Single(producer!["contract"]!["selectedOutputs"]!.AsArray())!;
            Assert.Equal(pointer, selected["pointer"]!.ToString());
            Assert.Equal("schema:allocate@v1:" + pointer, selected["schemaEvidence"]!.ToString());
            Assert.Null(selected["schema"]);
            Assert.Equal("port-page:allocate@v1:/output:0", producer["contract"]!["outputDirectory"]!.ToString());
        }
        Assert.Contains("schema:allocate@v1:" + pointer, PlanningCompactConstruction.NavigationEvidence([context["interfaces"]]));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var review = context.DeepClone().AsObject();
        PlanningCompactConstruction.AddDecisionContext(state, review, review: true, discovery: true);
        foreach (var row in review["interfaces"]!.AsArray())
        {
            var contract = row!["producers"]![0]!["contract"]!;
            Assert.NotNull(contract["selectedOutputs"]![0]!["schema"]);
            Assert.Null(contract["selectedOutputSchemasOmitted"]);
        }
    }

    [Fact]
    public async Task IssuedVersionSixDiscoveryRetainsItsAutomaticClosuresOnRecovery()
    {
        var state = await ArtifactProducerState(6);
        state.Construction!.Compact!.CurrentStep!.Kind = null;
        state.Construction.Compact.CurrentStep.OperationIds.Clear();
        AuthorAllowance(state, 128000);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        PlanningCompactConstruction.AddDecisionContext(state, context, review: false);
        var proposal = AuthorRequest(state, PlanningCompactConstruction.DiscoveryPurpose, context);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, proposal.Prompt,
            proposal.StructuredOutputSchema!.AsObject(), state.Construction.Limits.DraftOutputTokens);
        Assert.False(string.IsNullOrWhiteSpace(issued.ClientRequestId)); Assert.True(issued.MaxTokens is > 0);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued };
        state.ModelCalls = 1; state = PlannerFixture.Clone(state);
        var saved = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var next = AuthorRequest(state, PlanningCompactConstruction.DiscoveryPurpose, PlanningCompactConstruction.DiscoveryContext(state));
        Assert.NotEqual(issued.Prompt, next.Prompt);
        Assert.NotEqual(saved, JsonSerializer.Serialize(next, PlanningJsonContext.Default.LLMRequest));
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(saved, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = new JsonObject { ["evidenceRequests"] = new JsonArray("requirement:req_0"),
                ["binding"] = null, ["discoveryRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null } };
        } };
        await PlanningModelCalls.CallAsync(state, runtime, PlanningCompactConstruction.DiscoveryPurpose,
            next.Prompt, next.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, state.Construction!.Limits.DraftOutputTokens);
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall); Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecisionProducerDirectoryOccursOnceInThePreparedRequest(bool review)
    {
        var state = await ArtifactProducerState(6);
        var capability = state.Catalog!.Capabilities.Single(value => value.Id == "allocate");
        for (var index = 0; index < 17; index++)
            capability.OutputSchema["properties"]!["additional_" + index] = new JsonObject { ["type"] = "string" };
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        step.Inputs = [TaskArtifactBindingTests.Reference("make", "handle")];
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        if (review) construction.Candidate = PlanningConstruction.Apply(state, ArtifactConsumer("handle"));
        AuthorAllowance(state, 128000);
        var request = review ? HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false).Request
            : PlanningConstruction.DraftRequest(state, repair: false);
        // Ordinary review preparation records its source-window manifest. The
        // projection/measurement comparison must not create any further change.
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
        var producer = Assert.Single(context["interfaces"]![0]!["producers"]!.AsArray())!;
        var navigation = Assert.Single(context["taskEvidence"]!["items"]!.AsArray(), row => row?["task"]?.ToString() == "make")!;
        Assert.Equal(19, producer["contract"]!["outputs"]!.AsArray().Count);
        Assert.Null(navigation["outputs"]);
        Assert.Equal(producer["contract"]!["outputDirectory"]!.ToString(), navigation["outputDirectory"]!.ToString());
        Assert.NotNull(producer["task"]!["inputs"]);
        Assert.NotEmpty(producer["contract"]!["selectedOutputs"]!.AsArray());

        var repeated = context.DeepClone().AsObject();
        RestoreEmbeddedSchemaMetadata(repeated["interfaces"]![0]!["producers"]![0]!["contract"]!.AsObject());
        Assert.Single(repeated["taskEvidence"]!["items"]!.AsArray(), row => row?["task"]?.ToString() == "make")!["outputs"] =
            producer["contract"]!["outputs"]!.DeepClone();
        var currentLinks = PlanningCompactConstruction.NavigationEvidence([context["interfaces"], context["taskEvidence"]]);
        Assert.True(currentLinks.SetEquals(PlanningCompactConstruction.NavigationEvidence([repeated["interfaces"], repeated["taskEvidence"]])));
        var restored = context["interfaces"]!.DeepClone();
        RestoreEmbeddedSchemaMetadata(restored[0]!["producers"]![0]!["contract"]!.AsObject());
        Assert.True(JsonNode.DeepEquals(restored, repeated["interfaces"]));
        var currentSchema = request.StructuredOutputSchema!.DeepClone().AsObject();
        var repeatedSchema = currentSchema.DeepClone().AsObject();
        if (review)
        {
            PlanningCompactConstruction.ConstrainReviewEvidence(state, context, currentSchema);
            PlanningCompactConstruction.ConstrainReviewEvidence(state, repeated, repeatedSchema);
        }
        else
        {
            PlanningCompactConstruction.ConstrainDraftEvidence(state, context, currentSchema);
            PlanningCompactConstruction.ConstrainDraftEvidence(state, repeated, repeatedSchema);
        }
        Assert.True(JsonNode.DeepEquals(currentSchema, repeatedSchema));
        var purpose = review ? PlanningConstruction.LocalPurpose : PlanningConstruction.DraftPurpose;
        var output = review ? construction.Limits.ReviewOutputTokens : construction.Limits.DraftOutputTokens;
        var prepared = PlanningModelCalls.PrepareRequest(state, purpose, request.Prompt, request.StructuredOutputSchema.AsObject(), output);
        var repeatedPrompt = request.Prompt[..(request.Prompt.LastIndexOf('\n') + 1)] + PlanningJsonTransport.Prompt(repeated);
        var oldPrepared = PlanningModelCalls.PrepareRequest(state, purpose, repeatedPrompt, request.StructuredOutputSchema.AsObject(), output);
        var measured = PlanningRequestMeasurements.Measure(prepared, purpose, 128000);
        var previous = PlanningRequestMeasurements.Measure(oldPrepared, purpose, 128000);
        Assert.Equal(previous.Contracts.Select(item => (item.Id, item.Version, item.Hash)),
            measured.Contracts.Select(item => (item.Id, item.Version, item.Hash)));
        Assert.Equal(previous.SchemaHash, measured.SchemaHash);
        Assert.True(measured.CompleteRequestBytes < previous.CompleteRequestBytes);
        Assert.True(measured.EstimatedInputTokens < previous.EstimatedInputTokens);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData("task")]
    [InlineData("scope")]
    [InlineData("phase")]
    [InlineData("operation")]
    [InlineData("operationVersion")]
    [InlineData("contractHash")]
    [InlineData("outputs")]
    public async Task DecisionProducerDirectoryRequiresExactIdentityAndContents(string changed)
    {
        var state = await ArtifactProducerState(6);
        var context = Context(state, review: false);
        var producer = context["interfaces"]![0]!["producers"]![0]!;
        var navigation = Assert.Single(context["taskEvidence"]!["items"]!.AsArray(), row => row?["task"]?.ToString() == "make")!;
        navigation["outputs"] = producer["contract"]!["outputs"]!.DeepClone();
        if (changed == "outputs") navigation["outputs"]![0]!["required"] = !navigation["outputs"]![0]!["required"]!.GetValue<bool>();
        else navigation[changed] = "different";
        var retained = navigation.ToJsonString();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        PlanningCompactConstruction.AddDecisionContext(state, context, review: false);
        Assert.Equal(retained, navigation.ToJsonString());
        Assert.NotNull(context["interfaces"]![0]!["producers"]![0]!["contract"]!["outputs"]);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task IssuedVersionSixReviewKeepsItsRepeatedDirectoryDuringRecovery()
    {
        var state = await ArtifactProducerState(6);
        state.Construction!.Compact!.CurrentStep!.Inputs = [TaskArtifactBindingTests.Reference("make", "handle")];
        state.Construction.Candidate = PlanningConstruction.Apply(state, ArtifactConsumer("handle"));
        AuthorAllowance(state, 128000);
        var review = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false);
        var producer = review.Context["interfaces"]![0]!["producers"]![0]!;
        RestoreEmbeddedSchemaMetadata(producer["contract"]!.AsObject());
        Assert.NotNull(producer["contract"]!["selectedOutputs"]![0]!["id"]);
        Assert.Single(review.Context["taskEvidence"]!["items"]!.AsArray(), row => row?["task"]?.ToString() == "make")!["outputs"] =
            producer["contract"]!["outputs"]!.DeepClone();
        var prompt = review.Request.Prompt[..(review.Request.Prompt.LastIndexOf('\n') + 1)] + PlanningJsonTransport.Prompt(review.Context);
        var interrupted = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(state, interrupted,
            PlanningConstruction.LocalPurpose, prompt, review.Request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct,
            state.Construction.Limits.ReviewOutputTokens));
        var saved = PlannerFixture.Clone(interrupted.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
        var exact = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls;
        var next = HybridWorkflowPlanner.CreateConstructionReviewRequest(saved, false);
        Assert.Null(next.Context["interfaces"]![0]!["producers"]![0]!["contract"]!["selectedOutputs"]![0]!["id"]);
        Assert.Null(Assert.Single(next.Context["taskEvidence"]!["items"]!.AsArray(), row => row?["task"]?.ToString() == "make")!["outputs"]);
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = new JsonObject { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray() },
                ["uncertainty"] = "Runtime execution remains unverified." } };
        } };
        await PlanningModelCalls.CallAsync(saved, recovery, PlanningConstruction.LocalPurpose, next.Request.Prompt,
            next.Request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, saved.Construction!.Limits.ReviewOutputTokens);
        Assert.Single(recovery.Calls); Assert.Equal(calls, saved.ModelCalls); Assert.Equal(0, saved.ReplanAttempts);
    }

    [Theory]
    [InlineData("id", false)]
    [InlineData("version", false)]
    [InlineData("hash", false)]
    [InlineData("id", true)]
    [InlineData("version", true)]
    [InlineData("hash", true)]
    public async Task EmbeddedProducerSchemasRequireExactParentIdentity(string key, bool absent)
    {
        var state = await ArtifactProducerState(6);
        var contract = Context(state, false)["interfaces"]![0]!["producers"]![0]!["contract"]!.AsObject();
        RestoreEmbeddedSchemaMetadata(contract);
        var selected = contract["selectedOutputs"]![0]!.AsObject();
        if (absent) selected.Remove(key); else selected[key] = "different";
        var before = contract.ToJsonString();
        PlanningCompactConstruction.CompactProducerSchemaMetadata(contract);
        Assert.Equal(before, contract.ToJsonString());
    }

    [Fact]
    public async Task EmbeddedProducerSchemasKeepDifferentViewDescriptions()
    {
        var state = await ArtifactProducerState(6);
        state.Construction!.Compact!.CurrentStep!.Inputs = [TaskArtifactBindingTests.Reference("make", "display"),
            TaskArtifactBindingTests.Reference("make", "handle")];
        var contract = Context(state, false)["interfaces"]![0]!["producers"]![0]!["contract"]!.AsObject();
        RestoreEmbeddedSchemaMetadata(contract);
        var views = contract["selectedOutputs"]!.AsArray(); Assert.Equal(2, views.Count);
        views[1]!["view"] = "A different retained qualification.";
        var descriptions = views.Select(view => view!["view"]!.ToString()).ToArray();
        PlanningCompactConstruction.CompactProducerSchemaMetadata(contract);
        Assert.Null(contract["selectedSchemaView"]);
        Assert.Equal(descriptions, views.Select(view => view!["view"]!.ToString()));
        Assert.All(views, view => Assert.Null(view!["id"]));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task HistoricalProducerContextKeepsExactBytesWhenDecisionProjectionIsRequested(int version)
    {
        var state = await ArtifactProducerState(version);
        var context = Context(state, false); var before = context.ToJsonString();
        PlanningCompactConstruction.AddDecisionContext(state, context, review: false);
        Assert.Equal(before, context.ToJsonString());
    }

    private static void RestoreEmbeddedSchemaMetadata(JsonObject contract)
    {
        foreach (var view in new[] { "selectedInputs", "selectedOutputs" }.SelectMany(name =>
                     contract[name]!.AsArray().OfType<JsonObject>()).Where(view => view["schema"] is not null))
        {
            foreach (var key in new[] { "id", "version", "hash" }) view[key] = contract[key]!.DeepClone();
            view["view"] = contract["selectedSchemaView"]!.DeepClone();
        }
        contract.Remove("selectedSchemaView");
    }

    [Fact]
    public async Task DecisionViewSelectionKeepsEveryCompactPortWithoutUnconsumedProducerProse()
    {
        var state = await ArtifactProducerState(6);
        var producer = state.Catalog!.Capabilities.Single(capability => capability.Id == "allocate");
        producer.Description = "Each output has its own declared business identity.";
        for (var index = 0; index < 11; index++)
            producer.OutputSchema["properties"]!["port_" + index] = new JsonObject { ["type"] = "string" };
        state.Construction!.Compact!.CurrentStep = null;
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.SelectionContext(state);
        var row = Assert.Single(context["interfaces"]!.AsArray(), item => item?["task"]?.ToString() == "make")!;
        Assert.Null(row["producers"]);
        Assert.Equal("description:allocate@v1:0", row["descriptionEvidence"]!.ToString());
        Assert.Equal(13, row["outputs"]!.AsArray().Count);
        Assert.DoesNotContain(producer.Description, context.ToJsonString(), StringComparison.Ordinal);
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        PlanningCompactConstruction.ConstrainSelectionEvidence(state, context, schema);
        Assert.Contains("schema:allocate@v1:/output/properties/port_10", schema.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("draft")]
    [InlineData("discovery")]
    public void DecisionViewIncludesUnassociatedOriginalClausesAndWholeContinuations(string phase)
    {
        var state = Ready("/root", 6);
        var compact = state.Construction!.Compact!;
        compact.Sources.AddRange([
            new() { Id = "other:0", Version = PlanningGraphCompiler.Fingerprint("Use the exact "), Text = "Use the exact ", Indexed = true },
            new() { Id = "other:1", Version = PlanningGraphCompiler.Fingerprint("approved branch."), Text = "approved branch.", ContinuationOf = "other:0", Indexed = true },
            new() { Id = "global:0", Version = PlanningGraphCompiler.Fingerprint("Keep all values."), Text = "Keep all values.", Indexed = true }
        ]);
        state.Requirements!.Outcomes.Add(new("req_1", "Use the exact approved branch."));
        state.Construction.Requirements.Add(new() { Id = "req_1" });
        compact.RequirementSources["req_1"] = ["other:0", "other:1"];
        compact.GlobalSourceIds = ["global:0"];
        state.Construction.Candidate = null;
        if (phase == "selection") compact.CurrentStep = null;
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = phase switch
        {
            "selection" => PlanningCompactConstruction.SelectionContext(state),
            "discovery" => PlanningCompactConstruction.DiscoveryContext(state),
            _ => Context(state, false)
        };
        Assert.Equal(new[] { "req_0", "req_1" }, context["requirements"]!.AsArray().Select(row => row!["id"]!.ToString()));
        var original = context["requirements"]![1]!;
        Assert.Equal("Use the exact approved branch.", original["original"]!.ToString());
        Assert.Equal(new[] { "other:0", "other:1" }, original["sources"]!.AsArray().Select(row => row!["id"]!.ToString()));
        Assert.All(original["sources"]!.AsArray(), row => Assert.Null(row!["text"]));
        Assert.Equal("Keep all values.", Assert.Single(context["globalConstraints"]!.AsArray())!["text"]!.ToString());
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public void DecisionOriginalRowsPreserveMappedTextWhenRetainedDescriptionIsDifferent()
    {
        var state = Ready("/root", 6);
        var original = state.Construction!.Compact!.Sources[0];
        state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with { Description = "A retained summary." };
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var rows = PlanningCompactConstruction.OriginalRequirementRows(state);
        Assert.Equal("A retained summary.", rows[0]!["original"]!.ToString());
        Assert.Equal(original.Text, rows[0]!["sources"]![0]!["text"]!.ToString());
        Assert.Equal(original.Version, rows[0]!["sources"]![0]!["version"]!.ToString());
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(PlanningCompactConstruction.SelectPurpose)]
    [InlineData(PlanningCompactConstruction.DiscoveryPurpose)]
    [InlineData(PlanningConstruction.DraftPurpose)]
    [InlineData("replan")]
    public void DecisionAuthorKeepsEverySmallOriginalWhenItsCompleteRequestFits(string purpose)
    {
        var state = Ready("/root", 6);
        AddDecisionRequirement(state, "req_1", "Keep the complete ", "original constraint.");
        AuthorAllowance(state, 128000);
        state.Construction!.Candidate = null;
        if (purpose == PlanningCompactConstruction.SelectPurpose) state.Construction.Compact!.CurrentStep = null;
        var context = AuthorContext(state, purpose);
        var beforeState = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var request = AuthorRequest(state, purpose, context);
        Assert.Equal(new[] { "req_0", "req_1" }, context["requirements"]!.AsArray().Select(row => row!["id"]!.ToString()));
        Assert.Contains("Keep the complete original constraint.", request.Prompt, StringComparison.Ordinal);
        Assert.Null(context["omittedRequirementCount"]);
        Assert.Equal(beforeState, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(PlanningCompactConstruction.SelectPurpose)]
    [InlineData(PlanningCompactConstruction.DiscoveryPurpose)]
    [InlineData(PlanningConstruction.DraftPurpose)]
    [InlineData("replan")]
    public async Task DecisionAuthorBoundsWholeOriginalsAgainstThePreparedEnvelope(string purpose)
    {
        var state = await ArtifactProducerState(6);
        AddDecisionRequirement(state, "req_1", new string('x', 12000), " Exact final original.");
        AddDecisionRequirement(state, "req_2", "Keep the explicitly retrieved ", "complete original too.");
        var compact = state.Construction!.Compact!;
        compact.Sources.Add(new() { Id = "global:0", Version = "global-version", Text = "Preserve the exact global restriction.", Indexed = true });
        compact.GlobalSourceIds = ["global:0"];
        PlanningConstruction.LoadEvidence(state, ["requirement:req_2"]);
        AuthorAllowance(state, 128000);
        if (purpose == PlanningCompactConstruction.SelectPurpose) compact.CurrentStep = null;
        var context = AuthorContext(state, purpose);
        var allOriginals = context["requirements"]!.DeepClone();
        var originalInterfaces = context["interfaces"]!.DeepClone();
        var originalGlobals = context["globalConstraints"]!.DeepClone();
        var originalEvidence = context["evidence"]?.DeepClone();
        var full = AuthorRequest(state, purpose, context);
        var prepared = PlanningModelCalls.PrepareRequest(state, purpose, full.Prompt, full.StructuredOutputSchema!.AsObject(),
            state.Construction.Limits.DraftOutputTokens);
        var limit = checked((int)PlanningRequestMeasurements.Measure(prepared, purpose, 128000).EstimatedInputTokens - 1);
        // The original prompt alone fits: the complete envelope, including the
        // response grammar and captured model settings, triggers this fallback.
        Assert.True((Encoding.UTF8.GetByteCount(full.Prompt) + 2L) / 3 + 256 < limit);
        AuthorAllowance(state, limit);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var bounded = AuthorRequest(state, purpose, context);
        Assert.Equal(new[] { "req_0", "req_2" }, context["requirements"]!.AsArray().Select(row => row!["id"]!.ToString()));
        Assert.True(JsonNode.DeepEquals(allOriginals[0], context["requirements"]![0]));
        Assert.True(JsonNode.DeepEquals(allOriginals[2], context["requirements"]![1]));
        Assert.Equal(2, context["requirements"]![1]!["sources"]!.AsArray().Count);
        Assert.True(JsonNode.DeepEquals(originalInterfaces, context["interfaces"]));
        Assert.True(JsonNode.DeepEquals(originalGlobals, context["globalConstraints"]));
        Assert.True(JsonNode.DeepEquals(originalEvidence, context["evidence"]));
        Assert.Equal(1, context["omittedRequirementCount"]!.GetValue<int>());
        Assert.Equal("page:requirements:0", context["requirementsDirectory"]!.ToString());
        Assert.Equal("page:requirements:1", context["nextRequirements"]!.ToString());
        Assert.Contains("page:requirements:1", bounded.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("source:req_1:0", bounded.StructuredOutputSchema.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 12000), bounded.Prompt, StringComparison.Ordinal);
        var measured = PlanningRequestMeasurements.Measure(PlanningModelCalls.PrepareRequest(state, purpose, bounded.Prompt,
            bounded.StructuredOutputSchema.AsObject(), state.Construction.Limits.DraftOutputTokens), purpose, limit);
        Assert.InRange(measured.EstimatedInputTokens, 1, limit);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        Assert.Empty(PlanningContractValidation.ValidateSchema(bounded.StructuredOutputSchema.AsObject(), strict: true));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecisionTypedRepairInstallsOriginalsBeforeCompleteRequestBudgeting(bool bounded)
    {
        var state = await ArtifactProducerState(6);
        AddDecisionRequirement(state, "req_1", bounded ? new string('x', 12000) : "Keep this unassociated ", "original restriction.");
        AddDecisionRequirement(state, "req_2", "Keep the explicitly retrieved ", "whole original.");
        var compact = state.Construction!.Compact!;
        compact.Sources.Add(new() { Id = "global:0", Version = "global-version", Text = "Preserve the exact global restriction.", Indexed = true });
        compact.GlobalSourceIds = ["global:0"];
        PlanningConstruction.LoadEvidence(state, ["requirement:req_2"]);
        compact.CurrentStep!.Inputs = [TaskArtifactBindingTests.Reference("make", "handle")];
        state.Construction.Candidate = PlanningConstruction.Apply(state, ArtifactConsumer("handle"));
        var authority = PlannerFixture.Clone(state);
        authority.Plan = authority.Construction!.Candidate!.Plan;
        authority.EditablePaths = ["/tasks/consume/inputs/location"];
        authority.RevisionScope = authority.EditablePaths!;
        AuthorAllowance(state, 128000);
        var full = PlanningConstruction.RepairRequest(state, authority);
        var fullContext = PlanningRepairPatch.RequestContext(full);
        Assert.True(PlanningRepairPatch.Issued(full.StructuredOutputSchema!.AsObject()));
        Assert.Equal(new[] { "req_0", "req_1", "req_2" }, fullContext["requirements"]!.AsArray().Select(row => row!["id"]!.ToString()));
        Assert.Equal("/tasks/consume/inputs/location", Assert.Single(fullContext["repair"]!["slots"]!.AsArray())!["location"]!.ToString());
        Assert.NotEmpty(fullContext["interfaces"]![0]!["producers"]!.AsArray());
        var limit = 128000;
        if (bounded)
        {
            var prepared = PlanningModelCalls.PrepareRequest(state, "replan", full.Prompt, full.StructuredOutputSchema.AsObject(),
                state.Construction.Limits.DraftOutputTokens);
            limit = checked((int)PlanningRequestMeasurements.Measure(prepared, "replan", limit).EstimatedInputTokens - 1);
            Assert.True((Encoding.UTF8.GetByteCount(full.Prompt) + 2L) / 3 + 256 < limit);
            AuthorAllowance(state, limit);
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var request = PlanningConstruction.RepairRequest(state, authority);
        var context = PlanningRepairPatch.RequestContext(request);
        Assert.Equal(bounded ? new[] { "req_0", "req_2" } : ["req_0", "req_1", "req_2"],
            context["requirements"]!.AsArray().Select(row => row!["id"]!.ToString()));
        foreach (var key in new[] { "repair", "globalConstraints", "interfaces", "evidence" })
            Assert.True(JsonNode.DeepEquals(fullContext[key], context[key]), key);
        Assert.True(JsonNode.DeepEquals(fullContext["requirements"]![0], context["requirements"]![0]));
        Assert.True(JsonNode.DeepEquals(fullContext["requirements"]![2], context["requirements"]![bounded ? 1 : 2]));
        if (bounded)
        {
            Assert.Equal(1, context["omittedRequirementCount"]!.GetValue<int>());
            Assert.Equal("page:requirements:1", context["nextRequirements"]!.ToString());
            Assert.Contains("page:requirements:1", request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain("source:req_1:0", request.StructuredOutputSchema.ToJsonString(), StringComparison.Ordinal);
        }
        else Assert.Null(context["omittedRequirementCount"]);
        var measured = PlanningRequestMeasurements.Measure(PlanningModelCalls.PrepareRequest(state, "replan", request.Prompt,
            request.StructuredOutputSchema!.AsObject(), state.Construction.Limits.DraftOutputTokens), "replan", limit);
        Assert.InRange(measured.EstimatedInputTokens, 1, limit);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema.AsObject(), strict: true));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DecisionAuthorBudgetingLeavesHistoricalRequestBytesUnchanged(int version)
    {
        var state = Ready("/root", version);
        var context = new JsonObject { ["requirements"] = new JsonArray(new JsonObject
            { ["id"] = "req_0", ["original"] = new string('x', 50000) }) };
        var schema = PlanningSchemas.Object(("result", PlanningSchemas.String()));
        var expected = PlanningCompactConstruction.Request("Retained instructions.", context, schema.DeepClone().AsObject(), PlanningConstruction.DraftPurpose);
        var actual = PlanningCompactConstruction.Request(state, "Retained instructions.", context, schema.DeepClone().AsObject(), PlanningConstruction.DraftPurpose);
        Assert.Equal(JsonSerializer.Serialize(expected, PlanningJsonContext.Default.LLMRequest),
            JsonSerializer.Serialize(actual, PlanningJsonContext.Default.LLMRequest));
        Assert.Null(context["omittedRequirementCount"]);
    }

    [Fact]
    public async Task DecisionAuthorRetainsIndivisibleGlobalsEvenWhenAdmissionMustStop()
    {
        var state = Ready("/root", 6);
        state.Construction!.Candidate = null;
        AddDecisionRequirement(state, "req_1", new string('x', 12000));
        var compact = state.Construction.Compact!;
        var global = new string('g', 60000) + " exact final global restriction.";
        compact.Sources.Add(new() { Id = "global:0", Version = "global-version", Text = global, Indexed = true });
        compact.GlobalSourceIds = ["global:0"];
        AuthorAllowance(state, 6000);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Contains(global, request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 12000), request.Prompt, StringComparison.Ordinal);
        var runtime = new TestRuntime();
        var exception = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningConstruction.DraftPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct));
        Assert.Equal("MODEL_INPUT_LIMIT", exception.Code);
        Assert.Empty(runtime.Calls);
        Assert.Null(state.PendingCall);
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal("rejected_before_dispatch", Assert.Single(state.RequestMeasurements!).Admission);
    }

    private static void AddDecisionRequirement(PlanningSession state, string id, params string[] parts)
    {
        var compact = state.Construction!.Compact!;
        var sourceIds = new List<string>();
        for (var index = 0; index < parts.Length; index++)
        {
            var sourceId = id + ":" + index;
            compact.Sources.Add(new() { Id = sourceId, Version = PlanningGraphCompiler.Fingerprint(parts[index]), Text = parts[index],
                ContinuationOf = index == 0 ? null : sourceIds[0], Indexed = true });
            sourceIds.Add(sourceId);
        }
        state.Requirements!.Outcomes.Add(new(id, string.Concat(parts)));
        state.Construction.Requirements.Add(new() { Id = id });
        compact.RequirementSources[id] = sourceIds;
    }

    [Theory]
    [InlineData(PlanningCompactConstruction.SelectPurpose)]
    [InlineData(PlanningCompactConstruction.DiscoveryPurpose)]
    [InlineData(PlanningConstruction.DraftPurpose)]
    [InlineData("replan")]
    public void AuthorProgressSharingRestoresEveryOrderedRowAndPreservesEvidenceAuthority(string purpose)
    {
        var state = ProgressState();
        if (purpose == PlanningCompactConstruction.SelectPurpose) state.Construction!.Compact!.CurrentStep = null;
        var context = AuthorContext(state, purpose);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var request = AuthorRequest(state, purpose, context);
        var displayed = PlanningRepairPatch.RequestContext(request);
        Assert.Contains("detailsPointer", displayed.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(context.ToJsonString(), RestoreProgress(displayed).ToJsonString());
        var addresses = ProgressAddresses(context);
        Assert.NotEmpty(addresses);
        Assert.True(addresses.SetEquals(ProgressAddresses(displayed)));
        var original = UnsharedRequest(request, context, purpose);
        Assert.Equal(original.StructuredOutputSchema!.ToJsonString(), request.StructuredOutputSchema!.ToJsonString());
        var prepared = PlanningModelCalls.PrepareRequest(state, purpose, request.Prompt, request.StructuredOutputSchema.AsObject(), state.Construction!.Limits.DraftOutputTokens);
        var prior = PlanningModelCalls.PrepareRequest(state, purpose, original.Prompt, original.StructuredOutputSchema.AsObject(), state.Construction.Limits.DraftOutputTokens);
        var measured = PlanningRequestMeasurements.Measure(prepared, purpose, 128000);
        var previous = PlanningRequestMeasurements.Measure(prior, purpose, 128000);
        Assert.True(measured.CompleteRequestBytes < previous.CompleteRequestBytes);
        Assert.True(measured.EstimatedInputTokens < previous.EstimatedInputTokens);
        Assert.Equal(previous.SchemaHash, measured.SchemaHash);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        foreach (var envelope in new[] { prepared, prior }) { envelope.Prompt = ""; envelope.ClientRequestId = null; }
        Assert.Equal(JsonSerializer.Serialize(prior, PlanningJsonContext.Default.LLMRequest), JsonSerializer.Serialize(prepared, PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public void AuthorProgressSharingKeepsTheUncompressedFallbackAndUsesOnlyFinalViewPointers()
    {
        var state = ProgressState();
        state.Construction!.Compact!.CurrentStep!.RequirementIds = ["req_1", "req_2"];
        var purpose = PlanningCompactConstruction.DiscoveryPurpose;
        var fullContext = AuthorContext(state, purpose);
        var full = AuthorRequest(state, purpose, fullContext);
        var original = UnsharedRequest(full, fullContext, purpose);
        var prepared = PlanningModelCalls.PrepareRequest(state, purpose, original.Prompt, original.StructuredOutputSchema!.AsObject(), state.Construction.Limits.DraftOutputTokens);
        var limit = checked((int)PlanningRequestMeasurements.Measure(prepared, purpose, 128000).EstimatedInputTokens - 1);
        var compactPrepared = PlanningModelCalls.PrepareRequest(state, purpose, full.Prompt, full.StructuredOutputSchema!.AsObject(), state.Construction.Limits.DraftOutputTokens);
        Assert.True(PlanningRequestMeasurements.Measure(compactPrepared, purpose, 128000).EstimatedInputTokens < limit);
        AuthorAllowance(state, limit);
        var finalContext = AuthorContext(state, purpose);
        var request = AuthorRequest(state, purpose, finalContext);
        Assert.Equal(new[] { "req_1", "req_2" }, finalContext["requirements"]!.AsArray().Select(row => row!["id"]!.ToString()));
        Assert.Equal(1, finalContext["omittedRequirementCount"]!.GetValue<int>());
        var displayed = PlanningRepairPatch.RequestContext(request);
        Assert.Equal(finalContext.ToJsonString(), RestoreProgress(displayed).ToJsonString());
        Assert.Equal("/requirements/0/acceptedProgress/items/0", displayed["requirements"]![1]!["acceptedProgress"]!["items"]![0]!["detailsPointer"]!.ToString());
        Assert.Equal(UnsharedRequest(request, finalContext, purpose).StructuredOutputSchema!.ToJsonString(), request.StructuredOutputSchema!.ToJsonString());
        var addresses = ProgressAddresses(finalContext);
        Assert.NotEmpty(addresses);
        Assert.True(addresses.SetEquals(ProgressAddresses(displayed)));
    }

    [Fact]
    public void ProgressSharingDoesNotMergeDifferentViewsOrMakeReferencesWithoutByteSavings()
    {
        var context = PlanningCompactConstruction.OriginalRequirementRows(ProgressState());
        context[1]!["acceptedProgress"]!["items"]![0]!["scopeOpen"] = false;
        var root = new JsonObject { ["requirements"] = context };
        var before = root.ToJsonString();
        var displayed = PlanningCompactConstruction.CompactAcceptedProgressContext(root);
        Assert.Null(displayed["requirements"]![1]!["acceptedProgress"]!["items"]![0]!["detailsPointer"]);
        Assert.NotNull(displayed["requirements"]![2]!["acceptedProgress"]!["items"]![0]!["detailsPointer"]);
        Assert.Equal(before, RestoreProgress(displayed).ToJsonString());
        Assert.Equal(before, root.ToJsonString());
        Assert.Equal(displayed.ToJsonString(), PlanningCompactConstruction.CompactAcceptedProgressContext(displayed).ToJsonString());
        var reordered = root.DeepClone().AsObject();
        var row = reordered["requirements"]![1]!["acceptedProgress"]!["items"]![0]!.AsObject();
        row["scopeOpen"] = true;
        var sequence = row["sequence"]!.DeepClone(); row.Remove("sequence"); row["sequence"] = sequence;
        Assert.Null(PlanningCompactConstruction.CompactAcceptedProgressContext(reordered)["requirements"]![1]!["acceptedProgress"]!["items"]![0]!["detailsPointer"]);
        var small = JsonNode.Parse("""{"requirements":[{"acceptedProgress":{"items":[{"sequence":1,"evidence":"x"},{"sequence":1,"evidence":"x"}]}}]}""")!.AsObject();
        Assert.Same(small, PlanningCompactConstruction.CompactAcceptedProgressContext(small));
        var unicode = new JsonObject { ["sequence"] = 1, ["evidence"] = "x", ["label"] = new string('漢', 16) };
        var reference = new JsonObject { ["sequence"] = 1, ["evidence"] = "x", ["detailsPointer"] = "/requirements/0/acceptedProgress/items/0" };
        Assert.True(Encoding.UTF8.GetByteCount(unicode.ToJsonString()) > Encoding.UTF8.GetByteCount(reference.ToJsonString()));
        Assert.True(Encoding.UTF8.GetByteCount(PlanningJsonTransport.Prompt(unicode)) < Encoding.UTF8.GetByteCount(PlanningJsonTransport.Prompt(reference)));
        small["requirements"]![0]!["acceptedProgress"]!["items"] = new JsonArray(unicode.DeepClone(), unicode.DeepClone());
        Assert.Same(small, PlanningCompactConstruction.CompactAcceptedProgressContext(small));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ProgressSharingDoesNotChangeHistoricalAuthorBytes(int version)
    {
        var state = ProgressState(version);
        var context = AuthorContext(state, PlanningConstruction.DraftPurpose);
        var request = AuthorRequest(state, PlanningConstruction.DraftPurpose, context);
        Assert.Equal(JsonSerializer.Serialize(UnsharedRequest(request, context, PlanningConstruction.DraftPurpose), PlanningJsonContext.Default.LLMRequest),
            JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        Assert.DoesNotContain("detailsPointer", request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressSharingDoesNotChangeJudgeContextOrGrammar()
    {
        var state = ProgressState();
        var context = AuthorContext(state, PlanningConstruction.DraftPurpose);
        var before = context.ToJsonString();
        var schema = PlanningSchemas.Object(("verdict", PlanningSchemas.String()));
        var expected = PlanningCompactConstruction.Request("Exact Judge instructions.", context, schema, PlanningConstruction.LocalPurpose);
        var actual = PlanningCompactConstruction.Request(state, "Exact Judge instructions.", context, schema, PlanningConstruction.LocalPurpose);
        Assert.Equal(JsonSerializer.Serialize(expected, PlanningJsonContext.Default.LLMRequest), JsonSerializer.Serialize(actual, PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(before, context.ToJsonString());
    }

    [Fact]
    public async Task SavedVersionEightAuthorRequestRecoversItsUnsharedIssuedBytesExactly()
    {
        var state = ProgressState();
        var purpose = PlanningCompactConstruction.DiscoveryPurpose;
        var context = AuthorContext(state, purpose);
        var fresh = AuthorRequest(state, purpose, context);
        var original = UnsharedRequest(fresh, context, purpose);
        var issued = PlanningModelCalls.PrepareRequest(state, purpose, original.Prompt, original.StructuredOutputSchema!.AsObject(), state.Construction!.Limits.DraftOutputTokens);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = purpose, Request = issued };
        state.ModelCalls = 1; state = PlannerFixture.Clone(state);
        var saved = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        Assert.NotEqual(issued.Prompt, fresh.Prompt);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(saved, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = new JsonObject { ["evidenceRequests"] = new JsonArray("requirement:req_0"),
                ["binding"] = null, ["discoveryRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null } };
        } };
        await PlanningModelCalls.CallAsync(state, runtime, purpose, fresh.Prompt, fresh.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, state.Construction!.Limits.DraftOutputTokens);
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.PendingCall);
    }

    private static PlanningSession ProgressState(int version = 8)
    {
        var state = Ready("/root", version);
        AddDecisionRequirement(state, "req_1", "Keep the exact Unicode original ", "漢字 and ordered values.");
        AddDecisionRequirement(state, "req_2", "Keep the cleanup and permission boundary unchanged.");
        state.Construction!.Checkpoints = [new() { Sequence = 1, Step = new() { Scope = "/root" },
            TaskIds = ["root_value", "container"], RequirementIds = ["req_0", "req_1", "req_2"] }];
        foreach (var requirement in state.Construction.Requirements) requirement.Checkpoints = [1];
        state.Construction.Candidate = null;
        AuthorAllowance(state, 128000);
        return state;
    }

    private static LLMRequest UnsharedRequest(LLMRequest request, JsonObject context, string purpose) => PlanningCompactConstruction.Request(
        request.Prompt[..request.Prompt.LastIndexOf('\n')], context, request.StructuredOutputSchema!.DeepClone().AsObject(), purpose);

    private static HashSet<string> ProgressAddresses(JsonObject context) => PlanningCompactConstruction.NavigationEvidence(
        context["requirements"]!.AsArray().Select(row => row?["acceptedProgress"]));

    private static JsonObject RestoreProgress(JsonObject context)
    {
        var restored = context.DeepClone().AsObject();
        foreach (var requirement in restored["requirements"]!.AsArray())
        {
            var items = requirement!["acceptedProgress"]?["items"] as JsonArray ?? [];
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index]?["detailsPointer"] is not { } pointer) continue;
                JsonNode target = context;
                foreach (var segment in pointer.ToString().Split('/').Skip(1))
                    target = target is JsonArray array ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)]! : target[segment]!;
                Assert.Null(target["detailsPointer"]);
                Assert.Equal(items[index]!["sequence"]!.ToJsonString(), target["sequence"]!.ToJsonString());
                Assert.Equal(items[index]!["evidence"]!.ToJsonString(), target["evidence"]!.ToJsonString());
                items[index] = target.DeepClone();
            }
        }
        return restored;
    }

    private static void AuthorAllowance(PlanningSession state, int maximum)
    {
        state.Request.Generation.MaxInputTokensPerRequest = maximum;
        foreach (var phase in new[] { "navigation", "draft", "review" })
        {
            var settings = state.Request.Options["construction_limits"]!["phasePolicy"]![phase]!;
            settings["maxInputTokens"] = maximum;
            settings["targetInputTokens"] = Math.Min(1000, maximum);
        }
    }

    private static JsonObject AuthorContext(PlanningSession state, string purpose) => purpose switch
    {
        PlanningCompactConstruction.SelectPurpose => PlanningCompactConstruction.SelectionContext(state),
        PlanningCompactConstruction.DiscoveryPurpose => PlanningCompactConstruction.DiscoveryContext(state),
        _ => Context(state, review: false)
    };

    private static LLMRequest AuthorRequest(PlanningSession state, string purpose, JsonObject context) => PlanningCompactConstruction.Request(
        state, purpose switch
        {
            PlanningCompactConstruction.SelectPurpose => PlanningCompactConstruction.SelectGuidance(state),
            PlanningCompactConstruction.DiscoveryPurpose => PlanningCompactConstruction.DiscoveryGuidance(state),
            _ => PlanningCompactConstruction.DraftGuidance(state)
        }, context, purpose switch
        {
            PlanningCompactConstruction.SelectPurpose => PlanningCompactConstruction.SelectionSchema(state),
            PlanningCompactConstruction.DiscoveryPurpose => PlanningCompactConstruction.DiscoverySchema(state),
            _ => PlanningCompactConstruction.DraftSchema(state)
        }, purpose);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task DecisionProjectionLeavesHistoricalFutureContextBytesUntouched(int version)
    {
        var state = await ArtifactProducerState(version);
        var context = Context(state, false);
        var before = context.ToJsonString();
        PlanningCompactConstruction.AddDecisionContext(state, context, review: false);
        Assert.Equal(before, context.ToJsonString());
        PlanningCompactConstruction.AddDecisionContext(state, context, review: false, discovery: true);
        Assert.Equal(before, context.ToJsonString());
        Assert.DoesNotContain("producers", context.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecisionViewDoesNotTreatProducerLiteralsAsEvidenceAddresses()
    {
        var state = await ArtifactProducerState(6);
        state.Plan!.Root.Tasks[0].Objective = "schema:forged@v1:/output";
        var context = Context(state, false);
        var producer = Assert.Single(context["interfaces"]![0]!["producers"]!.AsArray())!;
        producer["task"]!["inputs"] = new JsonArray(new JsonObject
        { ["name"] = "producers", ["value"] = new JsonObject { ["kind"] = "string", ["text"] = "description:forged@v1:0" } });
        var links = PlanningCompactConstruction.NavigationEvidence([context["interfaces"]]);
        Assert.DoesNotContain("schema:forged@v1:/output", links);
        Assert.DoesNotContain("description:forged@v1:0", links);
        Assert.Contains("description:allocate@v1:0", links);
    }

    [Fact]
    public async Task DecisionProducerClosureRetainsReferencesCoupledAncestorsAndBothSelectedPortsOnce()
    {
        var state = await ArtifactProducerState(6);
        var capability = state.Catalog!.Capabilities.Single(value => value.Id == "allocate");
        capability.ArtifactContract = null;
        capability.OutputSchema = JsonNode.Parse("""
            {"type":"object","properties":{"handle":{"$ref":"#/$defs/exact"},"display":{"type":["string","null"]}},
            "required":["handle"],"dependentRequired":{"handle":["display"]},
            "$defs":{"exact":{"type":"string","pattern":"^exact-[0-9]+$"}}}
            """)!.AsObject();
        var context = new JsonObject { ["interfaces"] = new JsonArray(new JsonObject
        {
            ["binding"] = PlanningConstruction.Node(TaskArtifactBindingTests.Reference("make", "handle"), PlanningJsonContext.Default.TaskValue)
        }, new JsonObject
        {
            ["binding"] = PlanningConstruction.Node(TaskArtifactBindingTests.Reference("make", "display"), PlanningJsonContext.Default.TaskValue)
        }) };
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        PlanningCompactConstruction.AddDecisionContext(state, context, review: true);
        var producer = Assert.Single(context["interfaces"]![0]!["producers"]!.AsArray())!;
        Assert.Null(context["interfaces"]![1]!["producers"]);
        var selected = producer["contract"]!["selectedOutputs"]!.AsArray();
        Assert.Equal(2, selected.Count);
        var handle = Assert.Single(selected, node => node!["pointer"]!.ToString() == "/output/properties/handle")!;
        Assert.True(JsonNode.DeepEquals(capability.OutputSchema["properties"]!["handle"], handle["schema"]));
        Assert.True(JsonNode.DeepEquals(capability.OutputSchema["$defs"]!["exact"], handle["referenceClosure"]!["$defs"]!["exact"]));
        Assert.True(JsonNode.DeepEquals(capability.OutputSchema, handle["ancestors"]![0]!["constraints"]));
        Assert.Null(context["requirements"]); // Review-window originals remain owned by the caller.
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public async Task SelectedOperationOutputNavigationRetainsCompletePortIdentitiesOnlyInDecisionView(int version, bool complete)
    {
        var state = await ArtifactProducerState(version);
        var operation = state.Catalog!.Capabilities.Single(value => value.Id == "inspect");
        operation.OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        for (var index = 0; index < 13; index++)
            operation.OutputSchema["properties"]!["port_" + index] = new JsonObject { ["type"] = "string" };
        var context = Context(state, false);
        var view = Assert.Single(context["operations"]!.AsArray())!;
        if (complete)
        {
            Assert.Equal(13, view["outputs"]!.AsArray().Count);
            Assert.Null(view["outputsOmitted"]);
        }
        else
        {
            Assert.Null(view["outputs"]);
            Assert.True(view["outputsOmitted"]!.GetValue<bool>());
        }
        Assert.Equal("port-page:inspect@v1:/output:0", view["outputDirectory"]!.ToString());
        PlanningConstruction.LoadEvidence(state, [view["outputDirectory"]!.ToString()]);
        var page = state.Construction!.Evidence![view["outputDirectory"]!.ToString()]!;
        Assert.Equal(8, page["items"]!.AsArray().Count);
        Assert.Equal("port-page:inspect@v1:/output:8", page["next"]!.ToString());
    }

    [Fact]
    public async Task IndivisibleProducerSemanticsStopAtExistingAdmissionInsteadOfTruncating()
    {
        var state = await ArtifactProducerState(6);
        var producer = state.Catalog!.Capabilities.Single(value => value.Id == "allocate");
        producer.Description = new string('x', 200000) + " exact final restriction.";
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Contains(producer.Description, request.Prompt, StringComparison.Ordinal);
        var runtime = new TestRuntime();
        var exception = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningConstruction.DraftPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct));
        Assert.Equal("MODEL_INPUT_LIMIT", exception.Code);
        Assert.Empty(runtime.Calls);
        Assert.Null(state.PendingCall);
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void DecisionReviewRetainsFullNormativeProseAndExactBoundInputSchemaAfterPaging(int version)
    {
        var state = OperationState("/root", version);
        var capability = Assert.Single(state.Catalog!.Capabilities);
        capability.Description = string.Concat(Enumerable.Repeat("The target identifies the exact authorized entity, not a related container. ", 50));
        var target = capability.InputSchema["properties"]!["target"]!.AsObject();
        target["description"] = string.Concat(Enumerable.Repeat("Preserve the caller's complete target identity. ", 30));
        target["minLength"] = 1;
        var original = target.DeepClone();
        var before = Assert.Single(PlanningCompactConstruction.ContractViews(state, review: true))!;
        PlanningConstruction.LoadEvidence(state, ["description:create_resource@v1:0"]);
        var after = Assert.Single(PlanningCompactConstruction.ContractViews(state, review: true))!;
        if (version == 6)
        {
            Assert.Equal(capability.Description, before["description"]!.ToString());
            Assert.Equal(capability.Description, after["description"]!.ToString());
            Assert.True(JsonNode.DeepEquals(original, Assert.Single(after["selectedInputs"]!.AsArray())!["schema"]));
            Assert.Null(after["boundInputs"]![0]!["description"]);
            Assert.Equal("schema:create_resource@v1:/input/properties/target", after["boundInputs"]![0]!["schemaEvidence"]!.ToString());
            Assert.Equal("resource", Assert.Single(after["outputs"]!.AsArray())!["name"]!.ToString());
        }
        else
        {
            Assert.NotEqual(capability.Description, before["description"]!.ToString());
            Assert.Null(after["description"]);
            Assert.Null(after["selectedInputs"]);
            Assert.Null(after["outputs"]);
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ExplicitRevisionRetainsOnlyAuthorizedTasksBoundInputClosures(int version)
    {
        var state = OperationState("/root", version);
        var capability = Assert.Single(state.Catalog!.Capabilities);
        var revisionSchema = new JsonObject
        {
            ["type"] = "string", ["enum"] = new JsonArray("head", "release"),
            ["description"] = string.Concat(Enumerable.Repeat("This value selects the exact original revision. ", 30))
        };
        capability.InputSchema["properties"]!["revision"] = revisionSchema.DeepClone();
        capability.InputSchema["properties"]!["unrelated"] = new JsonObject { ["type"] = "string" };
        state.Plan!.Root.Tasks.Single(task => task.Id == "accepted_operation").Inputs.Add(new("revision", new() { Kind = "string", Text = "release" }));
        state.Plan.Root.Tasks.Add(new()
        {
            Id = "independent_operation", Kind = "operation", Operation = capability.Id, Objective = "Keep the independent resource unchanged.",
            Inputs = [new("target", new() { Kind = "string", Text = "workspace/independent" }),
                new("unrelated", new() { Kind = "string", Text = "unchanged" })]
        });
        const string path = "/tasks/accepted_operation/inputs/revision";
        var runtime = new TestRuntime();
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new()
        {
            Kind = "revise", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(), PreserveRequirements = true,
            EditablePaths = [path], Text = "Correct only the accepted operation's revision argument to head."
        }, runtime, PlannerFixture.Ct);
        Assert.Empty(runtime.Calls);
        Assert.Equal([path], state.Construction!.Candidate!.AuthorizedPaths);
        Assert.Empty(state.Construction.Candidate.Extension.Tasks);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var author = Assert.Single(PlanningCompactConstruction.ContractViews(state))!;
        var review = Assert.Single(PlanningCompactConstruction.ContractViews(state, review: true))!;
        Assert.Null(author["inputSchema"]!["properties"]!["unrelated"]);
        if (version == 6)
        {
            Assert.True(JsonNode.DeepEquals(revisionSchema, author["inputSchema"]!["properties"]!["revision"]));
            Assert.Equal(new[] { "target", "revision" }.Order(StringComparer.Ordinal),
                review["boundInputs"]!.AsArray().Select(port => port!["name"]!.ToString()).Order(StringComparer.Ordinal));
            var closure = Assert.Single(review["selectedInputs"]!.AsArray(), port => port!["pointer"]!.ToString() == "/input/properties/revision")!;
            Assert.True(JsonNode.DeepEquals(revisionSchema, closure["schema"]));
            Assert.DoesNotContain(review["boundInputs"]!.AsArray(), port => port!["name"]!.ToString() == "unrelated");
        }
        else
        {
            Assert.Null(author["inputSchema"]!["properties"]!["revision"]);
            Assert.Empty(review["boundInputs"]!.AsArray());
            Assert.Null(review["selectedInputs"]);
        }
        var authority = PlannerFixture.Clone(state);
        authority.Plan = authority.Construction!.Candidate!.Plan;
        authority.EditablePaths = authority.Construction.Candidate.AuthorizedPaths;
        authority.RevisionScope = authority.EditablePaths!;
        var repair = PlanningConstruction.RepairRequest(state, authority);
        Assert.True(PlanningRepairPatch.Issued(repair.StructuredOutputSchema!.AsObject()));
        Assert.Equal(path, Assert.Single(PlanningRepairPatch.RequestContext(repair)["repair"]!["slots"]!.AsArray())!["location"]!.ToString());
        Assert.Equal("null", repair.StructuredOutputSchema["properties"]!["reselectStep"]!["type"]!.ToString());
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task SelectedProducerPortsDistinguishSameTypeArtifactBindingsBeforeDrafting()
    {
        var state = await ArtifactProducerState();
        var baseline = PlanningConstruction.Hash(state.Plan);
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = Context(state, false);
        var producer = Assert.Single(context["taskEvidence"]!["items"]!.AsArray(), row => row?["task"]?.ToString() == "make")!;
        Assert.Equal("handle", Assert.Single(producer["produces"]!.AsArray())!["port"]!.ToString());
        Assert.Equal("resource", producer["produces"]![0]!["kind"]!.ToString());
        Assert.Equal("v1", producer["operationVersion"]!.ToString());
        Assert.Equal(64, producer["contractHash"]!.ToString().Length);
        Assert.Equal("string", Assert.Single(context["interfaces"]!.AsArray())!["schema"]!["type"]!.ToString());
        var request = ArtifactDraftRequest(state);
        Assert.Contains("\"produces\"", request.Prompt, StringComparison.Ordinal);
        Assert.Contains(producer["outputDirectory"]!.ToString(), request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        var wrong = PlanningConstruction.Apply(state, ArtifactConsumer("display"));
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(wrong.Plan, state.Catalog!, state.Request,
            wrong.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics, diagnostic => diagnostic.Code == "TASK_ARTIFACT_BINDING");
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        var correct = PlanningConstruction.Apply(state, ArtifactConsumer("handle"));
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(correct.Plan, state.Catalog!, state.Request,
            correct.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Null(correct.AuthorizedPaths);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
    }

    [Fact]
    public async Task ProducerOutputPagesAndExactSchemaRemainReadOnlyAndDoNotExpandDraftGrammar()
    {
        var state = await ArtifactProducerState();
        var before = ArtifactDraftRequest(state).StructuredOutputSchema!.DeepClone().AsObject();
        var step = state.Construction!.Compact!.CurrentStep!;
        var stepBefore = JsonSerializer.Serialize(step, PlanningJsonContext.Default.PlanningConstructionStep);
        var context = Context(state, false);
        var address = context["taskEvidence"]!["items"]![0]!["outputDirectory"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        var page = state.Construction.Evidence![address]!;
        Assert.Equal("handle", Assert.Single(page["produces"]!.AsArray())!["port"]!.ToString());
        var schemaAddress = Assert.Single(page["items"]!.AsArray(), row => row?["name"]?.ToString() == "handle")!["schema"]!.ToString();
        Assert.Contains(schemaAddress, ArtifactDraftRequest(state).StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        PlanningConstruction.LoadEvidence(state, [schemaAddress]);
        Assert.Equal("string", state.Construction.Evidence[schemaAddress]!["schema"]!["type"]!.ToString());
        Assert.Equal(stepBefore, JsonSerializer.Serialize(state.Construction.Compact.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
        Assert.Empty(state.Construction.Compact.CurrentStep!.SchemaPointers);
        var after = ArtifactDraftRequest(state).StructuredOutputSchema!.DeepClone().AsObject();
        // Evidence and suspension navigation reflect the supplied originals;
        // the executable extension grammar and its definitions must not grow.
        foreach (var name in new[] { "evidenceRequests", "reselectStep" })
        { before["properties"]!.AsObject().Remove(name); after["properties"]!.AsObject().Remove(name); }
        Assert.Equal(before.ToJsonString(), after.ToJsonString());
        Assert.Empty(state.Construction.Compact.PendingCommitments);
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalConsumersRetrieveExactProducerDescriptionContinuationsBeforeDraftAndReview(bool review)
    {
        var state = await ArtifactProducerState();
        var producer = state.Catalog!.Capabilities.Single(capability => capability.Id == "allocate");
        producer.Description = string.Concat(Enumerable.Repeat(
            "The display port is a label. Preserve the complete original payload under the declared acquisition mode. ", 50));
        producer.ArtifactContract = null;
        var originalContract = JsonSerializer.Serialize(producer, PlanningJsonContext.Default.PlanningCapability);
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "transform"; step.OperationIds.Clear();
        if (review)
            state.Construction.Candidate = PlanningConstruction.Apply(state, new()
            {
                Scope = step.Scope, RequirementIds = ["req_0"], Tasks = [new()
                {
                    Id = "consume", Kind = "transform", Mode = "interpret", Objective = "Interpret the supplied observed value.",
                    Inputs = [new("observed", TaskArtifactBindingTests.Reference("make", "display"))],
                    ResultType = new() { Kind = "object", Fields = [new() { Name = "result", Type = new() { Kind = "string" } }] }
                }]
            });
        var plan = PlanningConstruction.Hash(state.Plan);
        var candidate = state.Construction.Candidate?.Hash;
        var stepBytes = JsonSerializer.Serialize(step, PlanningJsonContext.Default.PlanningConstructionStep);
        var purpose = review ? PlanningConstruction.LocalPurpose : PlanningConstruction.DraftPurpose;
        LLMRequest Request(PlanningSession current)
        {
            var raw = review ? HybridWorkflowPlanner.CreateConstructionReviewRequest(current, false).Request
                : PlanningConstruction.DraftRequest(current, false);
            return PlanningModelCalls.PrepareRequest(current, purpose, raw.Prompt, raw.StructuredOutputSchema!.AsObject());
        }
        static JsonObject Presented(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
        var initial = Request(state);
        var context = Presented(initial);
        Assert.Empty(context["operations"]!.AsArray());
        Assert.Equal("string", Assert.Single(context["interfaces"]!.AsArray())!["schema"]!["type"]!.ToString());
        var entry = Assert.Single(context["taskEvidence"]!["items"]!.AsArray(), item => item?["task"]?.ToString() == "make")!;
        var address = entry["descriptionEvidence"]!.ToString();
        Assert.Equal("description:allocate@v1:0", address);
        Assert.DoesNotContain(producer.Description, initial.Prompt, StringComparison.Ordinal);
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/taskEvidence/items/0", true));
        var text = new StringBuilder(); var pages = 0;
        do
        {
            var request = Request(state);
            if (review)
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(new JsonObject
                {
                    ["action"] = new JsonObject { ["kind"] = "evidenceRequests", ["addresses"] = new JsonArray(address) },
                    ["uncertainty"] = "Inspect the exact producer contract."
                }, request.StructuredOutputSchema!));
            else Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(new JsonArray(address),
                request.StructuredOutputSchema!["properties"]!["evidenceRequests"]!));
            PlanningConstruction.LoadEvidence(state, [address], followup: Request, purpose: purpose);
            var page = state.Construction.Evidence![address]!;
            Assert.Equal("v1", page["version"]!.ToString());
            Assert.Equal(PlanningGraphCompiler.Fingerprint(producer.Description), page["descriptionHash"]!.ToString());
            Assert.InRange(Encoding.UTF8.GetByteCount(page["text"]!.ToJsonString()), 1, 2060);
            Assert.True(JsonNode.DeepEquals(page, Presented(Request(state))["evidence"]![address]));
            text.Append(page["text"]!.ToString()); pages++;
            address = page["next"]?.ToString();
        } while (address is not null);
        Assert.True(pages > 1);
        Assert.Equal(producer.Description, text.ToString());
        Assert.Equal(originalContract, JsonSerializer.Serialize(producer, PlanningJsonContext.Default.PlanningCapability));
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(candidate, state.Construction.Candidate?.Hash);
        Assert.Equal(stepBytes, JsonSerializer.Serialize(state.Construction.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
        Assert.Empty(state.Construction.Compact.CurrentStep!.OperationIds);
        Assert.Empty(state.Construction.Compact.CurrentStep.SchemaPointers);
        Assert.Empty(state.Construction.Compact.PendingCommitments);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(6, 19)]
    [InlineData(7, 19)]
    [InlineData(8, 8)]
    [InlineData(8, 9)]
    [InlineData(8, 19)]
    [InlineData(10, 19)]
    [InlineData(11, 19)]
    [InlineData(11, 100)]
    public async Task SelectionTypedPortsUseExistingPagesOnlyForFreshVersionEight(int version, int count)
    {
        var state = await PagedSelectionState(version, count);
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.SelectionContext(state);
        var raw = AuthorRequest(state, PlanningCompactConstruction.SelectPurpose, context);
        var presented = JsonNode.Parse(raw.Prompt[(raw.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
        var full = Assert.Single(context["interfaces"]!.AsArray(), node => node?["task"]?.ToString() == "make")!;
        var row = Assert.Single(presented["interfaces"]!.AsArray(), node => node?["task"]?.ToString() == "make")!;
        Assert.Equal(count, full["outputs"]!.AsArray().Count);
        var originalSchema = PlanningCompactConstruction.SelectionSchema(state);
        PlanningCompactConstruction.ConstrainSelectionEvidence(state, context, originalSchema);
        var original = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state), context, originalSchema,
            PlanningCompactConstruction.SelectPurpose);
        if (version < 8 || count <= 8)
        {
            Assert.Equal(JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest), JsonSerializer.Serialize(raw, PlanningJsonContext.Default.LLMRequest));
            Assert.Null(row["outputCount"]); Assert.Null(row["next"]);
        }
        else
        {
            Assert.Equal(8, row["outputs"]!.AsArray().Count); Assert.Equal(count, row["outputCount"]!.GetValue<int>());
            Assert.Equal("port-page:allocate@v1:/output:8", row["next"]!.ToString());
            Assert.StartsWith("page:task-ports:", row["nextPorts"]!.ToString(), StringComparison.Ordinal);
            var addresses = PlanningCompactConstruction.NavigationEvidence([context["interfaces"]]);
            Assert.NotEmpty(addresses);
            var displayedAddresses = PlanningCompactConstruction.NavigationEvidence([presented["interfaces"]]);
            foreach (var address in addresses.Append(row["next"]!.ToString()))
            {
                var findings = PlanningContractValidation.ValidateInstanceFindings(new JsonArray(address), raw.StructuredOutputSchema!["properties"]!["evidenceRequests"]!);
                if (version == 11 && !displayedAddresses.Contains(address)) Assert.NotEmpty(findings);
                else Assert.Empty(findings);
            }
            if (version == 11)
            {
                var historical = PlannerFixture.Clone(state);
                historical.Construction!.Compact!.PresentationVersion = 10;
                historical.Request.Options["construction_presentation_version"] = 10;
                var previous = AuthorRequest(historical, PlanningCompactConstruction.SelectPurpose,
                    PlanningCompactConstruction.SelectionContext(historical));
                Assert.True(raw.StructuredOutputSchema!.ToJsonString().Length < previous.StructuredOutputSchema!.ToJsonString().Length);
                var measured = PlanningRequestMeasurements.Measure(PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose,
                    raw.Prompt, raw.StructuredOutputSchema.AsObject()), PlanningCompactConstruction.SelectPurpose, 3000);
                var before = PlanningRequestMeasurements.Measure(PlanningModelCalls.PrepareRequest(historical, PlanningCompactConstruction.SelectPurpose,
                    previous.Prompt, previous.StructuredOutputSchema.AsObject()), PlanningCompactConstruction.SelectPurpose, 3000);
                Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
                Assert.True(measured.CompleteRequestBytes < before.CompleteRequestBytes);
                output.WriteLine($"ports={count}; control_bytes={before.CompleteRequestBytes}; candidate_bytes={measured.CompleteRequestBytes}; control_tokens={before.EstimatedInputTokens}; candidate_tokens={measured.EstimatedInputTokens}");
            }
            var expectedSchema = original.StructuredOutputSchema!.DeepClone();
            expectedSchema["properties"]!["evidenceRequests"] = raw.StructuredOutputSchema!["properties"]!["evidenceRequests"]!.DeepClone();
            Assert.True(JsonNode.DeepEquals(expectedSchema, raw.StructuredOutputSchema));
            row["outputs"] = full["outputs"]!.DeepClone(); row.AsObject().Remove("outputCount"); row.AsObject().Remove("next");
            Assert.True(JsonNode.DeepEquals(context, presented));
            var rows = new JsonArray(); string? addressNext = full["outputDirectory"]!.ToString();
            while (addressNext is not null)
            {
                var page = PlanningCompactConstruction.LoadContractEvidence(state, addressNext)!;
                Assert.Equal(count, page["total"]!.GetValue<int>()); Assert.Equal(full["contractHash"]!.ToString(), page["hash"]!.ToString());
                foreach (var item in page["items"]!.AsArray()) rows.Add((JsonNode)item!.DeepClone());
                addressNext = page["next"]?.ToString();
            }
            Assert.True(JsonNode.DeepEquals(full["outputs"], rows));
            if (count == 19) Assert.Contains("null", Assert.Single(rows, item => item?["name"]?.ToString() == "port_08")!["type"]!.ToJsonString(), StringComparison.Ordinal);
            foreach (var invalid in new[] { "port-page:allocate@old:/output:8", "port-page:allocate@v1:/output:999" })
                Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.LoadContractEvidence(state, invalid));
        }
        Assert.Equal(saved, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task SelectionTypedPageReadAdmitsTheCompleteFollowupAtomically()
    {
        var state = await PagedSelectionState(8, 19);
        const string address = "port-page:allocate@v1:/output:8";
        static LLMRequest Followup(PlanningSession value)
        {
            var raw = AuthorRequest(value, PlanningCompactConstruction.SelectPurpose, PlanningCompactConstruction.SelectionContext(value));
            return PlanningModelCalls.PrepareRequest(value, PlanningCompactConstruction.SelectPurpose, raw.Prompt,
                raw.StructuredOutputSchema!.AsObject(), value.Construction!.Limits.DraftOutputTokens);
        }
        var request = Followup(state);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(new JsonArray(address), request.StructuredOutputSchema!["properties"]!["evidenceRequests"]!));
        var admitted = PlannerFixture.Clone(state);
        PlanningConstruction.LoadEvidence(admitted, [address], followup: Followup, purpose: PlanningCompactConstruction.SelectPurpose);
        var displayed = JsonNode.Parse(Followup(admitted).Prompt.Split('\n')[^1])!;
        Assert.True(JsonNode.DeepEquals(admitted.Construction!.Evidence![address], displayed["evidence"]![address]));
        var complete = Followup(admitted);
        AuthorAllowance(state, checked((int)PlanningRequestMeasurements.Measure(complete, PlanningCompactConstruction.SelectPurpose, 128000).EstimatedInputTokens - 1));
        var saved = JsonSerializer.Serialize(state.Construction, PlanningJsonContext.Default.PlanningConstructionState);
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address], followup: Followup,
            purpose: PlanningCompactConstruction.SelectPurpose));
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_LIMIT");
        Assert.Equal(saved, JsonSerializer.Serialize(state.Construction, PlanningJsonContext.Default.PlanningConstructionState));
    }

    [Fact]
    public async Task IssuedVersionEightSelectionRecoversTheFullTypedDirectoryExactly()
    {
        var state = await PagedSelectionState(8, 19);
        var context = PlanningCompactConstruction.SelectionContext(state); var schema = PlanningCompactConstruction.SelectionSchema(state);
        PlanningCompactConstruction.ConstrainSelectionEvidence(state, context, schema);
        var old = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state), context, schema, PlanningCompactConstruction.SelectPurpose);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose, old.Prompt, old.StructuredOutputSchema!.AsObject(), state.Construction!.Limits.DraftOutputTokens);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.SelectPurpose, Request = issued };
        state.ModelCalls = 1; state = PlannerFixture.Clone(state);
        var saved = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var fresh = AuthorRequest(state, PlanningCompactConstruction.SelectPurpose, PlanningCompactConstruction.SelectionContext(state));
        Assert.NotEqual(old.Prompt, fresh.Prompt);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(saved, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = new JsonObject { ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["step"] = null,
                ["evidenceRequests"] = new JsonArray("port-page:allocate@v1:/output:0"), ["clarifications"] = null } };
        } };
        await PlanningModelCalls.CallAsync(state, runtime, PlanningCompactConstruction.SelectPurpose, fresh.Prompt,
            fresh.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, state.Construction!.Limits.DraftOutputTokens);
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Null(state.PendingCall);
    }

    private static async Task<PlanningSession> PagedSelectionState(int version, int count)
    {
        var state = await ArtifactProducerState(version); var producer = state.Catalog!.Capabilities.Single(capability => capability.Id == "allocate");
        producer.ArtifactContract = null;
        for (var index = 0; index < count - 2; index++)
            producer.OutputSchema["properties"]!["port_" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)] = index == 8
                ? new JsonObject { ["type"] = new JsonArray("object", "null"), ["properties"] = new JsonObject { ["pages"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } } }
                : new JsonObject { ["type"] = "string" };
        state.Construction!.Compact!.CurrentStep = null; AuthorAllowance(state, 128000); return state;
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public async Task PagedNullableProducerContractSurvivesSelectionResetThroughDraftAndJudge(int version)
    {
        const string port = "zz_snapshot";
        var state = await ArtifactProducerState(version);
        if (version == 8) AuthorAllowance(state, 128000);
        var producer = state.Catalog!.Capabilities.Single(capability => capability.Id == "allocate");
        producer.ArtifactContract = null;
        producer.Description = string.Concat(Enumerable.Repeat(
            "The display value is a label. The nullable snapshot contains complete pages of observed records; preserve their order and null state. ", 17));
        var structured = JsonNode.Parse("""
            {"type":["object","null"],"properties":{"pages":{"type":"array","items":{"type":"object",
            "properties":{"records":{"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"amount":{"type":"number"}},
            "required":["label","amount"],"additionalProperties":false}}},"required":["records"],"additionalProperties":false}}},
            "required":["pages"],"additionalProperties":false}
            """)!.AsObject();
        for (var index = 0; index < 7; index++)
        {
            var name = "extra_" + index;
            producer.OutputSchema["properties"]![name] = new JsonObject { ["type"] = "string" };
            producer.OutputSchema["required"]!.AsArray().Add((JsonNode)JsonValue.Create(name)!);
        }
        producer.OutputSchema["properties"]![port] = structured.DeepClone();
        producer.OutputSchema["required"]!.AsArray().Add((JsonNode)JsonValue.Create(port)!);
        var contract = JsonSerializer.Serialize(producer, PlanningJsonContext.Default.PlanningCapability);
        var compact = state.Construction!.Compact!;
        compact.CurrentStep = null;
        compact.InterfaceExtractions = [];
        var source = compact.Sources[0];
        compact.Sources.Add(new() { Id = source.Id + ":clause:0", Version = source.Version, Text = source.Text,
            Kind = "requirement", InterfaceKind = "none", Indexed = true });
        compact.RequirementSources["req_0"] = [source.Id + ":clause:0"];
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        var baseline = PlanningConstruction.Hash(state.Plan);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);

        static JsonObject Presented(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
        static string Purpose(PlanningSession current) => current.Construction!.Compact!.CurrentStep is null
            ? PlanningCompactConstruction.SelectPurpose : current.Construction.Candidate is null
                ? PlanningConstruction.DraftPurpose : PlanningConstruction.LocalPurpose;
        LLMRequest Request(PlanningSession current)
        {
            LLMRequest raw;
            if (current.Construction!.Compact!.CurrentStep is null)
            {
                var context = PlanningCompactConstruction.SelectionContext(current);
                var schema = PlanningCompactConstruction.SelectionSchema(current);
                PlanningCompactConstruction.ConstrainSelectionEvidence(current, context, schema);
                raw = PlanningCompactConstruction.Request(current, PlanningCompactConstruction.SelectGuidance(current), context, schema,
                    PlanningCompactConstruction.SelectPurpose);
            }
            else raw = current.Construction.Candidate is null ? PlanningConstruction.DraftRequest(current, false)
                : HybridWorkflowPlanner.CreateConstructionReviewRequest(current, false).Request;
            return PlanningModelCalls.PrepareRequest(current, Purpose(current), raw.Prompt, raw.StructuredOutputSchema!.AsObject());
        }
        void Read(PlanningSession current, string address)
        {
            var request = Request(current);
            if (current.Construction!.Candidate is null)
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(new JsonArray(address),
                    request.StructuredOutputSchema!["properties"]!["evidenceRequests"]!));
            else Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(new JsonObject
            {
                ["action"] = new JsonObject { ["kind"] = "evidenceRequests", ["addresses"] = new JsonArray(address) },
                ["uncertainty"] = "Inspect the exact accepted producer."
            }, request.StructuredOutputSchema!));
            try { PlanningConstruction.LoadEvidence(current, [address], followup: Request, purpose: Purpose(current)); }
            catch (PlanningResponseException exception)
            { Assert.Fail(address + ": " + string.Join("; ", exception.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message))); }
        }
        string ReadDescription(PlanningSession current, string first)
        {
            var text = new StringBuilder(); string? address = first;
            do
            {
                if (current.Construction!.Evidence?.ContainsKey(address) != true) Read(current, address);
                var page = current.Construction.Evidence![address]!;
                Assert.Equal(PlanningGraphCompiler.Fingerprint(producer.Description), page["descriptionHash"]!.ToString());
                text.Append(page["text"]!.ToString()); address = page["next"]?.ToString();
            } while (address is not null);
            return text.ToString();
        }

        var selection = Presented(Request(state));
        var row = Assert.Single(selection["interfaces"]!.AsArray(), item => item?["task"]?.ToString() == "make")!;
        Assert.DoesNotContain(port, row["ports"]!.AsArray().Select(item => item!.ToString()));
        var description = row["descriptionEvidence"]!.ToString();
        Assert.Equal(producer.Description, ReadDescription(state, description));
        var descriptionFingerprint = PlanningConstruction.EvidenceBatchFingerprint(state, [description]);
        Assert.Contains(descriptionFingerprint, state.Construction.EvidenceRequests);
        var directory = row["outputDirectory"]!.ToString();
        Read(state, directory);
        Assert.DoesNotContain(state.Construction.Evidence![directory]!["items"]!.AsArray(), item => item?["name"]?.ToString() == port);
        var next = state.Construction.Evidence[directory]!["next"]!.ToString();
        Read(state, next);
        var selected = Assert.Single(state.Construction.Evidence[next]!["items"]!.AsArray(), item => item?["name"]?.ToString() == port)!;
        var schemaAddress = selected["schema"]!.ToString();
        var nestedDirectory = selected["directory"]!.ToString();
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [description]));
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        var binding = TaskArtifactBindingTests.Reference("make", port);
        var projection = TaskPlanCompiler.ValidateConstruction(state.Plan!, state.Catalog!, state.Request,
            state.Construction.OpenScopes.ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<TaskValue>> { ["/root"] = [TaskFieldBindingTests.Field(binding, "pages")] });
        Assert.Empty(projection.Diagnostics);
        Assert.True(JsonNode.DeepEquals(structured["properties"]!["pages"], Assert.Single(projection.Interfaces["/root"]).Schema));
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            var response = new JsonObject
            {
                ["baseHash"] = baseline, ["evidenceRequests"] = null, ["clarifications"] = null,
                ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = "transform",
                    ["objective"] = "Interpret the supplied observed records, preserving their ordering and null state.",
                    ["requirementIds"] = new JsonArray("req_0"),
                    ["inputs"] = new JsonArray(new JsonObject { ["kind"] = "output", ["source"] = "make", ["port"] = port }),
                    ["groupId"] = null, ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray("result"),
                    ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray(), ["prerequisites"] = null }
            };
            if (version == 8) { response["step"]!["operationIds"] = new JsonArray(); response["step"]!["closeScopes"] = null; }
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema!));
            return new() { Json = response };
        } };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics); Assert.Single(runtime.Calls);
        Assert.Equal("transform", state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.Null(state.Construction.Evidence); Assert.Empty(state.Construction.EvidenceRequests);
        Assert.Equal(descriptionFingerprint, PlanningConstruction.EvidenceBatchFingerprint(state, [description]));
        var draft = Presented(Request(state));
        Assert.True(JsonNode.DeepEquals(structured, Assert.Single(draft["interfaces"]!.AsArray())!["schema"]));
        Assert.Equal(description, draft["taskEvidence"]!["items"]![0]!["descriptionEvidence"]!.ToString());
        Assert.Equal(producer.Description, ReadDescription(state, description));
        Read(state, directory); Read(state, next);
        Read(state, schemaAddress);
        Assert.True(JsonNode.DeepEquals(structured, state.Construction.Evidence![schemaAddress]!["schema"]));
        Read(state, nestedDirectory);
        var pages = Assert.Single(state.Construction.Evidence[nestedDirectory]!["items"]!.AsArray())!;
        Assert.EndsWith("/properties/pages", pages["pointer"]!.ToString(), StringComparison.Ordinal);
        var pagesAddress = pages["evidence"]!.ToString();
        Read(state, pagesAddress);
        Assert.True(JsonNode.DeepEquals(structured["properties"]!["pages"], state.Construction.Evidence[pagesAddress]!["schema"]));
        foreach (var invalid in new[] { "schema:allocate@old:/output/properties/" + port, "schema:allocate@v1:/input" })
        {
            var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
            Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [invalid]));
            Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        }
        var oversized = PlannerFixture.Clone(state);
        if (version == 8) AuthorAllowance(oversized, 6000);
        oversized.Construction!.Evidence = null; oversized.Construction.EvidenceRequests.Clear();
        oversized.Catalog!.Capabilities.Single(capability => capability.Id == "allocate").OutputSchema["properties"]![port]!["description"] = new string('x', 100_000);
        var evidenceBefore = JsonSerializer.Serialize(oversized.Construction, PlanningJsonContext.Default.PlanningConstructionState);
        var admission = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(oversized, [schemaAddress],
            followup: Request, purpose: PlanningConstruction.DraftPurpose));
        Assert.Contains(admission.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_LIMIT");
        Assert.Equal(evidenceBefore, JsonSerializer.Serialize(oversized.Construction, PlanningJsonContext.Default.PlanningConstructionState));
        Assert.Contains(oversized.RequestMeasurements!, measurement => measurement.Admission == "rejected_before_dispatch");

        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["req_0"], Tasks = [new() { Id = "consume", Kind = "transform", Mode = "interpret",
                Objective = "Interpret the supplied observed records, preserving their ordering and null state.", Inputs = [new("snapshot", binding)],
                ResultType = new() { Kind = "object", Fields = [new() { Name = "result", Type = new() { Kind = "string" } }] } }]
        });
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Construction.Candidate.Plan, state.Catalog!, state.Request,
            state.Construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        var judgeRequest = Request(state);
        PlanningConstruction.AdmitEvidenceFollowup(state, judgeRequest, PlanningConstruction.LocalPurpose);
        var judge = Presented(judgeRequest);
        Assert.Empty(judge["operations"]!.AsArray());
        Assert.True(JsonNode.DeepEquals(structured, Assert.Single(judge["interfaces"]!.AsArray())!["schema"]));
        Assert.True(JsonNode.DeepEquals(structured, judge["evidence"]![schemaAddress]!["schema"]));
        Assert.Contains(producer.Description[..100], judge["evidence"]![description]!["text"]!.ToString(), StringComparison.Ordinal);
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(judge, "/taskEvidence/items/0", true));
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(contract, JsonSerializer.Serialize(state.Catalog!.Capabilities.Single(capability => capability.Id == "allocate"), PlanningJsonContext.Default.PlanningCapability));
        Assert.Empty(state.Construction.Compact.CurrentStep.OperationIds); Assert.Empty(state.Construction.Compact.CurrentStep.SchemaPointers);
        Assert.Empty(state.Construction.Compact.CurrentStep.CloseRequirementIds); Assert.Empty(state.Construction.Compact.PendingCommitments);
        Assert.Null(state.Construction.Candidate.AuthorizedPaths); Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Equal(0, runtime.Discoveries);
    }

    [Fact]
    public async Task SelectionCanInspectTheVisibleAcceptedProducerWithoutSelectingAnOperation()
    {
        var state = await ArtifactProducerState();
        state.Construction!.Compact!.CurrentStep = null;
        var baseline = PlanningConstruction.Hash(state.Plan);
        var context = PlanningCompactConstruction.SelectionContext(state);
        var row = Assert.Single(context["interfaces"]!.AsArray(), item => item?["task"]?.ToString() == "make")!;
        Assert.Equal("handle", Assert.Single(row["produces"]!.AsArray())!["port"]!.ToString());
        var address = row["outputDirectory"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        var schema = state.Construction.Evidence![address]!["items"]![0]!["schema"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [schema]);
        Assert.Null(state.Construction.Compact.CurrentStep);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction.Compact.PendingCommitments);
    }

    [Fact]
    public async Task RetainedProducerPageLinksRemainReadableAfterSelectionPagination()
    {
        var state = await ArtifactProducerState();
        state.Construction!.Compact!.CurrentStep = null;
        foreach (var id in new[] { "second", "third", "fourth", "fifth" }) state.Plan!.Root.Tasks.Add(Value(id));
        PlanningConstruction.LoadEvidence(state, ["page:interfaces:0"]);
        var page = state.Construction.Evidence!["page:interfaces:0"]!.ToJsonString();
        var address = state.Construction.Evidence["page:interfaces:0"]![0]!["outputDirectory"]!.ToString();
        PlanningConstruction.LoadEvidence(state, ["page:interfaces:4"]);
        Assert.Equal(4, state.Construction.Compact.InterfaceOffset);
        Assert.DoesNotContain("make", PlanningCompactConstruction.InterfaceDirectory(state, null, 4).Select(row => row?["task"]?.ToString()));
        var context = PlanningCompactConstruction.SelectionContext(state);
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        PlanningCompactConstruction.ConstrainSelectionEvidence(state, context, schema);
        Assert.Contains(address, schema.ToJsonString(), StringComparison.Ordinal);
        PlanningConstruction.LoadEvidence(state, [address]);
        var exact = state.Construction.Evidence[address]!["items"]![0]!["schema"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [exact]);
        Assert.Equal("string", state.Construction.Evidence[exact]!["schema"]!["type"]!.ToString());
        Assert.Equal(page, state.Construction.Evidence["page:interfaces:0"]!.ToJsonString());
        Assert.Null(state.Construction.Compact.CurrentStep);
        Assert.Empty(state.Construction.Compact.PendingCommitments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactProducerOutputCannotBypassAmbiguousAcceptedContractVersions(bool retained)
    {
        var state = await ArtifactProducerState();
        state.Construction!.Compact!.CurrentStep = null;
        if (retained)
        {
            PlanningConstruction.LoadEvidence(state, ["page:interfaces:0"]);
            state.Construction.Compact.InterfaceOffset = 4;
        }
        var another = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Catalog!.Capabilities.Single(c => c.Id == "allocate"),
            PlanningJsonContext.Default.PlanningCapability), PlanningJsonContext.Default.PlanningCapability)!;
        another.Version = "v2";
        state.Discovery.Resolved.Add(another);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        foreach (var address in new[] { "schema:allocate@v1:/output/properties/handle", "description:allocate@v1:0" })
        {
            Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address]));
            Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        }
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("not_displayed")]
    [InlineData("denied")]
    [InlineData("stale")]
    [InlineData("unavailable")]
    public async Task SelectionProducerDescriptionRetainsExactContractAndAccessGuards(string invalid)
    {
        var state = await ArtifactProducerState();
        state.Construction!.Compact!.CurrentStep = null;
        var address = "description:allocate@v1:0";
        if (invalid == "unrelated") address = "description:inspect@v1:0";
        if (invalid == "not_displayed") state.Construction.Compact.InterfaceOffset = 4;
        if (invalid == "denied") state.Catalog!.Policy.DeniedCapabilityIds.Add("allocate");
        if (invalid == "stale") address = "description:allocate@old:0";
        if (invalid == "unavailable") state.Catalog!.Capabilities.RemoveAll(capability => capability.Id == "allocate");
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address]));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingVersionFiveDraftKeepsItsIssuedProducerPresentationAndBytes(bool retainPorts)
    {
        var state = await ArtifactProducerState();
        var context = Context(state, false);
        foreach (var row in context["taskEvidence"]!["items"]!.AsArray().OfType<JsonObject>())
            foreach (var key in retainPorts ? new[] { "descriptionEvidence" } :
                new[] { "operationVersion", "contractHash", "outputDirectory", "descriptionEvidence", "produces", "producesCount", "producesOmitted" }) row.Remove(key);
        var schema = PlanningCompactConstruction.DraftSchema(state);
        PlanningCompactConstruction.ConstrainDraftEvidence(state, context, schema);
        var issued = PlanningCompactConstruction.Request(PlanningCompactConstruction.DraftGuidance(state), context, schema, PlanningConstruction.DraftPurpose);
        issued.ClientRequestId = "retained-producer-draft";
        state.PendingCall = new() { Id = issued.ClientRequestId, Purpose = PlanningConstruction.DraftPurpose, Request = issued };
        state.ModelCalls = 1;
        state = PlannerFixture.Clone(state);
        var saved = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(saved, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = new JsonObject { ["baseHash"] = PlanningConstruction.Hash(state.Plan),
                ["requirements"] = null, ["commitments"] = null, ["extension"] = null, ["discoveryRequests"] = null,
                ["clarifications"] = null, ["evidenceRequests"] = new JsonArray("requirement:req_0"),
                ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null } };
        } };
        var next = ArtifactDraftRequest(state);
        Assert.NotEqual(saved, JsonSerializer.Serialize(next, PlanningJsonContext.Default.LLMRequest));
        await PlanningModelCalls.CallAsync(state, runtime, PlanningConstruction.DraftPurpose,
            next.Prompt, next.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, 8192);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(runtime.Calls);
        Assert.Empty(state.Construction!.Checkpoints);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("draft")]
    [InlineData("discovery")]
    [InlineData("review")]
    public async Task AccessibleProducerInputOriginalsRemainReadOnlyAcrossPagedConsumerDecisions(string phase)
    {
        var state = await ProducerInputState(6, phase);
        // This fixture retains several pages to verify closure, independently of
        // the default navigation ceiling exercised by the oversized case below.
        AuthorAllowance(state, 16000);
        var capability = state.Catalog!.Capabilities.Single(value => value.Id == "allocate");
        var baseline = PlanningConstruction.Hash(state.Plan);
        var candidate = state.Construction!.Candidate?.Hash;
        var step = JsonSerializer.Serialize(state.Construction.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var contract = JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability);
        var request = ProducerInputRequest(state, phase);
        var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
        var links = PlanningCompactConstruction.NavigationEvidence([context["interfaces"], context["taskEvidence"]]);
        const string first = "schema-page:allocate@v1:/input:0";
        Assert.Contains(first, links);
        var count = 0;
        JsonNode? settings = null;
        for (string? address = first; address is not null;)
        {
            Read(address);
            var page = state.Construction.Evidence![address]!;
            settings ??= page["items"]!.AsArray().SingleOrDefault(item => item!["pointer"]!.ToString() == "/input/properties/settings");
            count++; address = page["next"]?.ToString();
        }
        Assert.True(count > 1);
        Assert.NotNull(settings);
        var nested = settings!["directory"]!.ToString();
        Read(nested);
        var mode = Assert.Single(state.Construction.Evidence![nested]!["items"]!.AsArray(),
            item => item!["pointer"]!.ToString() == "/input/properties/settings/properties/mode");
        var exact = mode!["evidence"]!.ToString();
        Read(exact);
        var original = state.Construction.Evidence[exact]!;
        Assert.True(JsonNode.DeepEquals(capability.InputSchema["properties"]!["settings"]!["properties"]!["mode"], original["schema"]));
        Assert.True(JsonNode.DeepEquals(capability.InputSchema["$defs"]!["mode"], original["referenceClosure"]!["$defs"]!["mode"]));
        Assert.Equal("markup", original["referenceClosure"]!["$defs"]!["mode"]!["default"]!.ToString());
        Assert.True(JsonNode.DeepEquals(capability.InputSchema, original["ancestors"]![0]!["constraints"]));
        var ancestor = Assert.Single(original["ancestors"]!.AsArray(), value => value!["pointer"]!.ToString() == "/input/properties/settings");
        Assert.Equal("utf8", ancestor!["requiredSiblings"]!["encoding"]!["default"]!.ToString());
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(candidate, state.Construction.Candidate?.Hash);
        Assert.Equal(step, JsonSerializer.Serialize(state.Construction.Compact.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
        Assert.Equal(contract, JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));
        Assert.Empty(state.Plan!.Root.Tasks.Single(task => task.Id == "make").Inputs);
        Assert.Empty(state.Construction.Compact.PendingCommitments);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [exact]));
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        void Read(string address)
        {
            var issued = ProducerInputRequest(state, phase);
            if (phase == "review")
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(new JsonObject
                {
                    ["action"] = new JsonObject { ["kind"] = "evidenceRequests", ["addresses"] = new JsonArray(address) },
                    ["uncertainty"] = "Read the existing producer's exact input semantics."
                }, issued.StructuredOutputSchema!));
            else Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(new JsonArray(address),
                issued.StructuredOutputSchema!["properties"]!["evidenceRequests"]!));
            PlanningConstruction.LoadEvidence(state, [address], followup: current => ProducerInputRequest(current, phase),
                purpose: ProducerInputPurpose(phase));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task HistoricalProducerInputInspectionRemainsUnavailable(int version)
    {
        var state = await ProducerInputState(version, "draft");
        var context = Context(state, false);
        Assert.DoesNotContain("schema-page:allocate@v1:/input:0", context.ToJsonString(), StringComparison.Ordinal);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state,
            ["schema:allocate@v1:/input/properties/settings/properties/mode"]));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("unselected")]
    [InlineData("inaccessible")]
    [InlineData("selection_hidden")]
    [InlineData("stale")]
    [InlineData("denied")]
    [InlineData("ambiguous")]
    [InlineData("whole_input")]
    public async Task ProducerInputInspectionPreservesExistingIdentityScopeAndPermissionGuards(string invalid)
    {
        var state = await ProducerInputState(6, invalid == "selection_hidden" ? "selection" : "draft");
        var address = "schema:allocate@v1:/input/properties/settings/properties/mode";
        if (invalid == "unrelated") address = "schema:inspect@v1:/input/properties/location";
        if (invalid == "unselected") state.Construction!.Compact!.CurrentStep!.Inputs.Clear();
        if (invalid == "selection_hidden") state.Construction!.Compact!.InterfaceOffset = 4;
        if (invalid == "stale") address = "schema:allocate@old:/input/properties/settings/properties/mode";
        if (invalid == "denied") state.Catalog!.Policy.DeniedCapabilityIds.Add("allocate");
        if (invalid == "whole_input") address = "schema:allocate@v1:/input";
        if (invalid == "ambiguous")
        {
            var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Catalog!.Capabilities.Single(value => value.Id == "allocate"),
                PlanningJsonContext.Default.PlanningCapability), PlanningJsonContext.Default.PlanningCapability)!;
            copy.Version = "v2"; state.Discovery.Resolved.Add(copy);
        }
        if (invalid == "inaccessible")
        {
            var task = Assert.Single(state.Plan!.Root.Tasks); state.Plan.Root.Tasks.Clear();
            state.Plan.Groups = [new() { Id = "isolated", Body = new() { Tasks = [task] } }];
            state.Construction!.OpenScopes.Add("/groups/isolated/body");
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address]));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task OversizedProducerInputOriginalDoesNotRetainEvidenceOrGrantAuthoringAuthority()
    {
        var state = await ProducerInputState(6, "draft");
        var capability = state.Catalog!.Capabilities.Single(value => value.Id == "allocate");
        capability.InputSchema["$defs"]!["mode"]!["description"] = new string('x', 200000);
        foreach (var address in new[] { "schema-page:allocate@v1:/input:0", "schema-page:allocate@v1:/input:8",
                     "schema-page:allocate@v1:/input/properties/settings:0" })
            PlanningConstruction.LoadEvidence(state, [address], followup: current => ProducerInputRequest(current, "draft"),
                purpose: PlanningConstruction.DraftPurpose);
        const string exact = "schema:allocate@v1:/input/properties/settings/properties/mode";
        var issued = ProducerInputRequest(state, "draft");
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(new JsonArray(exact),
            issued.StructuredOutputSchema!["properties"]!["evidenceRequests"]!));
        var evidence = state.Construction!.Evidence!.ToJsonString();
        var reads = state.Construction.EvidenceRequests.ToArray();
        var step = JsonSerializer.Serialize(state.Construction.Compact!.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var exception = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [exact],
            followup: current => ProducerInputRequest(current, "draft"), purpose: PlanningConstruction.DraftPurpose));
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_LIMIT");
        Assert.Equal(evidence, state.Construction.Evidence.ToJsonString());
        Assert.Equal(reads, state.Construction.EvidenceRequests);
        Assert.Equal(step, JsonSerializer.Serialize(state.Construction.Compact.CurrentStep, PlanningJsonContext.Default.PlanningConstructionStep));
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task PendingVersionSixDraftKeepsItsIssuedBytesBeforeProducerInputNavigation()
    {
        var state = await ProducerInputState(6, "draft");
        var context = Context(state, false);
        RemoveInputDirectories(context);
        var schema = PlanningCompactConstruction.DraftSchema(state);
        PlanningCompactConstruction.ConstrainDraftEvidence(state, context, schema);
        var issued = PlanningCompactConstruction.Request(PlanningCompactConstruction.DraftGuidance(state), context, schema, PlanningConstruction.DraftPurpose);
        issued.ClientRequestId = "retained-producer-input-draft";
        state.PendingCall = new() { Id = issued.ClientRequestId, Purpose = PlanningConstruction.DraftPurpose, Request = issued };
        state.ModelCalls = 1;
        state = PlannerFixture.Clone(state);
        var saved = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(saved, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = new JsonObject { ["baseHash"] = PlanningConstruction.Hash(state.Plan),
                ["requirements"] = null, ["commitments"] = null, ["extension"] = null, ["discoveryRequests"] = null,
                ["clarifications"] = null, ["evidenceRequests"] = new JsonArray("requirement:req_0"),
                ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null } };
        } };
        var next = PlanningConstruction.DraftRequest(state, false);
        Assert.NotEqual(saved, JsonSerializer.Serialize(next, PlanningJsonContext.Default.LLMRequest));
        await PlanningModelCalls.CallAsync(state, runtime, PlanningConstruction.DraftPurpose, next.Prompt,
            next.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, 8192);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(runtime.Calls); Assert.Empty(state.Construction!.Checkpoints);

        static void RemoveInputDirectories(JsonNode? node)
        {
            if (node is JsonObject value)
            {
                value.Remove("inputDirectory");
                foreach (var child in value.Select(item => item.Value).ToArray()) RemoveInputDirectories(child);
            }
            else if (node is JsonArray list) foreach (var child in list) RemoveInputDirectories(child);
        }
    }

    private static async Task<PlanningSession> ProducerInputState(int version, string phase)
    {
        var state = await ArtifactProducerState(version);
        var capability = state.Catalog!.Capabilities.Single(value => value.Id == "allocate");
        capability.ArtifactContract = null;
        capability.InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        for (var index = 0; index < 8; index++)
            capability.InputSchema["properties"]!["optional_" + index] = new JsonObject { ["type"] = "string" };
        capability.InputSchema["properties"]!["settings"] = JsonNode.Parse("""
            {"type":"object","properties":{"mode":{"$ref":"#/$defs/mode"},"encoding":{"type":"string","default":"utf8"},
            "explicitNull":{"type":["string","null"],"default":null},"unspecified":{"type":"string"}},"required":["mode","encoding"]}
            """);
        capability.InputSchema["$defs"] = JsonNode.Parse("""
            {"mode":{"type":"string","enum":["markup","records"],"default":"markup","description":"The producer's omitted representation."}}
            """);
        capability.InputSchema["allOf"] = JsonNode.Parse("""[{"type":"object","maxProperties":9}]""");
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "transform"; step.OperationIds.Clear(); step.BaseHash = PlanningConstruction.Hash(state.Plan);
        Assert.Empty(TaskOperations.Validate(capability));
        var projection = TaskPlanCompiler.ValidateConstruction(state.Plan!, state.Catalog!, state.Request,
            state.Construction.OpenScopes.ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<TaskValue>> { [step.Scope] = step.Inputs });
        Assert.Empty(projection.Diagnostics);
        Assert.Single(projection.Interfaces[step.Scope]);
        if (phase == "selection") state.Construction.Compact.CurrentStep = null;
        if (phase == "review") state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = step.Scope, RequirementIds = ["req_0"], Tasks = [new()
            {
                Id = "consume", Kind = "transform", Mode = "interpret", Objective = "Interpret only the supplied producer data.",
                Inputs = [new("observed", TaskArtifactBindingTests.Reference("make", "display"))],
                ResultType = new() { Kind = "object", Fields = [new() { Name = "result", Type = new() { Kind = "string" } }] }
            }]
        });
        return state;
    }

    private static string ProducerInputPurpose(string phase) => phase switch
    {
        "selection" => PlanningCompactConstruction.SelectPurpose,
        "discovery" => PlanningCompactConstruction.DiscoveryPurpose,
        "review" => PlanningConstruction.LocalPurpose,
        _ => PlanningConstruction.DraftPurpose
    };

    private static LLMRequest ProducerInputRequest(PlanningSession state, string phase)
    {
        LLMRequest raw;
        if (phase == "selection")
        {
            var context = PlanningCompactConstruction.SelectionContext(state);
            var schema = PlanningCompactConstruction.SelectionSchema(state);
            PlanningCompactConstruction.ConstrainSelectionEvidence(state, context, schema);
            raw = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state), context, schema,
                PlanningCompactConstruction.SelectPurpose);
        }
        else if (phase == "discovery") raw = DiscoveryRequest(state);
        else raw = phase == "review" ? HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false).Request
            : PlanningConstruction.DraftRequest(state, false);
        return PlanningModelCalls.PrepareRequest(state, ProducerInputPurpose(phase), raw.Prompt, raw.StructuredOutputSchema!.AsObject());
    }

    [Theory]
    [InlineData("input")]
    [InlineData("unselected")]
    [InlineData("inaccessible")]
    [InlineData("stale")]
    public async Task ProducerSchemaRetrievalDoesNotGrantNewSourceOrInputAccess(string invalid)
    {
        var state = await ArtifactProducerState();
        var address = "schema:allocate@v1:/output/properties/handle";
        if (invalid == "input") address = "schema:allocate@v1:/input";
        if (invalid == "stale") address = "schema:allocate@old:/output/properties/handle";
        if (invalid == "unselected") state.Construction!.Compact!.CurrentStep!.Inputs.Clear();
        if (invalid == "inaccessible")
        {
            var task = Assert.Single(state.Plan!.Root.Tasks);
            state.Plan.Root.Tasks.Clear();
            state.Plan.Groups = [new() { Id = "isolated", Body = new() { Tasks = [task] } }];
            state.Construction!.OpenScopes.Add("/groups/isolated/body");
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address]));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task OversizedProducerArtifactsRemainCompleteAcrossExistingOutputPages()
    {
        var state = await ArtifactProducerState();
        var capability = state.Catalog!.Capabilities.Single(c => c.Id == "allocate");
        var schema = capability.OutputSchema;
        var artifacts = capability.ArtifactContract!.Produces.ToList();
        for (var index = 0; index < 10; index++)
        {
            var name = "extra_" + index;
            schema["properties"]![name] = new JsonObject { ["type"] = "string" };
            schema["required"]!.AsArray().Add((JsonNode)JsonValue.Create(name)!);
            artifacts.Add(new("resource_" + new string('x', 250) + index, "/" + name, "materialize"));
        }
        capability.ArtifactContract = new(1, artifacts, []);
        var context = Context(state, false);
        var row = context["taskEvidence"]!["items"]![0]!;
        Assert.True(row["producesOmitted"]!.GetValue<bool>());
        Assert.Null(row["produces"]);
        Assert.Equal(11, row["producesCount"]!.GetValue<int>());
        var produced = new JsonArray();
        var address = row["outputDirectory"]!.ToString();
        do
        {
            PlanningConstruction.LoadEvidence(state, [address]);
            var page = state.Construction!.Evidence![address]!;
            Assert.InRange(page["items"]!.AsArray().Count, 1, 8);
            foreach (var artifact in page["produces"]!.AsArray()) produced.Add(artifact!.DeepClone());
            address = page["next"]?.ToString();
        } while (address is not null);
        var expected = TaskOperations.ArtifactPorts(capability)["produces"]!.AsArray();
        Assert.Equal(expected.OrderBy(item => item!["port"]!.ToString()).Select(item => item!.ToJsonString()),
            produced.OrderBy(item => item!["port"]!.ToString()).Select(item => item!.ToJsonString()));
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.SchemaPointers);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task HistoricalProducerMetadataDoesNotChangeIssuedDraftBytes(int version)
    {
        var state = await ArtifactProducerState(version);
        var before = JsonSerializer.Serialize(ArtifactDraftRequest(state), PlanningJsonContext.Default.LLMRequest);
        state.Catalog!.Capabilities.Single(c => c.Id == "allocate").ArtifactContract = new(1, [new("different", "/handle", "materialize")], []);
        state.Catalog.Capabilities.Single(c => c.Id == "allocate").Description = "Changed producer prose outside this historical presentation.";
        Assert.Equal(before, JsonSerializer.Serialize(ArtifactDraftRequest(state), PlanningJsonContext.Default.LLMRequest));
        state.Construction!.Compact!.CurrentStep = null;
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, ["description:allocate@v1:0"]));
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    private static async Task<PlanningSession> ArtifactProducerState(int version = 5)
    {
        var state = Ready("/root", version);
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        plan.Root.Tasks.RemoveAt(1); plan.Root.Outputs.Clear();
        foreach (var capability in catalog.Capabilities.Where(c => c.Id is "allocate" or "inspect")) capability.Version = "v1";
        state.Plan = plan; state.Catalog = catalog;
        state.Construction!.Candidate = null; state.Construction.OpenScopes = ["/root"];
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "operation"; step.OperationIds = ["inspect"]; step.InputTaskIds = ["make"];
        step.Inputs = [TaskArtifactBindingTests.Reference("make", "display")];
        step.BaseHash = PlanningConstruction.Hash(plan);
        return state;
    }

    private static PlanningConstructionExtension ArtifactConsumer(string port) => new()
    {
        Scope = "/root", RequirementIds = ["req_0"], Tasks = [new() { Id = "consume", Kind = "operation", Operation = "inspect",
            Objective = "Inspect the declared resource.", Inputs = [new("location", TaskArtifactBindingTests.Reference("make", port))] }]
    };

    private static LLMRequest ArtifactDraftRequest(PlanningSession state)
    {
        var context = Context(state, false); var schema = PlanningCompactConstruction.DraftSchema(state);
        PlanningCompactConstruction.ConstrainDraftEvidence(state, context, schema);
        return PlanningCompactConstruction.Request(PlanningCompactConstruction.DraftGuidance(state), context, schema, PlanningConstruction.DraftPurpose);
    }

    [Theory]
    [InlineData("/root")]
    [InlineData(NestedScope)]
    [InlineData("/groups/worker/body")]
    public void AcceptedOperationsSupplyWholeOriginalsWithoutImportedBindingsOrNewEvidenceProgress(string scope)
    {
        var state = OperationState(scope);
        var accepted = new TaskPlanSymbols(state.Plan!);
        var site = accepted.Tasks["accepted_operation"];
        var expected = PlanningConstruction.TaskNode(site.Task);
        expected["scope"] = scope; expected["phase"] = accepted.Phase(site.Task.Id);
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var selection = PlannerFixture.Clone(state);
        selection.Construction!.Candidate = null;
        // The existing selection directory is paged; use the page containing this operation.
        selection.Construction.Compact!.InterfaceOffset = accepted.Tasks.Keys.ToList().IndexOf(site.Task.Id) / 4 * 4;
        var contexts = new[] { PlanningCompactConstruction.SelectionContext(selection), Context(state, false),
            HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false).Context,
            HybridWorkflowPlanner.CreateConstructionReviewRequest(state, true).Context };
        foreach (var context in contexts)
        {
            Assert.True(JsonNode.DeepEquals(expected, context["evidence"]!["task:accepted_operation"]));
            Assert.DoesNotContain("task:candidate", context["evidence"]!.AsObject().Select(item => item.Key));
            var original = context["evidence"]!["task:accepted_operation"]!;
            Assert.Equal("workspace/完整\"target\"\n", original["inputs"]![0]!["value"]!["text"]!.ToString());
            Assert.True(original["condition"]!["boolean"]!.GetValue<bool>());
            Assert.True(original["requires"]!["boolean"]!.GetValue<bool>());
            Assert.Equal(site.Task.DependsOn, original["dependsOn"]!.AsArray().Select(item => item!.ToString()));
            Assert.True(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/evidence/task:accepted_operation", true));
            Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/taskEvidence/items/0", true));
            Assert.InRange(Encoding.UTF8.GetByteCount(context["evidence"]!.ToJsonString()), 1, 1024);
        }
        Assert.Empty(contexts[2]["interfaces"]!.AsArray());
        Assert.Empty(contexts[3]["interfaces"]!.AsArray());
        Assert.True(HybridWorkflowPlanner.ConstructionBusinessFindingSupported(contexts[2], new JsonObject
        {
            ["location"] = "/draft/tasks/0", ["evidence"] = new JsonArray("/requirements/0", "/evidence/task:accepted_operation")
        }));
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public void AutomaticOriginalsShareOneBoundAndOversizedFieldsRemainOnlyAtTheirExactRetrievalAddress()
    {
        var state = OperationState("/root");
        var operation = state.Plan!.Root.Tasks.Single(task => task.Id == "accepted_operation");
        operation.Inputs[0] = new("target", new() { Kind = "string", Text = new string('界', 1000) });
        foreach (var id in new[] { "second", "third", "fourth" })
            state.Plan.Root.Tasks.Add(new() { Id = id, Kind = "operation", Operation = operation.Operation,
                Objective = "Create the declared resource.", Inputs = [new("target", new() { Kind = "string", Text = id })] });
        StageOperation(state);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = Context(state, true);
        var evidence = context["evidence"]!.AsObject();
        Assert.InRange(evidence.Count, 1, 3);
        Assert.InRange(Encoding.UTF8.GetByteCount(evidence.ToJsonString()), 1, 1024);
        Assert.False(evidence.ContainsKey("task:accepted_operation"));
        var row = Assert.Single(context["taskEvidence"]!["items"]!.AsArray(), item => item!["task"]!.ToString() == operation.Id)!;
        Assert.Contains("inputs", row["omittedFields"]!.AsArray().Select(item => item!.ToString()));
        Assert.Equal("task:accepted_operation", row["address"]!.ToString());
        Assert.DoesNotContain(new string('界', 10), context.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        foreach (var (address, original) in evidence)
        {
            var expected = PlanningConstruction.TaskNode(new TaskPlanSymbols(state.Plan).Tasks[address[5..]].Task);
            expected["scope"] = "/root"; expected["phase"] = "tasks";
            Assert.True(JsonNode.DeepEquals(expected, original));
        }
        PlanningConstruction.LoadEvidence(state, ["task:accepted_operation"]);
        var retained = state.Construction!.Evidence!["task:accepted_operation"]!.ToJsonString();
        Assert.Equal(retained, Context(state, true)["evidence"]!["task:accepted_operation"]!.ToJsonString());
        Assert.Contains(new string('界', 1000), state.Construction.Evidence["task:accepted_operation"]!["inputs"]![0]!["value"]!["text"]!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HistoricalPresentationsDoNotAddAutomaticOriginalsOrOmissionMetadata(int version)
    {
        var state = OperationState("/root", version);
        var selection = PlannerFixture.Clone(state); selection.Construction!.Candidate = null;
        foreach (var context in new[] { PlanningCompactConstruction.SelectionContext(selection), Context(state, false), Context(state, true) })
        {
            Assert.Null(context["evidence"]?["task:accepted_operation"]);
            Assert.DoesNotContain("omittedFields", context.ToJsonString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InlineAcceptedProducerIsNotNewReassessmentSupport()
    {
        var state = OperationState("/root");
        state.Plan!.Root.Tasks.Single(task => task.Id == "accepted_operation").Condition = null;
        StageOperation(state);
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        step.Kind = "value"; step.OperationIds.Clear(); step.InputTaskIds = ["accepted_operation"];
        construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["req_0"],
            Tasks = [new() { Id = "consume", Kind = "value", Objective = "Retain the accepted resource.",
                Outputs = [new("value", new() { Kind = "output", Source = "accepted_operation", Port = "resource" })] }] });
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            Assert.NotEmpty(context["interfaces"]!.AsArray());
            Assert.NotNull(context["evidence"]!["task:accepted_operation"]);
            return new() { Json = new JsonObject { ["action"] = new JsonObject { ["kind"] = "inconclusive",
                ["reason"] = "The already supplied producer does not establish business completion." }, ["uncertainty"] = "Unverified." } };
        } };
        var result = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Single(runtime.Calls);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_REVIEW_INCONCLUSIVE");
        Assert.DoesNotContain(result.Construction!.Candidate!.Reviews, report => report["reassessment"] is not null);
        Assert.Null(result.Construction.Evidence);
        Assert.Equal(0, result.ReplanAttempts);
        Assert.Empty(result.Construction.Checkpoints);
    }

    [Fact]
    public async Task IssuedVersionFiveReviewRetainsItsExactContextWithoutAutomaticOriginals()
    {
        var state = OperationState("/root");
        var review = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false);
        review.Context.Remove("evidence");
        var prompt = review.Request.Prompt[..(review.Request.Prompt.LastIndexOf('\n') + 1)] + review.Context.ToJsonString();
        var interrupted = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(state, interrupted,
            PlanningConstruction.LocalPurpose, prompt, review.Request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, state.Construction!.Limits.ReviewOutputTokens));
        var saved = PlannerFixture.Clone(interrupted.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
        var exact = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls;
        var next = HybridWorkflowPlanner.CreateConstructionReviewRequest(saved, false);
        Assert.NotNull(next.Context["evidence"]!["task:accepted_operation"]);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = new JsonObject { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray() },
                ["uncertainty"] = "Execution remains unverified." } };
        } };
        await PlanningModelCalls.CallAsync(saved, runtime, PlanningConstruction.LocalPurpose, next.Request.Prompt,
            next.Request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, saved.Construction!.Limits.ReviewOutputTokens);
        Assert.Single(runtime.Calls); Assert.Equal(calls, saved.ModelCalls); Assert.Equal(0, saved.ReplanAttempts);
        Assert.Null(saved.Construction.Evidence);
    }

    [Fact]
    public void UnrelatedAcceptedOperationsDoNotExpandScopedAutomaticEvidence()
    {
        var state = OperationState(NestedScope);
        var before = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false);
        for (var index = 0; index < 100; index++)
            state.Plan!.Root.Tasks.Add(new() { Id = "unrelated_" + index, Kind = "operation", Operation = "create_resource",
                Objective = "Create an unrelated resource.", Inputs = [new("target", new() { Kind = "string", Text = new string('x', 1000) })] });
        StageOperation(state);
        var after = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false);
        Assert.NotEqual(before.Context["baseHash"]!.ToString(), after.Context["baseHash"]!.ToString());
        Assert.NotEqual(before.Context["candidateHash"]!.ToString(), after.Context["candidateHash"]!.ToString());
        foreach (var context in new[] { before.Context, after.Context })
        { context.Remove("baseHash"); context.Remove("candidateHash"); }
        Assert.True(JsonNode.DeepEquals(before.Context, after.Context));
        Assert.Equal(before.Request.StructuredOutputSchema!.ToJsonString(), after.Request.StructuredOutputSchema!.ToJsonString());
        var first = PlanningRequestMeasurements.Measure(before.Request, PlanningConstruction.LocalPurpose, 8000);
        var last = PlanningRequestMeasurements.Measure(after.Request, PlanningConstruction.LocalPurpose, 8000);
        Assert.Equal(first.CompleteRequestBytes, last.CompleteRequestBytes);
        Assert.Equal(first.EstimatedInputTokens, last.EstimatedInputTokens);
    }

    [Fact]
    public void RetainedCurrentInterfacePageStillSuppliesTheSameBoundedOriginalsWithoutChangingThePage()
    {
        var state = OperationState("/root");
        state.Plan!.Root.Tasks.RemoveAll(task => task.Id == "container");
        state.Plan.Root.Tasks.Add(new() { Id = "large_operation", Kind = "operation", Operation = "create_resource",
            Objective = "Create another resource.", Inputs = [new("target", new() { Kind = "string", Text = new string('x', 2000) })] });
        state.Construction!.Candidate = null;
        var symbols = new TaskPlanSymbols(state.Plan!);
        state.Construction.OpenScopes = symbols.Scopes.Select(site => site.Path).ToList();
        state.Construction.Compact!.InterfaceOffset = symbols.Tasks.Keys.ToList().IndexOf("accepted_operation") / 4 * 4;
        var before = PlanningCompactConstruction.SelectionContext(state);
        Assert.NotNull(Assert.Single(before["interfaces"]!.AsArray(), item => item?["task"]?.ToString() == "large_operation")!["omittedFields"]);
        var address = "page:interfaces:" + state.Construction.Compact.InterfaceOffset;
        PlanningConstruction.LoadEvidence(state, [address]);
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var originalPage = state.Construction.Evidence![address]!.ToJsonString();
        var after = PlanningCompactConstruction.SelectionContext(state);
        Assert.Null(after["interfaces"]);
        Assert.Equal(originalPage, after["evidence"]![address]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(before["evidence"]!["task:accepted_operation"], after["evidence"]!["task:accepted_operation"]));
        Assert.Null(after["evidence"]!["task:large_operation"]);
        Assert.Null(state.Construction.Evidence["task:accepted_operation"]);
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData("/root", false)]
    [InlineData("/root", true)]
    [InlineData(NestedScope, false)]
    [InlineData(NestedScope, true)]
    [InlineData("/groups/worker/body", false)]
    [InlineData("/groups/worker/body", true)]
    public void NavigationIncludesOnlyAcceptedImmediateSiblingsInTheSelectedScope(string scope, bool review)
    {
        var state = Ready(scope);
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var symbols = new TaskPlanSymbols(state.Plan!);
        var siblings = symbols.Tasks.Values.Where(site => site.Scope.Path == scope).Select(site => site.Task).ToArray();
        var navigation = Navigation(state, review);
        Assert.Equal(siblings.Length, navigation["count"]!.GetValue<int>());
        Assert.Equal(0, navigation["remaining"]!.GetValue<int>());
        Assert.Null(navigation["remainingEvidence"]);
        Assert.Null(navigation["scopeEvidence"]);
        Assert.Equal(siblings.Select(task => "task:" + task.Id), Addresses(navigation));
        foreach (var item in navigation["items"]!.AsArray())
        {
            var id = item!["task"]!.ToString();
            Assert.Equal(scope, item["scope"]!.ToString());
            Assert.Equal(symbols.Phase(id), item["phase"]!.ToString());
            Assert.Equal(symbols.Tasks[id].Task.Kind, item["kind"]!.ToString());
            Assert.Equal(symbols.Tasks[id].Task.Objective, item["objective"]!.ToString());
            Assert.True(item["objectiveComplete"]!.GetValue<bool>());
        }
        Assert.DoesNotContain("candidate", navigation.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public void UnrelatedScopesAndGroupsDoNotGrowTheCompleteRequest(int presentationVersion, bool review)
    {
        var state = Ready(NestedScope, presentationVersion);
        var before = Context(state, review);
        var first = Measure(state, review);
        for (var index = 0; index < 100; index++)
        {
            state.Plan!.Root.Tasks.Add(new() { Id = "unrelated_" + index, Kind = "sequence", Objective = new string('x', 4000),
                Body = new() { Tasks = [Value("unrelated_child_" + index)] } });
            state.Plan.Groups.Add(new() { Id = "unrelated_group_" + index, Body = new() { Tasks = [Value("unrelated_group_task_" + index)] } });
        }
        Stage(state);
        var after = Context(state, review);
        var last = Measure(state, review);
        Assert.True(JsonNode.DeepEquals(before["taskEvidence"], after["taskEvidence"]));
        Assert.DoesNotContain("unrelated", after.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(first.CompleteRequestBytes, last.CompleteRequestBytes);
        Assert.Equal(first.EstimatedInputTokens, last.EstimatedInputTokens);
        Assert.Equal(last.CompleteRequestBytes, last.Components.Sum(component => component.SerializedBytes));
        Assert.InRange(last.EstimatedInputTokens, 1, review ? 8000 : 16000);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    public void ImportsHavePriorityAndRecentSiblingsShareTheBoundWithoutDuplicates(int externalCount, bool review)
    {
        var state = Ready(NestedScope);
        var scope = new TaskPlanSymbols(state.Plan!).Scopes.Single(site => site.Path == NestedScope).Source;
        scope.Tasks = Enumerable.Range(0, 6).Select(index => Value("sibling_" + index)).ToList();
        scope.Always = [Value("cleanup")];
        var external = Enumerable.Range(0, externalCount).Select(index => "external_" + index).ToArray();
        state.Plan!.Root.Tasks.InsertRange(0, external.Select(Value));
        Stage(state);
        var producers = external.Concat(["sibling_0"]).ToArray();
        // These are presentation hints; the candidate's semantic bindings and
        // complete compiler validation remain separate from this directory.
        state.Construction!.Compact!.CurrentStep!.InputTaskIds = producers.Concat(["sibling_0"]).ToList();
        state.Construction.Compact.CurrentStep.DependencyTaskIds = ["sibling_0"];
        state.Construction.Candidate!.ImportedTaskIds = producers.Concat(["sibling_0"]).ToList();
        var navigation = Navigation(state, review);
        var addresses = Addresses(navigation);
        var importedCount = Math.Min(8, producers.Length);
        var siblingCount = Math.Min(4, 8 - importedCount);
        Assert.Equal(externalCount + 7, navigation["count"]!.GetValue<int>());
        Assert.Equal(importedCount + siblingCount, addresses.Length);
        Assert.Equal(addresses.Length, addresses.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(producers.Take(8).Select(id => "task:" + id), addresses.Take(importedCount));
        var recent = scope.Tasks.Concat(scope.Always).Where(task => task.Id != "sibling_0").TakeLast(siblingCount);
        Assert.Equal(recent.Select(task => "task:" + task.Id), addresses.Skip(importedCount));
        Assert.Equal(externalCount + 7 - addresses.Length, navigation["remaining"]!.GetValue<int>());
        Assert.Equal("page:tasks:0", navigation["remainingEvidence"]!.ToString());
        Assert.Equal("scope:" + NestedScope, navigation["scopeEvidence"]!.ToString());
        Assert.DoesNotContain("task:candidate", addresses);
    }

    [Theory]
    [InlineData("opaque repeated text ")]
    [InlineData("汉字😀\"\\\n")]
    public void BoundedObjectivesRetainExactRetrievalAndCallOrOperationIdentity(string text)
    {
        var state = Ready(NestedScope);
        var scope = new TaskPlanSymbols(state.Plan!).Scopes.Single(site => site.Path == NestedScope).Source;
        var objective = string.Concat(Enumerable.Repeat(text, 80));
        scope.Tasks =
        [
            new() { Id = "reusable_call", Kind = "call", Group = "worker", Objective = objective },
            new() { Id = "operation_call", Kind = "operation", Operation = "service@v1/read", Objective = objective },
            new() { Id = "nested_container", Kind = "sequence", Objective = "Retain its existing child scope.",
                Body = new() { Tasks = [Value("child")] } }
        ];
        scope.Always.Clear();
        state.Construction!.OpenScopes = new TaskPlanSymbols(state.Plan!).Scopes.Select(site => site.Path).ToList();
        Stage(state);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var context = Context(state, review: true);
        var items = context["taskEvidence"]!["items"]!.AsArray();
        Assert.Equal("worker", items[0]!["group"]!.ToString());
        Assert.Equal("service@v1/read", items[1]!["operation"]!.ToString());
        Assert.Equal(1, items[2]!["scopeCount"]!.GetValue<int>());
        Assert.Equal("scope:/tasks/nested_container/body", Assert.Single(items[2]!["scopes"]!.AsArray())!.ToString());
        foreach (var historical in new[] { false, true })
        {
            var selection = PlannerFixture.Clone(state);
            if (historical)
            {
                selection.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
                selection.Construction!.Limits.PhasePolicy = null;
            }
            var directory = PlanningCompactConstruction.InterfaceDirectory(selection, null, 0);
            var presented = PlanningCompactConstruction.SelectionContext(selection)["interfaces"]!.AsArray();
            foreach (var entries in new[] { directory, presented })
            {
                var call = Assert.Single(entries, entry => entry?["task"]?.ToString() == "reusable_call")!;
                var operation = Assert.Single(entries, entry => entry?["task"]?.ToString() == "operation_call")!;
                Assert.Equal(historical ? null : "worker", call["group"]?.ToString());
                Assert.Equal(historical ? null : "service@v1/read", operation["operation"]?.ToString());
                Assert.Equal("task:reusable_call", call["evidence"]!.ToString());
                Assert.Equal("task:operation_call", operation["evidence"]!.ToString());
            }
        }
        foreach (var item in items.Take(2))
        {
            var excerpt = item!["objective"]!.ToString();
            Assert.False(item["objectiveComplete"]!.GetValue<bool>());
            Assert.StartsWith(excerpt, objective, StringComparison.Ordinal);
            Assert.InRange(Encoding.UTF8.GetByteCount(item["objective"]!.ToJsonString()), 1, 204);
            Assert.StartsWith("task-objective:" + item["task"] + "@", item["objectiveEvidence"]!.ToString(), StringComparison.Ordinal);
        }
        var address = items[0]!["objectiveEvidence"]!.ToString();
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, address, out var evidence));
        Assert.StartsWith(evidence!["text"]!.ToString(), objective, StringComparison.Ordinal);
        Assert.Equal(PlanningGraphCompiler.Fingerprint(objective), evidence["version"]!.ToString());
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
    }

    [Fact]
    public void NavigationCannotGroundFindingsButItsTaskAddressRetrievesOriginalBindings()
    {
        var state = Ready(NestedScope);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var context = Context(state, review: true);
        var item = context["taskEvidence"]!["items"]![0]!;
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/taskEvidence/items/0", compact: true));
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/taskEvidence/items/0/objective", compact: true));
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/taskEvidence/items/0/address", compact: true));
        var address = item["address"]!.ToString();
        var task = new TaskPlanSymbols(state.Plan!).Tasks[item["task"]!.ToString()].Task;
        PlanningConstruction.LoadEvidence(state, [address]);
        var reviewed = Context(state, review: true);
        var original = PlanningConstruction.TaskNode(task);
        original["scope"] = NestedScope;
        original["phase"] = "tasks";
        Assert.True(JsonNode.DeepEquals(original, reviewed["evidence"]![address]));
        Assert.Equal(7d, reviewed["evidence"]![address]!["outputs"]![0]!["value"]!["number"]!.GetValue<double>());
        Assert.True(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(reviewed, "/evidence/" + address, compact: true));
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public void HistoricalPolicyKeepsTheExactProducerOnlyShapeAndNoGenerationDirectory()
    {
        var state = Ready(NestedScope);
        state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        state.Construction!.Limits.PhasePolicy = null;
        state.Construction.Candidate!.ImportedTaskIds = ["nested_value"];
        var expected = new JsonObject
        {
            ["count"] = 1,
            ["items"] = new JsonArray(new JsonObject { ["address"] = "task:nested_value", ["scopeCount"] = 0, ["scopes"] = new JsonArray() })
        };
        Assert.True(JsonNode.DeepEquals(expected, Navigation(state, review: true)));
        Assert.Null(Context(state, review: false)["taskEvidence"]);
        state.Construction.Compact!.PresentationVersion = null;
        Assert.Null(Context(state, review: true)["taskEvidence"]);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task IssuedProducerOnlyReviewResumesExactBytesWithoutRebuildingSiblingContext(int presentationVersion)
    {
        var state = Ready(NestedScope, presentationVersion);
        Stage(state, ["nested_value"]);
        var construction = state.Construction!;
        var pending = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false);
        pending.Context["taskEvidence"] = new JsonObject
        {
            ["count"] = 1,
            ["items"] = new JsonArray(new JsonObject { ["address"] = "task:nested_value", ["scopeCount"] = 0, ["scopes"] = new JsonArray() })
        };
        var prompt = pending.Request.Prompt[..(pending.Request.Prompt.LastIndexOf('\n') + 1)] + pending.Context.ToJsonString();
        construction.PendingReviewContext = pending.Context;
        construction.PendingReviewHash = PlanningGraphCompiler.Fingerprint(PlanningConstruction.LocalPurpose + "\n" +
            construction.Candidate!.Hash + "\n" + pending.Context.ToJsonString());
        var interrupted = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(state, interrupted,
            PlanningConstruction.LocalPurpose, prompt, pending.Request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct,
            construction.Limits.ReviewOutputTokens));
        var saved = PlannerFixture.Clone(interrupted.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
        var exact = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var retainedContext = saved.Construction!.PendingReviewContext!.ToJsonString();
        var retainedHash = saved.Construction.PendingReviewHash;
        var calls = saved.ModelCalls;
        var accepted = PlanningConstruction.Hash(saved.Plan);
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(retainedContext, saved.Construction.PendingReviewContext!.ToJsonString());
            Assert.Equal(retainedHash, saved.Construction.PendingReviewHash);
            Assert.Equal(accepted, PlanningConstruction.Hash(saved.Plan));
            Assert.Null(saved.Construction.PendingReviewContext!["taskEvidence"]!["scopeOwner"]);
            var response = new JsonObject { ["findings"] = new JsonArray(), ["evidenceRequests"] = null,
                ["uncertainty"] = "Runtime execution remains unverified." };
            if (presentationVersion == 4) response["inconclusive"] = null;
            return new() { Json = response };
        } };
        var result = await new HybridWorkflowPlanner().AdvanceAsync(saved, new() { ExpectedRevision = saved.Revision }, recovery, PlannerFixture.Ct);
        Assert.Empty(result.Diagnostics);
        Assert.Single(recovery.Calls);
        Assert.Equal(calls, result.ModelCalls);
        Assert.Equal(0, result.ReplanAttempts);
        Assert.Null(result.PendingCall);
        Assert.Single(result.Construction!.Checkpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptySelectedScopeExposesItsBoundedOwnerWithoutClaimingItsResult(bool review)
    {
        var state = Ready(NestedScope, 4);
        var owner = state.Plan!.Root.Tasks.Single(task => task.Id == "container");
        owner.Objective = string.Concat(Enumerable.Repeat("Retain the cleanup boundary \"汉字😀\". ", 40));
        owner.Body!.Tasks.Clear(); owner.Body.Always.Clear();
        state.Construction!.OpenScopes = ["/root", NestedScope];
        Stage(state);
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = Context(state, review);
        var navigation = context["taskEvidence"]!;
        Assert.Empty(navigation["items"]!.AsArray());
        Assert.Equal(0, navigation["count"]!.GetValue<int>());
        var entry = navigation["scopeOwner"]!;
        Assert.Equal("container", entry["task"]!.ToString());
        Assert.Equal("sequence", entry["kind"]!.ToString());
        Assert.Equal("/root", entry["scope"]!.ToString());
        Assert.Equal("task:container", entry["address"]!.ToString());
        Assert.Null(entry["body"]);
        Assert.False(entry["objectiveComplete"]!.GetValue<bool>());
        Assert.StartsWith(entry["objective"]!.ToString(), owner.Objective, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(entry["objective"]!.ToJsonString()), 1, 204);
        Assert.Empty(context["interfaces"]!.AsArray());
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/taskEvidence/scopeOwner", compact: true));
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        if (!review)
        {
            var request = PlanningConstruction.DraftRequest(state, repair: false);
            var choices = request.StructuredOutputSchema!["properties"]!["evidenceRequests"]!;
            foreach (var address in new[] { entry["address"]!.ToString(), entry["objectiveEvidence"]!.ToString() })
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(new JsonArray(address), choices));
        }
        PlanningConstruction.LoadEvidence(state, [entry["address"]!.ToString()]);
        Assert.Equal(owner.Objective, state.Construction.Evidence!["task:container"]!["objective"]!.ToString());
        Assert.Equal(PlanningGraphCompiler.Fingerprint(owner.Objective),
            PlanningCompactConstruction.LoadEvidence(state, entry["objectiveEvidence"]!.ToString(), out var original)
                ? original!["version"]!.ToString() : null);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("/root", false)]
    [InlineData("/root", true)]
    [InlineData("/groups/worker/body", false)]
    [InlineData("/groups/worker/body", true)]
    public void RootAndReusableGroupScopesDoNotInventATaskOwner(string scope, bool review)
    {
        var current = Ready(scope, 4);
        var historical = Ready(scope, 3);
        Assert.Null(Context(current, review)["taskEvidence"]?["scopeOwner"]);
        Assert.Equal(Context(historical, review)["taskEvidence"]!.ToJsonString(), Context(current, review)["taskEvidence"]!.ToJsonString());
    }

    [Fact]
    public void DiscoveryExposesOnlyTheSelectedOwnerAndItsExactEvidenceBeforeKindBinding()
    {
        var state = DiscoveryState(NestedScope);
        var owner = state.Plan!.Root.Tasks.Single(task => task.Id == "container");
        owner.Objective = string.Concat(Enumerable.Repeat("Retain the cleanup boundary \"汉字😀\". ", 40));
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        var entry = context["scopeOwner"]!;
        Assert.Null(context["step"]!["kind"]);
        Assert.True(JsonNode.DeepEquals(Context(state, review: false)["taskEvidence"]!["scopeOwner"], entry));
        Assert.Equal("task:container", entry["address"]!.ToString());
        Assert.Equal("/root", entry["scope"]!.ToString());
        Assert.False(entry["objectiveComplete"]!.GetValue<bool>());
        Assert.InRange(Encoding.UTF8.GetByteCount(entry["objective"]!.ToJsonString()), 1, 204);
        Assert.Null(entry["body"]);
        Assert.Null(context["taskEvidence"]);
        Assert.DoesNotContain("nested_value", context.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("root_value", context.ToJsonString(), StringComparison.Ordinal);
        var choices = PlanningCompactConstruction.DiscoverySchema(state)["properties"]!["evidenceRequests"]!;
        foreach (var address in new[] { entry["address"]!.ToString(), entry["objectiveEvidence"]!.ToString() })
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(new JsonArray(address), choices));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(new JsonArray("task:nested_value"), choices));
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        PlanningConstruction.LoadEvidence(state, [entry["address"]!.ToString(), entry["objectiveEvidence"]!.ToString()]);
        Assert.Equal(owner.Objective, state.Construction!.Evidence!["task:container"]!["objective"]!.ToString());
        Assert.Equal(PlanningGraphCompiler.Fingerprint(owner.Objective),
            state.Construction.Evidence[entry["objectiveEvidence"]!.ToString()]!["version"]!.ToString());
        Assert.Null(state.Construction.Compact!.CurrentStep!.Kind);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public void UnrelatedTasksAndOwnerChildrenDoNotGrowTheDiscoveryRequest()
    {
        var state = DiscoveryState(NestedScope);
        var before = DiscoveryRequest(state);
        var owner = state.Plan!.Root.Tasks.Single(task => task.Id == "container");
        for (var index = 0; index < 100; index++)
        {
            state.Plan.Root.Tasks.Add(Value("unrelated_" + index));
            owner.Body!.Tasks.Add(Value("sibling_" + index));
            state.Plan.Groups.Add(new() { Id = "unrelated_group_" + index, Body = new() { Tasks = [Value("group_task_" + index)] } });
        }
        var after = DiscoveryRequest(state);
        Assert.Equal(before.Prompt, after.Prompt);
        Assert.Equal(before.StructuredOutputSchema!.ToJsonString(), after.StructuredOutputSchema!.ToJsonString());
        var measurement = PlanningRequestMeasurements.Measure(after, PlanningCompactConstruction.DiscoveryPurpose, 12000);
        Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(component => component.SerializedBytes));
    }

    [Theory]
    [InlineData(NestedScope, 3)]
    [InlineData("/root", 4)]
    [InlineData("/groups/worker/body", 4)]
    public void DiscoveryDoesNotInventOwnersOrChangeHistoricalPresentation(string scope, int presentationVersion)
    {
        var state = DiscoveryState(scope, presentationVersion);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.False(context.ContainsKey("scopeOwner"));
        var request = DiscoveryRequest(state);
        state.Plan!.Root.Tasks.Single(task => task.Id == "container").Objective = "Changed navigation outside this presentation.";
        Assert.Equal(request.Prompt, DiscoveryRequest(state).Prompt);
        Assert.Equal(request.StructuredOutputSchema!.ToJsonString(), DiscoveryRequest(state).StructuredOutputSchema!.ToJsonString());
        state.Plan = null;
        Assert.False(PlanningCompactConstruction.DiscoveryContext(state).ContainsKey("scopeOwner"));
    }

    [Fact]
    public async Task AlreadyIssuedDiscoveryWithoutOwnerResumesExactBytesWithoutNewAccounting()
    {
        var state = DiscoveryState(NestedScope);
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        context.Remove("scopeOwner");
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        foreach (var alternative in schema["properties"]!["evidenceRequests"]!["anyOf"]!.AsArray())
            if (alternative?["items"]?["enum"] is JsonArray choices)
                foreach (var choice in choices.Where(choice => choice?.ToString() == "task:container").ToArray())
                    choices.Remove(choice);
        var issued = PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state), context, schema,
            PlanningCompactConstruction.DiscoveryPurpose);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = issued };
        state.ModelCalls = 1;
        state = PlannerFixture.Clone(state);
        var saved = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var retained = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(saved, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = new JsonObject { ["evidenceRequests"] = new JsonArray("requirement:req_0"),
                ["binding"] = null, ["discoveryRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null } };
        } };
        var next = DiscoveryRequest(state);
        Assert.NotEqual(saved, JsonSerializer.Serialize(next, PlanningJsonContext.Default.LLMRequest));
        await PlanningModelCalls.CallAsync(state, runtime, PlanningCompactConstruction.DiscoveryPurpose,
            next.Prompt, next.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, 8192);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(runtime.Calls);
        Assert.Null(state.PendingCall);
        Assert.Equal(retained, PlanningConstruction.Hash(state.Plan));
    }

    private static PlanningSession DiscoveryState(string scope, int presentationVersion = 4)
    {
        var state = Ready(scope, presentationVersion);
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = null;
        state.Construction.Compact.CurrentStep.DiscoveryComplete = false;
        return state;
    }

    private static LLMRequest DiscoveryRequest(PlanningSession state) => PlanningCompactConstruction.Request(
        PlanningCompactConstruction.DiscoveryGuidance(state), PlanningCompactConstruction.DiscoveryContext(state),
        PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose);

    private static PlanningSession Ready(string scope, int presentationVersion = 3)
    {
        const string requirement = "Preserve the requested values and complete their declared processing.";
        var state = new PlanningSession { Request = new() { TenantId = "accepted-task-navigation", Prompt = requirement } };
        PlanningSessionCreation.Capture(state);
        // Directory-only fixtures deliberately contain unresolved synthetic
        // plans; retain their protocol instead of asserting verified v4 facts.
        state.Request.Options["construction_presentation_version"] = presentationVersion;
        state.Construction!.Compact!.PresentationVersion = presentationVersion;
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        PlanningCompactConstruction.InitializeSources(state);
        var construction = state.Construction!;
        var compact = construction.Compact!;
        foreach (var source in compact.Sources) source.Indexed = true;
        compact.RequirementSources["req_0"] = [compact.Sources[0].Id];
        state.Requirements = new() { Outcomes = [new("req_0", requirement)], Inputs = [], Outputs = [] };
        construction.Requirements = [new() { Id = "req_0" }];
        state.Catalog = new();
        state.Plan = new()
        {
            Root = new() { Tasks = [Value("root_value"), new() { Id = "container", Kind = "sequence", Objective = "Process the declared values.",
                Body = new() { Tasks = [Value("nested_value"), new() { Id = "child_container", Kind = "sequence", Objective = "Keep its child isolated.",
                    Body = new() { Tasks = [Value("child_value")] } }], Always = [Value("cleanup_value")] } }] },
            Groups = [new() { Id = "worker", Body = new() { Tasks = [Value("group_value")] } }]
        };
        construction.OpenScopes = new TaskPlanSymbols(state.Plan).Scopes.Select(site => site.Path).ToList();
        compact.CurrentStep = new() { Id = "selected", Scope = scope, Kind = "value", Objective = "Retain the next declared value.",
            RequirementIds = ["req_0"], DiscoveryComplete = true };
        Stage(state);
        return state;
    }

    private static PlanningSession OperationState(string scope, int presentationVersion = 5)
    {
        var state = Ready(scope, presentationVersion);
        var capability = new PlanningCapability { Id = "create_resource", Version = "v1", Kind = "tool", StepType = "mcp.call",
            Server = "resource", Method = "create", EffectKind = "write", Description = "Create a resource at the exact target.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["target"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("target"), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["resource"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("resource"), ["additionalProperties"] = false } };
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"], Capabilities = [capability] };
        state.Discovery.Resolved = [capability];
        var target = new TaskPlanSymbols(state.Plan!).Scopes.Single(site => site.Path == scope).Source;
        target.Tasks.Add(new() { Id = "accepted_operation", Kind = "operation", Operation = capability.Id,
            Objective = "Create the resource at its declared target.", DependsOn = [target.Tasks[0].Id],
            Condition = new() { Kind = "boolean", Boolean = true }, Requires = new() { Kind = "boolean", Boolean = true },
            Inputs = [new("target", new() { Kind = "string", Text = "workspace/完整\"target\"\n" })] });
        StageOperation(state);
        return state;
    }

    private static void StageOperation(PlanningSession state)
    {
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "operation"; step.OperationIds = ["create_resource"];
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = step.Scope, RequirementIds = ["req_0"],
            Tasks = [new() { Id = "candidate", Kind = "operation", Operation = "create_resource", Objective = "Create the next resource.",
                Inputs = [new("target", new() { Kind = "string", Text = "workspace/next" })] }] });
    }

    private static void Stage(PlanningSession state, List<string>? dependencies = null)
    {
        var step = state.Construction!.Compact!.CurrentStep!;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        var task = Value("candidate");
        task.DependsOn = dependencies ?? [];
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
            { Scope = step.Scope, Tasks = [task], RequirementIds = ["req_0"] });
    }
    private static PlanTask Value(string id) => new() { Id = id, Kind = "value", Objective = "Preserve the declared value.",
        Outputs = [new("value", new() { Kind = "number", Number = 7 })] };
    private static JsonObject Context(PlanningSession state, bool review) => PlanningCompactConstruction.Context(state, review);
    private static JsonObject Navigation(PlanningSession state, bool review) => Context(state, review)["taskEvidence"]!.AsObject();
    private static string[] Addresses(JsonObject navigation) => navigation["items"]!.AsArray().Select(item => item!["address"]!.ToString()).ToArray();
    private static PlanningRequestMeasurement Measure(PlanningSession state, bool review)
    {
        var purpose = review ? PlanningConstruction.LocalPurpose : PlanningConstruction.DraftPurpose;
        var request = review ? HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Request :
            PlanningCompactConstruction.Request(PlanningCompactConstruction.DraftGuidance(state), Context(state, review: false),
                PlanningCompactConstruction.DraftSchema(state), purpose);
        return PlanningRequestMeasurements.Measure(request, purpose, review ? 8000 : 16000);
    }
}
