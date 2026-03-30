using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Diff.Core.Data;
using GnOuGo.Diff.Core.Services;
using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public class AgentRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentDbContext _db;
    private readonly DiffDbContext _diffDb;
    private readonly AgentRepository _repo;

    public AgentRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var agentOptions = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AgentDbContext(agentOptions);
        _db.Database.EnsureCreated();

        var diffOptions = new DbContextOptionsBuilder<DiffDbContext>()
            .UseSqlite(_connection)
            .Options;
        _diffDb = new DiffDbContext(diffOptions);
        // EnsureCreated skips when DB already exists — use CreateTables for the second context
        _diffDb.GetService<IRelationalDatabaseCreator>().CreateTables();

        var diffService = new DiffService(_diffDb);
        _repo = new AgentRepository(_db, diffService);
    }

    public void Dispose()
    {
        _diffDb.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    // ── AddAgent ─────────────────────────────────────────────────────

    [Fact]
    public async Task AddAgent_CreatesAgentWithSchedules()
    {
        var schedules = new List<Schedule>
        {
            new() { Name = "daily", Cron = "0 8 * * *" },
            new() { Name = "weekly", Cron = "0 9 * * 1" }
        };

        var agent = await _repo.AddAgentAsync("TestAgent", "step1: hello", schedules);

        Assert.NotEqual(Guid.Empty, agent.Id);
        Assert.Equal("TestAgent", agent.Name);
        Assert.Equal("step1: hello", agent.Workflow);

        var deserialized = AgentRepository.DeserializeSchedules(agent.SchedulesJson);
        Assert.Equal(2, deserialized.Count);
        Assert.Equal("daily", deserialized[0].Name);
        Assert.Equal("0 8 * * *", deserialized[0].Cron);
        Assert.Equal("weekly", deserialized[1].Name);
    }

    [Fact]
    public async Task AddAgent_EmptySchedules_CreatesAgentWithEmptyArray()
    {
        var agent = await _repo.AddAgentAsync("NoSchedule", "workflow", []);

        Assert.Equal("[]", agent.SchedulesJson);
    }

    [Fact]
    public async Task AddAgent_ThrowsOnEmptyName()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repo.AddAgentAsync("", "workflow", []));
    }

    [Fact]
    public async Task AddAgent_ThrowsOnEmptyWorkflow()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repo.AddAgentAsync("name", "", []));
    }

    [Fact]
    public async Task AddAgent_TrimsName()
    {
        var agent = await _repo.AddAgentAsync("  padded  ", "wf", []);
        Assert.Equal("padded", agent.Name);
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
        await _repo.AddAgentAsync("Bravo", "wf", []);
        await _repo.AddAgentAsync("Alpha", "wf", []);

        var agents = await _repo.ListAgentsAsync();

        Assert.Equal(2, agents.Count);
        Assert.Equal("Alpha", agents[0].Name);
        Assert.Equal("Bravo", agents[1].Name);
    }

    // ── UpdateAgent ──────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAgent_ModifiesAllFields()
    {
        var created = await _repo.AddAgentAsync("Original", "wf1", [new Schedule { Name = "old", Cron = "0 0 * * *" }]);

        var newSchedules = new List<Schedule> { new() { Name = "new-sched", Cron = "*/5 * * * *" } };
        var updated = await _repo.UpdateAgentAsync(created.Id, "Renamed", "wf2", newSchedules);

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("wf2", updated.Workflow);

        var deserialized = AgentRepository.DeserializeSchedules(updated.SchedulesJson);
        Assert.Single(deserialized);
        Assert.Equal("new-sched", deserialized[0].Name);
        Assert.Equal("*/5 * * * *", deserialized[0].Cron);
    }

    [Fact]
    public async Task UpdateAgent_ThrowsWhenNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _repo.UpdateAgentAsync(Guid.NewGuid(), "name", "wf", []));
    }

    [Fact]
    public async Task UpdateAgent_UpdatesTimestamp()
    {
        var created = await _repo.AddAgentAsync("Agent", "wf", []);
        var createdTime = created.UpdatedAt;

        // Small delay to ensure different tick
        await Task.Delay(10);

        var updated = await _repo.UpdateAgentAsync(created.Id, "Agent", "wf2", []);
        Assert.True(updated.UpdatedAt >= createdTime);
    }

    // ── DeleteAgent ──────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAgent_RemovesAgent()
    {
        var agent = await _repo.AddAgentAsync("ToDelete", "wf", []);

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
        var agent = await _repo.AddAgentAsync("RoundTrip", "step1", [new Schedule { Name = "s1", Cron = "0 0 * * *" }]);
        Assert.NotEqual(Guid.Empty, agent.Id);

        // List
        var list = await _repo.ListAgentsAsync();
        Assert.Single(list);

        // Update
        await _repo.UpdateAgentAsync(agent.Id, "RoundTrip-Updated", "step2", []);

        var updated = (await _repo.ListAgentsAsync())[0];
        Assert.Equal("RoundTrip-Updated", updated.Name);
        Assert.Equal("step2", updated.Workflow);

        // Delete
        await _repo.DeleteAgentAsync(agent.Id);
        Assert.Empty(await _repo.ListAgentsAsync());
    }

    // ── Diff revisions ───────────────────────────────────────────────

    [Fact]
    public async Task AddAgent_CreatesDiffRevision()
    {
        var agent = await _repo.AddAgentAsync("DiffTest", "wf", [new Schedule { Name = "s", Cron = "0 0 * * *" }]);

        var diffService = new DiffService(_diffDb);
        var revisions = await diffService.GetRevisionsAsync("AgentDefinition", agent.Id.ToString());

        Assert.Single(revisions);
        Assert.True(revisions[0].IsFirstRevision);
        Assert.Contains("DiffTest", revisions[0].CurrentValue);
        Assert.Contains("0 0 * * *", revisions[0].CurrentValue);
    }

    [Fact]
    public async Task UpdateAgent_CreatesDiffWithPreviousRevision()
    {
        var agent = await _repo.AddAgentAsync("V1", "wf1", []);
        await _repo.UpdateAgentAsync(agent.Id, "V2", "wf2", [new Schedule { Name = "new", Cron = "*/5 * * * *" }]);

        var diffService = new DiffService(_diffDb);
        var revisions = await diffService.GetRevisionsAsync("AgentDefinition", agent.Id.ToString());

        Assert.Equal(2, revisions.Count);
        // Most recent first
        Assert.False(revisions[0].IsFirstRevision);
        Assert.NotNull(revisions[0].DiffFromPrevious);
        Assert.Contains("V2", revisions[0].CurrentValue);
    }

    [Fact]
    public async Task DeleteAgent_CreatesTombstoneRevision()
    {
        var agent = await _repo.AddAgentAsync("WillDelete", "wf", []);
        await _repo.DeleteAgentAsync(agent.Id);

        var diffService = new DiffService(_diffDb);
        var revisions = await diffService.GetRevisionsAsync("AgentDefinition", agent.Id.ToString());

        Assert.Equal(2, revisions.Count);
        Assert.Contains("Agent deleted", revisions[0].CurrentValue);
    }

    [Fact]
    public async Task SerializeAgentToYaml_ProducesValidYaml()
    {
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "YamlTest",
            Workflow = "step1: echo hello\nstep2: done",
            SchedulesJson = """[{"id":"abc","name":"daily","cron":"0 8 * * *"}]""",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var yaml = AgentRepository.SerializeAgentToYaml(agent);

        Assert.Contains("name: YamlTest", yaml);
        Assert.Contains("0 8 * * *", yaml);
        Assert.Contains("daily", yaml);
    }
}
