using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;
using static GnOuGo.Flow.Planning.Tests.HoleSessionTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RepairAcceptanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonObject Patch(string workflow, string? node, string field, JsonNode? value) => new()
    {
        ["patches"] = new JsonArray(new JsonObject { ["target"] = "f_" + PlanningGraphCompiler.Fingerprint(PlanningPatches.Coordinate(workflow, node, field))[..16], ["value"] = value })
    };
    private static JsonObject ModelPatch(JsonObject patch) => new(patch["patches"]!.AsArray().Select(p => new KeyValuePair<string, JsonNode?>(p!["target"]!.ToString(), p["value"]?.DeepClone())));
    private static PlanningDiagnostic Finding(string code) => new(code, "/workflows/0/steps/0/input/members/0/value/text", code);
    private static PlanningScenarioResult Scenario(string outcome) => new("same-fixture", outcome, "Fixed fixture", []);

    [Fact]
    public async Task CallerRepairIncludesOnlyTheRelevantCalleeArgumentContract()
    {
        var behavior = BehaviorPlan();
        behavior.Workflows[0].Inputs = [new("subject", "Runtime subject", true)];
        behavior.Workflows[0].Steps = [new() { Key = "call", Kind = "workflow", WorkflowKey = "child", Purpose = "Get result", InputDependencies = ["subject"] }];
        var childBehavior = BehaviorPlan().Workflows[0]; childBehavior.Key = "child"; behavior.Workflows.Add(childBehavior);
        var state = Ready(behavior);
        var child = FakeRuntime.ExecutableWorkflow(); child.Key = "child";
        child.Inputs = [new() { Name = "requiredArgument", Required = true, Schema = new() { Type = "number" } }];
        child.Outputs[0].Name = "returnedPort"; child.Steps[0].Purpose = "private implementation marker";
        state.Graph!.Workflows[1] = child;
        state.Graph.Workflows[0].Inputs[0].Schema = new() { Type = "number" };
        state.Graph.Workflows.Add(new() { Key = "unrelated marker" });
        state.Graph.Workflows[0].Steps[0].Input = Obj(("ref", new() { Kind = "workflow", Source = "child" }), ("args", Obj(("requiredArgument", Str("invalid")))));
        state.Diagnostics = [new("ARGUMENT_INVALID", "/workflows/0/steps/0/input/args/requiredArgument", "Fix argument")];
        var called = false;
        var runtime = new FakeRuntime { OnCall = (_, request, _) =>
        {
            called = true;
            Assert.Contains("requiredArgument", request.Prompt);
            Assert.DoesNotContain("returnedPort", request.Prompt);
            Assert.DoesNotContain("private implementation marker", request.Prompt);
            Assert.DoesNotContain("unrelated marker", request.Prompt);
            return Task.FromResult(new LLMResponse { Json = new JsonObject() });
        } };
        var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => new PlanningTypedRepair(new()).AdvanceAsync(state, runtime, Ct));
        Assert.True(called, error.Code + ": " + error.Message);
    }

    [Fact]
    public void AcceptedExecutorCannotBeSubstitutedDuringConstruction()
    {
        var plan = BehaviorPlan(); var prep = Preparation();
        prep.Capabilities = [new() { Id = "local", StepType = "set", Resolution = "local", EffectKind = "none" }];
        prep.AllowedStepTypes = ["set", "emit", "mcp.call"];
        plan.Workflows[0].Steps[0].CapabilityId = "local";
        var graph = PlanningBehaviorPlans.Display(plan, prep);
        graph.Workflows[0].Steps[0].Type = "emit";
        Assert.Contains(PlanningBehaviorPlans.ValidateImplementation(plan, graph, prep), d => d.Code == "BEHAVIOR_IMPLEMENTATION_CHANGED");
        graph.Workflows[0].Steps[0].Type = "mcp.call";
        Assert.Contains(PlanningBehaviorPlans.ValidateImplementation(plan, graph, prep), d => d.Code == "BEHAVIOR_IMPLEMENTATION_CHANGED");
        graph.Workflows[0].Steps[0].Type = "emit";
        prep.AllowedStepTypes.Remove("emit");
        Assert.Contains(PlanningBehaviorPlans.ValidateImplementation(plan, graph, prep), d => d.Code == "BEHAVIOR_IMPLEMENTATION_CHANGED");
    }

    [Fact]
    public async Task ProducerRepairCanProgressWhileItsCallerStillHasAnUnimplementedSkeleton()
    {
        var behavior = BehaviorPlan();
        behavior.Workflows[0].Inputs = [new("subject", "Runtime subject", true)];
        behavior.Workflows[0].Steps = [new() { Key = "call", Kind = "workflow", WorkflowKey = "child", Purpose = "Get the greeting", InputDependencies = ["subject"] }];
        var childBehavior = BehaviorPlan().Workflows[0]; childBehavior.Key = "child"; behavior.Workflows.Add(childBehavior);
        var state = Ready(behavior);
        var child = FakeRuntime.ExecutableWorkflow(); child.Key = "child"; state.Graph!.Workflows[1] = child;
        state.Construction.Workflows.Single(w => w.WorkflowKey == "child").Status = "constructed";
        state.Validation.Stage = 1;
        state.Diagnostics = [new("BAD_VALUE", "/workflows/1/steps/0/input/members/0/value/text", "Fix greeting")];
        var patch = Patch("child", "greeting", "input/members/0/value/text", JsonValue.Create("Fixed"));
        var runtime = new FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { Json = ModelPatch(patch) }) };
        await new PlanningTypedRepair(new()).AdvanceAsync(state, runtime, Ct);
        Assert.True(state.Attempts.Last().Retained, string.Join("; ", state.Attempts.Last().Diagnostics.Select(d => d.Code)));
        Assert.Equal("Fixed", state.Graph.Workflows[1].Steps[0].Input.Members[0].Value.Text);
        Assert.Equal("pending", state.Construction.Workflows.Single(w => w.WorkflowKey == "main").Status);
    }

    [Fact]
    public void ProgressRequiresStrictReductionAndPreservesEarlierGatesAndScenarioPasses()
    {
        var baseline = new PlanningValidationReport(3, [Finding("A"), Finding("B")], [Scenario("passed")]);
        Assert.True(PlanningTypedRepair.IsProgress(baseline, new(3, [Finding("B")], [Scenario("passed")])));
        Assert.True(PlanningTypedRepair.IsProgress(baseline, new(4, [], [Scenario("passed")])));
        Assert.False(PlanningTypedRepair.IsProgress(baseline, new(3, [Finding("C")], [Scenario("passed")])));
        Assert.False(PlanningTypedRepair.IsProgress(baseline, new(3, baseline.Diagnostics, [Scenario("passed")])));
        Assert.False(PlanningTypedRepair.IsProgress(baseline, new(2, [], [Scenario("passed")])));
        Assert.False(PlanningTypedRepair.IsProgress(baseline, new(4, [], [Scenario("failed")])));
        Assert.False(PlanningTypedRepair.IsProgress(baseline, new(4, [], [])));
    }

    [Fact]
    public void ProvenSchemasMayBeRefinedButNotWeakenedByRemovingEnumsOrRequiredFields()
    {
        static JsonObject Schema(string value) => JsonNode.Parse(value)!.AsObject();
        var before = Schema("""{"type":"object","properties":{"outcome":{"type":"string","enum":["a","b"]}},"required":["outcome"],"additionalProperties":false}""");
        var narrower = (JsonObject)before.DeepClone(); narrower["properties"]!["outcome"]!["enum"] = new JsonArray("a");
        Assert.True(PlanningContractPreservation.RefinesSchema(before, narrower));
        narrower["properties"]!["outcome"]!.AsObject().Remove("enum");
        Assert.False(PlanningContractPreservation.RefinesSchema(before, narrower));
        var optional = (JsonObject)before.DeepClone(); optional["required"] = new JsonArray();
        Assert.False(PlanningContractPreservation.RefinesSchema(before, optional));
        var nullable = (JsonObject)before.DeepClone(); nullable["type"] = new JsonArray("object", "null");
        Assert.False(PlanningContractPreservation.RefinesSchema(before, nullable));
    }

    [Fact]
    public void InputPatchCannotChangeUndiagnosedSiblingFields()
    {
        var graph = Graph(); graph.Workflows[0].Steps[0].Input.Members.Add(new("retained", Str("proven")));
        var candidate = PlanningContext.Clone(graph); candidate.Workflows[0].Steps[0].Input.Members[0].Value.Text = "Fixed";
        Assert.True(PlanningRepairInvariants.PreservesUndiagnosedFields(graph, candidate, [Finding("BAD_VALUE")]));
        candidate.Workflows[0].Steps[0].Input.Members[1].Value.Text = "changed";
        Assert.False(PlanningRepairInvariants.PreservesUndiagnosedFields(graph, candidate, [Finding("BAD_VALUE")]));
        Assert.Empty(PlanningPatches.Scope(graph, [new("GLOBAL", "$", "No located defect")]));
    }

    [Fact]
    public void OutputValueFindingGrantsOnlyTheValueCoordinate()
    {
        var graph = Graph();
        var diagnostics = new List<PlanningDiagnostic> { new("OUTPUT_REFERENCE_INVALID", "/workflows/0/outputs/0/value", "Fix the result binding") };
        var scope = PlanningPatches.Scope(graph, diagnostics);
        Assert.Contains(PlanningPatches.Coordinate("main", null, "outputs/0/value"), scope);
        Assert.DoesNotContain(PlanningPatches.Coordinate("main", null, "outputs"), scope);
        var patch = Patch("main", null, "outputs/0/value", new JsonObject { ["kind"] = "literal", ["json"] = "Fixed" });
        var candidate = PlanningPatches.Apply(graph, patch, scope, Preparation(), new PlanningDataflowContract());
        Assert.True(PlanningRepairInvariants.PreservesUndiagnosedFields(graph, candidate, diagnostics));
        candidate.Workflows[0].Outputs[0].Schema.Description = "Unrelated change";
        Assert.False(PlanningRepairInvariants.PreservesUndiagnosedFields(graph, candidate, diagnostics));
        patch["patches"]![0]!["field"] = "outputs";
        patch["patches"]![0]!["value"] = PlanningFixtures.Workflow(graph.Workflows[0])["outputs"]!.DeepClone();
        Assert.Throws<InvalidOperationException>(() => PlanningPatches.Apply(graph, patch, scope, Preparation(), new PlanningDataflowContract()));
    }

    [Fact]
    public void ModelRepairSchemaRejectsUnscopedCoordinatesAndOmitsUnusedContracts()
    {
        var scope = new HashSet<string> { PlanningPatches.Coordinate("main", null, "outputs/0/value") };
        var schema = PlanningPatches.CreateRequest(Graph(), Preparation(), scope, new PlanningDataflowContract()).Schema;
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Null(schema["$defs"]?["workflow"]);
        var patch = Patch("main", null, "outputs/0/value", new JsonObject { ["kind"] = "literal", ["json"] = "Fixed" });
        Assert.Empty(PlanningContractValidation.ValidateInstance(patch, schema));
        patch["patches"]![0]!["workflow"] = "unrelated";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(patch, schema));
        patch["patches"]![0]!["workflow"] = "main"; patch["patches"]![0]!["field"] = "outputs";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(patch, schema));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("unknown")]
    public void MissingOutputLocationsNeverGrantWholeContractReplacement(string index)
        => Assert.Empty(PlanningPatches.Scope(Graph(), [new("INVALID", "/workflows/0/outputs/" + index + "/value", "Unlocated output")]));

    [Fact]
    public void NativeArgumentRemovalPreservesUntouchedMembersDespiteIndexChanges()
    {
        var graph = Graph(); graph.Workflows[0].Steps[0].Input.Members.Insert(0, new("invalid", Str("remove")));
        var candidate = PlanningContext.Clone(graph); candidate.Workflows[0].Steps[0].Input.Members.RemoveAt(0);
        var diagnostics = new List<PlanningDiagnostic> { new("NATIVE_INPUT_INVALID", "/workflows/0/steps/0/input/invalid", "unknown field") };
        Assert.True(PlanningRepairInvariants.PreservesUndiagnosedFields(graph, candidate, diagnostics));
        candidate.Workflows[0].Steps[0].Input.Members[0].Value.Text = "Changed sibling";
        Assert.False(PlanningRepairInvariants.PreservesUndiagnosedFields(graph, candidate, diagnostics));
    }

    [Fact]
    public void NativeArgumentNamesCannotAuthorizeTypedMetadataChanges()
    {
        var graph = Graph(); graph.Workflows[0].Steps[0].Input.Members.Add(new("kind", Str("invalid argument")));
        var diagnostics = new List<PlanningDiagnostic> { new("NATIVE_INPUT_INVALID", "/workflows/0/steps/0/input/kind", "unknown field") };
        var candidate = PlanningContext.Clone(graph); candidate.Workflows[0].Steps[0].Input.Members.RemoveAt(1);
        Assert.True(PlanningRepairInvariants.PreservesUndiagnosedFields(graph, candidate, diagnostics));
        candidate.Workflows[0].Steps[0].Input.Kind = "compute";
        Assert.False(PlanningRepairInvariants.PreservesUndiagnosedFields(graph, candidate, diagnostics));
    }

    [Fact]
    public void TypedMemberCoordinatesCannotAuthorizeSimilarlyNamedNativeArguments()
    {
        var graph = Graph(); graph.Workflows[0].Steps[0].Input.Members.Add(new("members", Obj(("0", Obj(("value", Obj(("text", Str("retained")))))))));
        var candidate = PlanningContext.Clone(graph);
        candidate.Workflows[0].Steps[0].Input.Members[1].Value.Members[0].Value.Members[0].Value.Members[0].Value.Text = "Changed";
        Assert.False(PlanningRepairInvariants.PreservesUndiagnosedFields(graph, candidate, [Finding("BAD_VALUE")]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagedRepairSurvivesRestartAndCommitsOnlyAfterValidation(bool regression)
    {
        var state = Ready(); state.Graph!.Workflows[0] = FakeRuntime.ExecutableWorkflow();
        state.Construction.Workflows[0].Status = "validated";
        state.CurrentPhase = PlanningPhase.Repair; state.Diagnostics = [Finding("BAD_VALUE")]; state.Validation.Stage = 2;
        var hash = PlanningGraphCompiler.Fingerprint(state.Graph);
        var patch = Patch("main", "greeting", "input/members/0/value/text", JsonValue.Create("Fixed"));
        PlanningSnapshot? staged = null;
        var runtime = new FakeRuntime
        {
            OnCall = (_, _, _) => Task.FromResult(new LLMResponse { Json = ModelPatch(patch) }),
            OnCheckpoint = snapshot =>
            {
                if (snapshot.Construction.Repair is { Ready: true }) { staged = PlanningContext.Clone(snapshot); throw new IOException("Restart before candidate validation"); }
                return Task.CompletedTask;
            }
        };
        await Assert.ThrowsAsync<IOException>(() => new PlanningTypedRepair(new()).AdvanceAsync(state, runtime, Ct));
        Assert.NotNull(staged); Assert.Equal(hash, PlanningGraphCompiler.Fingerprint(staged.Graph!));
        var replay = new FakeRuntime { ValidationResult = _ => regression ? [Finding("NEW_FAILURE")] : [] };
        await new PlanningTypedRepair(new()).AdvanceAsync(staged, replay, Ct);
        Assert.Empty(replay.Requests); Assert.Equal(1, staged.RepairAllowances.Sum(a => a.Attempts));
        Assert.Equal(!regression, staged.Attempts.Last().Retained);
        if (regression) Assert.Equal(hash, PlanningGraphCompiler.Fingerprint(staged.Graph!));
        else Assert.NotEqual(hash, PlanningGraphCompiler.Fingerprint(staged.Graph!));
        Assert.Equal(regression ? "Hello" : "Fixed", staged.Graph!.Workflows[0].Steps[0].Input.Members[0].Value.Text);
    }

    [Fact]
    public async Task RepairDomainsAndPageIdentityArePersistedBeforeDispatch()
    {
        var state = Ready(); state.Graph!.Workflows[0] = FakeRuntime.ExecutableWorkflow();
        state.Construction.Workflows[0].Status = "validated";
        state.CurrentPhase = PlanningPhase.Repair; state.Diagnostics = [Finding("BAD_VALUE")]; state.Validation.Stage = 2;
        PlanningSnapshot? retained = null;
        var first = new FakeRuntime { OnCheckpoint = snapshot =>
        {
            if (snapshot.Construction.PendingCalls.Count > 0)
            { retained = PlanningContext.Clone(snapshot); throw new IOException("Stopped before dispatch"); }
            return Task.CompletedTask;
        } };
        await Assert.ThrowsAsync<IOException>(() => new PlanningTypedRepair(new()).AdvanceAsync(state, first, Ct));
        Assert.Empty(first.Requests); Assert.NotNull(retained);
        Assert.False(retained.Construction.Repair!.Ready); Assert.NotNull(retained.Construction.Repair.Bindings);
        var request = Assert.Single(retained.Construction.PendingCalls);
        var replay = new FakeRuntime { OnCall = (_, current, _) =>
        {
            Assert.Equal(request.Request.Prompt, current.Prompt);
            Assert.Equal(request.Request.ClientRequestId, current.ClientRequestId);
            Assert.True(JsonNode.DeepEquals(request.Request.StructuredOutputSchema, current.StructuredOutputSchema));
            return Task.FromResult(new LLMResponse { Json = ModelPatch(Patch("main", "greeting", "input/members/0/value/text", JsonValue.Create("Fixed"))) });
        } };
        await new PlanningTypedRepair(new()).AdvanceAsync(retained, replay, Ct);
        Assert.Single(replay.Requests); Assert.Equal(1, retained.RepairAllowances.Sum(a => a.Attempts));
        Assert.Equal("Fixed", retained.Graph!.Workflows[0].Steps[0].Input.Members[0].Value.Text);
    }

    [Fact]
    public async Task ProducerContractRepairInvalidatesValidatedCallersWithoutRegeneratingThem()
    {
        var behavior = BehaviorPlan();
        behavior.Workflows[0].Steps = [new() { Key = "call", Kind = "workflow", WorkflowKey = "child", Purpose = "Get the greeting", InputDependencies = [] }];
        var childBehavior = BehaviorPlan().Workflows[0]; childBehavior.Key = "child"; behavior.Workflows.Add(childBehavior);
        var state = Ready(behavior);
        var child = FakeRuntime.ExecutableWorkflow(); child.Key = "child"; state.Graph!.Workflows[1] = child;
        state.Graph.Workflows[0].Steps = [new() { Key = "call", Type = "workflow.call", Input = Obj(("ref", new() { Kind = "workflow", Source = "child" }), ("args", Obj())) }];
        state.Graph.Workflows[0].Outputs = [new() { Name = "message", Schema = new() { Type = "string" }, Value = new() { Kind = "output", Source = "call", Path = ["message"] } }];
        state.Graph.Workflows = state.Graph.Workflows.Select(w => JsonSerializer.Deserialize(PlanningFixtures.Workflow(w), PlanningJsonContext.Default.PlanningWorkflow)!).ToList();
        foreach (var progress in state.Construction.Workflows)
        {
            progress.Status = "validated";
            progress.DependencyFingerprint = PlanningWorkflowConstruction.DependencyFingerprint(state, progress);
        }
        state.Validation.Stage = 2; state.CurrentPhase = PlanningPhase.Repair;
        state.Diagnostics = [new("CONTRACT_DESCRIPTION_INVALID", "/workflows/1/outputs/0/schema/description", "Describe the returned message")];
        var corrected = PlanningContext.Clone(state.Graph).Workflows[1]; corrected.Outputs[0].Schema.Description = "Returned greeting";
        var patch = Patch("child", null, "outputs/0/schema/description", JsonValue.Create("Returned greeting"));
        var runtime = new FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { Json = ModelPatch(patch) }) };
        await new PlanningTypedRepair(new()).AdvanceAsync(state, runtime, Ct);
        Assert.True(state.Attempts.Last().Retained, string.Join("; ", state.Attempts.Last().Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal("constructed", state.Construction.Workflows.Single(w => w.WorkflowKey == "main").Status);
        Assert.Single(runtime.Requests);
        await new PlanningValidationPipeline().AdvanceAsync(state, new FakeRuntime(), Ct);
        Assert.All(state.Construction.Workflows, w => Assert.Equal("validated", w.Status));
    }

    [Fact]
    public async Task StoppedRepairCannotRestartItsDecisionOrResetItsAllowance()
    {
        var state = Ready(); state.Graph!.Workflows[0] = FakeRuntime.ExecutableWorkflow();
        state.Graph.Workflows[0].Outputs[0].Value.Source = "missing";
        state.Construction.Workflows[0].Status = "constructed";
        state.RepairAllowances.Add(new() { WorkflowKey = "main", Gate = PlanningGates.Typed, Attempts = 2 });
        state.Status = PlanningStatus.Stopped; state.CurrentPhase = PlanningPhase.Repair;
        state.Diagnostics = [new("REPAIR_REPEATED", "$", "A candidate was repeated")];
        var runtime = new FakeRuntime();
        await Assert.ThrowsAsync<ArgumentException>(() => Advance(state, runtime, "retry"));
        var unchanged = await Advance(PlanningContext.Clone(state), runtime);
        Assert.Equal(PlanningStatus.Stopped, unchanged.Status);
        Assert.Contains(unchanged.Diagnostics, d => d.Code == "REPAIR_REPEATED");
        Assert.Equal(2, unchanged.RepairAllowances.Sum(a => a.Attempts));
        Assert.Empty(runtime.Requests);

    }

    [Fact]
    public async Task NoOpAndRepeatedCandidatesStopWithTheRetainedGraph()
    {
        var state = Ready(); state.Graph!.Workflows[0] = FakeRuntime.ExecutableWorkflow(); state.Construction.Workflows[0].Status = "validated";
        state.CurrentPhase = PlanningPhase.Repair; state.Diagnostics = [Finding("BAD_VALUE")]; state.Validation.Stage = 2;
        var runtime = new FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse
            { Json = ModelPatch(Patch("main", "greeting", "input/members/0/value/text", JsonValue.Create("Hello"))) }) };
        var before = PlanningGraphCompiler.Fingerprint(state.Graph);
        await new PlanningTypedRepair(new()).AdvanceAsync(state, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Contains(state.Diagnostics, d => d.Code == "REPAIR_REPEATED");
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(state.Graph));
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("fixture")]
    [InlineData("graph")]
    [InlineData("yaml")]
    public async Task FinalApprovalRejectsChangedValidationEvidence(string change)
    {
        var state = Ready(); var runtime = new FakeRuntime();
        for (var i = 0; i < 20 && state.Status != PlanningStatus.FinalReview; i++) state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        switch (change)
        {
            case "contract": state.Preparation!.AllowedStepTypes.Add("invented"); break;
            case "fixture": state.Validation.Inputs!["modified"] = "fixture"; break;
            case "graph": state.Graph!.Workflows[0].Steps[0].Input.Members[0].Value.Text = "Modified"; break;
            case "yaml": state.Yaml += "\n# modified"; break;
        }
        await Assert.ThrowsAsync<PlanningConflictException>(() => Advance(state, runtime, "approve"));
        Assert.Null(state.ApprovedHash);
    }
}
