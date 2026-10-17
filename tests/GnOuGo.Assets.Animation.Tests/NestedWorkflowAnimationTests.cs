using System.Xml.Linq;
using GnOuGo.Assets.Animation.Preview;
using Xunit;

namespace GnOuGo.Assets.Animation.Tests;

public sealed class NestedWorkflowAnimationTests
{
    [Theory]
    [InlineData("sequence")]
    [InlineData("loop.sequential")]
    [InlineData("loop.parallel")]
    [InlineData("parallel")]
    public void NestedCallsHaveOutgoingAndReturnEdgesWithoutSelfChildren(string container)
    {
        var body = container == "parallel"
            ? "branches:\n          - name: left\n            steps:\n              - { id: call, type: workflow.call, input: { ref: { kind: local, name: leaf } } }\n          - name: right\n            steps:\n              - { id: other, type: workflow.call, input: { ref: { kind: local, name: leaf } } }"
            : "input: { times: 2 }\n        steps:\n          - { id: call, type: workflow.call, input: { ref: { kind: local, name: leaf } } }";
        var yaml = $$"""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - { id: call, type: workflow.call, input: { ref: { kind: local, name: middle } } }
              middle:
                steps:
                  - id: container
                    type: {{container}}
                    {{body}}
              leaf:
                steps:
                  - { id: call, type: mcp.call }
            """;
        var validation = WorkflowPreviewValidator.ParseAndValidate(yaml);
        Assert.True(validation.IsValid);
        var plan = GnouGnouAnimationPlanner.BuildLive(validation, new() { Seed = 7 });
        Assert.Empty(plan.Events);
        var nodes = plan.Nodes.ToDictionary(node => node.Id);
        Assert.Equal(plan.Nodes.Count, nodes.Count);
        var handoffs = plan.Edges.Where(edge => edge.Kind == AnimationFlowEdgeKind.Handoff).ToArray();
        Assert.True(handoffs.Length >= 2);
        foreach (var handoff in handoffs)
        {
            var caller = nodes[handoff.FromNodeId];
            var child = nodes[handoff.ToNodeId];
            Assert.NotEqual(caller.WorkflowInstanceId, child.WorkflowInstanceId);
            Assert.Contains(plan.Edges, edge => edge.Kind == AnimationFlowEdgeKind.Return
                && nodes[edge.FromNodeId].WorkflowInstanceId == child.WorkflowInstanceId
                && nodes[edge.ToNodeId].WorkflowInstanceId == caller.WorkflowInstanceId);
        }
        Assert.All(plan.Edges, edge => { Assert.Contains(edge.FromNodeId, nodes); Assert.Contains(edge.ToNodeId, nodes); });
        _ = XDocument.Parse(GnouGnouAnimationSvgRenderer.Render(plan).Svg);
    }
}
