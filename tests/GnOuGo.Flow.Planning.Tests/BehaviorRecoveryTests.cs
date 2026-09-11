using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BehaviorRecoveryTests
{
    [Theory]
    [InlineData("steps")]
    [InlineData("nested")]
    [InlineData("finally")]
    public async Task CompletedDecisionProducersDoNotShiftStagedRepairCoordinates(string placement)
    {
        var state = Behavior(); var preparation = state.Preparation!;
        preparation.Capabilities.Clear();
        preparation.Capabilities.Add(new() { Id = "decision", StepType = "decision.evaluate", Resolution = "native", Required = true, OperationIds = ["decide"] });
        preparation.Capabilities.Add(new() { Id = "other", StepType = "set", Resolution = "local", EffectKind = "none", OperationIds = ["other"] });
        var plan = BehaviorPlan(); var workflow = plan.Workflows[0]; workflow.OperationIds = ["decide", "other"];
        var routing = new PlanningBehaviorNode { Key = "route", Kind = "decision", Purpose = "Route the declared decision", OperationIds = ["decide"], CapabilityId = "other", InputDependencies = [],
            Outcomes = [new("yes", "Selected", false, []), new("default", "No action", true, [])] };
        workflow.Steps = [new() { Key = "retained", Kind = "operation", Purpose = "Retain the other operation", OperationIds = ["other"], CapabilityId = "other", InputDependencies = [] }];
        var path = placement == "nested" ? "/workflows/0/steps/0/steps/0/capabilityId" : "/workflows/0/" + placement + "/0/capabilityId";
        if (placement == "finally") workflow.Finally.Add(routing);
        else workflow.Steps.Insert(0, placement == "nested" ? new() { Key = "group", Kind = "sequence", Purpose = "Group the decision", InputDependencies = [], Steps = [routing] } : routing);
        state.BehaviorAssessment.Candidate = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        var runtime = new FakeRuntime { OnCall = (_, request, _) =>
        {
            Assert.Contains(path, request.Prompt);
            var variant = Assert.Single(request.StructuredOutputSchema!["properties"]!["patches"]!["items"]!["anyOf"]!.AsArray())!;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = variant["properties"]!["target"]!["enum"]![0]!.DeepClone(), ["value"] = null }) } });
        } };
        await new PlanningBehaviorAssessment(TimeProvider.System).AssessAsync(state, runtime, Ct);
        Assert.Equal(PlanningStatus.BehaviorReview, state.Status);
        Assert.Single(runtime.Requests); Assert.All(state.Attempts, attempt => Assert.True(attempt.Retained));
        Assert.Null(PlanningFieldPaths.Read(state.BehaviorAssessment.Candidate, path));
        Assert.Equal("other", PlanningBehaviorPlans.Enumerate(state.BehaviorPlan!.Workflows[0].Steps).Single(n => n.Key == "retained").CapabilityId);
        Assert.Single(PlanningBehaviorPlans.Enumerate(state.BehaviorPlan.Workflows[0].Steps.Concat(state.BehaviorPlan.Workflows[0].Finally)), n => n.CapabilityId == "decision");
    }

    [Fact]
    public void MissingCapabilityInsertionCannotInventOwnershipOrBusinessInputs()
    {
        var plan = BehaviorPlan(); plan.Workflows[0].Inputs.Add(new("request", "Runtime request", true));
        var candidate = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        var preparation = Preparation(); preparation.Capabilities.Add(new() { Id = "missing", StepType = "set", Resolution = "local", EffectKind = "none", OperationIds = ["owned"] });
        var source = PlanningSchemas.Behavior(preparation);
        var path = "/workflows/0/steps/" + plan.Workflows[0].Steps.Count;
        var targets = PlanningBehaviorPatches.Scope(candidate, source, [new("BEHAVIOR_CONTRACT_INVALID", path, "Missing implementation", Rule: "insert_behavior_node:missing")]);
        var previous = PlanningExactPatches.Schema(targets, source);
        var target = Assert.Single(targets);
        var node = candidate["workflows"]![0]!["steps"]![0]!.DeepClone();
        node["key"] = target.Schema["properties"]!["key"]!["enum"]![0]!.DeepClone();
        node["capabilityId"] = "missing"; node["operationIds"] = new JsonArray("owned"); node["inputDependencies"] = new JsonArray("owned");
        var patch = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = target.Id, ["value"] = node }) };
        Assert.Empty(PlanningContractValidation.ValidateInstance(patch, previous)); // Previously cost a failed behavior repair.
        PlanningBehaviorPatches.RestrictCapabilities(candidate, targets, preparation);
        var schema = PlanningExactPatches.Schema(targets, source);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(patch, schema));
        node["inputDependencies"] = new JsonArray("request");
        Assert.Empty(PlanningContractValidation.ValidateInstance(patch, schema));
        node["operationIds"] = new JsonArray();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(patch, schema));
        node["operationIds"] = new JsonArray("owned"); node["inputDependencies"] = new JsonArray("request", "request");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(patch, schema));
        Assert.True(JsonNode.DeepEquals(candidate, JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.PlanningBehaviorPlan)));
    }

    [Fact]
    public void BehaviorCapabilityRepairsExposeOnlyCompatibleOwnedImplementations()
    {
        var candidate = JsonSerializer.SerializeToNode(BehaviorPlan(), PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        candidate["workflows"]![0]!["steps"]![0]!["operationIds"] = new JsonArray("owned");
        var preparation = Preparation(); preparation.Capabilities.Clear();
        preparation.Capabilities.Add(new() { Id = "right", Resolution = "local", EffectKind = "none", StepType = "set", OperationIds = ["owned"] });
        preparation.Capabilities.Add(new() { Id = "wrong", Resolution = "local", EffectKind = "none", StepType = "set", OperationIds = ["different"] });
        preparation.Capabilities.Add(new() { Id = "confirmation", StepType = "human.input", OperationIds = ["owned"] });
        var prior = new JsonObject { ["type"] = new JsonArray("string", "null"), ["enum"] = new JsonArray("right", "wrong", "confirmation", null) };
        List<PlanningExactPatches.Target> targets = [new("field", "/workflows/0/steps/0/capabilityId", prior)];
        PlanningBehaviorPatches.RestrictCapabilities(candidate, targets, preparation);
        var schema = PlanningExactPatches.Schema(targets, new());
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var patch = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = "field", ["value"] = "right" }) };
        Assert.Empty(PlanningContractValidation.ValidateInstance(patch, schema));
        patch["patches"]![0]!["value"] = "wrong";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(patch, schema));
        patch["patches"]![0]!["value"] = "confirmation";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(patch, schema));
        patch["patches"]![0]!["value"] = null;
        Assert.Empty(PlanningContractValidation.ValidateInstance(patch, schema));
        Assert.True(PlanningJsonTransport.EstimateInputTokens("", schema) < PlanningJsonTransport.EstimateInputTokens("", PlanningExactPatches.Schema([targets[0] with { Schema = prior }], new())));

        var insertion = new PlanningExactPatches.Target("missing", "/workflows/0/steps/1",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["capabilityId"] = PlanningHoleRequests.Enum("right") } }, Add: true);
        targets.Add(insertion);
        var beforeInsertion = PlanningExactPatches.Schema(targets, new());
        PlanningBehaviorPatches.RestrictCapabilities(candidate, targets, preparation);
        Assert.Equal("field", Assert.Single(targets).Id); // Existing references precede potentially redundant additions.
        Assert.True(PlanningJsonTransport.EstimateInputTokens("", PlanningExactPatches.Schema(targets, new())) < PlanningJsonTransport.EstimateInputTokens("", beforeInsertion));
        List<PlanningExactPatches.Target> standalone = [insertion];
        PlanningBehaviorPatches.RestrictCapabilities(candidate, standalone, preparation);
        Assert.Single(standalone); // An independently missing implementation still permits its exact insertion.

        targets = [targets[0] with { Schema = prior }, new("ownership", "/workflows/0/steps/0/operationIds/0", PlanningHoleRequests.Enum("owned", "different"))];
        PlanningBehaviorPatches.RestrictCapabilities(candidate, targets, preparation);
        Assert.True(JsonNode.DeepEquals(prior, targets[0].Schema)); // Explicit companion ownership changes govern the joint domain.
        targets = [targets[0] with { Schema = PlanningHoleRequests.Enum("wrong") }];
        Assert.Throws<PlanningHoleUnavailableException>(() => PlanningBehaviorPatches.RestrictCapabilities(candidate, targets, preparation));
    }

    [Fact]
    public void RepeatedExactPatchContractsAreSharedWithoutExpandingEditPermissions()
    {
        var values = Enumerable.Range(0, 30).Select(i => "declared_operation_" + i).ToArray();
        var field = PlanningHoleRequests.Enum(values);
        var targets = Enumerable.Range(0, 20).Select(i => new PlanningExactPatches.Target("f" + i, "/field" + i, field.DeepClone().AsObject())).ToArray();
        var source = new JsonObject { ["$defs"] = new JsonObject { ["patchField0"] = PlanningHoleRequests.Type("boolean") } };
        var original = source.DeepClone();
        var schema = PlanningExactPatches.Schema(targets, source);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var shared = Assert.Single(schema["$defs"]!.AsObject());
        Assert.Equal("patchField1", shared.Key);
        var expanded = schema.DeepClone().AsObject();
        foreach (var variant in expanded["properties"]!["patches"]!["items"]!["anyOf"]!.AsArray())
            variant!["properties"]!["value"] = shared.Value!.DeepClone();
        expanded.Remove("$defs");
        Assert.True(PlanningJsonTransport.EstimateInputTokens("", expanded) - PlanningJsonTransport.EstimateInputTokens("", schema) > 1000);
        var response = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = "f0", ["value"] = values[0] }) };
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, schema));
        response["patches"]![0]!["value"] = "undeclared";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, schema));
        response["patches"]![0]!["value"] = values[0]; response["patches"]![0]!["target"] = "outside";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, schema));
        Assert.True(JsonNode.DeepEquals(original, source));
    }

    [Fact]
    public async Task TruncatedInitialBehaviorRetriesConsumeTheResponseGateAllowance()
    {
        var state = Behavior(); state.Request.MaxRepairsPerWorkflowGate = 1;
        var runtime = new FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" }) };
        state = await HoleSessionTests.Advance(state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.Single(runtime.Requests);
        state = await HoleSessionTests.Advance(state, runtime, "retry");
        state = await HoleSessionTests.Advance(state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.Equal(2, runtime.Requests.Count);
        var allowance = Assert.Single(state.RepairAllowances); Assert.Equal("$plan", allowance.WorkflowKey); Assert.Equal(PlanningGates.Response, allowance.Gate); Assert.Equal(1, allowance.Attempts);
        Assert.Equal(2, Assert.Single(state.GateProgress).Failures);
        state = await HoleSessionTests.Advance(PlanningContext.Clone(state), runtime, "retry");
        state = await HoleSessionTests.Advance(state, runtime);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPAIR_EXHAUSTED"); Assert.Equal(2, runtime.Requests.Count);
    }

    [Fact]
    public async Task InvalidBehaviorPatchRecordsAResponseGateFailure()
    {
        var state = Behavior(); state.BehaviorPlan = BehaviorPlan(); state.BehaviorPlan.Workflows[0].Steps[0].Purpose = "";
        var runtime = Responses(new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = "unknown", ["value"] = "new purpose" }) });
        state = await Send(state, runtime);
        Assert.Contains(state.Diagnostics, d => d.Code == "PATCH_INVALID");
        Assert.Equal(1, Assert.Single(state.GateProgress, g => g.Gate == PlanningGates.Response).Failures);
    }

    [Fact]
    public void RevisionContextCannotPromoteAnUnreviewedCandidateToBaselineEvidence()
    {
        var state = Behavior(); state.Request.Baseline = Graph();
        state.BehaviorRevision = new() { Text = "Change the result" };
        state.BehaviorAssessment.Candidate = new() { ["summary"] = "Invented external authorization" };
        Assert.DoesNotContain("Invented external authorization", PlanningContext.BaselineText(state));
        state.BehaviorPlan = BehaviorPlan(); state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        Assert.Equal(JsonSerializer.Serialize(state.BehaviorPlan, PlanningJsonContext.Default.PlanningBehaviorPlan), PlanningContext.BaselineText(state));
    }

    [Fact]
    public void HumanRevisionCanInsertOnePortWithoutReplacingTheCollection()
    {
        var candidate = JsonSerializer.SerializeToNode(BehaviorPlan(), PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        var schema = PlanningSchemas.Behavior(Preparation());
        var target = Assert.Single(PlanningBehaviorPatches.Scope(candidate, schema,
            [new("BEHAVIOR_REVISION_REQUIRED", "/workflows/0/inputs/0", "Add the requested runtime input", Rule: "revision_add")]));
        Assert.True(target.Add);
        var response = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = target.Id,
            ["value"] = new JsonObject { ["name"] = "source", ["description"] = "Required runtime source", ["required"] = true } }) };
        var result = PlanningExactPatches.Apply(candidate, response, [target], PlanningExactPatches.Schema([target], schema));
        Assert.Equal("source", result["workflows"]![0]!["inputs"]![0]!["name"]!.ToString());
        Assert.True(JsonNode.DeepEquals(candidate["workflows"]![0]!["steps"], result["workflows"]![0]!["steps"]));
        Assert.Empty(candidate["workflows"]![0]!["inputs"]!.AsArray());
        Assert.Empty(PlanningBehaviorPatches.Scope(candidate, schema,
            [new("BEHAVIOR_REVISION_REQUIRED", "/workflows/0/inputs", "Cannot replace a collection", Rule: "revision_add")]));
    }

    [Fact]
    public void BehaviorRemovalDoesNotExposeOverlappingDescendantEdits()
    {
        var candidate = JsonSerializer.SerializeToNode(BehaviorPlan(), PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        var targets = PlanningBehaviorPatches.Scope(candidate, PlanningSchemas.Behavior(Preparation()),
            [new("BEHAVIOR_REVISION_REQUIRED", "/workflows/0/steps/0", "Remove action", Rule: "revision_remove"),
             new("BEHAVIOR_REVISION_REQUIRED", "/workflows/0/steps/0/purpose", "Revise action", Rule: "revision_replace")]);
        var target = Assert.Single(targets); Assert.True(target.Remove); Assert.Equal("/workflows/0/steps/0", target.Path);
        var context = PlanningBehaviorRevision.Context(candidate);
        Assert.Equal(new[] { "anchors", "fields" }, context.Select(p => p.Key));
        Assert.All(context["fields"]!.AsObject().SelectMany(p => p.Value!.AsObject()), p => Assert.IsType<JsonArray>(p.Value));
        Assert.DoesNotContain("\"steps\":", context.ToJsonString());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningPreparation Catalog(string server = "opaque-host", string method = "opaque-tool")
    {
        var preparation = Preparation();
        preparation.Capabilities.Add(new()
        {
            Id = "producer",
            StepType = "mcp.call",
            Server = server,
            Method = method,
            Kind = "tool",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"content":{"type":"string"}},"required":["content"],"additionalProperties":false}""")!.AsObject()
        });
        return preparation;
    }
    private static PlanningGraph Candidate(string pointer = "/content")
    {
        var graph = Graph();
        graph.Workflows[0].Steps[0] = new() { Key = "greeting", Type = "mcp.call", CapabilityId = "producer" };
        graph.Workflows[0].Outputs[0].Schema = new() { CapabilityId = "producer", SchemaPointer = pointer };
        graph.Workflows[0].Outputs[0].Value.Path = ["content"];
        return graph;
    }
    private static PlanningSnapshot Behavior(PlanningGraph? retained = null, string status = PlanningStatus.Created)
    {
        var state = Session(status); state.Intent.Checked = true; state.Preparation = Catalog();
        state.CurrentPhase = PlanningPhase.Behavior; state.Graph = retained;
        return state;
    }
    private static Task<PlanningSnapshot> Send(PlanningSnapshot state, FakeRuntime runtime, string kind = "advance", string? text = null)
        => new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = kind, ExpectedRevision = state.Revision, ArtifactHash = state.ArtifactHash, Text = text }, runtime, Ct);
    private static JsonNode Json(PlanningGraph graph) => JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningGraph)!;
    private static FakeRuntime Responses(params JsonNode[] responses)
    {
        var count = 0;
        return new() { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { Json = responses[Math.Min(count++, responses.Length - 1)].DeepClone() }) };
    }

    [Fact]
    public async Task HumanRevisionLocatesThenPatchesRetainedBehaviorBeforeRenewedReview()
    {
        var state = Behavior(); state.BehaviorPlan = BehaviorPlan(); state.Status = PlanningStatus.BehaviorReview;
        state.ArtifactHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        var revised = await Send(state, new(), "revise", "Describe the greeting as formal.");
        Assert.NotNull(revised.BehaviorAssessment.Candidate); Assert.Null(revised.ApprovedBehaviorHash);
        revised.Preparation = state.Preparation; revised.Intent.Checked = true;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            if (phase == "behavior_revision_scope")
            {
                var context = JsonNode.Parse(request.Prompt![ (request.Prompt.IndexOf("Coordinates:\n", StringComparison.Ordinal) + "Coordinates:\n".Length)..])!.AsObject();
                var anchor = context["anchors"]!.AsObject().Single(p => p.Value![1]!.ToString() == "greeting").Key;
                var target = context["fields"]!["replace"]!.AsObject().Single(p => p.Value![0]!.ToString() == anchor + "/purpose").Key;
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["target"] = target, ["evidence"] = "formal" }) } });
            }
            Assert.Equal("behavior_repair", phase);
            var variant = Assert.Single(request.StructuredOutputSchema!["properties"]!["patches"]!["items"]!["anyOf"]!.AsArray());
            var id = variant!["properties"]!["target"]!["enum"]![0]!.ToString();
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = id, ["value"] = "Return a formal greeting" }) } });
        } };
        revised = await Send(revised, runtime);
        revised = await Send(revised, runtime);
        Assert.Equal(PlanningStatus.BehaviorReview, revised.Status);
        Assert.Equal("greeting", revised.BehaviorPlan!.Workflows[0].Steps[0].Key);
        Assert.Equal("Return a formal greeting", revised.BehaviorPlan.Workflows[0].Steps[0].Purpose);
        Assert.Equal(["behavior_revision_scope", "behavior_repair"], runtime.Phases);
        Assert.Single(revised.RepairAllowances);
    }

    [Fact]
    public async Task RevisionCoordinatesShareOperationLabelsAndUseScopedIdsWithFewerTokens()
    {
        var state = Behavior(); var plan = BehaviorPlan();
        var prototype = JsonSerializer.SerializeToNode(plan.Workflows[0].Steps[0], PlanningJsonContext.Default.PlanningBehaviorNode)!;
        for (var i = 1; i < 24; i++)
        {
            var node = JsonSerializer.Deserialize(prototype, PlanningJsonContext.Default.PlanningBehaviorNode)!;
            node.Key = "action_" + i; plan.Workflows[0].Steps.Add(node);
        }
        state.BehaviorAssessment.Candidate = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        state.BehaviorRevision = new() { Text = "Change the description." };
        var original = state.BehaviorAssessment.Candidate.DeepClone();
        var runtime = new FakeRuntime { OnCall = (_, request, _) =>
        {
            var context = PlanningBehaviorRevision.Context(state.BehaviorAssessment.Candidate);
            var oldFields = new JsonObject();
            var oldIds = new JsonArray();
            foreach (var operation in context["fields"]!.AsObject())
                foreach (var field in operation.Value!.AsObject())
                {
                    var id = "r_" + PlanningGraphCompiler.Fingerprint(field.Key)[..16];
                    oldIds.Add(id);
                    oldFields[id] = new JsonArray(field.Value![0]!.DeepClone(), JsonValue.Create(operation.Key), field.Value[1]?.DeepClone());
                }
            var oldSchema = request.StructuredOutputSchema!.DeepClone().AsObject();
            oldSchema["properties"]!["fields"]!["items"]!["properties"]!["target"]!["enum"] = oldIds;
            var oldContext = new JsonObject { ["anchors"] = context["anchors"]!.DeepClone(), ["fields"] = oldFields };
            var prefix = request.Prompt![..(request.Prompt.IndexOf("Coordinates:\n", StringComparison.Ordinal) + "Coordinates:\n".Length)];
            var before = PlanningJsonTransport.EstimateInputTokens(prefix + oldContext.ToJsonString(), oldSchema);
            var after = PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema.AsObject());
            Assert.True(before - after > 1000, $"Before {before}, after {after}");
            var chosen = context["fields"]!["replace"]!.AsObject().First(p => p.Value![0]!.ToString().EndsWith("/purpose", StringComparison.Ordinal)).Key;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["target"] = chosen, ["evidence"] = "description" }) } });
        } };
        await PlanningBehaviorRevision.LocateAsync(state, runtime, Ct);
        Assert.True(state.BehaviorRevision.Located);
        Assert.Single(state.BehaviorRevision.Fields);
        Assert.True(JsonNode.DeepEquals(original, state.BehaviorAssessment.Candidate));
    }

    [Fact]
    public async Task RevisionScopeReplaysThePersistedTargetDomainAgainstTheRetainedCandidate()
    {
        var state = Behavior();
        state.BehaviorAssessment.Candidate = JsonSerializer.SerializeToNode(BehaviorPlan(), PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        state.BehaviorRevision = new() { Text = "Revise this description." };
        LLMRequest? issued = null;
        var capture = new FakeRuntime { OnCall = (_, request, _) =>
        {
            issued = request;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["target"] = "r0", ["evidence"] = "description" }) } });
        } };
        await PlanningBehaviorRevision.LocateAsync(state, capture, Ct);
        var expectedPath = state.BehaviorRevision.Fields.Single().Path;
        state.BehaviorRevision.Located = false; state.BehaviorRevision.Fields.Clear();
        var schema = issued!.StructuredOutputSchema!.DeepClone().AsObject();
        var ids = schema["properties"]!["fields"]!["items"]!["properties"]!["target"]!["enum"]!.AsArray();
        for (var i = 0; i < ids.Count; i++) ids[i] = "retained_" + i;
        var call = PlanningModelCalls.Reserve(state, "behavior_revision_scope", "", PlanningModelCalls.Request(state, "Persisted coordinates", schema));
        var replay = new FakeRuntime { OnCall = (_, request, _) =>
        {
            Assert.Equal(call.Id, request.ClientRequestId);
            Assert.Equal("Persisted coordinates", request.Prompt);
            Assert.True(JsonNode.DeepEquals(schema, request.StructuredOutputSchema));
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["target"] = "retained_0", ["evidence"] = "description" }) } });
        } };
        await PlanningBehaviorRevision.LocateAsync(state, replay, Ct);
        Assert.Equal(expectedPath, Assert.Single(state.BehaviorRevision.Fields).Path);
        Assert.Empty(state.Construction.PendingCalls);
    }

    [Fact]
    public void WorkflowCallOwnershipProjectsFromAnExplicitUniqueOperationClaim()
    {
        var preparation = Preparation();
        preparation.Capabilities.Add(new() { Id = "call", Resolution = "local", Required = true, OperationIds = ["call_operation"] });
        var plan = BehaviorPlan();
        plan.Workflows[0].Steps = [new() { Key = "invoke", Kind = "workflow", WorkflowKey = "child", Purpose = "Invoke the child", OperationIds = ["call_operation"], InputDependencies = [] }];
        plan.Workflows.Add(new() { Key = "child", Purpose = "Reusable child" });
        PlanningBehaviorPlans.CompleteOwnership(plan, preparation);
        Assert.Equal(["call_operation"], plan.Workflows[0].OperationIds);
        Assert.DoesNotContain(PlanningBehaviorPlans.Validate(plan, preparation), d => d.Rule is "behavior_08" or "behavior_22");
        plan.Workflows[0].OperationIds.Clear();
        plan.Workflows[1].Steps.Add(new() { Key = "ambiguous", OperationIds = ["call_operation"] });
        PlanningBehaviorPlans.CompleteOwnership(plan, preparation);
        Assert.Empty(plan.Workflows[0].OperationIds);
        Assert.Empty(plan.Workflows[1].OperationIds);
    }

    [Fact]
    public async Task BehaviorRepairsUseFiveDurableAttemptsAndRetainUnrelatedIdentities()
    {
        var plan = BehaviorPlan(); plan.Workflows[0].Steps = Enumerable.Range(0, 6).Select(i => new PlanningBehaviorNode
            { Key = "node_" + i, Purpose = "", InputDependencies = [] }).ToList();
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            if (phase == "behavior") return Task.FromResult(new LLMResponse { Json = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.PlanningBehaviorPlan) });
            Assert.Equal("behavior_repair", phase);
            var target = request.StructuredOutputSchema!["properties"]!["patches"]!["items"]!["anyOf"]![0]!["properties"]!["target"]!["enum"]![0]!.ToString();
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = target, ["value"] = "Return the declared result" }) } });
        } };
        var state = Behavior(); state.Request.MaxRepairsPerWorkflowGate = 5;
        for (var i = 0; i < 10 && !PlanningStatus.IsWaiting(state.Status); i++) state = await Send(state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPAIR_EXHAUSTED");
        Assert.Equal(5, Assert.Single(state.RepairAllowances).Attempts);
        Assert.Single(runtime.Phases, p => p == "behavior");
        Assert.Equal(5, runtime.Phases.Count(p => p == "behavior_repair"));
        Assert.Equal(plan.Workflows[0].Steps.Select(n => n.Key), state.BehaviorAssessment.Candidate!["workflows"]![0]!["steps"]!.AsArray().Select(n => n!["key"]!.ToString()));
    }

    [Fact]
    public async Task RetainedInvalidDependencyUsesOneExactPatchAndScopedInputNames()
    {
        var state = Behavior(); state.BehaviorPlan = BehaviorPlan();
        state.BehaviorPlan.Workflows[0].Inputs.Add(new("resource", "Dynamic resource", true));
        state.BehaviorPlan.Workflows[0].Steps[0].InputDependencies = ["producer_step"];
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_repair", phase);
            var variant = request.StructuredOutputSchema!["properties"]!["patches"]!["items"]!["anyOf"]!.AsArray()
                .Single(v => v!["properties"]!["value"]?["enum"] is JsonArray values && values.Any(x => x?.ToString() == "resource"));
            var target = variant!["properties"]!["target"]!["enum"]![0]!.ToString();
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = target, ["value"] = "resource" }) } });
        } };
        var result = await Send(state, runtime);
        Assert.Equal(PlanningStatus.BehaviorReview, result.Status);
        var request = Assert.Single(runtime.Requests);
        Assert.Contains("producer_step", request.Prompt); Assert.Contains("Allowed inputs: resource", request.Prompt);
        Assert.Null(request.StructuredOutputSchema!["$defs"]?["behaviorNode"]);
        Assert.Equal("resource", Assert.Single(result.BehaviorPlan!.Workflows[0].Steps[0].InputDependencies!));
    }

    [Fact]
    public async Task RecoveryWithGraph_CanEditWithoutCompiling_AndPreservesSpentBudgets()
    {
        var state = Behavior(Candidate(), PlanningStatus.Recovery);
        state.Intent.Forms = 2; state.Intent.Questions = 7; state.BehaviorAssessmentCalls = 2;
        state.Intent.Answers.Add(new("Earlier question", new JsonObject { ["answer"] = "Earlier answer" }));
        state.Usage = new LLMUsageBudgetScope(new() { MaxCalls = 10 }).Snapshot;
        state.Diagnostics.Add(new("SCHEMA_REFERENCE_INVALID", "/workflows/0/outputs/0/schema", "Unresolved pointer"));
        state.WaitingSinceUtc = DateTimeOffset.UtcNow.AddHours(-2);
        var priorUsage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var edited = await Send(state, new FakeRuntime(), "edit_intent", "Return a greeting");
        Assert.Null(edited.Graph); Assert.Null(edited.Preparation); Assert.False(edited.Intent.Checked);
        Assert.Equal(state.Request.SessionId, edited.Request.SessionId);
        Assert.Equal(2, edited.Intent.Forms); Assert.Equal(7, edited.Intent.Questions);
        Assert.Equal(priorUsage, JsonSerializer.Serialize(edited.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Single(Assert.Single(edited.Intent.History).Answers); Assert.Empty(edited.Intent.Answers);
        Assert.True(edited.HumanWaitMilliseconds >= 7_200_000); Assert.InRange(edited.ActiveMilliseconds, 0, 10_000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PatchCannotDropBoundaryOrChangeUnrelatedValidWork(bool dropOutput)
    {
        var graph = Candidate(); graph.Workflows[0].Steps.Add(new() { Key = "independent", Input = Obj(("nonce", Str("Keep this"))) });
        var patch = new JsonObject
        {
            ["patches"] = new JsonArray(new JsonObject
            {
                ["workflow"] = "main",
                ["node"] = dropOutput ? null : "independent",
                ["field"] = dropOutput ? "outputs" : "input",
                ["value"] = dropOutput ? new JsonArray() : PlanningFixtures.Workflow(new() { Steps = [new() { Input = Obj(("nonce", Str("Changed"))) }] })["steps"]![0]!["input"]!.DeepClone()
            })
        };
        Assert.Throws<InvalidOperationException>(() => PlanningPatches.Apply(graph, patch, new HashSet<string>(), Catalog(), new PlanningDataflowContract()));
        Assert.Single(graph.Workflows[0].Outputs);
        Assert.Equal("Keep this", graph.Workflows[0].Steps[1].Input.Members[0].Value.Text);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("/content")]
    [InlineData("/output/properties")]
    [InlineData("/output/properties/content~2")]
    [InlineData("/output/properties/missing")]
    public void InvalidPointersAreRejectedWithExactLocations(string pointer)
    {
        var errors = PlanningGraphValidation.Validate(Candidate(pointer), Catalog());
        Assert.Contains(errors, d => d.Code == "SCHEMA_REFERENCE_INVALID" && d.Location == "/workflows/0/outputs/0/schema");
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(Candidate(pointer), Catalog()));
    }

    [Fact]
    public void RepairCannotRelaxAProducerGuardWhileFixingItsSchemaReference()
    {
        var graph = Candidate(); graph.Workflows[0].Steps[0].If = new() { Kind = "boolean", Boolean = false };
        var diagnostics = PlanningGraphValidation.Validate(graph, Catalog());
        var scope = PlanningPatches.Scope(graph, diagnostics);
        var patch = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["workflow"] = "main", ["node"] = "greeting", ["field"] = "if", ["value"] = null }) };
        Assert.Throws<InvalidOperationException>(() => PlanningPatches.Apply(graph, patch, scope, Catalog(), new PlanningDataflowContract()));
        Assert.False(graph.Workflows[0].Steps[0].If!.Boolean);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("structured")]
    public void InvalidOrUndeclaredResultChannelsCannotBeExported(string channel)
    {
        var graph = Candidate("/output/properties/content");
        graph.Workflows[0].Outputs[0].Value.ResultChannel = channel;
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(graph, Catalog()));
    }

    [Fact]
    public void MatchingObjectTypesCannotHideAnUndeclaredRequiredBoundaryField()
    {
        var graph = Candidate("/output");
        graph.Workflows[0].Outputs[0].Value.Path.Clear();
        graph.Workflows[0].Outputs[0].Schema = new() { Type = "object", Properties = [new() { Name = "invented", Required = true, Schema = new() { Type = "string" } }] };
        Assert.Contains(PlanningGraphValidation.Validate(graph, Catalog()), d => d.Code == "OUTPUT_TYPE_MISMATCH");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiteralArraysAndEnumsHaveProvableContractsWithoutModelShaping(bool empty)
    {
        var graph = Graph();
        graph.Workflows[0].Steps[0].Input = Obj(("message", new() { Kind = "array", Items = empty ? [] : [Str("ready"), Str("done")] }));
        graph.Workflows[0].Outputs[0].Schema = new() { Type = "array", Items = new() { Type = "string", Enum = ["ready", "done"] } };
        var yaml = new PlanningGraphCompiler().Compile(graph, Preparation());
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(empty ? 0 : 2, result.Outputs?["message"]?.AsArray().Count);
    }

    [Fact]
    public void EscapedPropertyAndArraySchemaPointer_PreserveExactConstraints()
    {
        var catalog = Catalog();
        catalog.Capabilities[0].OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"a/b~c":{"anyOf":[{"type":"string","minLength":2},{"type":"null"}]}}}""")!.AsObject();
        var reference = new PlanningSchema { CapabilityId = "producer", SchemaPointer = "/output/properties/a~1b~0c/anyOf/0" };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"type":"string","minLength":2}"""), PlanningGraphCompiler.ToJsonSchema(reference, catalog)));
        reference.SchemaPointer = "/output/properties/a~1b~0c/anyOf/00";
        Assert.Throws<InvalidOperationException>(() => PlanningGraphCompiler.ToJsonSchema(reference, catalog));
    }

    [Fact]
    public void MixedSchemaAndInventedProducerFields_AreReportedTogether()
    {
        var graph = Candidate();
        graph.Workflows[0].Outputs[0].Value.Path = ["decision"];
        graph.Workflows[0].Steps[0].OutputSchema = new() { CapabilityId = "producer", Properties = [new() { Name = "decision", Schema = new() { Type = "string" } }] };
        graph.Workflows[0].Steps[0].Input = Obj(("structured_output", Obj(("schema_inline", Obj(("type", Str("object")), ("required", new() { Kind = "array", Items = [Str("decision")] }))), ("strict", new() { Kind = "boolean", Boolean = true }))));
        var errors = PlanningGraphValidation.Validate(graph, Catalog());
        Assert.Contains(errors, d => d.Code == "STRUCTURED_OUTPUT_INVALID");
        Assert.Contains(errors, d => d.Code == "SCHEMA_REFERENCE_INVALID" && d.Location.Contains("outputSchema", StringComparison.Ordinal));
        Assert.Contains(errors, d => d.Code == "OUTPUT_REFERENCE_INVALID");
    }

    [Theory]
    [InlineData("opaque-host", "opaque-tool")]
    [InlineData("renamed-producer", "unrelated_operation")]
    public async Task RawAndStructuredResults_CompileAndExecuteAgainstDifferentContracts(string server, string method)
    {
        var catalog = Catalog(server, method);
        var graph = Candidate("/output/properties/content");
        var synthesized = Obj(("schema_inline", Obj(("type", Str("object")),
            ("properties", Obj(("content", Obj(("type", Str("integer")))))),
            ("required", new() { Kind = "array", Items = [Str("content")] }),
            ("additionalProperties", new() { Kind = "boolean", Boolean = false }))),
            ("strict", new() { Kind = "boolean", Boolean = true }));
        graph.Workflows[0].Steps[0].Input = Obj(("structured_output", synthesized), ("model", Str("fake")));
        graph.Workflows[0].Outputs.Add(new() { Name = "structured", Schema = new() { Type = "integer" }, Value = new() { Kind = "output", Source = "greeting", ResultChannel = "structured", Path = ["content"] } });
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer(server, new()
        {
            Tools = [new() { Name = method, InputSchema = catalog.Capabilities[0].InputSchema, OutputSchema = catalog.Capabilities[0].OutputSchema }],
            ToolHandlers = new() { [method] = _ => new McpCallResult { Content = new JsonObject { ["content"] = "raw" } } }
        });
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog);
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine { McpClientFactory = factory, LLMClient = new StructuredClient() }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("raw", result.Outputs?["message"]?.GetValue<string>());
        Assert.Equal(42, result.Outputs?["structured"]?.GetValue<int>());
        graph.Workflows[0].Outputs[1].Value.ResultChannel = "default";
        Assert.Contains(PlanningGraphValidation.Validate(graph, catalog), d => d.Code == "OUTPUT_TYPE_MISMATCH");
    }

    private sealed class StructuredClient : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
            => Task.FromResult(new LLMResponse { Json = new JsonObject { ["content"] = 42 } });
    }
}
