using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConfirmationGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("true", 1, "provider_one", "publish")]
    [InlineData("false", 0, "provider_one", "publish")]
    [InlineData("null", 0, "renamed_provider", "apply")]
    [InlineData("\"yes\"", 1, "renamed_provider", "apply")]
    [InlineData("\"uncertain\"", 0, "renamed_provider", "apply")]
    [InlineData("true", 1, "renamed_provider", "apply", true)]
    public async Task DeterministicPermissionGuardControlsOnlyItsDeclaredEffect(string answer, int expectedWrites, string server, string method, bool failWrite = false)
    {
        var (graph, preparation) = Fixture(server, method);
        var writes = 0; var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer(server, new()
        {
            Tools = [new() { Name = method, InputSchema = new JsonObject { ["type"] = "object" } }],
            ToolHandlers = new() { [method] = _ => { writes++; if (failWrite) throw new WorkflowRuntimeException("FIXTURE_WRITE_FAILURE", "Injected failure"); return new McpCallResult { Content = new JsonObject() }; } }
        });
        Assert.Empty(PlanningConfirmationGuards.GraphFindings(graph, preparation));
        var compiler = new PlanningGraphCompiler(); var yaml = compiler.Compile(graph, preparation);
        Assert.Equal(yaml, compiler.Compile(graph, preparation));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var observations = new FinalizerObservations(document.Workflows[document.Entrypoint!].Finally.Single().Id);
        var result = await new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new Permission(answer), Telemetry = observations }
            .ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), Ct);
        Assert.Equal(expectedWrites, writes);
        Assert.Equal(1, observations.Count);
        if (answer is "true" or "false") Assert.True(result.Success == !failWrite, result.Error?.Message);
    }

    [Theory]
    [InlineData("selector")]
    [InlineData("default")]
    [InlineData("branch")]
    [InlineData("loop")]
    [InlineData("finalizer")]
    [InlineData("ownership")]
    public void CompilerRejectsScopeBypasses(string kind)
    {
        var (graph, preparation) = Fixture("neutral", "effect"); var workflow = graph.Workflows[0];
        var guard = workflow.Steps.Single(n => n.Type == "switch");
        var write = guard.Cases[0].Steps.Single();
        if (kind == "selector") guard.Expr = new() { Kind = "string", Text = guard.Cases[0].Value };
        else
        {
            guard.Cases[0].Steps.Clear();
            if (kind == "default") guard.Default.Add(write);
            if (kind == "branch") workflow.Steps.Add(new() { Key = "parallel", Type = "parallel", Branches = [new([write])] });
            if (kind == "loop") workflow.Steps.Add(new() { Key = "loop", Type = "loop.sequential", Steps = [write] });
            if (kind == "finalizer") workflow.Finally.Add(write);
            if (kind == "ownership") { write.OperationIds.Clear(); workflow.Steps.Add(write); }
        }
        Assert.Contains(PlanningConfirmationGuards.GraphFindings(graph, preparation), d => d.Code is "CONFIRMATION_GUARD_INVALID" or "CONFIRMATION_GUARD_REQUIRED");
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(graph, preparation));
    }

    private static (PlanningGraph, PlanningPreparation) Fixture(string server, string method)
    {
        var policy = ConfirmationPolicyTests.Policy("write_policy", "require_confirmation", "write"); policy.PermissionOperationId = "permission";
        var preparation = TypedPlannerTests.Preparation(); preparation.ScopedPolicies = [policy];
        preparation.Capabilities = [new() { Id = "permission", StepType = "human.input", OperationIds = ["permission"], Required = true,
            InputSchema = new() { ["type"] = "object" }, OutputSchema = HumanInputContract.ResolveOutputSchema(HumanInputContract.ConfirmationInput("Approve this effect")) },
            new() { Id = "write", StepType = "mcp.call", Server = server, Method = method, Kind = "tool", OperationIds = ["write"], InputOperationIds = ["permission"],
                EffectKind = "write", Required = true, InputSchema = new() { ["type"] = "object" }, OutputSchema = new() { ["type"] = "object" } }];
        PlanningConfirmationGuards.Lock(preparation);
        var behavior = new PlanningBehaviorPlan { Summary = "Perform an effect with its scoped permission", Workflows = [new()
        {
            Key = "main", Purpose = "Perform an effect", OperationIds = ["permission", "write"],
            Steps = [new() { Key = "permission", Kind = "confirmation", CapabilityId = "permission", OperationIds = ["permission"], Purpose = "Approve the effect" },
                new() { Key = "write", Kind = "operation", CapabilityId = "write", OperationIds = ["write"], Purpose = "Perform the effect" }]
        }] };
        PlanningConfirmationGuards.Wrap(behavior, preparation);
        Assert.Empty(PlanningConfirmationGuards.BehaviorFindings(behavior, preparation));
        var graph = PlanningBehaviorPlans.Display(behavior, preparation);
        graph.Workflows[0].Finally.Add(new() { Key = "cleanup", Type = "set", Input = new() { Kind = "object", Members = [new("finished", new() { Kind = "boolean", Boolean = true })] } });
        graph.Workflows[0].Steps[0].Input = PlanningJsonTransport.Literal(HumanInputContract.ConfirmationInput("Approve the effect"));
        var guard = graph.Workflows[0].Steps.Single(n => n.Type == "switch");
        guard.Expr = PlanningDecisionRouting.Resolve(graph.Workflows[0], guard, preparation, graph);
        return (graph, preparation);
    }

    private sealed class Permission(string answer) : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult(JsonNode.Parse(answer));
    }

    private sealed class FinalizerObservations(string expected) : IWorkflowTelemetry
    {
        internal int Count;
        public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info) => NullTelemetrySpan.Instance;
        public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info) { if (info.StepId == expected) Count++; return NullTelemetrySpan.Instance; }
        public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }
        public void StepEnd(IStepSpan span, StepResultInfo result) { }
    }
}
