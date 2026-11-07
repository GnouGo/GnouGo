using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactLoopCapacityPresentationTests
{
    [Fact]
    public async Task OpenLoopPresentsItsActualCollectionAndCapacityWithoutInventingAnOriginalBound()
    {
        var state = await Ready(open: true);
        var before = PlanningConstruction.Hash(state.Plan);
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        var loop = Assert.Single(context["actions"]!.AsArray());
        Assert.Equal("iterate", loop!["id"]!.ToString());
        Assert.Equal("values", loop["items"]!["source"]!.ToString());
        Assert.Equal(1, loop["maxItems"]!.GetValue<int>());
        Assert.Contains(context["scopes"]!.AsArray(), scope => scope!["id"]!.ToString() == "/tasks/iterate/body" && scope["open"]!.GetValue<bool>());
        Assert.False(context["claims"]!["workflowComplete"]!.GetValue<bool>());
        Assert.Contains("no declared length bound", Assert.Single(context["requirements"]!.AsArray())!["original"]!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("maxItems", Assert.Single(context["inputs"]!.AsArray())!.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(state.Plan!.Inputs[0].Type.MaxItems);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
        Assert.Equal(before, PlanningConstruction.Hash(state.Plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeCapacityNeverReturnsTruncatedSuccess(bool oversized)
    {
        var state = await Ready(open: false);
        var plan = state.Construction!.Candidate!.Plan;
        var compilation = new TaskPlanCompiler().Compile(plan, state.Catalog!, state.Request);
        Assert.Empty(compilation.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, state.Catalog!);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var values = oversized ? new JsonArray(7, 7) : new JsonArray(7);
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["values"] = values }, PlannerFixture.Ct);
        Assert.Equal(!oversized, result.Success);
        if (oversized) { Assert.NotNull(result.Error); Assert.Null(result.Outputs); }
        else Assert.True(JsonNode.DeepEquals(new JsonArray(7), result.Outputs!["result"]));
    }

    [Fact]
    public async Task KnownRequiredCountRemainsVisibleBesideAnIncompatibleCapacity()
    {
        var state = await Ready(open: false, original: "Process all original numeric values [1,2], preserving order and repetitions.");
        var (context, _) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: true);
        Assert.Contains("[1,2]", Assert.Single(context["requirements"]!.AsArray())!["original"]!.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(context["actions"]!.AsArray())!["maxItems"]!.GetValue<int>());
        // Technical validation establishes capacity semantics; business review
        // must assess the explicit two-item obligation independently.
        Assert.Empty(new TaskPlanCompiler().Compile(state.Construction!.Candidate!.Plan, state.Catalog!, state.Request).Diagnostics);
    }

    private static async Task<PlanningSession> Ready(bool open, string original = "Process every value of caller input values, a required numeric array with no declared length bound, preserving order and repetitions.")
    {
        var state = PlannerFixture.Capture(new() { Request = new() { TenantId = "loop-capacity", Prompt = original } });
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var source in state.Construction!.Compact!.Sources) source.Indexed = true;
        state.Construction.Compact.RequirementSources["req_0"] = [state.Construction.Compact.Sources[0].Id];
        var input = new TaskInput { Name = "values", Required = true, Type = new() { Kind = "array", Items = new() { Kind = "number" } } };
        state.Requirements = new() { Inputs = [input], Outputs = [new() { Name = "result", Type = input.Type }], Outcomes = [new("req_0", original)] };
        state.Plan = new() { Inputs = [input] };
        state.Construction.Requirements = [new() { Id = "req_0", Status = "in_progress" }];
        state.Construction.OpenScopes = ["/root"];
        state.Construction.Compact.CurrentStep = new() { Id = "iterate_step", Scope = "/root", Kind = "foreach", Objective = original,
            RequirementIds = ["req_0"], Inputs = [new() { Kind = "input", Source = "values" }], BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["req_0"], CloseScopes = open ? [] : ["/tasks/iterate/body", "/root"], Complete = !open,
            ContinueInScope = open ? "/tasks/iterate/body" : null,
            Tasks = [new() { Id = "iterate", Kind = "foreach", Objective = original, Items = new() { Kind = "input", Source = "values" }, MaxItems = 1,
                Body = open ? new() : new() { Outputs = [new("number", new() { Kind = "item" })] } }],
            Outputs = open ? [] : [new("result", new() { Kind = "output", Source = "iterate", Port = "number" })]
        });
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Construction.Candidate.Plan, state.Catalog, state.Request,
            state.Construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        return state;
    }
}
