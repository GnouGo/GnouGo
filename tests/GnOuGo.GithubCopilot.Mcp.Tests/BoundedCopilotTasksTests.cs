using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class BoundedCopilotTasksTests
{
    [Theory]
    [InlineData(CopilotSandboxReadiness.Configured)]
    [InlineData(CopilotSandboxReadiness.NotConfigured)]
    [InlineData(CopilotSandboxReadiness.Invalid)]
    [InlineData(CopilotSandboxReadiness.Unavailable)]
    public async Task PublishedContractReflectsPolicyWithoutDispatchAndValidationRechecksIt(CopilotSandboxReadiness readiness)
    {
        await using var fixture = new Fixture(); fixture.Host.Readiness = readiness;
        var contract = await fixture.Tasks.ContractAsync(TestContext.Current.CancellationToken);
        var schema = contract["contract"]!["input_schema"]!;
        Assert.Equal(readiness == CopilotSandboxReadiness.Configured, schema["properties"]!["capabilities"]!["items"]!["enum"]!.AsArray().Any(v => v!.ToString() == "command.execute"));
        Assert.Equal(readiness == CopilotSandboxReadiness.Configured, schema["properties"]!["verification"]!["items"]!["properties"]!["kind"]!["enum"]!.AsArray().Any(v => v!.ToString() == "command.exit"));
        Assert.Equal(0, fixture.Host.SessionsCreated); Assert.Equal(0, fixture.Host.Sends); Assert.Equal(1, fixture.Host.PolicyReads);
        fixture.Host.Readiness = CopilotSandboxReadiness.NotConfigured;
        var task = fixture.Context with { Task = fixture.Context.Task with { Capabilities = ["project.read", "command.execute"] } };
        Assert.Contains(await fixture.Tasks.ValidateAsync(task, TestContext.Current.CancellationToken), e => e.StartsWith("AGENT_ISOLATION_REQUIRED:", StringComparison.Ordinal));
        Assert.Equal(2, fixture.Host.PolicyReads); Assert.Equal(0, fixture.Host.SessionsCreated);
        var refused = await fixture.Tasks.RunAsync(task, TestContext.Current.CancellationToken);
        Assert.Equal("failed", refused.Status); Assert.NotNull(refused.Failure); Assert.Equal(0, fixture.Host.Sends);
        Assert.Empty(await fixture.Tasks.ValidateAsync(fixture.Context, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredPolicyDoesNotBypassEnforcementAndPreparationFailuresStaySafe(bool isolation)
    {
        await using var fixture = new Fixture();
        var context = fixture.Context with { Task = fixture.Context.Task with { Capabilities = ["project.read", "project.write", "command.execute"] } };
        fixture.Host.PreparationFailure = isolation ? new CopilotSandboxRequiredException(CopilotSandboxReadiness.Unavailable) : new IOException("credential=private-and-never-public");
        var result = await fixture.Tasks.RunAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal("failed", result.Status); Assert.Equal(0, fixture.Host.Sends); Assert.Equal(1, fixture.Host.PolicyReads);
        Assert.Equal(isolation ? "AGENT_ISOLATION_UNAVAILABLE" : "AGENT_PREPARATION_FAILED", result.Failure!.Code);
        Assert.DoesNotContain("private", result.Message); Assert.DoesNotContain("private", result.Failure.Message);
        Assert.False(result.Failure.Retryable);
        var retained = await fixture.Tasks.InspectAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(result.Failure.Code, retained.Failure!.Code);
        await fixture.Tasks.RunAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Host.SessionsCreated);
    }

    [Fact]
    public async Task ManagedExecutionPublishesObservedFilesAndReceiptsWithoutRepeatingWork()
    {
        await using var fixture = new Fixture();
        fixture.Host.OnSend = async (configuration, handle, request, ct) =>
        {
            Assert.Equal("interactive", request.AgentMode);
            await configuration.FileSystem!.WriteFileAsync("result.txt", "edited content", null, ct);
            return new(handle, "session", "{\"done\":true}", "fixture", []);
        };
        var result = await fixture.Tasks.RunAsync(fixture.Context, TestContext.Current.CancellationToken);
        Assert.Equal("completed", result.Status); Assert.True(Assert.Single(result.Evidence).Facts["changed"]!.GetValue<bool>());
        Assert.Equal("edited content", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "result.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("reserved_upper_bound", result.Usage.Metering);
        Assert.Equal("completed", (await fixture.Tasks.InspectAsync(fixture.Context, TestContext.Current.CancellationToken)).Status);
        await fixture.Tasks.RunAsync(fixture.Context, TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Host.Sends); Assert.Equal(1, fixture.Host.DisposedSessions);
    }
    [Fact]
    public async Task ClaimsCannotInventCommandEvidenceAndOpenCommandsRemainUncertain()
    {
        await using var fixture = new Fixture();
        var context = fixture.Context with { Task = fixture.Context.Task with { Capabilities = ["command.execute"], Verification = [new("tests", "command.exit", "dotnet test", new() { ["type"] = "object" })] } };
        fixture.Host.OnSend = (_, handle, _, _) => Task.FromResult(new CopilotSendResult(handle, "session", "{\"done\":true}", "fixture", [])
        { ToolExecutions = [new("call", null, "bash", "{\"command\":\"dotnet test\"}", true, true, false, [new(fixture.Root, null, "All tests passed") { ShellId = "active" }], null)] });
        var result = await fixture.Tasks.RunAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal("needs_reconciliation", result.Status); Assert.Empty(result.Evidence); Assert.Equal(0, fixture.Host.DisposedSessions);
        var findings = await new EvidenceAgentTaskVerifier().VerifyAsync(context, result, TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(findings).Passed);
    }
    [Fact]
    public async Task FailedTestThenEditThenPassingTestPreservesAttemptsAndVerifiesFinalWork()
    {
        await using var fixture = new Fixture();
        var context = fixture.Context with { Task = fixture.Context.Task with { Capabilities = ["command.execute", "project.read", "project.write"],
            Verification = [new("tests", "command.exit", "dotnet test", new() { ["type"] = "object", ["required"] = new JsonArray("exit_code"), ["properties"] = new JsonObject { ["exit_code"] = new JsonObject { ["const"] = 0 } } })] } };
        static CopilotToolExecutionObservation Command(string id, long start, long exit) => new(id, null, "bash", "{\"command\":\"dotnet test\"}", true, true, false, [new("/project", exit, "observed")], null)
        { StartedSequence = start, CompletedSequence = start + 1 };
        fixture.Host.OnSend = (_, handle, _, _) => Task.FromResult(new CopilotSendResult(handle, "session", "{}", "fixture", [])
        { ToolExecutions = [Command("before", 1, 1), new("edit", null, "project_write", "{}", true, true, false, [], null) { StartedSequence = 3, CompletedSequence = 4 }, Command("after", 5, 0)] });
        var result = await fixture.Tasks.RunAsync(context, TestContext.Current.CancellationToken);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(2, evidence.Facts["attempts"]!.AsArray().Count);
        Assert.Equal(1L, evidence.Facts["attempts"]![0]!["exit_code"]!.GetValue<long>());
        Assert.True(Assert.Single(await new EvidenceAgentTaskVerifier().VerifyAsync(context, result, TestContext.Current.CancellationToken)).Passed);
    }

    [Fact]
    public async Task InterruptedReceiptDoesNotTriggerAnotherManagedSession()
    {
        await using var fixture = new Fixture();
        fixture.Host.OnSend = (_, handle, _, _) =>
        {
            fixture.Records.FailReceipt = true;
            return Task.FromResult(new CopilotSendResult(handle, "session", "{}", "fixture", []));
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.Tasks.RunAsync(fixture.Context, TestContext.Current.CancellationToken));
        fixture.Records.FailReceipt = false;
        Assert.Equal("needs_reconciliation", (await fixture.Tasks.RunAsync(fixture.Context, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, fixture.Host.Sends);
    }
    [Fact]
    public async Task ScopeExpansionAndCrossTenantReceiptsCannotBeReused()
    {
        await using var fixture = new Fixture();
        fixture.Host.OnSend = (_, handle, _, _) => Task.FromResult(new CopilotSendResult(handle, "session", "{}", "fixture", []));
        await fixture.Tasks.RunAsync(fixture.Context, TestContext.Current.CancellationToken);
        var expanded = fixture.Context with { Task = fixture.Context.Task with { Objective = "A different unapproved task" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Tasks.RunAsync(expanded, TestContext.Current.CancellationToken));
        Assert.Equal("needs_reconciliation", (await fixture.Tasks.InspectAsync(fixture.Context with { TenantId = "other" }, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, fixture.Host.Sends);
    }
    [Fact]
    public async Task CancellationKeepsUncertaintyAndConcurrentOwnersCannotDispatch()
    {
        await using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Host.OnSend = async (_, _, _, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = fixture.Tasks.RunAsync(fixture.Context, cancel.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => fixture.Tasks.RunAsync(fixture.Context, TestContext.Current.CancellationToken));
        await cancel.CancelAsync();
        Assert.Equal("needs_reconciliation", (await running).Status); Assert.Equal(1, fixture.Host.Sends);
    }

    [Fact]
    public async Task ScopeFailuresDistinguishIdentityContractAndWorkspaceWithoutLeakingPaths()
    {
        await using var fixture = new Fixture();
        Assert.Empty(await fixture.Tasks.ValidateAsync(fixture.Context, TestContext.Current.CancellationToken));
        var missing = fixture.Context with { Task = fixture.Context.Task with { Workspace = Path.Combine(fixture.Root, "not-created") } };
        var finding = Assert.Single(await fixture.Tasks.ValidateAsync(missing, TestContext.Current.CancellationToken));
        Assert.StartsWith("AGENT_WORKSPACE_UNAVAILABLE:", finding);
        Assert.DoesNotContain(fixture.Root, finding);
        Assert.Contains(await fixture.Tasks.ValidateAsync(fixture.Context with { TenantId = "" }, TestContext.Current.CancellationToken), e => e.StartsWith("AGENT_IDENTITY_INVALID:", StringComparison.Ordinal));
        Assert.Contains(await fixture.Tasks.ValidateAsync(fixture.Context with { Task = fixture.Context.Task with { Objective = "" } }, TestContext.Current.CancellationToken), e => e.StartsWith("AGENT_CONTRACT_INVALID:", StringComparison.Ordinal));
        Assert.Equal(0, fixture.Host.Sends);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gnougo-bounded-" + Guid.NewGuid().ToString("N"));
        public CopilotTestHost Host { get; }
        public Records Records { get; } = new();
        public BoundedCopilotTasks Tasks { get; }
        public AgentTaskContext Context { get; }
        private readonly IDisposable _tenant;
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var settings = new CodeServerSettings { DefaultWorkingDirectory = Root, AllowedWorkingRoots = [Root], AllowWrites = true, AllowedExtensions = [".txt"] };
            var policy = new CodePolicy(settings, Root);
            Host = new(settings, Root, policy);
            var trace = new CodeMcpTraceContextAccessor();
            _tenant = trace.Push(CodeMcpTraceContext.FromMcpMeta(new JsonObject { ["gnougo"] = new JsonObject { ["tenantId"] = "tenant" } }));
            var options = Options.Create(settings);
            Tasks = new(Host.Manager, new(policy, options, trace), new LocalProjectSessionFsFactory(policy, options, NullLoggerFactory.Instance), Records, new(trace), policy) { LockDirectory = Path.Combine(Root, "locks") };
            Context = new("tenant", "run", "invocation", new()
            {
                Runner = "coding", Objective = "Edit result.txt", Workspace = Root, Capabilities = ["project.read", "project.write"],
                OutputSchema = new() { ["type"] = "object" }, Verification = [new("edit", "file.content", "result.txt", new() { ["type"] = "object" })]
            });
        }
        public async ValueTask DisposeAsync() { await Host.Manager.DisposeAsync(); _tenant.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Records : IKeyVaultRecordStore
    {
        private readonly Dictionary<(string, string, string), KeyVaultRecordValue> _values = [];
        public bool FailReceipt { get; set; }
        public Task<KeyVaultRecordValue?> GetAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
            => Task.FromResult(_values.GetValueOrDefault((collection, tenantId, key)));
        public Task<KeyVaultRecordValue> UpsertAsync(string collection, string tenantId, string key, string value, string author, CancellationToken ct = default)
        {
            if (FailReceipt && JsonNode.Parse(value)?["result"] is not null) throw new IOException("Injected crash persisting receipt");
            var record = new KeyVaultRecordValue(collection, tenantId, key, value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            _values[(collection, tenantId, key)] = record; return Task.FromResult(record);
        }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string collection, string tenantId, string author, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
