using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

// Sanitized reproductions of retained schema-9 failures. No benchmark identities,
// provider names, private requests or generated responses are required.
public sealed class AgentScopeApprovalTests
{
    [Theory]
    [InlineData("input", "folder")]
    [InlineData("output", "prepare")]
    [InlineData("expression", "data.inputs.folder")]
    [InlineData("string", "${data.inputs.folder}")]
    [InlineData("string", " ")]
    [InlineData("null", null)]
    public void AgentWorkspaceMustBeAnExplicitNonemptyLiteral(string kind, string? value)
    {
        var graph = AgentGraph();
        graph.Workflows[0].Steps[0].Input.Members.Add(new("workspace", new() { Kind = kind, Source = value, Text = value }));
        Assert.Contains(PlanningGeneratedGraph.Validate(graph, AgentCatalog()),
            d => d.Code == "AGENT_SCOPE_DYNAMIC" && d.Location.EndsWith("/workspace", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkspaceIsVisibleAndChangingItInvalidatesApproval()
    {
        var graph = AgentGraph(); var stage = graph.Workflows[0].Steps[0];
        stage.Input.Members.Add(new("workspace", PlanningCorpus.Text("workspaces/approved")));
        Assert.Empty(PlanningGeneratedGraph.Validate(graph, AgentCatalog()));
        var session = new PlanningSession { Graph = graph, Catalog = AgentCatalog(), Yaml = "reviewed artifact" };
        var hash = session.ComputeArtifactHash();
        Assert.Contains("workspaces/approved", PlanningReviewFormatter.Diagram(graph));
        stage.Input.Members[^1] = new("workspace", PlanningCorpus.Text("workspaces/different"));
        Assert.NotEqual(hash, session.ComputeArtifactHash());
    }

    private static PlanningCatalog AgentCatalog() => new() { Capabilities = [new() { Id = "agent", StepType = "agent.run" }] };
    private static PlanningGraph AgentGraph() => new() { Workflows = [new() { Steps = [new() { Key = "work", Type = "agent.run", CapabilityId = "agent",
        Input = PlanningCorpus.Obj(("objective", PlanningCorpus.Text("Update the project")), ("capabilities", new() { Kind = "array" }),
            ("budget", PlanningCorpus.Obj()), ("verification", new() { Kind = "array" }), ("output_schema", PlanningCorpus.Obj())) }] }] };
}
