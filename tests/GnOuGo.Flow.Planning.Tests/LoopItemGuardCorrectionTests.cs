using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LoopItemGuardCorrectionTests
{
    private const string Path = "/tasks/use/requires";
    private static TaskValue Field(string name) => new() { Kind = "field", Port = name, Items = [new() { Kind = "item" }] };
    private static TaskValue Present() => new() { Kind = "predicate", Predicate = "not_equal", Items = [Field("reference"), new() { Kind = "null" }] };
    private static TaskValue Conjunction(TaskValue flag) => new() { Kind = "predicate", Predicate = "and", Items = [Present(), flag] };

    [Theory]
    [InlineData("observed", true, true)]
    [InlineData(null, true, false)]
    [InlineData("observed", false, false)]
    public async Task TypedItemFieldRepairsThroughOneGuardSlotAndRetainsRuntimeAssertionAndCleanup(string? reference, bool allowed, bool succeeds)
    {
        var (state, runtime) = await Fixture();
        var baseline = PlanningConstruction.Hash(state.Plan);
        var original = JsonSerializer.SerializeToNode(state.Construction!.Candidate!.Plan, PlanningJsonContext.Default.TaskPlan)!;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            return Reply(request, Conjunction(Field("allowed")));
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        var corrected = state.Construction!.Candidate!.Plan;
        var loop = original["root"]!["tasks"]!.AsArray().Single(task => task!["id"]!.ToString() == "loop")!;
        loop["body"]!["tasks"]![0]!["requires"] = JsonSerializer.SerializeToNode(Conjunction(Field("allowed")), PlanningJsonContext.Default.TaskValue);
        Assert.True(JsonNode.DeepEquals(original, JsonSerializer.SerializeToNode(corrected, PlanningJsonContext.Default.TaskPlan)));
        Assert.False(state.Construction.Candidate.BusinessReviewed);
        Assert.Null(state.ApprovedHash);
        var compiled = new TaskPlanCompiler().Compile(corrected, state.Catalog!, state.Request);
        Assert.Empty(compiled.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, state.Catalog!);
        var result = await LookupCompilationTests.Execute(yaml, new JsonObject { ["x"] = 7,
            ["items"] = new JsonArray(new JsonObject { ["reference"] = reference, ["allowed"] = allowed }) }, new WorkflowEngine());
        Assert.Equal(succeeds, result.Success);
        Assert.Contains(result.StepResults, step => step.Output?["cleaned"]?.GetValue<bool>() == true);
        if (!succeeds) Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("other-producer")]
    public async Task ItemOriginDoesNotPermitDroppingOrReplacingTheOriginalAssertion(string change)
    {
        var (state, runtime) = await Fixture();
        var original = PlanningConstruction.Hash(state.Construction!.Candidate!.Plan);
        runtime.Respond = (request, _) => Reply(request, change switch
        {
            "drop" => new() { Kind = "boolean", Boolean = true },
            "other-producer" => Conjunction(new() { Kind = "output", Source = "independent", Port = "allowed" }),
            _ => Conjunction(Field("undeclared"))
        });
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.NotEmpty(state.Diagnostics);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(original, PlanningConstruction.Hash(state.Construction!.Candidate!.Plan));
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task NonnullableItemFieldsDoNotGainAnInferredBusinessCondition()
    {
        var (state, runtime) = await Fixture(nullable: false);
        runtime.Respond = (_, _) => throw new InvalidOperationException("Unsupported assertions must stop before dispatch.");
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "AUTOMATIC_GUARD_CORRECTION_UNSAFE");
        Assert.Equal(0, state.ModelCalls);
    }

    private static LLMResponse Reply(LLMRequest request, TaskValue guard)
    {
        var slots = PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray();
        var slot = Assert.Single(slots)!;
        Assert.Equal(Path, slot["location"]!.ToString());
        return new() { Json = PlanningCorpus.Transport(new JsonObject { ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
        {
            ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace", ["value"] = JsonSerializer.SerializeToNode(guard, PlanningJsonContext.Default.TaskValue)
        }) } }, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Fixture(bool nullable = true)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Request.Options["compilation_profile"] = TaskPlanCompiler.CompactProfile;
        var input = new TaskInput { Name = "items", Required = true, Type = new() { Kind = "array", Items = new()
        {
            Kind = "object", Fields = [new() { Name = "reference", Type = new() { Kind = "string", Nullable = nullable } },
                new() { Name = "allowed", Type = new() { Kind = "boolean" } }]
        }, MaxItems = 2 } };
        state.Requirements!.Inputs!.Add(input); state.Plan!.Inputs.Add(input);
        state.Plan.Root.Tasks.Add(new() { Id = "independent", Kind = "value", Objective = "Retain independent accepted evidence",
            Outputs = [new("allowed", new() { Kind = "boolean", Boolean = true })] });
        state.Plan.Root.Always.Add(new() { Id = "cleanup", Kind = "value", Objective = "Finish on success and assertion failure",
            Outputs = [new("cleaned", new() { Kind = "boolean", Boolean = true })] });
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "foreach"; step.CloseRequirementIds.Clear(); step.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"], CompleteBlock = false,
            Tasks = [new() { Id = "loop", Kind = "foreach", Objective = "Use the supplied elements", Items = new() { Kind = "input", Source = "items" }, MaxItems = 2,
                Body = new() { Tasks = [new() { Id = "use", Kind = "value", Objective = "Use the observed element only after its assertion",
                    Requires = Field("reference"), Outputs = [new("value", new() { Kind = "number", Number = 7 })] }],
                    Outputs = [new("value", new() { Kind = "output", Source = "use", Port = "value" })] } }] });
        state.Construction.Candidate.Diagnostics = TaskPlanCompiler.ValidateConstruction(state.Construction.Candidate.Plan, state.Catalog!, state.Request,
            state.Construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics.ToList();
        Assert.Contains(state.Construction.Candidate.Diagnostics, diagnostic => diagnostic.Code == "TASK_CONDITION_TYPE" && diagnostic.Location == Path);
        state.Diagnostics = state.Construction.Candidate.Diagnostics.ToList();
        return (state, runtime);
    }
}
