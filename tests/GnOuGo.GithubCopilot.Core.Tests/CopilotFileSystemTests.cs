using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotFileSystemTests
{
    [Theory]
    [InlineData(null, "warning")]
    [InlineData("", "warning")]
    [InlineData("warn", "warning")]
    [InlineData("WARNING", "warning")]
    [InlineData("trace", "all")]
    [InlineData("debug", "debug")]
    [InlineData("default", null)]
    public void LogLevel_IsNormalizedByCore(string? value, string? expected)
        => Assert.Equal(expected, GitHubCopilotSdkClientFactory.ParseLogLevel(value)?.ToString());

    [Fact]
    public void UnknownLogLevel_IsRejected() => Assert.Throws<ArgumentException>(() => GitHubCopilotSdkClientFactory.ParseLogLevel("verbose"));

    [Fact]
    public void Progress_ContainsOperationalEventsAndExcludesReasoning()
    {
        Assert.Null(GitHubCopilotSdkSession.MapProgressEvent(new AssistantReasoningEvent { Data = new() { Content = "private reasoning", ReasoningId = "r" } }));
        Assert.Null(GitHubCopilotSdkSession.MapProgressEvent(new AssistantReasoningDeltaEvent { Data = new() { DeltaContent = "private delta", ReasoningId = "r" } }));
        var value = GitHubCopilotSdkSession.MapProgressEvent(new ToolExecutionStartEvent { Data = new() { ToolCallId = "call", ToolName = "test" } });
        Assert.Equal("tool.execution_start", value!.Kind);
        Assert.Equal("Copilot started a tool.", value.Message);
    }

    [Fact]
    public async Task HostFilePolicy_PrecedesBroadAndInteractivePermissions()
    {
        var fs = new TestSessionFileSystem { DenyWrites = true };
        var request = new CopilotSessionCreateRequest(new("tenant"), new(Path.GetTempPath(), "model", EnableApproveAll: true), PermissionMode: CopilotPermissionMode.ApproveAll);
        var source = new CopilotSdkSessionConfiguration(request, null, null, FileSystem: fs);
        var write = new PermissionRequestWrite { FileName = "protected.txt", CanOfferSessionApproval = true, Diff = "", Intention = "test" };
        var result = await GitHubCopilotSdkClient.BuildPermissionHandler(source)(write, new PermissionInvocation());
        Assert.IsType<PermissionDecisionReject>(result);
        result = await GitHubCopilotSdkClient.RequestInteractivePermissionAsync(write, source,
            new GitHubCopilotSdkClient.InteractivePermissionTaskState { AllowAll = true });
        Assert.IsType<PermissionDecisionReject>(result);
        Assert.Equal("deny", GitHubCopilotSdkClient.ValidateFileTool(new PreToolUseHookInput
        {
            ToolName = "edit", ToolArgs = JsonSerializer.Deserialize<JsonElement>("{\"path\":\"protected.txt\"}")
        }, fs)!.PermissionDecision);
    }

    [Fact]
    public async Task CreateAndResume_PreserveFileSystemHooksAndElicitation()
    {
        var config = new CopilotRuntimeConfiguration(Path.GetTempPath(), "model") { UseSessionFileSystem = true };
        await using var client = new GitHubCopilotSdkClient(new CopilotClient(new CopilotClientOptions()), config, NullLogger.Instance);
        var source = new CopilotSdkSessionConfiguration(new(new("tenant"), config), null, new Human(), FileSystem: new TestSessionFileSystem()) { SessionState = new() };
        var create = client.BuildCreateConfig(source);
        var resume = client.BuildResumeConfig(source);
        Assert.NotNull(create.CreateSessionFsProvider);
        Assert.NotNull(resume.CreateSessionFsProvider);
        Assert.NotNull(resume.Hooks);
        Assert.Equal(create.Tools!.Select(t => t.Name), resume.Tools!.Select(t => t.Name));
        Assert.Contains("apply_patch", create.ExcludedTools!);
        Assert.Contains("task", create.ExcludedTools!);
        Assert.NotNull(resume.OnElicitationRequest);
        Assert.NotNull(resume.OnUserInputRequest);
        Assert.False(resume.ContinuePendingWork);
    }

    [Fact]
    public void TransientState_RejectsTraversalAndSupportsRuntimeFileOperations()
    {
        var state = new CopilotTransientSessionState();
        var path = CopilotTransientSessionState.Root + "/events/session.jsonl";
        state.Write(path, "first", false);
        state.Write(path, "second", true);
        Assert.Equal("firstsecond", state.Read(path));
        state.Rename(path, path + ".old");
        Assert.False(state.Exists(path));
        Assert.True(state.Stat(path + ".old").IsFile);
        Assert.Single(state.List(CopilotTransientSessionState.Root + "/events"));
        Assert.Throws<UnauthorizedAccessException>(() => state.Read(CopilotTransientSessionState.Root + "/../secret"));
        state.Remove(CopilotTransientSessionState.Root + "/events", true, false);
        Assert.False(state.Exists(path + ".old"));
    }

    [Fact]
    public async Task ControlledTools_ExecuteThroughHostFileSystemAndCannotSkipPermission()
    {
        var files = new TestSessionFileSystem();
        var tools = CopilotProjectFileTool.Create(files).Cast<Microsoft.Extensions.AI.AIFunction>().ToArray();
        Assert.Equal(8, tools.Length);
        Assert.All(tools, tool => Assert.False(tool.AdditionalProperties.TryGetValue("skip_permission", out var value) && value is true));
        var read = tools.Single(t => t.Name == "project_read");
        Assert.Equal("content", await read.InvokeAsync(new() { ["path"] = "file.py" }, TestContext.Current.CancellationToken));
        var write = tools.Single(t => t.Name == "project_write");
        _ = await write.InvokeAsync(new() { ["path"] = "file.py", ["content"] = "changed" }, TestContext.Current.CancellationToken);
        Assert.Equal("file.py", files.WrittenPath);
        Assert.Equal("changed", files.WrittenContent);
        var request = new PermissionRequestCustomTool
        {
            ToolName = "project_write", ToolDescription = "Write", Args = JsonSerializer.Deserialize<JsonElement>("{\"path\":\"file.py\",\"content\":\"changed\"}")
        };
        Assert.False(GitHubCopilotSdkClient.IsAllowlisted(request, ["project_write"]));
        Assert.IsType<PermissionDecisionReject>(GitHubCopilotSdkClient.ValidateFilePermission(request, new TestSessionFileSystem { DenyWrites = true }));
        Assert.Equal("deny", GitHubCopilotSdkClient.ValidateFileTool(new PreToolUseHookInput
        { ToolName = "project_write", ToolArgs = request.Args }, new TestSessionFileSystem { DenyWrites = true })!.PermissionDecision);
    }

    private sealed class Human : ICopilotHumanInputProvider
    {
        public Task<CopilotHumanInputResponse> RequestAsync(CopilotHumanInputRequest request, CancellationToken cancellationToken) => Task.FromResult(new CopilotHumanInputResponse(false));
    }
}

