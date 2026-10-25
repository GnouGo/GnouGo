using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactAcceptedTaskNavigationTests
{
    private const string NestedScope = "/tasks/container/body";

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
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, ["schema:allocate@v1:/output/properties/handle"]));
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
