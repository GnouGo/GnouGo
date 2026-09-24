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

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gnougo-bounded-" + Guid.NewGuid().ToString("N"));
        public CopilotTestHost Host { get; }
        public Records Records { get; } = new();
        public BoundedCopilotTasks Tasks { get; }
        public AgentTaskContext Context { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var settings = new CodeServerSettings { DefaultWorkingDirectory = Root, AllowedWorkingRoots = [Root], AllowWrites = true, AllowedExtensions = [".txt"] };
            var policy = new CodePolicy(settings, Root);
            Host = new(settings, Root, policy);
            var trace = new CodeMcpTraceContextAccessor();
            var options = Options.Create(settings);
            Tasks = new(Host.Manager, new(policy, options, trace), new LocalProjectSessionFsFactory(policy, options, NullLoggerFactory.Instance), Records, new(trace), policy) { LockDirectory = Path.Combine(Root, "locks") };
            Context = new("tenant", "run", "invocation", new()
            {
                Runner = "coding", Objective = "Edit result.txt", Workspace = Root, Capabilities = ["project.read", "project.write"],
                OutputSchema = new() { ["type"] = "object" }, Verification = [new("edit", "file.content", "result.txt", new() { ["type"] = "object" })]
            });
        }
        public async ValueTask DisposeAsync() { await Host.Manager.DisposeAsync(); Directory.Delete(Root, true); }
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
