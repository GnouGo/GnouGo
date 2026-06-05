using Microsoft.Extensions.Logging.Abstractions;
namespace GnOuGo.Agent.Mcp.Tests;

public class AgentToolsTests : IDisposable
{
    private readonly AgentMcpTestDatabase _database;
    private readonly AgentTools _tools;

    public AgentToolsTests()
    {
        _database = new AgentMcpTestDatabase();
        var repo = _database.CreateAgentRepository();
        _tools = new AgentTools(repo, NullLogger<AgentTools>.Instance);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task AgentAdd_CreatesAgent()
    {
        var result = await _tools.AgentAdd("MyAgent", "do something", "original prompt");

        Assert.True(result.Success);
        Assert.NotNull(result.Agent);
        Assert.Equal("MyAgent", result.Agent.Name);
        Assert.Equal("do something", result.Agent.Workflow);
        Assert.Equal("original prompt", result.Agent.OriginalPrompt);
    }

    [Fact]
    public async Task AgentAdd_ReturnsError_WhenNameIsEmpty()
    {
        var result = await _tools.AgentAdd("", "wf");

        Assert.False(result.Success);
        Assert.Equal("INVALID_INPUT", result.ErrorCode);
    }

    [Fact]
    public async Task AgentAdd_ReturnsAlreadyExists_WhenNameAlreadyExistsIgnoringCase()
    {
        await _tools.AgentAdd("DailyReporter", "wf");

        var result = await _tools.AgentAdd(" dailyreporter ", "wf2");

        Assert.False(result.Success);
        Assert.Equal("ALREADY_EXISTS", result.ErrorCode);
    }

    [Fact]
    public async Task AgentList_ReturnsEmptyArray_WhenNoAgents()
    {
        var result = await _tools.AgentList();

        Assert.True(result.Success);
        Assert.NotNull(result.Agents);
        Assert.Empty(result.Agents);
    }

    [Fact]
    public async Task AgentList_ReturnsAllAgents()
    {
        await _tools.AgentAdd("A", "wf1");
        await _tools.AgentAdd("B", "wf2");

        var result = await _tools.AgentList();

        Assert.True(result.Success);
        Assert.NotNull(result.Agents);
        Assert.Equal(2, result.Agents.Count);
    }

    [Fact]
    public async Task AgentUpdate_ModifiesAgent()
    {
        var addResult = await _tools.AgentAdd("Original", "wf1");
        var id = addResult.Agent!.Id;

        var updateResult = await _tools.AgentUpdate(id, "Updated", "wf2", "new prompt");

        Assert.True(updateResult.Success);
        Assert.NotNull(updateResult.Agent);
        Assert.Equal("Updated", updateResult.Agent.Name);
        Assert.Equal("wf2", updateResult.Agent.Workflow);
        Assert.Equal("new prompt", updateResult.Agent.OriginalPrompt);
    }

    [Fact]
    public async Task AgentUpdate_ReturnsError_WhenNotFound()
    {
        var result = await _tools.AgentUpdate(Guid.NewGuid().ToString(), "name", "wf");

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task AgentUpdate_ReturnsError_WhenIdIsInvalid()
    {
        var result = await _tools.AgentUpdate("not-a-guid", "name", "wf");

        Assert.False(result.Success);
        Assert.Equal("INVALID_INPUT", result.ErrorCode);
    }

    [Fact]
    public async Task AgentUpdate_ReturnsAlreadyExists_WhenRenamingToExistingName()
    {
        await _tools.AgentAdd("Alpha", "wf1");
        var second = await _tools.AgentAdd("Bravo", "wf2");
        var secondId = second.Agent!.Id;

        var result = await _tools.AgentUpdate(secondId, " alpha ", "wf3");

        Assert.False(result.Success);
        Assert.Equal("ALREADY_EXISTS", result.ErrorCode);
    }

    [Fact]
    public async Task AgentDelete_RemovesAgent()
    {
        var addResult = await _tools.AgentAdd("ToDelete", "wf");
        var id = addResult.Agent!.Id;

        var deleteResult = await _tools.AgentDelete(id);

        Assert.True(deleteResult.Success);
        Assert.Equal(id, deleteResult.DeletedId);

        var listResult = await _tools.AgentList();
        Assert.NotNull(listResult.Agents);
        Assert.Empty(listResult.Agents);
    }

    [Fact]
    public async Task AgentDelete_ReturnsError_WhenNotFound()
    {
        var result = await _tools.AgentDelete(Guid.NewGuid().ToString());

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task AgentDelete_ReturnsError_WhenIdIsInvalid()
    {
        var result = await _tools.AgentDelete("bad-id");

        Assert.False(result.Success);
        Assert.Equal("INVALID_INPUT", result.ErrorCode);
    }

    [Fact]
    public async Task RoundTrip_AddListUpdateDelete()
    {
        var add = await _tools.AgentAdd("RT", "step1");
        Assert.True(add.Success);
        var id = add.Agent!.Id;

        var list = await _tools.AgentList();
        Assert.NotNull(list.Agents);
        Assert.Single(list.Agents);

        var upd = await _tools.AgentUpdate(id, "RT-v2", "step2");
        Assert.True(upd.Success);
        Assert.Equal("RT-v2", upd.Agent!.Name);

        var del = await _tools.AgentDelete(id);
        Assert.True(del.Success);

        var final = await _tools.AgentList();
        Assert.NotNull(final.Agents);
        Assert.Empty(final.Agents);
    }
}
