using System.Text.Json.Nodes;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AgentDatabaseWorkflowCallResolverTests
{
    [Fact]
    public async Task WorkflowCall_Database_LoadsWorkflowByAgentName()
    {
        var repository = new FakeAgentRepository(new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "math-agent",
            Workflow = """
version: 1
workflows:
  main:
    steps:
      - id: out
        type: set
        input: { value: "${data.inputs.x * 3}" }
    outputs:
      value: "${data.steps.out.value}"
"""
        });

        var compiled = CompileDoc("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref: { kind: database, agent: math-agent }
          args: { x: 7 }
    outputs:
      value: "${data.steps.call.outputs.value}"
      workflow: "${data.steps.call.workflow}"
""");

        var engine = new WorkflowEngine
        {
            WorkflowCallResolver = new AgentDatabaseWorkflowCallResolver(repository)
        };

        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(21, result.Outputs!["value"]!.GetValue<int>());
        Assert.Equal("math-agent", result.Outputs!["workflow"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowCall_DatatabaseAlias_IsAcceptedForBackwardTypoTolerance()
    {
        var repository = new FakeAgentRepository(new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "alias-agent",
            Workflow = "version: 1\nworkflows: { main: { steps: [] } }"
        });

        var compiled = CompileDoc("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref: { kind: datatabase, name: alias-agent }
""");

        var engine = new WorkflowEngine
        {
            WorkflowCallResolver = new AgentDatabaseWorkflowCallResolver(repository)
        };

        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    private static CompiledDocument CompileDoc(string yaml)
        => new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));

    private sealed class FakeAgentRepository : IAgentRepository
    {
        private readonly Dictionary<string, AgentDefinition> _agents;

        public FakeAgentRepository(params AgentDefinition[] agents)
        {
            _agents = agents.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        }

        public Task<AgentDefinition> AddAgentAsync(string name, string workflow, string? originalPrompt = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentDefinition> AddAgentBundleAsync(string name, string workflow, IReadOnlyDictionary<string, string>? workflows, string? originalPrompt = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentDefinition> UpdateAgentAsync(Guid id, string name, string workflow, string? originalPrompt = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<List<AgentDefinition>> ListAgentsAsync(CancellationToken ct = default)
            => Task.FromResult(_agents.Values.ToList());

        public Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_agents.GetValueOrDefault(name));

        public Task DeleteAgentAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
