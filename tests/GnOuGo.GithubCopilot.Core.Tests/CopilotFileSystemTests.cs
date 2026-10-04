using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotFileSystemTests
{
    [Fact]
    public async Task ReadRangesRemainStructuredAndBoundedAfterWireEscaping()
    {
        var state = new CopilotTransientSessionState();
        var path = CopilotTransientSessionState.Root + "/temp/unicode-output.txt";
        var content = string.Concat(Enumerable.Repeat("\"quoted\"\\\n\t漢字😀", 4000));
        state.Write(path, content, false); state.RegisterOutput(path);
        var read = CopilotProjectFileTool.Create(new TestSessionFileSystem(), state: state).Cast<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "project_read");
        var observed = new System.Text.StringBuilder(); var offset = 0;
        do
        {
            var result = Assert.IsType<JsonElement>(await read.InvokeAsync(new() { ["path"] = path, ["offset"] = offset }, TestContext.Current.CancellationToken));
            Assert.Equal(JsonValueKind.Object, result.ValueKind);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(result).Length <= CopilotProjectFileTool.ReadLimit);
            var chunk = result.Deserialize(CopilotCoreJsonContext.Default.CopilotFileReadResult)!;
            Assert.Equal(offset, chunk.Offset); Assert.NotEmpty(chunk.Text); Assert.False(char.IsHighSurrogate(chunk.Text[^1]));
            observed.Append(chunk.Text); offset += chunk.Text.Length;
            Assert.Equal(offset < content.Length, chunk.Truncated);
            Assert.Equal(offset < content.Length ? offset : (int?)null, chunk.NextOffset);
        } while (offset < content.Length);
        Assert.Equal(content, observed.ToString());
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("powershell")]
    public async Task VirtualOutputLogsRejectHostShellAccessBeforePermissionApproval(string tool)
    {
        var state = new CopilotTransientSessionState();
        var path = CopilotTransientSessionState.Root + "/temp/observed-output.txt";
        state.Write(path, "observed output", false); state.RegisterOutput(path);
        var files = new TestSessionFileSystem();
        foreach (var command in new[] { "tail -80 " + path, "Get-Content '" + path.Replace('/', '\\') + "'", "grep failure '" + path + "'" })
        {
            var rejection = GitHubCopilotSdkClient.ValidateFileTool(new() { ToolName = tool,
                ToolArgs = JsonSerializer.SerializeToElement(new { command }) }, files, state);
            Assert.Equal("deny", rejection?.PermissionDecision);
            Assert.Contains("project_read", rejection!.PermissionDecisionReason);
            var source = new CopilotSdkSessionConfiguration(new(new("tenant"), new(Path.GetTempPath(), "model", EnableApproveAll: true),
                PermissionMode: CopilotPermissionMode.ApproveAll), null, null, FileSystem: files) { SessionState = state };
            var permission = new PermissionRequestShell { FullCommandText = command, RequestSandboxBypass = false,
                CanOfferSessionApproval = false, Commands = [], HasWriteFileRedirection = false,
                Intention = "Read completed command output", PossiblePaths = [], PossibleUrls = [] };
            Assert.IsType<PermissionDecisionReject>(await GitHubCopilotSdkClient.BuildPermissionHandler(source)(permission, new()));
        }
        Assert.Null(GitHubCopilotSdkClient.ValidateFileTool(new() { ToolName = tool,
            ToolArgs = JsonSerializer.SerializeToElement(new { command = "node --version" }) }, files, state));
        var read = CopilotProjectFileTool.Create(files, state: state).Cast<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "project_read");
        var result = (JsonElement)(await read.InvokeAsync(new() { ["path"] = path }, TestContext.Current.CancellationToken))!;
        Assert.Equal("observed output", JsonSerializer.Deserialize(result, CopilotCoreJsonContext.Default.CopilotFileReadResult)!.Text);
    }

    [Fact]
    public async Task PublishedVirtualLogsUseBoundedReadsAndNeverExposeOtherSessionState()
    {
        var state = new CopilotTransientSessionState();
        var path = CopilotTransientSessionState.Root + "/temp/output.txt";
        var secret = CopilotTransientSessionState.Root + "/events/private.json";
        var text = new string('a', 20_000) + "final observed failure";
        state.Write(path, text, false); state.Write(secret, "PRIVATE", false);
        var files = new TestSessionFileSystem();
        var tools = CopilotProjectFileTool.Create(files, state: state).Cast<Microsoft.Extensions.AI.AIFunction>().ToArray();
        var read = tools.Single(t => t.Name == "project_read");
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => read.InvokeAsync(new() { ["path"] = path }, ct).AsTask());
        var observations = new CopilotExecutionObservations("session", state);
        observations.Observe(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "log", Success = true,
            Result = new() { Content = "Preview", Contents = [new ToolExecutionCompleteContentShellExit { OutputFilePath = path, ExitCode = 1, OutputTruncated = true, ShellId = "log" }] } } });
        var firstJson = (JsonElement)(await read.InvokeAsync(new() { ["path"] = path }, ct))!;
        var first = JsonSerializer.Deserialize(firstJson, CopilotCoreJsonContext.Default.CopilotFileReadResult)!;
        Assert.InRange(first.Text.Length, 1, 16384); Assert.True(first.Truncated); Assert.Equal(first.Text.Length, first.NextOffset);
        var last = JsonSerializer.Deserialize((JsonElement)(await read.InvokeAsync(new() { ["path"] = path, ["offset"] = first.NextOffset!.Value }, ct))!, CopilotCoreJsonContext.Default.CopilotFileReadResult)!;
        Assert.Equal(text, first.Text + last.Text); Assert.False(last.Truncated); Assert.Null(last.NextOffset);
        Assert.True(firstJson.GetRawText().Length < text.Length);
        Assert.Equal(text, Assert.Single(state.OutputSnapshot()).Value);
        foreach (var denied in new[] { secret, CopilotTransientSessionState.Root + "/temp/../events/private.json" })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => read.InvokeAsync(new() { ["path"] = denied }, ct).AsTask());
        var other = CopilotProjectFileTool.Create(files, state: new()).Cast<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "project_read");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.InvokeAsync(new() { ["path"] = path }, ct).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.Single(t => t.Name == "project_write").InvokeAsync(new() { ["path"] = path, ["content"] = "replace" }, ct).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => read.InvokeAsync(new() { ["path"] = path, ["maxCharacters"] = 16385 }, ct).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => read.InvokeAsync(new() { ["path"] = path, ["offset"] = -1 }, ct).AsTask());
        var args = JsonSerializer.SerializeToElement(new { path });
        Assert.Null(GitHubCopilotSdkClient.ValidateFileTool(new() { ToolName = "project_read", ToolArgs = args }, files, state));
        Assert.Null(GitHubCopilotSdkClient.ValidateFilePermission(new PermissionRequestCustomTool { ToolName = "project_read", ToolDescription = "Read", Args = args }, files, state));
        Assert.NotNull(GitHubCopilotSdkClient.ValidateFilePermission(new PermissionRequestCustomTool { ToolName = "project_read", ToolDescription = "Read", Args = args }, files, new()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.InvokeAsync(new() { ["path"] = path }, cancelled.Token).AsTask());
    }

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
        Assert.True(Path.IsPathFullyQualified(CopilotTransientSessionState.Root));
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
    public async Task StoppingAwaitsActualFileHandlerAndPreventsLaterWrites()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new TestSessionFileSystem { BeforeWrite = async () => { entered.SetResult(); await release.Task; } };
        var bounds = new CopilotExecutionBounds(2, 10000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string> { "project_write" }, (_, _, _) => Task.CompletedTask);
        var tool = CopilotProjectFileTool.Create(files, bounds).Cast<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "project_write");
        var writing = tool.InvokeAsync(new() { ["path"] = "file.py", ["content"] = "first" }, TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var stopping = bounds.StopAsync(TestContext.Current.CancellationToken); Assert.False(stopping.IsCompleted);
        release.SetResult(); await writing; await stopping;
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(new() { ["path"] = "file.py", ["content"] = "late" }, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("first", files.WrittenContent);
    }

    [Fact]
    public async Task ControlledTools_ExecuteThroughHostFileSystemAndCannotSkipPermission()
    {
        var files = new TestSessionFileSystem();
        var tools = CopilotProjectFileTool.Create(files).Cast<Microsoft.Extensions.AI.AIFunction>().ToArray();
        Assert.Equal(8, tools.Length);
        Assert.All(tools, tool => Assert.False(tool.AdditionalProperties.TryGetValue("skip_permission", out var value) && value is true));
        var read = tools.Single(t => t.Name == "project_read");
        var chunk = JsonSerializer.Deserialize((JsonElement)(await read.InvokeAsync(new() { ["path"] = "file.py" }, TestContext.Current.CancellationToken))!, CopilotCoreJsonContext.Default.CopilotFileReadResult)!;
        Assert.Equal("content", chunk.Text); Assert.False(chunk.Truncated); Assert.Null(chunk.NextOffset);
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
    public Func<Task>? BeforeWrite { get; init; }
    public bool Disposed { get; private set; }
    public string? WrittenPath { get; private set; }
    public string? WrittenContent { get; private set; }
    public IReadOnlyList<string> ModifiedFiles => ["changed.py"];
    public void ValidateRead(string path) { }
    public void ValidateWrite(string path, string? content = null) { if (DenyWrites) throw new UnauthorizedAccessException("Host rejects writes."); }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    public Task<string> ReadFileAsync(string path, CancellationToken ct) => Task.FromResult("content");
    public async Task WriteFileAsync(string path, string content, int? mode, CancellationToken ct) { if (BeforeWrite is not null) await BeforeWrite(); ValidateWrite(path, content); WrittenPath = path; WrittenContent = content; }
    public Task AppendFileAsync(string path, string content, int? mode, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(true);
    public Task<CopilotFileStat> StatAsync(string path, CancellationToken ct) => Task.FromResult(new CopilotFileStat(true, false, 0, DateTime.UnixEpoch, DateTime.UnixEpoch));
    public Task MakeDirectoryAsync(string path, bool recursive, int? mode, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<CopilotDirectoryEntry>> ReadDirectoryAsync(string path, CancellationToken ct) => Task.FromResult<IReadOnlyList<CopilotDirectoryEntry>>([]);
    public Task RemoveAsync(string path, bool recursive, bool force, CancellationToken ct) => Task.CompletedTask;
    public Task RenameAsync(string source, string destination, CancellationToken ct) => Task.CompletedTask;
}
