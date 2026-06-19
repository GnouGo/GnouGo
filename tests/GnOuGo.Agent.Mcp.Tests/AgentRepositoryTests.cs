using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Agent.Mcp.Services;
using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public class AgentRepositoryTests : IDisposable
{
    private readonly AgentMcpTestDatabase _database;
    private readonly AgentRepository _repo;

    public AgentRepositoryTests()
    {
        _database = new AgentMcpTestDatabase();
        _repo = _database.CreateAgentRepository();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task AddAgent_CreatesYamlFileInAgentsDirectory()
    {
        var agent = await _repo.AddAgentAsync("TestAgent", "step1: hello", "original prompt");

        Assert.NotEqual(Guid.Empty, agent.Id);
        Assert.Equal("TestAgent", agent.Name);
        Assert.Equal("step1: hello", agent.Workflow);
        Assert.Equal("original prompt", agent.OriginalPrompt);

        var filePath = Path.Combine(_database.AgentsDirectory, "TestAgent.yaml");
        Assert.True(File.Exists(filePath));
        var yaml = await File.ReadAllTextAsync(filePath);
        Assert.Contains("name: \"TestAgent\"", yaml);
        Assert.Contains("workflow: \"step1: hello\"", yaml);
    }

    [Fact]
    public async Task AddAgentBundle_CreatesAgentAndWorkflowFilesUnderWorkspaceRoot()
    {
        var workflows = new Dictionary<string, string>
        {
            ["./TestAgent/workflow.yaml"] = "old main",
            ["./TestAgent/collect/workflow.yaml"] = "collect workflow"
        };

        var agent = await _repo.AddAgentBundleAsync("TestAgent", "approved main", workflows, "original prompt");

        Assert.Equal("TestAgent", agent.Name);
        Assert.Equal("approved main", agent.Workflow);
        Assert.Equal("original prompt", agent.OriginalPrompt);
        Assert.Equal("approved main", await File.ReadAllTextAsync(Path.Combine(_database.WorkspaceRoot, "TestAgent", "workflow.yaml")));
        Assert.Equal("collect workflow", await File.ReadAllTextAsync(Path.Combine(_database.WorkspaceRoot, "TestAgent", "collect", "workflow.yaml")));
    }

    [Theory]
    [InlineData("../outside/workflow.yaml")]
    [InlineData("/tmp/outside/workflow.yaml")]
    [InlineData("./safe/../outside/workflow.yaml")]
    public async Task AddAgentBundle_RejectsUnsafeWorkflowPaths(string relativePath)
    {
        var workflows = new Dictionary<string, string>
        {
            [relativePath] = "wf"
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => _repo.AddAgentBundleAsync("TestAgent", "main", workflows));
    }

    [Fact]
    public async Task AddAgent_ThrowsOnEmptyName()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repo.AddAgentAsync("", "workflow"));
    }

    [Fact]
    public async Task AddAgent_ThrowsOnEmptyWorkflow()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repo.AddAgentAsync("name", ""));
    }

    [Fact]
    public async Task AddAgent_TrimsName()
    {
        var agent = await _repo.AddAgentAsync("  padded  ", "wf");
        Assert.Equal("padded", agent.Name);
    }

    [Theory]
    [InlineData("../bad")]
    [InlineData("bad/name")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task AddAgent_RejectsUnsafeFileNames(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repo.AddAgentAsync(name, "wf"));
    }

    [Fact]
    public async Task AddAgent_ThrowsWhenNameAlreadyExists_IgnoringCaseAndWhitespace()
    {
        await _repo.AddAgentAsync("DailyReporter", "wf");

        var ex = await Assert.ThrowsAsync<DuplicateAgentNameException>(
            () => _repo.AddAgentAsync("  dailyreporter  ", "wf-2"));

        Assert.Equal("DailyReporter", (await _repo.ListAgentsAsync()).Single().Name);
        Assert.Equal("An agent named 'dailyreporter' already exists.", ex.Message);
    }

    // ── ListAgents ───────────────────────────────────────────────────

    [Fact]
    public async Task ListAgents_ReturnsEmpty_WhenNoAgents()
    {
        var agents = await _repo.ListAgentsAsync();
        Assert.Empty(agents);
    }

    [Fact]
    public async Task ListAgents_ReturnsAllAgents_OrderedByName()
    {
        await _repo.AddAgentAsync("Bravo", "wf");
        await _repo.AddAgentAsync("Alpha", "wf");

        var agents = await _repo.ListAgentsAsync();

        Assert.Equal(2, agents.Count);
        Assert.Equal("Alpha", agents[0].Name);
        Assert.Equal("Bravo", agents[1].Name);
    }

    // ── UpdateAgent ──────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAgent_ModifiesAllFields()
    {
        var created = await _repo.AddAgentAsync("Original", "wf1", "prompt1");

        var updated = await _repo.UpdateAgentAsync(created.Id, "Renamed", "wf2", "prompt2");

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("wf2", updated.Workflow);
        Assert.Equal("prompt2", updated.OriginalPrompt);

        Assert.False(File.Exists(Path.Combine(_database.AgentsDirectory, "Original.yaml")));
        Assert.True(File.Exists(Path.Combine(_database.AgentsDirectory, "Renamed.yaml")));
    }

    [Fact]
    public async Task UpdateAgent_ThrowsWhenNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _repo.UpdateAgentAsync(Guid.NewGuid(), "name", "wf"));
    }

    [Fact]
    public async Task UpdateAgent_UpdatesTimestamp()
    {
        var created = await _repo.AddAgentAsync("Agent", "wf");
        var createdTime = created.UpdatedAt;

        // Small delay to ensure different tick
        await Task.Delay(10);

        var updated = await _repo.UpdateAgentAsync(created.Id, "Agent", "wf2");
        Assert.True(updated.UpdatedAt >= createdTime);
    }

    [Fact]
    public async Task UpdateAgent_ThrowsWhenRenamingToExistingName_IgnoringCase()
    {
        await _repo.AddAgentAsync("Alpha", "wf");
        var second = await _repo.AddAgentAsync("Bravo", "wf");

        var ex = await Assert.ThrowsAsync<DuplicateAgentNameException>(
            () => _repo.UpdateAgentAsync(second.Id, " alpha ", "wf-2"));

        Assert.Equal("An agent named 'alpha' already exists.", ex.Message);
        Assert.Equal("Bravo", (await _repo.ListAgentsAsync()).Single(a => a.Id == second.Id).Name);
    }

    // ── DeleteAgent ──────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAgent_RemovesAgent()
    {
        var agent = await _repo.AddAgentAsync("ToDelete", "wf");

        await _repo.DeleteAgentAsync(agent.Id);

        var remaining = await _repo.ListAgentsAsync();
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task DeleteAgent_ThrowsWhenNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _repo.DeleteAgentAsync(Guid.NewGuid()));
    }

    // ── Round-trip ───────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_CreateListUpdateDelete()
    {
        // Create
        var agent = await _repo.AddAgentAsync("RoundTrip", "step1");
        Assert.NotEqual(Guid.Empty, agent.Id);

        // List
        var list = await _repo.ListAgentsAsync();
        Assert.Single(list);

        // Update
        await _repo.UpdateAgentAsync(agent.Id, "RoundTrip-Updated", "step2");

        var updated = (await _repo.ListAgentsAsync())[0];
        Assert.Equal("RoundTrip-Updated", updated.Name);
        Assert.Equal("step2", updated.Workflow);

        // Delete
        await _repo.DeleteAgentAsync(agent.Id);
        Assert.Empty(await _repo.ListAgentsAsync());
    }

    [Fact]
    public async Task SerializeAgentToYaml_ProducesValidYaml()
    {
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "YamlTest",
            Workflow = "step1: echo hello\nstep2: done",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var yaml = AgentRepository.SerializeAgentToYaml(agent);

        Assert.Contains("YamlTest", yaml);
        Assert.Contains("workflow: |-", yaml);
        Assert.DoesNotContain("schedules", yaml, StringComparison.OrdinalIgnoreCase);
    }
}
