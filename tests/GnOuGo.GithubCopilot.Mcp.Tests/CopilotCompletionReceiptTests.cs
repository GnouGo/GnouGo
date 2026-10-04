using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using Xunit;
using McpServerMetadata = GnOuGo.Flow.Core.Runtime.McpServerMetadata;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

[Collection("Copilot tool discovery")]
public sealed class CopilotCompletionReceiptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CodeMcpTraceContext Context(string tenant = "fixture-tenant") => CodeMcpTraceContext.FromMcpMeta(new()
        { ["gnougo"] = new JsonObject { ["tenantId"] = tenant } })!;

    [Theory]
    [InlineData("completed")]
    [InlineData("empty-final")]
    [InlineData("late-idle")]
    [InlineData("budget")]
    [InlineData("disposal-failure")]
    [InlineData("pending-command")]
    [InlineData("pending-shell")]
    public async Task BoundedLocalCommandAndRealMcpTransportSeparateTerminalFailureFromUnknownCompletion(string scenario)
    {
        await using var fixture = await CopilotAttachmentTests.Fixture.CreateAsync(configure: s => s.Copilot.LogicalLimits.InferenceAttempts = 1);
        string? taskId = null; var inferenceCalls = 0;
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Host.OnDispose = async () =>
        {
            using var tenant = fixture.Host.Trace.Push(Context());
            var saved = await fixture.tasks.GetTaskAsync(taskId!, Ct);
            Assert.Equal(McpTaskStatus.Completed, saved!.Status); Assert.NotNull(saved.Result);
            disposed.TrySetResult();
            if (scenario == "disposal-failure") throw new IOException("Test-only disposal failure");
        };
        fixture.Host.OnSend = async (configuration, handle, _, ct) =>
        {
            taskId = fixture.tasks.CurrentTaskId;
            var observations = new CopilotExecutionObservations();
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
                { WorkingDirectory = fixture.Project, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
            start.ArgumentList.Add(OperatingSystem.IsWindows() ? "echo receipt>>receipt.txt" : "printf 'receipt\\n' >> receipt.txt");
            // The pinned SDK marks its structured command/exit event fields experimental.
#pragma warning disable GHCP001
            var started = new ToolExecutionStartEvent { Data = new() { ToolCallId = "local", ToolName = "arbitrary-execution",
                ShellToolInfo = new() { DisplayCommand = start.ArgumentList[1], HasWriteFileRedirection = true, PossiblePaths = [] }, Arguments = JsonSerializer.SerializeToElement(new { command = start.ArgumentList[1] }) } };
            observations.Observe(started);
            using var command = Process.Start(start)!;
            await command.WaitForExitAsync(ct); Assert.Equal(0, command.ExitCode);
            var completed = new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "local", Success = true,
                ShellExecution = scenario == "pending-shell" ? null : new() { ExitCode = command.ExitCode } } };
#pragma warning restore GHCP001
            if (scenario != "pending-command") observations.Observe(completed);
            var budget = configuration.Request.Configuration.LogicalInferenceBudget!;
            if (scenario == "budget")
            {
                using var inference = new HttpRequestMessage(HttpMethod.Post, "https://example.test/responses") { Content = new StringContent("{\"input\":\"local\",\"max_output_tokens\":1}") };
                await budget.ReserveAsync(inference, ct); inferenceCalls++;
                await Assert.ThrowsAsync<InvalidOperationException>(() => budget.ReserveAsync(inference, ct));
                observations.Observe(new SessionErrorEvent { Data = new() { ErrorType = "query", Message = "PRIVATE_PROVIDER_ERROR" } });
            }
            var waiting = scenario == "late-idle" ? observations.WaitForIdleAsync(ct) : Task.CompletedTask;
            observations.Observe(new SessionIdleEvent { Data = new() { Mode = SessionMode.Interactive } });
            await waiting;
            if (scenario == "completed")
            {
                Assert.True(observations.VerifiedTerminalCompletion);
                return new(handle, "bounded-session", "Local command completed", "deterministic", []) { ToolExecutions = observations.Snapshot() };
            }
            throw observations.Interrupted(handle, "bounded-session", new InvalidOperationException("PRIVATE_ERROR"), null, ct, budget);
        };
        var args = new Dictionary<string, object?> { ["projectRoot"] = "workflows/project", ["prompt"] = "Run the local fixture command once and report its exit code." };
        var uncertain = scenario is "pending-command" or "pending-shell";
        if (uncertain) await Assert.ThrowsAsync<McpException>(() => fixture.Call("copilot_interactive_one_shot", args, "fixture-tenant"));
        else
        {
            var response = await fixture.Call("copilot_interactive_one_shot", args, "fixture-tenant");
            Assert.Equal(scenario != "completed", response.IsError == true);
            Assert.Equal(scenario == "completed", response.StructuredContent!.Value.GetProperty("completed").GetBoolean());
            Assert.DoesNotContain("PRIVATE", response.StructuredContent.Value.GetRawText());
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(fixture.Project, "receipt.txt"), Ct));
        Assert.Equal(1, fixture.Host.Sends); Assert.Equal(1, fixture.Host.SessionsCreated);
        Assert.Equal(uncertain ? 0 : 1, fixture.Host.DisposedSessions);
        using var tenantScope = fixture.Host.Trace.Push(Context());
        var checkpoint = (await fixture.tasks.ReadOperationAsync(taskId!, Ct))!;
        Assert.Equal(!uncertain, checkpoint["completionVerified"]!.GetValue<bool>());
        Assert.Equal(scenario == "budget" ? "Calls" : null, checkpoint["admissionStop"]?["kind"]?.ToString());
        Assert.Equal(inferenceCalls, checkpoint["inferenceAttempts"]!.GetValue<int>());
        // A restarted reader only returns the retained receipt, never calls the SDK.
        await using var restarted = new KeyVaultCopilotTaskStore(KeyVaultRecordStoreFactory.CreateWorkspaceStore(Path.Combine(fixture.Root, "vault.db"), fixture.Root), fixture.Host.Trace, Path.Combine(fixture.Root, "leases"));
        var saved = (await restarted.GetTaskAsync(taskId!, Ct))!;
        Assert.Equal(uncertain ? McpTaskStatus.Failed : McpTaskStatus.Completed, saved.Status);
        if (!uncertain)
        {
            Assert.NotNull(saved.Result);
            Assert.Equal(scenario != "completed", saved.Result.Value.GetProperty("isError").GetBoolean());
            await fixture.tasks.SetFailedAsync(taskId!, JsonSerializer.SerializeToElement(new { message = "late failure" }), Ct);
            Assert.Equal(saved.Result.Value.GetRawText(), (await restarted.GetTaskAsync(taskId!, Ct))!.Result!.Value.GetRawText());
        }
        using (fixture.Host.Trace.Push(Context("another-tenant"))) Assert.Null(await restarted.GetTaskAsync(taskId!, Ct));
        Assert.Equal(1, fixture.Host.Sends);
        fixture.Host.OnDispose = null; // Fixture teardown disconnects retained sessions; the operation did not.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealFlowOnlyReleasesCleanupForAVerifiedFinalToolResult(bool verified)
    {
        await using var fixture = await CopilotAttachmentTests.Fixture.CreateAsync();
        fixture.Host.OnSend = (_, handle, _, ct) =>
        {
            var observations = new CopilotExecutionObservations();
            observations.Observe(new ToolExecutionStartEvent { Data = new() { ToolCallId = "work", ToolName = "operation" } });
            if (verified) observations.Observe(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "work", Success = true } });
            observations.Observe(new SessionIdleEvent { Data = new() { Mode = SessionMode.Interactive } });
            throw observations.Interrupted(handle, "fixture", new InvalidOperationException(), null, ct,
                fixture.Host.Configuration!.Request.Configuration.LogicalInferenceBudget);
        };
        var transport = new FlowTransport(fixture); var journal = new InMemoryWorkflowRunStore();
        var engine = new WorkflowEngine { McpClientFactory = transport, RunStore = journal, Limits = new() { TenantId = "fixture-tenant", RunId = "receipt" } };
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: work
                    type: mcp.call
                    input:
                      server: code
                      method: copilot_interactive_one_shot
                      request: { projectRoot: workflows/project, prompt: Inspect local evidence }
                finally:
                  - id: cleanup
                    type: mcp.call
                    input: { server: code, method: cleanup }
            """)).Workflows["main"];
        var result = await engine.ExecuteAsync(workflow, null, Ct);
        Assert.False(result.Success);
        Assert.Equal(verified ? "MCP_CALL_ERROR" : "RUN_NEEDS_RECONCILIATION", result.Error?.Code);
        Assert.Equal(verified ? 1 : 0, transport.Cleanups);
        if (!verified)
        {
            var saved = (await journal.ReadAsync("fixture-tenant", "receipt", Ct))!;
            var resumed = await engine.ResumeAsync("fixture-tenant", "receipt", saved.Revision, workflow, Ct);
            Assert.Equal("RUN_NEEDS_RECONCILIATION", resumed.Error?.Code); Assert.Equal(0, transport.Cleanups);
        }
        Assert.Equal(1, fixture.Host.Sends);
    }

    private sealed class FlowTransport(CopilotAttachmentTests.Fixture fixture) : IMcpClientFactory, IMcpSession
    {
        internal int Cleanups;
        public string ServerName => "code";
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [];
        public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct) => Task.FromResult<IMcpSession>(this);
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);
        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);
        public Task<McpGetPromptResult> GetPromptAsync(string name, JsonNode? args, CancellationToken ct) => throw new NotSupportedException();
        public async Task<McpCallResult> CallToolAsync(string name, JsonNode? arguments, CancellationToken ct)
        {
            if (name == "cleanup") { Cleanups++; return new() { Content = new JsonObject() }; }
            var args = arguments!.AsObject().ToDictionary(p => p.Key, p => (object?)p.Value?.GetValue<string>());
            var result = await fixture.Call(name, args, "fixture-tenant", ct);
            return new() { IsError = result.IsError == true, Content = JsonNode.Parse(result.StructuredContent!.Value.GetRawText()) };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FailedReceiptWriteRetainsOwnershipUntilDurableCommit()
    {
        var root = Directory.CreateTempSubdirectory("copilot-receipt-write-").FullName;
        try
        {
            var records = new FailingStore(KeyVaultRecordStoreFactory.CreateWorkspaceStore(Path.Combine(root, "vault.db"), root));
            var trace = new CodeMcpTraceContextAccessor(); using var tenant = trace.Push(Context());
            await using var owner = new KeyVaultCopilotTaskStore(records, trace, Path.Combine(root, "leases"));
            await using var reader = new KeyVaultCopilotTaskStore(records, trace, Path.Combine(root, "leases"));
            using var invocation = owner.BeginInvocation(); var id = (await owner.CreateTaskAsync(Ct)).TaskId;
            var result = JsonSerializer.SerializeToElement(new { isError = true, content = new[] { new { type = "text", text = "verified failure" } } });
            records.Fail = true;
            await Assert.ThrowsAsync<IOException>(() => owner.SetCompletedAsync(id, result, Ct));
            Assert.Equal(McpTaskStatus.Working, (await reader.GetTaskAsync(id, Ct))!.Status);
            await owner.SetCompletedAsync(id, result, Ct);
            Assert.Equal(result.GetRawText(), (await reader.GetTaskAsync(id, Ct))!.Result!.Value.GetRawText());
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FailingStore(IKeyVaultRecordStore inner) : IKeyVaultRecordStore
    {
        internal bool Fail;
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default) => inner.GetAsync(c, t, k, a, ct);
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default) => inner.ListAsync(c, t, a, ct);
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default) => inner.DeleteAsync(c, t, k, a, ct);
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default)
        { if (Fail) { Fail = false; throw new IOException("Injected interrupted receipt write"); } return inner.UpsertAsync(c, t, k, v, a, ct); }
    }
}
