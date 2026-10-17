using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Assets.Animation;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class NestedWorkflowAnimationExecutionTests
{
    [Fact]
    public void RetainedGeneratedWorkflowPreparesSceneWithoutBusinessInputs()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Animation", "nested-generated-workflow.json")))!;
        var bridge = AgentWorkflowAnimationBridge.Create(fixture["yaml"]!.ToString(), "main", "sanitized-replay", _ => { }, out var prepared);
        Assert.NotNull(bridge);
        Assert.True(prepared.Animation!.Prepared!.LaneCount >= 3);
        _ = XDocument.Parse(prepared.Animation.Prepared.Svg);
    }

    [Theory]
    [InlineData("sequence", false)]
    [InlineData("loop.sequential", false)]
    [InlineData("loop.parallel", false)]
    [InlineData("parallel", false)]
    [InlineData("sequence", true)]
    public async Task RealEngineDrivesNestedMovementWaitingReturnsAndTerminalState(string container, bool fail)
    {
        var body = container == "parallel"
            ? "branches:\n          - name: left\n            steps:\n              - { id: same, type: workflow.call, input: { ref: { kind: local, name: leaf } } }\n          - name: right\n            steps:\n              - { id: other, type: workflow.call, input: { ref: { kind: local, name: leaf } } }"
            : "input: { times: 2 }\n        steps:\n          - { id: same, type: workflow.call, input: { ref: { kind: local, name: leaf } } }";
        if (container == "loop.parallel") body = body.Replace("times: 2", "items: [one, two]", StringComparison.Ordinal);
        var yaml = $$"""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - { id: same, type: workflow.call, input: { ref: { kind: local, name: middle } } }
              middle:
                steps:
                  - id: container
                    type: {{container}}
                    {{body}}
              leaf:
                steps:
                  - { id: question, type: human.input, input: { mode: choice, prompt: "Continue local test?", choices: [yes, no] } }
                  - { id: same, type: mcp.call, input: { server: local-test, tool: finish } }
            """;
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var events = new ConcurrentQueue<SmartFlowEvent>();
        var bridge = AgentWorkflowAnimationBridge.Create(yaml, "main", "nested-execution", events.Enqueue, out var prepared);
        Assert.True(prepared.Animation!.Prepared!.LaneCount >= 3);
        var calls = 0;
        var session = new FakeMcpSession("local-test").OnTool("finish", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new McpCallResult { IsError = fail, Content = new JsonObject { ["success"] = !fail, ["value"] = "local result", ["error"] = fail ? "deterministic failure" : null } });
        });
        var human = new DeterministicHuman();
        var engine = new WorkflowEngine
        {
            McpClientFactory = new FakeMcpClientFactory(session), HumanInputProvider = human,
            Telemetry = new AgentStreamingTelemetry(events.Enqueue, bridge)
        };
        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success == !fail, result.Error?.Message);
        Assert.Equal(container == "sequence" ? 1 : 2, calls);
        Assert.Equal(calls, human.Calls);
        var animation = events.Select(e => e.Animation?.Event).OfType<SimulationEvent>().ToArray();
        Assert.Contains(animation, e => e.Type == SimulationEventTypes.HumanInputWaiting);
        Assert.Contains(animation, e => e.Type == SimulationEventTypes.HumanInputResumed);
        Assert.Contains(animation, e => e.Type == SimulationEventTypes.ActorMoved);
        Assert.Contains(animation, e => e.Type == SimulationEventTypes.TaskHandedOff && e.WorkflowName == "middle");
        Assert.All(animation.Where(e => e.Type == SimulationEventTypes.TaskHandedOff), e => Assert.NotEqual(e.ActorId, e.TargetActorId));
        Assert.Contains(animation, e => e.Type == SimulationEventTypes.SimulationCompleted && e.Status == (fail ? SimulationStatus.Failed : SimulationStatus.Succeeded));
        if (fail) Assert.Contains(animation, e => e.Type == SimulationEventTypes.TaskDropped);
        else
        {
            var handoffs = animation.Where(e => e.Type == SimulationEventTypes.TaskHandedOff).ToArray();
            Assert.Contains(handoffs, outgoing => handoffs.Any(returning => returning.ActorId == outgoing.TargetActorId && returning.TargetActorId == outgoing.ActorId));
        }

        // Optional browser smoke capture contains only this synthetic workflow and telemetry.
        var captureDirectory = Environment.GetEnvironmentVariable("GNOUGO_ANIMATION_CAPTURE");
        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
            var capture = new JsonObject
            {
                ["prepared"] = JsonSerializer.SerializeToNode(prepared.Animation, AgentAnimationJsonContext.Default.AnimationStreamPayload),
                ["updates"] = new JsonArray(events.Where(e => e.Animation is not null).Select(e => JsonSerializer.SerializeToNode(e.Animation, AgentAnimationJsonContext.Default.AnimationStreamPayload)).ToArray())
            };
            await File.WriteAllTextAsync(Path.Combine(captureDirectory, $"{container}-{fail}.json"), capture.ToJsonString(), TestContext.Current.CancellationToken);
        }
    }

    private sealed class DeterministicHuman : IHumanInputProvider
    {
        private int _calls;
        public int Calls => _calls;
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<JsonNode?>(new JsonObject { ["response"] = "yes" });
        }
    }
}
