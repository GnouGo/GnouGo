using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Agent.Server.Tests;

public sealed class DatabaseAgentWorkflowCandidateProviderTests
{
    [Fact]
    public async Task GetCandidatesAsync_ParsesSkillMetadataAndAppliesTagFilters()
    {
        var repository = new FakeAgentRepository(
            new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = "GitAgent",
                Workflow = """
version: 1
skill:
  description: Inspects git repositories.
  tags: [git, code]
  inputs:
    prompt: { type: string, required: true }
  outputs:
    answer: { type: string }
workflows:
  main:
    steps:
      - id: s1
        type: template.render
""",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = "DocumentAgent",
                Workflow = """
version: 1
skill:
  description: Answers document questions.
  tags: [documents]
workflows:
  main:
    steps:
      - id: s1
        type: template.render
""",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

        var services = new ServiceCollection()
            .AddSingleton<IAgentRepository>(repository)
            .BuildServiceProvider();
        var provider = new DatabaseAgentWorkflowCandidateProvider(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatabaseAgentWorkflowCandidateProvider>.Instance);

        var candidates = await provider.GetCandidatesAsync(new WorkflowRouteCandidateQuery
        {
            Ref = new System.Text.Json.Nodes.JsonObject { ["kind"] = "database" },
            Kind = "database",
            TagsAny = ["git"]
        }, CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal("database:GitAgent", candidate.Id);
        Assert.Equal("GitAgent", candidate.Name);
        Assert.Equal("Inspects git repositories.", candidate.Description);
        Assert.Equal(["git", "code"], candidate.Tags);
        Assert.Equal("database", candidate.Ref["kind"]!.GetValue<string>());
        Assert.Equal("GitAgent", candidate.Ref["agent"]!.GetValue<string>());
        Assert.NotNull(candidate.Inputs);
        Assert.NotNull(candidate.Outputs);
    }

    private sealed class FakeAgentRepository : IAgentRepository
    {
        private readonly List<AgentDefinition> _agents;

        public FakeAgentRepository(params AgentDefinition[] agents)
        {
            _agents = agents.ToList();
        }

        public Task<AgentDefinition> AddAgentAsync(string name, string workflow, string? originalPrompt = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentDefinition> UpdateAgentAsync(Guid id, string name, string workflow, string? originalPrompt = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<List<AgentDefinition>> ListAgentsAsync(CancellationToken ct = default)
            => Task.FromResult(_agents.ToList());

        public Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_agents.FirstOrDefault(agent => string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase)));

        public Task DeleteAgentAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
