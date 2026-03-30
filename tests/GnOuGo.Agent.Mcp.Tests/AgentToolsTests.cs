using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Diff.Core.Data;
using GnOuGo.Diff.Core.Services;
using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public class AgentToolsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentDbContext _db;
    private readonly DiffDbContext _diffDb;
    private readonly AgentTools _tools;

    public AgentToolsTests()
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
        _diffDb.GetService<IRelationalDatabaseCreator>().CreateTables();

        var diffService = new DiffService(_diffDb);
        var repo = new AgentRepository(_db, diffService);
        _tools = new AgentTools(repo, NullLogger<AgentTools>.Instance);
    }

    public void Dispose()
    {
        _diffDb.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    // ── agent_add ────────────────────────────────────────────────────

    [Fact]
    public async Task AgentAdd_CreatesAgent_WithSchedules()
    {
        var schedules = new[] { new Schedule { Name = "daily", Cron = "0 8 * * *" } };

        var result = await _tools.AgentAdd("MyAgent", "do something", schedules);

        Assert.True(result["success"]!.GetValue<bool>());
        var agent = result["agent"]!.AsObject();
        Assert.Equal("MyAgent", agent["name"]!.GetValue<string>());
        Assert.Equal("do something", agent["workflow"]!.GetValue<string>());
        Assert.Single(agent["schedules"]!.AsArray());
    }

    [Fact]
    public async Task AgentAdd_CreatesAgent_WithNoSchedules()
    {
        var result = await _tools.AgentAdd("NoSched", "wf");

        Assert.True(result["success"]!.GetValue<bool>());
        var agent = result["agent"]!.AsObject();
        Assert.Empty(agent["schedules"]!.AsArray());
    }

    [Fact]
    public async Task AgentAdd_ReturnsError_WhenNameIsEmpty()
    {
        var result = await _tools.AgentAdd("", "wf");

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("INVALID_INPUT", result["error_code"]!.GetValue<string>());
    }

    // ── agent_list ───────────────────────────────────────────────────

    [Fact]
    public async Task AgentList_ReturnsEmptyArray_WhenNoAgents()
    {
        var result = await _tools.AgentList();

        Assert.True(result["success"]!.GetValue<bool>());
        Assert.Empty(result["agents"]!.AsArray());
    }

    [Fact]
    public async Task AgentList_ReturnsAllAgents()
    {
        await _tools.AgentAdd("A", "wf1");
        await _tools.AgentAdd("B", "wf2");

        var result = await _tools.AgentList();

        Assert.True(result["success"]!.GetValue<bool>());
        Assert.Equal(2, result["agents"]!.AsArray().Count);
    }

    // ── agent_update ─────────────────────────────────────────────────

    [Fact]
    public async Task AgentUpdate_ModifiesAgent()
    {
        var addResult = await _tools.AgentAdd("Original", "wf1");
        var id = addResult["agent"]!.AsObject()["id"]!.GetValue<string>();

        var newSchedules = new[] { new Schedule { Name = "nightly", Cron = "0 2 * * *" } };
        var updateResult = await _tools.AgentUpdate(id, "Updated", "wf2", newSchedules);

        Assert.True(updateResult["success"]!.GetValue<bool>());
        var agent = updateResult["agent"]!.AsObject();
        Assert.Equal("Updated", agent["name"]!.GetValue<string>());
        Assert.Equal("wf2", agent["workflow"]!.GetValue<string>());
        Assert.Single(agent["schedules"]!.AsArray());
    }

    [Fact]
    public async Task AgentUpdate_ReturnsError_WhenNotFound()
    {
        var result = await _tools.AgentUpdate(Guid.NewGuid().ToString(), "name", "wf");

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("NOT_FOUND", result["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task AgentUpdate_ReturnsError_WhenIdIsInvalid()
    {
        var result = await _tools.AgentUpdate("not-a-guid", "name", "wf");

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("INVALID_INPUT", result["error_code"]!.GetValue<string>());
    }

    // ── agent_delete ─────────────────────────────────────────────────

    [Fact]
    public async Task AgentDelete_RemovesAgent()
    {
        var addResult = await _tools.AgentAdd("ToDelete", "wf");
        var id = addResult["agent"]!.AsObject()["id"]!.GetValue<string>();

        var deleteResult = await _tools.AgentDelete(id);

        Assert.True(deleteResult["success"]!.GetValue<bool>());
        Assert.Equal(id, deleteResult["deleted_id"]!.GetValue<string>());

        var listResult = await _tools.AgentList();
        Assert.Empty(listResult["agents"]!.AsArray());
    }

    [Fact]
    public async Task AgentDelete_ReturnsError_WhenNotFound()
    {
        var result = await _tools.AgentDelete(Guid.NewGuid().ToString());

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("NOT_FOUND", result["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task AgentDelete_ReturnsError_WhenIdIsInvalid()
    {
        var result = await _tools.AgentDelete("bad-id");

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("INVALID_INPUT", result["error_code"]!.GetValue<string>());
    }

    // ── Round-trip ───────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_AddListUpdateDelete()
    {
        // Add
        var add = await _tools.AgentAdd("RT", "step1", [new Schedule { Name = "s", Cron = "0 0 * * *" }]);
        Assert.True(add["success"]!.GetValue<bool>());
        var id = add["agent"]!.AsObject()["id"]!.GetValue<string>();

        // List
        var list = await _tools.AgentList();
        Assert.Single(list["agents"]!.AsArray());

        // Update
        var upd = await _tools.AgentUpdate(id, "RT-v2", "step2", []);
        Assert.True(upd["success"]!.GetValue<bool>());
        Assert.Equal("RT-v2", upd["agent"]!.AsObject()["name"]!.GetValue<string>());

        // Delete
        var del = await _tools.AgentDelete(id);
        Assert.True(del["success"]!.GetValue<bool>());

        // Verify empty
        var final = await _tools.AgentList();
        Assert.Empty(final["agents"]!.AsArray());
    }
}
