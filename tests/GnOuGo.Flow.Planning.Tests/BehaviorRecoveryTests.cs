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
    [InlineData(false)]
    [InlineData(true)]
    public async Task GoverningRemovalRetiresExactOwnershipCompanionsWithoutRegeneratingBehavior(bool unrelatedInvalidOwnership)
    {
        var state = Behavior(); state.Preparation = Preparation(); var plan = BehaviorPlan();
        state.Preparation.Capabilities.Add(new() { Id = "retained", Resolution = "local", StepType = "set", EffectKind = "none", Required = true, OperationIds = ["read"] });
        var workflow = plan.Workflows[0]; workflow.OperationIds = ["read", "publish"];
        workflow.Steps[0].OperationIds = ["read"];
        workflow.Steps.Add(new() { Key = "publication", Kind = "operation", Purpose = "Publish the result", CapabilityId = "removed", OperationIds = ["publish"], InputDependencies = [] });
        if (unrelatedInvalidOwnership) workflow.OperationIds.Add("unrelated_invalid");
        var candidate = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        var path = "/workflows/0/steps/1";
        state.BehaviorRevision = new() { Text = "Remove publication.", Located = true, Fields = [new(path,
            PlanningFieldPaths.Canonical(candidate, path), "remove", PlanningGraphCompiler.Fingerprint(PlanningFieldPaths.Read(candidate, path)!.ToJsonString()), "Remove publication.")] };
        var revised = PlanningBehaviorRevision.ApplyRemovals(state, candidate, PlanningSchemas.Behavior(state.Preparation));
        Assert.Equal(2, candidate["workflows"]![0]!["steps"]!.AsArray().Count);
        Assert.Single(revised["workflows"]![0]!["steps"]!.AsArray());
        Assert.True(JsonNode.DeepEquals(candidate["workflows"]![0]!["steps"]![0], revised["workflows"]![0]!["steps"]![0]));
        Assert.Equal(unrelatedInvalidOwnership ? ["read", "unrelated_invalid"] : new[] { "read" },
            revised["workflows"]![0]!["operationIds"]!.AsArray().Select(o => o!.ToString()));
        if (unrelatedInvalidOwnership) return;
        state.BehaviorAssessment.Candidate = candidate;
        var runtime = new FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("This revision has no unresolved semantic choice.") };
        await new PlanningBehaviorAssessment(TimeProvider.System).AssessAsync(state, runtime, Ct);
        Assert.True(state.Status == PlanningStatus.BehaviorReview, string.Join(";", state.Diagnostics));
        Assert.Empty(runtime.Requests); Assert.Null(state.ApprovedBehaviorHash);
        Assert.Equal("greeting", Assert.Single(state.BehaviorPlan!.Workflows[0].Steps).Key);
        Assert.Equal("read", Assert.Single(state.BehaviorPlan.Workflows[0].OperationIds));
    }

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
            var field = Assert.Single(request.StructuredOutputSchema!["properties"]!.AsObject());
            return Task.FromResult(new LLMResponse { Json = new JsonObject { [field.Key] = null } });
        } };
        await new PlanningBehaviorAssessment(TimeProvider.System).AssessAsync(state, runtime, Ct);
        Assert.Equal(PlanningStatus.BehaviorReview, state.Status);
        Assert.Empty(runtime.Requests); Assert.All(state.Attempts, attempt => Assert.True(attempt.Retained));
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
    public async Task MechanicalBehaviorDoesNotRequestAFullModelPlan()
    {
        var state = Behavior(); state.Preparation = Preparation();
        state.Preparation.Capabilities.Add(new() { Id = "declared", Required = true, Description = "Return the declared value", Resolution = "local", StepType = "set", OperationIds = ["requested"] });
        var runtime = new FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("No semantic ambiguity") };
        state = await Send(state, runtime);
        Assert.True(state.Status == PlanningStatus.BehaviorReview, string.Join(";", state.Diagnostics)); Assert.Empty(runtime.Requests);
        Assert.Null(state.BehaviorPlan!.Workflows[0].Steps[0].CapabilityId);
        Assert.Contains("requested", state.BehaviorPlan.Workflows[0].Steps[0].OperationIds);
        Assert.Empty(state.RepairAllowances);
    }


    [Fact]
    public async Task InvalidBehaviorPatchRecordsAResponseGateFailure()
    {
        var state = Behavior(); state.BehaviorPlan = BehaviorPlan(); state.BehaviorPlan.Workflows[0].Steps[0].Purpose = "";
        var runtime = Responses(new JsonObject { ["unknown"] = "new purpose" });
        state = await Send(state, runtime);
        Assert.Contains(state.Diagnostics, d => d.Code == "DECISION_SCOPE_INVALID");
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
                return Task.FromResult(new LLMResponse { Json = RevisionAnswer(request, "/workflows/0/steps/0/purpose") });
            }
            Assert.Equal("behavior_repair", phase);
            var id = Assert.Single(request.StructuredOutputSchema!["properties"]!.AsObject()).Key;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { [id] = "Return a formal greeting" } });
        } };
        revised = await Send(revised, runtime);
        revised = await Send(revised, runtime);
        Assert.Equal(PlanningStatus.BehaviorReview, revised.Status);
        Assert.Equal("greeting", revised.BehaviorPlan!.Workflows[0].Steps[0].Key);
        Assert.Equal("Describe the greeting as formal.", revised.BehaviorPlan.Workflows[0].Steps[0].Purpose);
        Assert.Equal(["behavior_revision_scope"], runtime.Phases);
        Assert.Empty(revised.RepairAllowances);
    }

    [Fact]
    public async Task RevisionCoordinatesArePagedAndDoNotAuthorizeUnrelatedContent()
    {
        var state = Behavior(); var plan = BehaviorPlan();
        for (var i = 1; i < 24; i++) plan.Workflows[0].Steps.Add(new() { Key = "action_" + i, Purpose = "Retained purpose" });
        state.BehaviorAssessment.Candidate = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        state.BehaviorRevision = new() { Text = "Change the description." };
        var original = state.BehaviorAssessment.Candidate.DeepClone();
        var runtime = new FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse { Json = RevisionAnswer(request, "/workflows/0/steps/0/purpose") }) };
        await PlanningBehaviorRevision.LocateAsync(state, runtime, Ct);
        Assert.True(state.BehaviorRevision.Located); Assert.Single(state.BehaviorRevision.Fields);
        Assert.All(runtime.Requests, r => Assert.InRange(PlanningJsonTransport.EstimateInputTokens(r.Prompt, r.StructuredOutputSchema!.AsObject()), 1, 9600));
        Assert.True(JsonNode.DeepEquals(original, state.BehaviorAssessment.Candidate));
    }


    [Fact]
    public async Task RevisionScopeReplaysThePersistedTargetDomainAgainstTheRetainedCandidate()
    {
        var state = Behavior();
        state.BehaviorAssessment.Candidate = JsonSerializer.SerializeToNode(BehaviorPlan(), PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        state.BehaviorRevision = new() { Text = "Revise this description." };
        var runtime = new FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse { Json = RevisionAnswer(request, "/summary") }) };
        await PlanningBehaviorRevision.LocateAsync(state, runtime, Ct);
        var path = Assert.Single(state.BehaviorRevision.Fields).Path;
        var count = runtime.Requests.Count;
        state = PlanningContext.Clone(state); state.BehaviorRevision!.Located = false; state.BehaviorRevision.Fields.Clear();
        await PlanningBehaviorRevision.LocateAsync(state, runtime, Ct);
        Assert.Equal(path, Assert.Single(state.BehaviorRevision.Fields).Path);
        Assert.Equal(count, runtime.Requests.Count); Assert.Empty(state.Construction.PendingCalls);
    }

    private static JsonObject RevisionAnswer(LLMRequest request, string path)
    {
        var context = JsonNode.Parse(request.Prompt![(request.Prompt.IndexOf(PlanningPromptContext.Instructions, StringComparison.Ordinal) + PlanningPromptContext.Instructions.Length)..])!.AsObject();
        if (context["context"] is JsonObject compact) context = compact;
        return new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
            context[p.Key]?["coordinate"]?.ToString() == path && context[p.Key]?["operation"]?.ToString() == "replace"
                ? p.Value!["enum"]!.AsArray().First(v => v!.ToString() != "unchanged")!.DeepClone() : JsonValue.Create("unchanged"))));
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
    public async Task BehaviorCorrectionsKeepFiveGateAllowanceButCannotRepeatAnUnchangedDecision()
    {
        var state = Behavior(); state.Request.MaxRepairsPerWorkflowGate = 5; state.BehaviorPlan = BehaviorPlan(); state.BehaviorPlan.Workflows[0].Steps[0].Purpose = "";
        var runtime = new FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse { Json = new JsonObject(
            request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("")))) }) };
        state = await Send(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(5, state.Request.MaxRepairsPerWorkflowGate);
        Assert.Single(runtime.Requests); Assert.Equal(1, Assert.Single(state.RepairAllowances).Attempts);
        state = await Send(PlanningContext.Clone(state), runtime); Assert.Single(runtime.Requests);
    }


    [Fact]
    public async Task RetainedInvalidDependencyResolvesItsSoleScopedInputWithoutModelTranscription()
    {
        var state = Behavior(); state.BehaviorPlan = BehaviorPlan();
        state.BehaviorPlan.Workflows[0].Inputs.Add(new("resource", "Dynamic resource", true));
        state.BehaviorPlan.Workflows[0].Steps[0].InputDependencies = ["producer_step"];
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_repair", phase);
            var field = Assert.Single(request.StructuredOutputSchema!["properties"]!.AsObject());
            Assert.Contains("resource", field.Value!.ToJsonString());
            return Task.FromResult(new LLMResponse { Json = new JsonObject { [field.Key] = "resource" } });
        } };
        var result = await Send(state, runtime);
        Assert.True(result.Status == PlanningStatus.BehaviorReview, string.Join(";", result.Diagnostics));
        Assert.Empty(runtime.Requests);
        Assert.Equal("resource", Assert.Single(result.BehaviorPlan!.Workflows[0].Steps[0].InputDependencies!));
    }

    [Fact]
    public async Task RecoveryWithGraph_CanEditWithoutCompiling_AndPreservesSpentBudgets()
    {
        var state = Behavior(Candidate(), PlanningStatus.Stopped);
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
