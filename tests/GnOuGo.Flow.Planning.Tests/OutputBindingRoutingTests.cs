using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class OutputBindingRoutingTests
{
    [Theory]
    [InlineData("report")]
    [InlineData("résumé/},{_技术")]
    public async Task NullablePublicOutputUsesItsExactTypedRepairSlot(string name)
    {
        var state = await Fixture(name);
        Assert.Contains(state.Diagnostics, finding => finding.Code == "REQUIREMENTS_OUTPUTS_CHANGED" && finding.Location == "/outputs/" + name);
        var baseline = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var request = RepairPatchTests.Request(state);
        var slot = Assert.Single(PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray())!;
        Assert.Equal("/root/outputs/" + name, slot["location"]!.ToString());
        var corrected = Apply(state, RepairPatchTests.Edit(state, "/root/outputs/" + name, "replace", Node(Output("observe", "verified"))));
        var reviewed = PlannerFixture.Clone(state); reviewed.Plan = corrected;
        Assert.Empty(Findings(reviewed));
        Assert.Equal("success", corrected.Root.Outputs[1].Value.Port);
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task DiagnosedOutputDoesNotAuthorizeReinterpretingIndependentStatus()
    {
        var state = await Fixture(); var before = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var outside = new JsonObject { ["slot"] = "/root/outputs/finished", ["action"] = "replace", ["value"] = Node(new() { Kind = "boolean", Boolean = true }) };
        var response = new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null,
            ["patch"] = new JsonObject { ["edits"] = new JsonArray(outside) } };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, RepairPatchTests.Request(state).StructuredOutputSchema!));
        Assert.Equal(before, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal("observe", state.Plan!.Root.Outputs[1].Value.Source);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task RestoredTargetedRequestKeepsExactSchemaAndAuthority()
    {
        var state = await Fixture(); var before = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var issued = RepairPatchTests.Request(state);
        var bytes = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var restored = JsonSerializer.Deserialize(bytes, PlanningJsonContext.Default.LLMRequest)!;
        Assert.Equal(bytes, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.LLMRequest));
        var edit = RepairPatchTests.Edit(state, "/root/outputs/report", "replace", Node(Output("observe", "verified")));
        var corrected = Apply(PlannerFixture.Clone(state), restored, edit);
        var fixedState = PlannerFixture.Clone(state); fixedState.Plan = corrected;
        Assert.Empty(Findings(fixedState));
        Assert.Equal(before, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        var stale = PlannerFixture.Clone(state); stale.Plan!.Root.Tasks[0].Objective += " changed";
        Assert.Throws<PlanningConflictException>(() => PlanningRepairPatch.Verify(stale, restored));
    }

    internal static async Task<PlanningSession> Fixture(string name = "report")
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("records", new() { Tools = [new() { Name = "observe", EffectKind = "read",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"payload":{"type":["string","null"]},"verified":{"type":"string"},"success":{"type":"boolean"}},"required":["payload","verified","success"],"additionalProperties":false}""") }] });
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        var operation = TaskOperations.Describe(catalog.Capabilities.Single(capability => capability.Method == "observe")).Id;
        var state = PlannerFixture.Session(); state.Catalog = catalog;
        state.Requirements = new() { Summary = "Return verified text and its exact observed status", Inputs = [],
            Outputs = [new() { Name = name, Type = new() { Kind = "string" } }, new() { Name = "finished", Type = new() { Kind = "boolean" } }],
            Outcomes = [new("report", "Return the verified producer text") { Commitments = [new(operation) { Result = new("verified", name) }] }] };
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "observe", Objective = "Acquire authoritative text and status", Operation = operation }],
            Outputs = [new(name, Output("observe", "payload")), new("finished", Output("observe", "success"))] } };
        state.Diagnostics = Findings(state); AuthorizeOutputRepairs(state);
        return state;
    }
    internal static void AuthorizeOutputRepairs(PlanningSession state)
    {
        var paths = TaskPlanRevisions.Scope(state.Plan!, state.Diagnostics).Concat(state.Diagnostics
            .Where(diagnostic => diagnostic.Code == "REQUIREMENTS_OUTPUTS_CHANGED" && diagnostic.Location.StartsWith("/outputs/", StringComparison.Ordinal))
            .Select(diagnostic => "/root" + diagnostic.Location)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        TaskPlanRevisions.ValidateEditablePaths(state, paths);
        state.EditablePaths = paths; state.RevisionScope = paths.ToList();
    }
    internal static TaskPlan Apply(PlanningSession state, params JsonObject[] edits) => Apply(state, RepairPatchTests.Request(state), edits);
    internal static TaskPlan Apply(PlanningSession state, LLMRequest request, params JsonObject[] edits)
    {
        var schema = request.StructuredOutputSchema!.AsObject();
        var wire = PlanningCorpus.Transport(new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null,
            ["patch"] = new JsonObject { ["edits"] = new JsonArray(edits.Select(edit => (JsonNode)edit.DeepClone()).ToArray()) } }, schema, schema);
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(wire, schema));
        var patch = wire.Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!.Patch!;
        return PlanningRepairPatch.Apply(state, patch, request);
    }
    internal static List<PlanningDiagnostic> Findings(PlanningSession state)
    {
        var compiled = new TaskPlanCompiler().Compile(state.Plan!, state.Catalog!, state.Request);
        var findings = compiled.Diagnostics.ToList();
        if (compiled.Graph is { } graph) findings.AddRange(PlanningClarifications.OutputFindings(state, graph));
        findings.AddRange(PlanningCommitments.Assess(state).SelectMany(result => result.Diagnostics));
        return findings;
    }
    internal static TaskValue Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    internal static JsonNode Node(TaskValue value) => JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue)!;
}
