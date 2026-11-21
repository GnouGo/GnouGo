using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TentativeScopeBusinessRepairTests
{
    [Theory]
    [InlineData("group", false)]
    [InlineData("group", true)]
    [InlineData("sequence", false)]
    [InlineData("sequence", true)]
    public async Task OwnedScopeExportCanBeRepairedWithoutReplacingItsProducer(string kind, bool reconsider)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Plan!.Root.Tasks.Add(new() { Id = "independent", Kind = "value", Objective = "Keep independent accepted work.",
            Outputs = [new("value", new() { Kind = "number", Number = 9 })] });
        var independent = JsonSerializer.Serialize(state.Plan.Root.Tasks.Single(), PlanningJsonContext.Default.PlanTask);
        var step = state.Construction!.Compact!.CurrentStep!;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        step.Kind = kind;
        step.CloseRequirementIds.Clear();
        var body = new TaskScope
        {
            Tasks = [new()
            {
                Id = "compute", Kind = "value", Objective = "Double the supplied number.",
                Outputs = [new("result", new() { Kind = "arithmetic", Text = "multiply", Items =
                    [new() { Kind = "input", Source = "x" }, new() { Kind = "number", Number = 2 }] })]
            }],
            Outputs = [new("result", new() { Kind = "input", Source = "x" })]
        };
        var scope = kind == "group" ? "/groups/process/body" : "/tasks/process/body";
        var extension = new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["work"], CompleteBlock = true };
        if (kind == "group") extension.Groups = [new() { Id = "process", Inputs = [new() { Name = "x", Type = new() { Kind = "number" } }], Body = body }];
        else extension.Tasks = [new() { Id = "process", Kind = "sequence", Objective = "Calculate the requested result.", Body = body }];
        state.Construction.Candidate = PlanningConstruction.Apply(state, extension);
        var baseline = PlanningConstruction.Hash(state.Plan);
        var producer = JsonSerializer.Serialize(body.Tasks.Single(), PlanningJsonContext.Default.PlanTask);
        var reviews = 0; var corrections = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose != "replan")
            {
                Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
                reviews++;
                return new() { Json = reviews == 1 || reconsider && reviews == 2 ? new JsonObject
                {
                    ["decision"] = new JsonObject { ["verdict"] = "needs_change", ["reason"] = "The scope exports the original number instead of the computed double.",
                        ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("compute", scope) }
                } : BusinessReviewProtocolTests.Report("ok") };
            }
            corrections++;
            Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
            var slots = PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray();
            Assert.Contains(slots, slot => slot!["location"]!.ToString() == scope + "/outputs/result");
            Assert.All(slots, slot => Assert.Contains(slot!["location"]!.ToString(),
                new[] { scope + "/outputs/result", "/tasks/compute/outputs/result" }));
            if (reconsider && corrections == 1)
                return new() { Json = PlanningCorpus.Transport(new JsonObject { ["evidenceRequests"] = new JsonArray("task:compute") },
                    request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject()) };
            var selected = slots.Single(slot => slot!["location"]!.ToString() == scope + "/outputs/result")!;
            return new() { Json = PlanningCorpus.Transport(new JsonObject
            {
                ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
                {
                    ["slot"] = selected["id"]!.DeepClone(), ["action"] = "replace",
                    ["value"] = new JsonObject { ["kind"] = "output", ["source"] = "compute", ["port"] = "result" }
                }) }
            }, request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject()) };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW");
        Assert.Contains(scope + "/outputs/result", state.Construction!.Candidate!.AuthorizedPaths!);
        state = await BusinessReviewProtocolTests.Advance(BusinessReviewProtocolTests.Clone(state), runtime);
        if (reconsider)
        {
            Assert.Equal(0, state.ReplanAttempts);
            Assert.True(HybridWorkflowPlanner.HasPendingBusinessReconsideration(state));
            state = await BusinessReviewProtocolTests.Advance(BusinessReviewProtocolTests.Clone(state), runtime);
            Assert.Equal(PlanningStatus.Generating, state.Status);
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW");
            state = await BusinessReviewProtocolTests.Advance(BusinessReviewProtocolTests.Clone(state), runtime);
        }
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.False(state.Construction!.Candidate!.BusinessReviewed);
        Assert.Equal(independent, JsonSerializer.Serialize(state.Construction.Candidate.Plan.Root.Tasks.Single(task => task.Id == "independent"),
            PlanningJsonContext.Default.PlanTask));
        Assert.Equal(producer, JsonSerializer.Serialize(new TaskPlanSymbols(state.Construction.Candidate.Plan).Tasks["compute"].Task,
            PlanningJsonContext.Default.PlanTask));
        state = await BusinessReviewProtocolTests.Advance(BusinessReviewProtocolTests.Clone(state), runtime);
        Assert.Null(state.Construction!.Candidate);
        Assert.Single(state.Construction.Checkpoints);
        Assert.Equal(reconsider ? 3 : 2, reviews);
        var plan = PlanningConstruction.Clone(state.Plan!);
        if (kind == "group") plan.Root.Tasks.Add(new() { Id = "invoke", Kind = "call", Group = "process", Objective = "Invoke the reusable calculation.",
            Inputs = [new("x", new() { Kind = "input", Source = "x" })] });
        plan.Root.Outputs = [new("result", new() { Kind = "output", Source = kind == "group" ? "invoke" : "process", Port = "result" })];
        var compiled = new TaskPlanCompiler().Compile(plan, state.Catalog!);
        Assert.Empty(compiled.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, state.Catalog!);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["x"] = 7 }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(14, result.Outputs!["result"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedScopesAndMissingExportsDoNotGainAutomaticEditAuthority(bool accepted)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        var group = new TaskGroup { Id = "retained", Inputs = [new() { Name = "x", Type = new() { Kind = "number" } }], Body = new()
        {
            Tasks = accepted ? [] : state.Construction!.Candidate!.Plan.Root.Tasks,
            Outputs = accepted ? [new("result", new() { Kind = "number", Number = 9 })] : []
        } };
        if (accepted) state.Plan!.Groups.Add(group);
        var selected = state.Construction!.Compact!.CurrentStep!;
        selected.BaseHash = PlanningConstruction.Hash(state.Plan);
        selected.CloseRequirementIds.Clear();
        selected.Kind = accepted ? "call" : "group";
        if (accepted) selected.GroupId = "retained";
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = true,
            Tasks = accepted ? [new() { Id = "consume_retained", Kind = "call", Group = "retained", Objective = "Use the retained calculation.",
                Inputs = [new("x", new() { Kind = "input", Source = "x" })] }] : [], Groups = accepted ? [] : [group]
        });
        var baseline = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (_, _) => new() { Json = new JsonObject { ["decision"] = new JsonObject
        {
            ["verdict"] = "needs_change", ["reason"] = "The cited scope does not return the requested calculated value.",
            ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("/groups/retained/body")
        } } };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Null(state.Construction!.Candidate!.AuthorizedPaths);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "BUSINESS_REVIEW_REQUIRED");
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(0, state.ReplanAttempts);
    }
}
