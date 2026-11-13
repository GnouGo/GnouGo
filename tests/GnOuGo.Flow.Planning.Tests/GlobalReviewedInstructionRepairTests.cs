using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class GlobalReviewedInstructionRepairTests
{
    private const string CorrectedGoal = "Multiply the supplied number by two.\nReturn the computed number as result.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalFindingRepairsOnlyCitedBehaviorAndRequiresRenewedReview(bool callerRestricted)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Requirements!.Outcomes.RemoveAll(requirement => requirement.Id != "work");
        state.Construction!.Requirements.RemoveAll(requirement => requirement.Id != "work");
        state.Plan!.Root.Tasks.Add(new() { Id = "independent", Kind = "value", Objective = "Keep independent accepted work",
            Outputs = [new("value", new() { Kind = "number", Number = 9 })] });
        var independent = JsonSerializer.Serialize(state.Plan.Root.Tasks[0], PlanningJsonContext.Default.PlanTask);
        var capability = new PlanningCapability { Id = "send", Kind = "tool", StepType = "mcp.call", Server = "arbitrary", Method = "send", EffectKind = "write",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"number"}},"required":["value"],"additionalProperties":false}""")!.AsObject(),
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } };
        state.Catalog!.Capabilities.Add(capability); state.Catalog.AllowedStepTypes.Add("mcp.call"); state.Discovery.Resolved.Add(capability);
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "transform"; step.OperationIds = ["send"]; step.CloseRequirementIds.Clear(); step.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"], CompleteBlock = true, Tasks = [
            new() { Id = "compute", Kind = "transform", Mode = "interpret", Objective = "Return the supplied number unchanged.",
                Inputs = [new("x", new() { Kind = "input", Source = "x" })],
                ResultType = new() { Kind = "object", Fields = [new() { Name = "result", Type = new() { Kind = "number" } }] } },
            new() { Id = "consume", Kind = "operation", Operation = "send", Objective = "Consume the calculated number",
                Inputs = [new("value", new() { Kind = "output", Source = "compute", Port = "result" })] }
        ] });
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.NotNull(HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint));
        Assert.False(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>());
        var baseline = PlanningConstruction.Hash(state.Plan);
        var consumer = JsonSerializer.Serialize(state.Plan!.Root.Tasks.Single(task => task.Id == "consume"), PlanningJsonContext.Default.PlanTask);
        var contract = JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability);
        PlanningCompactConstruction.SelectCompletion(state, ["work"]);
        state = await BusinessReviewProtocolTests.Advance(state, runtime); // Mechanical global-review candidate.
        if (callerRestricted) state.EditablePaths = ["/tasks/independent/outputs/value"];
        var globals = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose != "replan")
            {
                Assert.Equal(PlanningConstruction.GlobalPurpose, purpose);
                return new() { Json = ++globals == 1 ? new JsonObject { ["decision"] = new JsonObject
                {
                    ["verdict"] = "needs_change", ["reason"] = "The executable transform returns the original number; the unchanged consumer needs its double.",
                    ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("compute", "consume")
                } } : BusinessReviewProtocolTests.Report("ok") };
            }
            var slots = PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray();
            Assert.All(slots, slot => Assert.Contains(slot!["location"]!.ToString(),
                new[] { "/tasks/compute/inputs", "/tasks/compute/objective", "/tasks/consume/inputs" }));
            var selected = slots.Single(slot => slot!["location"]!.ToString() == "/tasks/compute/objective")!;
            return new() { Json = PlanningCorpus.Transport(new JsonObject { ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
            {
                ["slot"] = selected["id"]!.DeepClone(), ["action"] = "replace", ["value"] = CorrectedGoal
            }) } }, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        if (callerRestricted)
        {
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "BUSINESS_REVIEW_REQUIRED");
            Assert.Null(state.Construction!.Candidate!.AuthorizedPaths);
            return;
        }
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW");
        Assert.Contains("/tasks/compute/objective", state.Construction!.Candidate!.AuthorizedPaths!);
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.False(state.Construction!.Candidate!.BusinessReviewed);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Null(state.ApprovedHash);
        Assert.Null(HybridWorkflowPlanner.ReviewedCheckpoint(state, state.Construction.Checkpoints[0]));
        Assert.Equal(CorrectedGoal, new TaskPlanSymbols(state.Construction.Candidate.Plan).Tasks["compute"].Task.Objective);
        Assert.Equal(consumer, JsonSerializer.Serialize(new TaskPlanSymbols(state.Construction.Candidate.Plan).Tasks["consume"].Task, PlanningJsonContext.Default.PlanTask));
        Assert.Equal(independent, JsonSerializer.Serialize(state.Construction.Candidate.Plan.Root.Tasks.Single(task => task.Id == "independent"), PlanningJsonContext.Default.PlanTask));
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, globals);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        PlanningArtifactApproval.Verify(state);
        Assert.Equal(contract, JsonSerializer.Serialize(state.Catalog!.Capabilities.Single(item => item.Id == "send"), PlanningJsonContext.Default.PlanningCapability));
        var calls = 0; var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new() { Tools = [new() { Name = "send", InputSchema = capability.InputSchema.DeepClone(), OutputSchema = capability.OutputSchema.DeepClone() }],
            ToolHandlers = new() { ["send"] = input => { Assert.Equal(14, input!["value"]!.GetValue<int>()); calls++; return new() { Content = new JsonObject() }; } } });
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var model = new DoubleModel();
        var result = await new WorkflowEngine { McpClientFactory = factory, LLMClient = model, HumanInputProvider = new PlanningCorpus.Human(true),
            LlmDefaults = new() { Model = "deterministic" } }.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["x"] = 7 }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, calls); Assert.Equal(1, model.Calls);
    }

    [Theory]
    [InlineData("transform", true)]
    [InlineData("value", false)]
    [InlineData("operation", false)]
    [InlineData("call", false)]
    public async Task ExplicitInstructionSlotUsesTextAndDoesNotAuthorizeNonExecutableLabels(string kind, bool permitted)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Plan!.Root.Tasks = [new() { Id = "focus", Kind = kind, Objective = "Original instruction" }];
        var paths = new[] { "/tasks/focus/objective" };
        if (!permitted) { Assert.Throws<ArgumentException>(() => TaskPlanRevisions.ValidateEditablePaths(state, paths)); return; }
        TaskPlanRevisions.ValidateEditablePaths(state, paths);
        state.EditablePaths = paths.ToList(); state.RevisionScope = paths.ToList();
        var definitions = PlanningRepairPatch.Template(state)["$defs"]!.AsObject();
        var slot = Assert.Single(PlanningRepairPatch.Slots(state, definitions));
        var schema = slot.ValueSchema.DeepClone().AsObject(); schema["$defs"] = definitions.DeepClone();
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonValue.Create(CorrectedGoal), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["kind"] = "string", ["text"] = CorrectedGoal }, schema));
    }

    private sealed class DoubleModel : ILLMClient, ILLMCapabilityResolver
    {
        internal int Calls;
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++; Assert.Contains("Multiply the supplied number by two", request.Prompt, StringComparison.Ordinal);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["result"] = 14 } });
        }
    }
}