internal sealed class TestSessionFileSystemFactory : ICopilotSessionFileSystemFactory
{
    internal List<TestSessionFileSystem> Instances { get; } = [];
    public ICopilotSessionFileSystem Create(CopilotSessionCreateRequest request) { var fs = new TestSessionFileSystem(); Instances.Add(fs); return fs; }
}

internal sealed class TestSessionFileSystem : ICopilotSessionFileSystem
{
    public bool DenyWrites { get; init; }
    public bool Disposed { get; private set; }
    public string? WrittenPath { get; private set; }
    public string? WrittenContent { get; private set; }
    public IReadOnlyList<string> ModifiedFiles => ["changed.py"];
    public void ValidateRead(string path) { }
    public void ValidateWrite(string path, string? content = null) { if (DenyWrites) throw new UnauthorizedAccessException("Host rejects writes."); }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    public Task<string> ReadFileAsync(string path, CancellationToken ct) => Task.FromResult("content");
    public Task WriteFileAsync(string path, string content, int? mode, CancellationToken ct) { ValidateWrite(path, content); WrittenPath = path; WrittenContent = content; return Task.CompletedTask; }
    public Task AppendFileAsync(string path, string content, int? mode, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(true);
    public Task<CopilotFileStat> StatAsync(string path, CancellationToken ct) => Task.FromResult(new CopilotFileStat(true, false, 0, DateTime.UnixEpoch, DateTime.UnixEpoch));
    public Task MakeDirectoryAsync(string path, bool recursive, int? mode, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<CopilotDirectoryEntry>> ReadDirectoryAsync(string path, CancellationToken ct) => Task.FromResult<IReadOnlyList<CopilotDirectoryEntry>>([]);
    public Task RemoveAsync(string path, bool recursive, bool force, CancellationToken ct) => Task.CompletedTask;
    public Task RenameAsync(string source, string destination, CancellationToken ct) => Task.CompletedTask;
}
