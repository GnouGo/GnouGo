using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

[Collection("Copilot tool discovery")]
public sealed class CopilotTasksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Dictionary<string, object?> Args => new() { ["projectRoot"] = "workflows/project", ["prompt"] = "Inspect the project and report verified results." };
    private static CodeMcpTraceContext Context(string tenant) => CodeMcpTraceContext.FromMcpMeta(new() { ["gnougo"] = new JsonObject { ["tenantId"] = tenant } })!;

    [Fact]
    public async Task VerifiedContinuationRetainsObjectiveEvidenceAndCumulativeReservations()
    {
        await using var fixture = await CopilotAttachmentTests.Fixture.CreateAsync();
        var calls = 0; string? taskId = null; CopilotInferenceBudget? firstBudget = null;
        fixture.Host.OnSend = async (configuration, handle, request, ct) =>
        {
            taskId = fixture.tasks.CurrentTaskId;
            var budget = configuration.Request.Configuration.LogicalInferenceBudget!;
            if (firstBudget is null) firstBudget = budget; else Assert.Same(firstBudget, budget);
            using var inference = new HttpRequestMessage(HttpMethod.Post, "https://example.test/responses")
                { Content = new StringContent("{\"input\":\"fixture\",\"max_output_tokens\":1}") };
            // Real SDK transport callbacks may run without the originating AsyncLocal metadata.
            using (fixture.Host.Trace.Push(null)) await budget.ReserveAsync(inference, ct);
            calls++;
            var result = new CopilotSendResult(handle, "session-" + calls, calls == 1 ? "Partial evidence" : "Finished", "mock", [], calls != 1)
            { ToolExecutions = [new("call", null, "arbitrary-operation", "{}", true, true, false, [], null) { StartedSequence = 1, CompletedSequence = 2 }] };
            if (calls == 1) throw new CopilotSendInterruptedException(result, false, verifiedContinuation: true);
            Assert.Contains((string)Args["prompt"]!, request.Prompt); Assert.Contains("Partial evidence", request.Prompt);
            Assert.Equal(2, budget.ModelCalls);
            return result;
        };
        var response = await fixture.Call("copilot_interactive_one_shot", Args, "fixture-tenant");
        Assert.False(response.IsError == true);
        var result = response.StructuredContent!.Value.Deserialize(CopilotCoreJsonContext.Default.CopilotSendResult)!;
        Assert.True(result.Completed); Assert.Equal(2, result.ToolExecutions.Count); Assert.Null(result.Usage);
        Assert.Equal(["session-1:call", "session-2:call"], result.ToolExecutions.Select(o => o.ToolCallId));
        Assert.Equal(2, fixture.Host.SessionsCreated); Assert.Equal(2, fixture.Host.DisposedSessions);
        using var scope = fixture.Host.Trace.Push(Context("fixture-tenant"));
        var checkpoint = await fixture.tasks.ReadOperationAsync(taskId!, Ct);
        Assert.Equal("completed", checkpoint!["phase"]!.GetValue<string>());
        Assert.Equal(2, checkpoint["inferenceAttempts"]!.GetValue<int>());
        Assert.Equal(firstBudget!.ChargedTokens, checkpoint["reservedTokens"]!.GetValue<long>());
        Assert.Equal(2, checkpoint["evidence"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task UnknownCompletionNeverContinuesAndVerifiedContinuationStopsAtHostCeiling(bool verified, int expected)
    {
        await using var fixture = await CopilotAttachmentTests.Fixture.CreateAsync(configure: settings => settings.Copilot.LogicalLimits.Sessions = 2);
        fixture.Host.OnSend = (_, handle, _, _) => throw new CopilotSendInterruptedException(
            new(handle, "mock-" + fixture.Host.SessionsCreated, "Partial only", null, [], false), false, verified);
        if (verified)
        {
            var response = await fixture.Call("copilot_interactive_one_shot", Args, "fixture-tenant");
            Assert.False(response.IsError == true);
            Assert.False(response.StructuredContent!.Value.GetProperty("completed").GetBoolean());
        }
        else await Assert.ThrowsAsync<McpException>(() => fixture.Call("copilot_interactive_one_shot", Args, "fixture-tenant"));
        Assert.Equal(expected, fixture.Host.SessionsCreated);
        Assert.Equal(verified ? expected : 0, fixture.Host.DisposedSessions);
    }

    [Fact]
    public async Task InteractionCeilingStopsWithoutAnotherSessionOrSuccessClaim()
    {
        var answers = 0;
        await using var fixture = await CopilotAttachmentTests.Fixture.CreateAsync(new McpClientOptions { Handlers = new()
        { ElicitationHandler = (_, _) => { answers++; return ValueTask.FromResult(new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement> { ["answer"] = JsonSerializer.SerializeToElement("A") } }); } } },
            settings => settings.Copilot.LogicalLimits.Interactions = 2);
        fixture.Host.OnSend = async (configuration, handle, _, ct) =>
        {
            for (var i = 0; i < 3; i++) await configuration.HumanInputProvider!.RequestAsync(new(new("fixture-tenant"), "question", "Decision " + i, ["A", "B"], true), ct);
            return new(handle, "mock", "Unexpected completion", null, []);
        };
        await Assert.ThrowsAsync<McpException>(() => fixture.Call("copilot_interactive_one_shot", Args, "fixture-tenant"));
        Assert.Equal(2, answers); Assert.Equal(1, fixture.Host.SessionsCreated);
    }

    [Fact]
    public async Task UnknownTaskCompletionStopsFlowWithoutCleanupOrRedispatch()
    {
        await using var fixture = await CopilotAttachmentTests.Fixture.CreateAsync();
        string? taskId = null;
        fixture.Host.OnSend = (_, handle, _, _) =>
        {
            taskId = fixture.tasks.CurrentTaskId;
            throw new CopilotSendInterruptedException(new(handle, "retained-session", "Partial evidence", null, [], false), false);
        };
        var store = new InMemoryWorkflowRunStore();
        var engine = new WorkflowEngine { RunStore = store, Limits = new() { TenantId = "fixture-tenant", RunId = "uncertain" } };
        var adapter = new TaskAdapter(fixture); engine.Registry.Register(adapter);
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - { id: work, type: mcp.call }
                finally:
                  - { id: cleanup, type: mcp.call, input: { cleanup: true } }
            """)).Workflows["main"];
        var result = await engine.ExecuteAsync(workflow, null, Ct);
        Assert.Equal("RUN_NEEDS_RECONCILIATION", result.Error?.Code);
        Assert.Equal(0, adapter.Cleanups); Assert.Equal(0, fixture.Host.DisposedSessions);
        var retained = (await store.ReadAsync("fixture-tenant", "uncertain", Ct))!;
        var resumed = await engine.ResumeAsync("fixture-tenant", "uncertain", retained.Revision, workflow, Ct);
        Assert.Equal("RUN_NEEDS_RECONCILIATION", resumed.Error?.Code);
        Assert.Equal(1, fixture.Host.Sends); Assert.Equal(0, adapter.Cleanups);
        using var scope = fixture.Host.Trace.Push(Context("fixture-tenant"));
        var checkpoint = (await fixture.tasks.ReadOperationAsync(taskId!, Ct))!;
        Assert.Equal("reconciliation_required", checkpoint["phase"]!.GetValue<string>());
        Assert.Contains("Partial evidence", checkpoint["result"]!.ToJsonString());
    }

    private sealed class TaskAdapter(CopilotAttachmentTests.Fixture fixture) : IStepExecutor
    {
        public string StepType => "mcp.call";
        internal int Cleanups;
        public async Task<JsonNode?> ExecuteAsync(StepExecutionContext context, CancellationToken ct)
        {
            if (context.Engine.GetResolvedInput(context)?["cleanup"]?.GetValue<bool>() == true)
            { Cleanups++; return new JsonObject(); }
            var response = await fixture.Call("copilot_interactive_one_shot", Args, "fixture-tenant", ct);
            return JsonNode.Parse(response.StructuredContent!.Value.GetRawText());
        }
    }

    [Fact]
    public async Task CancellationReachesTaskOwnerAndRetainsPartialCheckpoint()
    {
        await using var fixture = await CopilotAttachmentTests.Fixture.CreateAsync();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Host.OnSend = async (_, _, _, ct) =>
        {
            started.SetResult(fixture.tasks.CurrentTaskId!);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException();
        };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var call = fixture.Call("copilot_interactive_one_shot", Args, "fixture-tenant", cancel.Token);
        var id = await started.Task.WaitAsync(Ct); await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        using var scope = fixture.Host.Trace.Push(Context("fixture-tenant"));
        Assert.Equal(McpTaskStatus.Cancelled, (await fixture.tasks.GetTaskAsync(id, Ct))!.Status);
        // Cancellation acknowledgement precedes the owner's durable partial-result
        // flush. Keep the fixture alive until that work has actually completed.
        using var flush = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        flush.CancelAfter(TimeSpan.FromSeconds(5));
        JsonObject? checkpoint;
        while ((checkpoint = await fixture.tasks.ReadOperationAsync(id, flush.Token))?["result"] is null)
            await Task.Delay(10, flush.Token);
        Assert.Equal("reconciliation_required", checkpoint["phase"]!.ToString());
        Assert.False(checkpoint["result"]!["completed"]!.GetValue<bool>());
        Assert.Equal(1, fixture.Host.SessionsCreated); Assert.Equal(0, fixture.Host.DisposedSessions);
    }

    [Fact]
    public async Task EncryptedTaskRecoveryPreservesAnswersAndStopsAbandonedExternalWork()
    {
        var root = Directory.CreateTempSubdirectory("copilot-task-recovery-").FullName;
        try
        {
            var path = Path.Combine(root, "vault.db"); var leases = Path.Combine(root, "leases");
            var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(path, root);
            var trace = new CodeMcpTraceContextAccessor(); using var tenant = trace.Push(Context("tenant-a"));
            string id;
            await using (var owner = new KeyVaultCopilotTaskStore(records, trace, leases))
            {
                using var invocation = owner.BeginInvocation(); id = (await owner.CreateTaskAsync(Ct)).TaskId;
                await owner.UpdateOperationAsync(id, state => { state["objective"] = "private-objective"; state["reservedTokens"] = 1234; }, Ct);
                var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var deliveries = 0;
                owner.InputResponseReceived += _ => { Interlocked.Increment(ref deliveries); delivered.TrySetResult(); };
                await owner.SetInputRequestsAsync(id, new Dictionary<string, InputRequest> { ["q"] = new() { Method = "elicitation/create", Params = JsonSerializer.SerializeToElement(new { message = "question" }) } }, Ct);
                await using var second = new KeyVaultCopilotTaskStore(records, trace, leases);
                Assert.Equal(McpTaskStatus.InputRequired, (await second.GetTaskAsync(id, Ct))!.Status);
                var response = new Dictionary<string, InputResponse> { ["q"] = new() { RawValue = JsonSerializer.SerializeToElement(new { action = "decline" }) } };
                await second.ResolveInputRequestsAsync(id, response, Ct); await second.ResolveInputRequestsAsync(id, response, Ct);
                await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); Assert.Equal(1, deliveries);
                using (trace.Push(Context("tenant-b"))) Assert.Null(await second.GetTaskAsync(id, Ct));
            }
            await using var restarted = new KeyVaultCopilotTaskStore(KeyVaultRecordStoreFactory.CreateWorkspaceStore(path, root), trace, leases);
            var recovered = await restarted.GetTaskAsync(id, Ct);
            Assert.Equal(McpTaskStatus.Failed, recovered!.Status);
            Assert.Contains("COPILOT_NEEDS_RECONCILIATION", recovered.Error!.Value.GetRawText());
            Assert.Equal(1234, (await restarted.ReadOperationAsync(id, Ct))!["reservedTokens"]!.GetValue<int>());
            Assert.Contains("decline", (await restarted.AnswersAsync(id, Ct)).ToJsonString());
            Assert.DoesNotContain("private-objective", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, Ct)));
        }
        finally { Directory.Delete(root, true); }
    }
}
