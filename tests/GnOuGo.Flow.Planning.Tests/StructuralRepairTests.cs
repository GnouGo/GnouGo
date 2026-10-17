using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class StructuralRepairTests
{
    private static async Task<PlanningSession> MissingProducer()
    {
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        plan.Root.Tasks.RemoveAt(0); plan.Root.Tasks[0].Inputs = [new("location", new() { Kind = "string", Text = "unproven" })];
        plan.Root.Tasks.Add(new() { Id = "untouched", Kind = "value", Objective = "Keep unrelated intent", Outputs = [new("value", PlanningCorpus.Number(17))] });
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements(); state.Plan = plan; state.Catalog = catalog;
        Diagnose(state); return state;
    }
    private static void Diagnose(PlanningSession state)
    {
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan!, state.Catalog!).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan!, state.Diagnostics).ToList();
    }
    private static TaskPlan Apply(PlanningSession state, string kind, string action, JsonNode? value, LLMRequest? request = null)
    {
        request ??= new PlanningPrompt(state).Request();
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!, strict: true));
        var slot = Assert.Single(RepairPatchTests.Slots(state), s => s.Kind == kind);
        return PlanningRepairPatch.Apply(state, new() { Edits = [new() { Slot = slot.Id, Action = action, Value = value }] }, request);
    }
    private static JsonObject Prerequisite(string id = "new_resource") => new()
    {
        ["tasks"] = new JsonArray((JsonNode)new JsonObject { ["id"] = id, ["kind"] = "operation", ["objective"] = "Produce the required resource", ["dependsOn"] = new JsonArray(), ["operation"] = "allocate", ["inputs"] = new JsonArray() }),
        ["value"] = new JsonObject { ["kind"] = "output", ["source"] = id, ["port"] = "handle" }
    };
    [Fact]
    public async Task MissingProducerGetsAnAnchoredTypedInsertionWithoutEditingOtherTasks()
    {
        var state = await MissingProducer(); var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var candidate = Apply(state, "prerequisites", "insert_prerequisites", Prerequisite());
        Assert.Equal(new[] { "new_resource", "use", "untouched" }, candidate.Root.Tasks.Select(t => t.Id));
        Assert.Empty(new TaskPlanCompiler().Compile(candidate, state.Catalog!).Diagnostics);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal("Keep unrelated intent", candidate.Root.Tasks[^1].Objective);
    }
    [Theory]
    [InlineData("unused")][InlineData("collision")][InlineData("cycle")][InlineData("unrelated_dependency")][InlineData("wrong_origin")][InlineData("unissued_operation")]
    public async Task UnnecessaryOrInvalidStructuresAreRejectedAtomically(string mutation)
    {
        var state = await MissingProducer(); var value = Prerequisite(); var task = value["tasks"]![0]!;
        switch (mutation)
        {
            case "unused": value["tasks"]!.AsArray().Add(Prerequisite("unused")["tasks"]![0]!.DeepClone()); break;
            case "collision": task["id"] = "untouched"; break;
            case "cycle": task["dependsOn"]!.AsArray().Add("use"); break;
            case "unrelated_dependency": task["dependsOn"]!.AsArray().Add("untouched"); break;
            case "wrong_origin": value["value"]!["port"] = "display"; break;
            case "unissued_operation": task["operation"] = "invented"; break;
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Throws<PlanningResponseException>(() => Apply(state, "prerequisites", "insert_prerequisites", value));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }
    [Fact]
    public async Task UnavailableOrDeniedProducerNeverGrantsInsertion()
    {
        var state = await MissingProducer(); state.Catalog!.Policy.DeniedCapabilityIds.Add("allocate");
        Assert.DoesNotContain(RepairPatchTests.Slots(state), s => s.Kind == "prerequisites");
        state.Catalog.Policy.DeniedCapabilityIds.Clear(); state.Catalog.Capabilities.RemoveAll(c => c.Id == "allocate");
        Assert.DoesNotContain(RepairPatchTests.Slots(state), s => s.Kind == "prerequisites");
    }
    [Fact]
    public async Task PrerequisiteClosureFollowsOnlyDeclaredRequiredArtifactInputs()
    {
        var state = await MissingProducer(); var resource = state.Catalog!.Capabilities.Single(c => c.Id == "allocate");
        resource.InputSchema = JsonNode.Parse("""{"type":"object","properties":{"permit":{"type":"string"}},"required":["permit"],"additionalProperties":false}""")!.AsObject();
        resource.ArtifactContract = new(1, [new("resource", "/handle", "materialize")], [new("permit", "/permit", true)]);
        state.Catalog.Capabilities.Add(new() { Id = "authorize", Kind = "tool", StepType = "mcp.call", Server = "renamed", Method = "authorize", EffectKind = "read",
            InputSchema = new() { ["type"] = "object" }, OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"token":{"type":"string"}},"required":["token"]}""")!.AsObject(),
            ArtifactContract = new(1, [new("permit", "/token", "materialize")], []) });
        Diagnose(state); var patch = Prerequisite();
        patch["tasks"]![0]!["inputs"]!.AsArray().Add((JsonNode)new JsonObject { ["name"] = "permit", ["value"] = new JsonObject { ["kind"] = "output", ["source"] = "permission", ["port"] = "token" } });
        patch["tasks"]!.AsArray().Insert(0, new JsonObject { ["id"] = "permission", ["kind"] = "operation", ["objective"] = "Supply required permit", ["operation"] = "authorize", ["inputs"] = new JsonArray(), ["dependsOn"] = new JsonArray() });
        var repaired = Apply(state, "prerequisites", "insert_prerequisites", patch);
        Assert.Equal(new[] { "permission", "new_resource", "use", "untouched" }, repaired.Root.Tasks.Select(t => t.Id));
    }
    [Fact]
    public async Task InvalidLeafTaskKindHasOnlyAnIdentityPreservingReplacement()
    {
        var state = await MissingProducer(); state.Plan!.Root.Tasks.Clear(); state.Plan.Root.Outputs.Clear();
        state.Plan.Root.Tasks.Add(new() { Id = "producer", Kind = "invented", Objective = "Produce resource" }); Diagnose(state);
        var task = Prerequisite()["tasks"]![0]!.DeepClone(); task["id"] = "producer"; task["objective"] = "Produce resource";
        Assert.Equal("operation", Apply(state, "task", "replace_task", task).Root.Tasks[0].Kind);
    }
    [Fact]
    public async Task RetiredRepairEnvelopesCannotBeReinterpretedAsCurrentAuthority()
    {
        var state = await MissingProducer(); var v1 = RepairPatchTests.Request(state);
        v1.Prompt = v1.Prompt.Replace("\"version\":" + PlanningRepairPatch.CurrentVersion, "\"version\":1", StringComparison.Ordinal);
        Assert.Throws<PlanningConflictException>(() => Apply(state, "prerequisites", "insert_prerequisites", Prerequisite(), v1));
        var v2 = new PlanningPrompt(state).Request(); var recovered = PlannerFixture.Clone(state);
        Assert.Empty(new TaskPlanCompiler().Compile(Apply(recovered, "prerequisites", "insert_prerequisites", Prerequisite(), v2), state.Catalog!).Diagnostics);
        recovered.Catalog!.Capabilities.Single(c => c.Id == "allocate").Version = "changed";
        Assert.Throws<PlanningConflictException>(() => PlanningRepairPatch.Verify(recovered, v2));
    }
    [Fact]
    public async Task IssuedStructuralAuthorityAndSchemaRemainUsableWithoutClarificationPermission()
    {
        var state = await MissingProducer();
        var request = new LLMRequest
        {
            StructuredOutputSchema = PlanningRepairPatch.Schema(state, PlanningSchemas.FullProposal(state, compact: false, clarifications: false)),
            Prompt = "Repair\n" + new JsonObject { ["repair"] = new JsonObject { ["version"] = PlanningRepairPatch.CurrentVersion, ["authority"] = PlanningRepairPatch.Authority(state) } }.ToJsonString()
        };
        var schema = request.StructuredOutputSchema.ToJsonString();
        Assert.Null(request.StructuredOutputSchema["properties"]!["clarifications"]);
        var restored = PlannerFixture.Clone(state);
        var repaired = Apply(restored, "prerequisites", "insert_prerequisites", Prerequisite(), request);
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog!).Diagnostics);
        Assert.Equal(schema, request.StructuredOutputSchema.ToJsonString());
        Assert.True(PlanningSchemas.Proposal(state)["properties"]!.AsObject().ContainsKey("clarifications"));
    }
    [Fact]
    public async Task InvalidOperationReplacementPreservesIdentityObjectiveAndDependencies()
    {
        var state = await MissingProducer(); state.Plan!.Root.Tasks.Clear(); state.Plan.Root.Outputs.Clear();
        state.Plan.Root.Tasks.Add(new() { Id = "producer", Objective = "Produce requested resource", Operation = "unknown" }); Diagnose(state);
        var task = Prerequisite()["tasks"]![0]!.DeepClone(); task["id"] = "producer"; task["objective"] = "Produce requested resource";
        Assert.Equal("allocate", Apply(state, "task", "replace_task", task).Root.Tasks[0].Operation);
        task["objective"] = "Different goal";
        Assert.Throws<PlanningResponseException>(() => Apply(state, "task", "replace_task", task));
    }
    [Fact]
    public async Task ReplacementCannotDropOrRewriteAnUnaffectedInput()
    {
        var state = await MissingProducer(); state.Plan!.Root.Tasks.Clear(); state.Plan.Root.Outputs.Clear();
        state.Catalog!.Capabilities.Single(c => c.Id == "allocate").InputSchema = JsonNode.Parse("""{"type":"object","properties":{"label":{"type":"string"}},"additionalProperties":false}""")!.AsObject();
        state.Plan.Root.Tasks.Add(new() { Id = "producer", Objective = "Produce requested resource", Operation = "unknown",
            Inputs = [new("label", new() { Kind = "string", Text = "requested label" })] }); Diagnose(state);
        var task = Prerequisite()["tasks"]![0]!.DeepClone(); task["id"] = "producer"; task["objective"] = "Produce requested resource";
        Assert.Throws<PlanningResponseException>(() => Apply(state, "task", "replace_task", task));
        task["inputs"]!.AsArray().Add((JsonNode)new JsonObject { ["name"] = "label", ["value"] = new JsonObject { ["kind"] = "string", ["text"] = "different" } });
        Assert.Throws<PlanningResponseException>(() => Apply(state, "task", "replace_task", task));
        task["inputs"]![0]!["value"]!["text"] = "requested label";
        Assert.Equal("requested label", Apply(state, "task", "replace_task", task).Root.Tasks[0].Inputs[0].Value.Text);
    }
    [Fact]
    public async Task DiagnosedPureForwarderCanBeRemovedOnlyWithEquivalentReferenceRewrites()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime(); state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = PlanningCorpus.Greeting(); state.Requirements = PlannerFixture.Requirements();
        state.Plan.Root.Tasks.Add(new() { Id = "forward", Kind = "value", Objective = "Forward greeting", DependsOn = ["unknown"], Outputs = [new("copy", new() { Kind = "output", Source = "greet", Port = "message" })] });
        state.Plan.Root.Outputs = [new("result", new() { Kind = "output", Source = "forward", Port = "copy" })]; Diagnose(state);
        var fixedPlan = Apply(state, "forwarder", "remove_forwarder", null);
        Assert.Single(fixedPlan.Root.Tasks); Assert.Equal("greet", fixedPlan.Root.Outputs[0].Value.Source); Assert.Equal("message", fixedPlan.Root.Outputs[0].Value.Port);
        Assert.Equal(2, state.Plan.Root.Tasks.Count);
    }
    [Fact]
    public void OneConsumerDiagnosticAuthorizesEveryRequiredExportBoundary()
    {
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "outer", Kind = "sequence", Objective = "Outer", Body = new() { Tasks = [new() { Id = "inner", Kind = "sequence", Objective = "Inner", Body = PlanningCorpus.Greeting().Root }] } }],
            Outputs = [new("result", new() { Kind = "output", Source = "greet", Port = "message" })] } };
        var scope = TaskPlanRevisions.Scope(plan, [new("TASK_REFERENCE_UNKNOWN", "/root/outputs/result", "Not visible")]);
        Assert.DoesNotContain(scope, p => p.StartsWith("/tasks/inner/body/outputs", StringComparison.Ordinal)); Assert.Contains("/tasks/outer/body/outputs/message", scope);
        Assert.DoesNotContain("/tasks/greet", scope);
    }
    [Fact]
    public async Task EmptyArrayConditionalOutputCompilesAndExecutesBothAlternatives()
    {
        var runtime = new TestRuntime(); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        var plan = new TaskPlan { Inputs = [new() { Name = "enabled", Type = new() { Kind = "boolean" } }], Root = new()
        { Tasks = [new() { Id = "choose", Kind = "conditional", Objective = "Select collection", Condition = new() { Kind = "input", Source = "enabled" },
            Body = new() { Outputs = [new("result", new() { Kind = "array", Items = [new() { Kind = "string", Text = "observed" }] })] },
            Otherwise = new() { Outputs = [new("result", new() { Kind = "array" })] } }], Outputs = [new("result", new() { Kind = "output", Source = "choose", Port = "result" })] } };
        var compilation = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compilation.Diagnostics);
        Assert.Empty(PlanningGeneratedGraph.Validate(compilation.Graph!, catalog));
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compilation.Graph!, catalog)));
        foreach (var enabled in new[] { true, false })
        {
            var result = await new WorkflowEngine().ExecuteAsync(doc.Workflows[doc.Entrypoint!], new JsonObject { ["enabled"] = enabled }, PlannerFixture.Ct);
            Assert.True(result.Success); Assert.Equal(enabled ? "[\"observed\"]" : "[]", result.Outputs!["result"]!.ToJsonString());
        }
    }
}
