using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FinalizerBindingTests
{
    [Theory]
    [InlineData(false, false, false, 1)]
    [InlineData(false, false, true, 1)]
    [InlineData(false, true, false, 0)]
    [InlineData(true, false, false, 1)]
    [InlineData(true, false, true, 1)]
    [InlineData(true, true, false, 0)]
    public async Task OwnedResourceGuardAllowsCleanupAfterLaterFailureButNotFailedSetup(bool nested, bool setupFails, bool workFails, int cleanups)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; var preparation = Preparation();
        workflow.Outputs.Clear();
        var sourceSchema = JsonNode.Parse("""{"type":"object","properties":{"handle":{"type":"string"}},"required":["handle"],"additionalProperties":false}""")!.AsObject();
        var cleanupSchema = JsonNode.Parse("""{"type":"object","properties":{"handle":{"type":"string"}},"required":["handle"],"additionalProperties":false}""")!.AsObject();
        preparation.Capabilities = [new() { Id = "create", Server = "fixture", Method = "create", Kind = "tool", StepType = "mcp.call", OperationIds = ["acquire"],
            InputSchema = new() { ["type"] = "object" }, OutputSchema = sourceSchema, ArtifactContract = new(1, [new("owned.resource", "/handle", "materialize")], []) },
            new() { Id = "release", Server = "fixture", Method = "release", Kind = "tool", StepType = "mcp.call", OperationIds = ["release"], InputOperationIds = ["acquire"],
                InputSchema = cleanupSchema, OutputSchema = new() { ["type"] = "boolean" }, ArtifactContract = new(1, [], [new("owned.resource", "/handle", true)]) }];
        workflow.Steps = [new() { Key = "created", Type = "mcp.call", CapabilityId = "create", OperationIds = ["acquire"], Input = Obj(("request", Obj())) },
            new() { Key = "work", Type = "set", Input = Obj(("value", new() { Kind = "compute", Text = workFails ? "(() => { throw new Error('work failed'); })()" : "true" })) }];
        if (nested) workflow.Steps = [new() { Key = "setup_sequence", Type = "sequence", Steps = workflow.Steps }];
        var cleanup = new PlanningNode { Key = "cleanup", Type = "mcp.call", CapabilityId = "release", OperationIds = ["release"],
            Input = Obj(("request", Obj(("handle", new() { Kind = "output", Source = "created", Path = ["handle"] })))) };
        workflow.Finally = [cleanup];
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, preparation, graph, cleanup.Key).Values, b => b.Value.Source == "created");
        PlanningSkeletonInputs.GuardFinalizers(workflow, preparation);
        Assert.Contains(PlanningDataflow.Index(workflow, preparation, graph, cleanup.Key).Values, b => b.Value.Source == "created" && b.Value.Path.SequenceEqual(["handle"]));
        Assert.Contains(PlanningDataflow.Index(workflow, preparation, graph, PlanningDataflow.WorkflowOutputs).Values, b => b.Value.Source == "cleanup");
        workflow.Outputs = [new() { Name = "cleanupResult", Schema = new() { Type = "boolean" }, Value = new() { Kind = "output", Source = "cleanup" } }];
        var behavior = new PlanningBehaviorPlan { Workflows = [new() { Key = workflow.Key, OperationIds = workflow.OperationIds.ToList(),
            Outputs = [new("cleanupResult", "Original cleanup result", true)], Steps = workflow.Steps.Select(Behavior).ToList(), Finally = workflow.Finally.Select(Behavior).ToList() }] };
        Assert.Empty(PlanningBehaviorPlans.ValidateImplementation(behavior, graph, preparation));
        var fingerprint = PlanningGraphSkeleton.Fingerprint(graph);
        PlanningSkeletonInputs.GuardFinalizers(workflow, preparation);
        Assert.Equal(fingerprint, PlanningGraphSkeleton.Fingerprint(graph));
        var factory = new InMemoryMcpClientFactory(); var observed = 0;
        factory.RegisterServer("fixture", new() { Tools = [new() { Name = "create", InputSchema = preparation.Capabilities[0].InputSchema, OutputSchema = sourceSchema }, new() { Name = "release", InputSchema = cleanupSchema }],
            ToolHandlers = new()
            {
                ["create"] = _ => setupFails ? throw new InvalidOperationException("setup failed") : new() { Content = new JsonObject { ["handle"] = "owned-by-this-execution" } },
                ["release"] = input => { Assert.Equal("owned-by-this-execution", input!["handle"]!.ToString()); observed++; return new() { Content = JsonValue.Create(true) }; }
            } });
        var yaml = new PlanningGraphCompiler().Compile(graph, preparation);
        Assert.Equal(yaml, new PlanningGraphCompiler().Compile(graph, preparation));
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.Equal(!setupFails && !workFails, result.Success);
        Assert.Equal(cleanups, observed);
        if (result.Success) Assert.True(result.Outputs!["cleanupResult"]!.GetValue<bool>());
        else Assert.Null(result.Outputs);
        if (workFails) Assert.Contains("work failed", result.Error!.Message);
        cleanup.If = new() { Kind = "boolean", Boolean = true };
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, preparation, graph, cleanup.Key).Values, b => b.Value.Source == "created");
        Assert.Contains(PlanningBehaviorPlans.ValidateImplementation(behavior, graph, preparation), d => d.Code == "BEHAVIOR_IMPLEMENTATION_CHANGED");
        cleanup.If = null;
        preparation.Capabilities[0].ArtifactContract = new(1, [new("owned.resource", "/handle", "reference")], []);
        PlanningSkeletonInputs.GuardFinalizers(workflow, preparation);
        Assert.Null(cleanup.If); // An observed external resource is not a materialization owned by this execution.

        static PlanningBehaviorNode Behavior(PlanningNode node) => new() { Key = node.Key, Kind = node.Type == "sequence" ? "sequence" : "operation", CapabilityId = node.CapabilityId,
            OperationIds = node.OperationIds.ToList(), Steps = node.Steps.Select(Behavior).ToList() };
    }
}
