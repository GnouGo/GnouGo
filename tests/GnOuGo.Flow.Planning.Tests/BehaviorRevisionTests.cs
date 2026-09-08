using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BehaviorRevisionTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RetryRecognizesOnlyAnExactReviewedDependencyRevision(bool revisedDependency, bool topologyFinding)
    {
        var state = Session(PlanningStatus.Recovery); state.Preparation = Preparation(); state.IntentChecked = true;
        state.PreviousGraph = Graph(); state.Graph = Graph(); state.BehaviorPlan = BehaviorPlan();
        var baseline = BehaviorPlan(); baseline.Workflows[0].Steps[0].InputDependencies = ["unused"];
        if (!revisedDependency) state.BehaviorPlan.Workflows[0].Steps[0].InputDependencies = ["unused"];
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan); var approved = state.ApprovedBehaviorHash;
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.PreviousGraph), "semantic_review", 9, false,
            [new("REVISE_BEHAVIOR", "/workflows/0/steps/0/behavior", "Revise the evidenced contract.", ValidationStage: topologyFinding ? null : "business_input_review")]));
        state.Attempts.Add(new(PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan), "behavior_dependency_revision", 1, true,
            PlanningBehaviorRevisions.DependencyChanges(baseline, state.BehaviorPlan)));
        // Round trip proves that the revision evidence survives process restart.
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        var runtime = new FakeRuntime();
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "retry", ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Empty(runtime.Phases);
        if (revisedDependency && !topologyFinding) Assert.Equal(approved, state.ApprovedBehaviorHash);
        else Assert.Null(state.ApprovedBehaviorHash);
    }

    [Theory]
    [InlineData("source", "Read a page", false)]
    [InlineData("entree", "Lire une page", false)]
    [InlineData("source", "Read a page", true)]
    [InlineData("entree", "Lire une page", true)]
    public void NewLoopReceivesADeterministicKeyWhileItsOriginalProducerIdentityIsPreserved(string key, string purpose, bool nested)
    {
        var baseline = BehaviorPlan(); var source = baseline.Workflows[0].Steps[0]; source.Key = key;
        if (nested) baseline.Workflows[0].Steps = [new() { Key = "parent", Kind = "sequence", Purpose = "Collect", Steps = [source] }];
        var unchanged = PlanningBehaviorPlans.Fingerprint(baseline);
        var child = JsonSerializer.Deserialize(JsonSerializer.Serialize(source, PlanningJsonContext.Default.PlanningBehaviorNode), PlanningJsonContext.Default.PlanningBehaviorNode)!;
        child.Purpose = purpose;
        var wrapper = new PlanningBehaviorNode { Key = key, Kind = "loop", Purpose = "Repeat until complete", InputDependencies = [], Steps = [child] };
        var candidate = nested ? new PlanningBehaviorNode { Key = "parent", Kind = "sequence", Purpose = "Collect", Steps = [wrapper] } : wrapper;
        var coordinate = "main/" + (nested ? "parent" : key);
        var patch = new JsonObject { [coordinate] = new JsonObject { ["action"] = "replace", ["node"] = JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.PlanningBehaviorNode) } };
        var result = PlanningBehaviorRevisions.Apply(baseline, patch);
        var loop = nested ? result.Workflows[0].Steps[0].Steps[0] : result.Workflows[0].Steps[0];
        Assert.NotEqual(key, loop.Key); Assert.Equal("loop", loop.Kind);
        Assert.Equal(key, Assert.Single(loop.Steps).Key); Assert.Equal(source.CapabilityId, loop.Steps[0].CapabilityId);
        Assert.Equal(PlanningBehaviorPlans.Fingerprint(result), PlanningBehaviorPlans.Fingerprint(PlanningBehaviorRevisions.Apply(baseline, patch)));
        Assert.Equal(unchanged, PlanningBehaviorPlans.Fingerprint(baseline));
        // An altered producer cannot use the identity-preservation exception.
        child.CapabilityId = "different";
        patch[coordinate]!["node"] = JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.PlanningBehaviorNode);
        var invalid = PlanningBehaviorRevisions.Apply(baseline, patch);
        Assert.Equal(key, (nested ? invalid.Workflows[0].Steps[0].Steps[0] : invalid.Workflows[0].Steps[0]).Key);
        Assert.Contains(PlanningBehaviorPlans.Validate(invalid, Preparation()), d => d.Message.Contains("keys must be unique", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryReassessesRetainedPresentationOnlyRepairAndPreservesARealLoop(bool wrapped)
    {
        var state = Session(PlanningStatus.Recovery); state.Preparation = Preparation(); state.IntentChecked = true;
        state.PreviousGraph = Graph(); state.Graph = Graph(); state.BehaviorPlan = BehaviorPlan();
        state.Graph.Workflows[0].Steps[0].Purpose = "Now repeat every item";
        state.BehaviorPlan.Workflows[0].Steps[0].Purpose = "Now repeat every item";
        if (wrapped)
        {
            state.Graph.Workflows[0].Steps = [new() { Key = "items", Type = "loop.sequential", Purpose = "Repeat", Steps = state.Graph.Workflows[0].Steps }];
            state.BehaviorPlan.Workflows[0].Steps = [new() { Key = "items", Kind = "loop", Purpose = "Repeat", InputDependencies = [], Steps = state.BehaviorPlan.Workflows[0].Steps }];
        }
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        var approved = state.ApprovedBehaviorHash;
        state.Answers.Add(new("Retained answer", new JsonObject { ["value"] = "yes" })); state.ActiveMilliseconds = 1234;
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.PreviousGraph), "semantic_review", 9, false,
            [new("CARDINALITY", "/workflows/0/steps/0/behavior", "Repeat for every entry."), new("ARGUMENT", "/workflows/0/steps/0/input", "Preserve the dynamic input.")]));
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        var runtime = new FakeRuntime();
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "retry", ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Empty(runtime.Phases); Assert.Single(state.Answers); Assert.True(state.ActiveMilliseconds >= 1234);
        if (wrapped) Assert.Equal(approved, state.ApprovedBehaviorHash);
        else
        {
            Assert.Equal(PlanningPhase.Behavior, state.CurrentPhase); Assert.Null(state.ApprovedBehaviorHash); Assert.Null(state.Graph);
            Assert.NotNull(state.PreviousGraph); Assert.Contains("actual structure", state.Feedback);
            Assert.Contains(state.Diagnostics, d => d.Code == "ARGUMENT");
        }
    }

    [Theory]
    [InlineData("Read every page", false)]
    [InlineData("Lire toutes les pages", false)]
    [InlineData("Read every page", true)]
    public async Task PresentationOnlyRevisionIsRepairedBeforeAnyReviewOrElaboration(string prose, bool exhaust)
    {
        var state = Session(PlanningStatus.Created); state.Preparation = Preparation(); state.IntentChecked = true;
        state.PreviousGraph = Graph(); state.BehaviorRevisionSource = BehaviorPlan(); state.Feedback = "Repeat the existing operation.";
        var original = PlanningBehaviorPlans.Fingerprint(state.BehaviorRevisionSource);
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.PreviousGraph), "semantic_review", 9, false,
            [new("CARDINALITY", "/workflows/0/steps/0/behavior", state.Feedback)]));
        var calls = 0;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_revision", phase);
            if (++calls == 2) Assert.Contains("changes presentation only", request.Prompt);
            var repair = calls == 2 && !exhaust;
            var node = repair ? new PlanningBehaviorNode { Key = "pages", Kind = "loop", Purpose = prose, InputDependencies = [] }
                : JsonSerializer.Deserialize(JsonSerializer.Serialize(state.BehaviorRevisionSource.Workflows[0].Steps[0], PlanningJsonContext.Default.PlanningBehaviorNode), PlanningJsonContext.Default.PlanningBehaviorNode)!;
            node.Purpose = prose;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["main/greeting"] = new JsonObject
                { ["action"] = repair ? "wrap_loop" : "replace", ["node"] = JsonSerializer.SerializeToNode(node, PlanningJsonContext.Default.PlanningBehaviorNode) } } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls); Assert.Null(state.ApprovedBehaviorHash); Assert.Null(state.Graph);
        Assert.Contains(state.Attempts, a => a.Diagnostics.Any(d => d.Code == "BEHAVIOR_PATCH_INVALID"));
        if (exhaust)
        {
            Assert.Equal(PlanningStatus.Recovery, state.Status);
            Assert.Equal(original, PlanningBehaviorPlans.Fingerprint(state.BehaviorRevisionSource!));
            Assert.Contains(state.Diagnostics, d => d.Code == "BEHAVIOR_PATCH_INVALID");
        }
        else
        {
            Assert.True(state.Status == PlanningStatus.BehaviorReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            Assert.Equal("loop", state.BehaviorPlan!.Workflows[0].Steps[0].Kind);
            Assert.Equal("greeting", Assert.Single(state.BehaviorPlan.Workflows[0].Steps[0].Steps).Key);
        }
    }

    [Fact]
    public async Task RevisionReceivesOwnedContractsWithoutUnrelatedCatalogPayloads()
    {
        var state = Session(PlanningStatus.Created); state.Preparation = Preparation(); state.IntentChecked = true;
        state.PreviousGraph = Graph(); state.BehaviorRevisionSource = BehaviorPlan(); state.Feedback = "Repeat the owned operation.";
        state.BehaviorRevisionSource.Workflows[0].Steps[0].CapabilityId = "owned";
        state.BehaviorRevisionSource.Workflows[0].Steps[0].OperationIds = ["required"];
        state.Preparation.Capabilities.Add(new() { Id = "owned", StepType = "set", OperationIds = ["required"], InputOperationIds = ["source"] });
        state.Preparation.Capabilities.Add(new() { Id = "incoming", StepType = "set", OperationIds = ["source"], Description = "Established producer boundary" });
        for (var i = 0; i < 15; i++) state.Preparation.Capabilities.Add(new() { Id = "unrelated" + i, StepType = "set", OperationIds = ["other" + i], Description = new string('x', 5000) });
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.PreviousGraph), "semantic_review", 9, false,
            [new("CARDINALITY", "/workflows/0/steps/0/behavior", state.Feedback)]));
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_revision", phase);
            Assert.DoesNotContain("unrelated", request.Prompt); Assert.Contains("Established producer boundary", request.Prompt);
            Assert.DoesNotContain("unrelated", request.StructuredOutputSchema!.ToJsonString());
            Assert.InRange(PlanningConstruction.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema.AsObject()), 1, 12000);
            return Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Single(runtime.Phases); Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT");
        Assert.Equal(17, state.Preparation!.Capabilities.Count);
    }

    [Theory]
    [InlineData("operation", "resource")]
    [InlineData("renamed", "entrée")]
    public void RepeatedBehaviorVocabulariesAreSharedWithoutChangingAllowedValues(string prefix, string input)
    {
        var prep = Preparation(); var plan = BehaviorPlan();
        plan.Workflows[0].Inputs.Add(new(input, "Required dynamic value", true));
        for (var i = 0; i < 16; i++) prep.Capabilities.Add(new() { Id = "cap" + i, StepType = "set", OperationIds = [prefix + "_" + i + "_" + new string('x', 35)] });
        var schema = PlanningBehaviorRevisions.Schema(prep, plan, [("main", "greeting")]);
        var expanded = (JsonObject)schema.DeepClone();
        Expand(expanded);
        Assert.True(schema.ToJsonString().Length < expanded.ToJsonString().Length * .8);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var node = new PlanningBehaviorNode { Key = "greeting", Purpose = "Retain the operation", OperationIds = [prep.Capabilities[0].OperationIds[0]], InputDependencies = [input] };
        var candidate = new JsonObject { ["main/greeting"] = new JsonObject { ["action"] = "replace", ["node"] = JsonSerializer.SerializeToNode(node, PlanningJsonContext.Default.PlanningBehaviorNode) } };
        Assert.Empty(PlanningContractValidation.ValidateInstance(candidate, schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(candidate, expanded));
        candidate["main/greeting"]!["node"]!["operationIds"]![0] = "invented";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(candidate, schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(candidate, expanded));
        void Expand(JsonNode? value)
        {
            if (value is JsonArray array) { foreach (var child in array) Expand(child); return; }
            if (value is not JsonObject obj) return;
            if (obj["$ref"]?.ToString() is "#/$defs/behaviorOperationId" or "#/$defs/behaviorInputName")
            {
                var definition = schema["$defs"]![obj["$ref"]!.ToString().Split('/').Last()]!.AsObject(); obj.Clear();
                foreach (var entry in definition) obj[entry.Key] = entry.Value?.DeepClone();
            }
            foreach (var entry in obj) Expand(entry.Value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusedIterationRevisionPreservesOtherNodesAndRequiresNewApproval(bool legacy)
    {
        var state = Session(PlanningStatus.Created); state.Preparation = Preparation(); state.IntentChecked = true;
        var plan = BehaviorPlan(); state.PreviousGraph = Graph();
        state.BehaviorRevisionSource = legacy ? null : plan;
        state.Feedback = "Repeat the greeting for the complete supplied collection.";
        state.Answers.Add(new("Retain the greeting?", new JsonObject { ["choice"] = "yes" }));
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.PreviousGraph), "semantic_review", 9, false,
            [new("CARDINALITY", "/workflows/0/steps/0/behavior", state.Feedback)]));
        var calls = 0;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_revision", phase); calls++;
            Assert.DoesNotContain("workflows", request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => p.Key));
            var wrapper = new PlanningBehaviorNode { Key = "iterate", Kind = "loop", Purpose = "Repeat for every entry", InputDependencies = [] };
            var patch = new JsonObject { ["main/greeting"] = new JsonObject { ["action"] = "wrap_loop", ["node"] = JsonSerializer.SerializeToNode(wrapper, PlanningJsonContext.Default.PlanningBehaviorNode) } };
            return Task.FromResult(new LLMResponse { Json = patch });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(PlanningStatus.BehaviorReview, state.Status);
        Assert.Null(state.ApprovedBehaviorHash); Assert.Null(state.ApprovedHash); Assert.Null(state.Graph);
        Assert.Equal("greeting", Assert.Single(Assert.Single(state.BehaviorPlan!.Workflows[0].Steps).Steps).Key);
        Assert.Single(state.Answers); Assert.NotNull(state.PreviousGraph);
    }

    [Fact]
    public async Task OutputCeilingIsPersistedAndDoesNotTriggerAnIdenticalWholePlanRequest()
    {
        var state = Session(PlanningStatus.Created); state.Preparation = Preparation(); state.IntentChecked = true;
        state.PreviousGraph = Graph(); state.BehaviorRevisionSource = BehaviorPlan(); state.Feedback = "Repeat the operation.";
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.PreviousGraph), "semantic_review", 9, false,
            [new("CARDINALITY", "/workflows/0/steps/0/behavior", state.Feedback)]));
        var calls = 0;
        var runtime = new FakeRuntime { OnCall = (_, _, _) => { calls++; return Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" }); } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(1, state.BehaviorAssessmentCalls); Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT");
        Assert.Contains(state.Attempts, a => a.Phase == "behavior_revision" && a.Diagnostics.Any(d => d.Code == "MODEL_OUTPUT_LIMIT"));
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "BEHAVIOR_SCHEMA_INVALID");
        Assert.Null(state.ApprovedBehaviorHash); Assert.NotNull(state.BehaviorRevisionSource);
    }

    [Fact]
    public void IdenticalNodeKeysInDifferentWorkflowsRemainIsolated()
    {
        var baseline = BehaviorPlan(); baseline.Workflows.Add(new() { Key = "other", Purpose = "Separate", Steps = [new() { Key = "greeting", Purpose = "Retain this node" }] });
        var wrapper = new PlanningBehaviorNode { Key = "iterate", Kind = "loop", Purpose = "Repeat the first node" };
        var patch = new JsonObject { ["main/greeting"] = new JsonObject { ["action"] = "wrap_loop", ["node"] = JsonSerializer.SerializeToNode(wrapper, PlanningJsonContext.Default.PlanningBehaviorNode) } };
        var revised = PlanningBehaviorRevisions.Apply(baseline, patch);
        Assert.Equal("iterate", revised.Workflows[0].Steps[0].Key);
        Assert.Equal("greeting", revised.Workflows[1].Steps[0].Key);
        Assert.Equal("Retain this node", revised.Workflows[1].Steps[0].Purpose);
    }

    [Fact]
    public void InvalidWrapperCannotDiscardOrDuplicateTheOriginalOperation()
    {
        var baseline = BehaviorPlan(); var original = PlanningBehaviorPlans.Fingerprint(baseline);
        var wrapper = new PlanningBehaviorNode { Key = "greeting", Kind = "loop", Purpose = "Invalid reuse" };
        var patch = new JsonObject { ["main/greeting"] = new JsonObject { ["action"] = "wrap_loop", ["node"] = JsonSerializer.SerializeToNode(wrapper, PlanningJsonContext.Default.PlanningBehaviorNode) } };
        Assert.Throws<InvalidOperationException>(() => PlanningBehaviorRevisions.Apply(baseline, patch));
        Assert.Equal(original, PlanningBehaviorPlans.Fingerprint(baseline));
    }
}
