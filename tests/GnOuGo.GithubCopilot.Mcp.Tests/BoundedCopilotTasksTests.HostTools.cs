using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Copilot;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.GithubCopilot.Core;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed partial class BoundedCopilotTasksTests
{
    [Fact]
    public async Task SameApprovedScopeUsesCurrentObservationAcrossServersAndInspectsWithoutRewriting()
    {
        await using var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        var transport = new HostToolTransport(fixture.Tasks);
        var observe = new McpToolInfo { Name = "observe", InputSchema = new JsonObject { ["type"] = "object" }, EffectKind = "read" };
        var resolve = new McpToolInfo { Name = "resolve", InputSchema = JsonNode.Parse("""{"type":"object","required":["cursor"],"properties":{"cursor":{"type":"string"}},"additionalProperties":false}"""), EffectKind = "read" };
        var write = new McpToolInfo { Name = "write", InputSchema = JsonNode.Parse("""{"type":"object","required":["value"],"properties":{"value":{"type":"string"}},"additionalProperties":false}"""), EffectKind = "write" };
        var writes = new List<string>();
        var cursorReads = new List<string>();
        var upstream = "first";
        transport.Servers.RegisterServer("source", new() { Tools = [observe, resolve], ToolHandlers = {
            ["observe"] = _ => new() { Content = upstream == "changed" ? new JsonObject { ["cursor"] = "page-2" } : new JsonObject { ["value"] = upstream } },
            ["resolve"] = input =>
            {
                cursorReads.Add(input!["cursor"]!.GetValue<string>());
                return new() { Content = new JsonObject { ["value"] = upstream } };
            }
        } });
        transport.Servers.RegisterServer("destination", new() { Tools = [write], ToolHandlers = { ["write"] = input =>
        {
            writes.Add(input!["value"]!.GetValue<string>());
            return new() { Content = new JsonObject { ["saved"] = writes[^1] } };
        } } });
        var runner = new CopilotAgentTaskRunner(transport, "managed", [new("source", "observe"), new("source", "resolve"), new("destination", "write")]);
        var contract = await runner.DescribeAsync(ct);
        var tools = contract.InputSchema["properties"]!["capabilities"]!["items"]!["enum"]!.AsArray()
            .Select(v => v!.GetValue<string>()).Where(id => id.StartsWith("mcp_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, tools.Length);
        Assert.DoesNotContain("project.read", contract.InputSchema["properties"]!["capabilities"]!["items"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ValidateAsync(fixture.Context with {
            Task = fixture.Context.Task with { Capabilities = ["command.execute"] } }, ct));
        var task = fixture.Context.Task with { Capabilities = [.. tools], Budget = new() { MaxActions = 3 },
            Verification = [new("saved", "mcp.result", tools[2], JsonNode.Parse("""{"type":"object","required":["result"],"properties":{"result":{"type":"object","required":["saved"]}}}""")!.AsObject())] };
        fixture.Host.OnSend = async (configuration, handle, request, token) =>
        {
            var runtime = configuration.Request.Configuration;
            var bounds = runtime.ExecutionBounds!;
            var observed = new List<CopilotToolExecutionObservation>();
            var current = await Call(tools[0], "{}");
            if (current.TryGetProperty("cursor", out _))
                current = await Call(tools[1], current.GetRawText());
            await Call(tools[2], current.GetRawText());
            if (upstream == "interrupted") throw new IOException("Provider lost the final turn after tool completion.");
            return new(handle, "session", "{}", null, []) { ToolExecutions = observed };

            async Task<JsonElement> Call(string id, string args)
            {
                var function = runtime.HostTools.Single(t => t.Name == id);
                var call = $"call-{observed.Count}";
                using var parameters = JsonDocument.Parse(args);
                Assert.Null(await bounds.AdaptiveActions!.AdmitAsync(bounds, "session", call, id, parameters.RootElement));
                var invocation = new CopilotHostFunction(function, bounds);
                var inputs = new Microsoft.Extensions.AI.AIFunctionArguments();
                foreach (var property in parameters.RootElement.EnumerateObject()) inputs[property.Name] = property.Value;
                var value = Assert.IsType<JsonElement>(await invocation.InvokeAsync(inputs, token));
                await Assert.ThrowsAsync<InvalidOperationException>(async () => { await invocation.InvokeAsync(inputs, token); });
                await bounds.AdaptiveActions.CompleteAsync(bounds, "session", call, id, value, true);
                observed.Add(new(call, null, id, args, true, true, false, [], null) { StartedSequence = observed.Count * 2 + 1,
                    CompletedSequence = observed.Count * 2 + 2, CopilotSessionId = "session" });
                return value;
            }
        };
        var yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - id: collect
                    type: agent.run
                    input:
            """.TrimEnd() + " " + JsonSerializer.Serialize(task, AgentTaskJsonContext.Default.AgentTaskDefinition);
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"];
        var store = new InMemoryWorkflowRunStore();
        foreach (var variant in new[] { "first", "changed" })
        {
            upstream = variant;
            var engine = new WorkflowEngine { McpClientFactory = transport, RunStore = store, Limits = new() { TenantId = "tenant", RunId = variant } };
            engine.AgentTaskRunners[task.Runner] = runner;
            var executed = await engine.ExecuteAsync(workflow, null, ct);
            Assert.True(executed.Success, executed.Error?.Message);
            var journal = await store.ReadAsync("tenant", variant, ct);
            var invocation = Assert.Single(journal!.Invocations.Values);
            var context = fixture.Context with { RunId = variant, InvocationId = invocation.Id, Task = task };
            var result = await runner.RunAsync(context, ct);
            Assert.Equal("completed", result.Status);
            Assert.Equal(variant == "changed" ? 3 : 2, result.Usage.Actions);
            Assert.True(Assert.Single(await new EvidenceAgentTaskVerifier().VerifyAsync(context, result, ct)).Passed);
            var replay = await runner.ReconcileAsync(context, ct);
            Assert.Equal(variant, replay.Evidence.Single(e => e.Subject == tools[2]).Facts["result"]!["saved"]!.GetValue<string>());
            Assert.Equal(result.Usage.Actions, replay.Evidence.Count(e => e.Kind == "mcp.result"));
        }
        upstream = "interrupted";
        var interrupted = fixture.Context with { RunId = "interrupted", Task = task };
        Assert.Equal("needs_reconciliation", (await runner.RunAsync(interrupted, ct)).Status);
        var retained = await runner.ReconcileAsync(interrupted, ct);
        Assert.Equal("needs_reconciliation", retained.Status);
        Assert.Equal("interrupted", retained.Evidence.Single(e => e.Kind == "mcp.result" && e.Subject == tools[2]).Facts["result"]!["saved"]!.GetValue<string>());
        Assert.Equal(new[] { "first", "changed", "interrupted" }, writes);
        Assert.Equal(new[] { "page-2" }, cursorReads);
        write.InputSchema!["additionalProperties"] = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ValidateAsync(fixture.Context with { Task = task }, ct));
        Assert.Equal(3, fixture.Host.Sends);
    }

    [Fact]
    public async Task CanceledQueuedActionCannotBeIssuedAndClaimedActionCannotBeIssuedTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        using var exchange = new CopilotTaskToolExchange(JsonNode.Parse("""{"write":{"server":"dst","method":"write","input_schema":{"type":"object"}}}""")!.AsObject());
        var function = Assert.Single(exchange.BuildTools());
        using var first = JsonDocument.Parse("""{"value":"old"}""");
        using var second = JsonDocument.Parse("""{"value":"current"}""");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var abandoned = function.Invoke(first.RootElement, cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        var current = function.Invoke(second.RootElement, ct);
        var claimed = await exchange.ExchangeAsync(null, ct);
        Assert.Equal("current", claimed["request"]!["value"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => exchange.ExchangeAsync(null, ct));
        exchange.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => current);
        Assert.True(exchange.Uncertain);
        Assert.Empty(exchange.Evidence);
    }

    private sealed class HostToolTransport(BoundedCopilotTasks tasks) : IMcpClientFactory, IMcpSession
    {
        internal InMemoryMcpClientFactory Servers { get; } = new();
        private CopilotTaskToolExchange? _exchange;
        public string ServerName => "managed";
        public IReadOnlyList<McpServerMetadata> ServerMetadata => Servers.ServerMetadata;
        public Task<IMcpSession> GetClientAsync(string server, CancellationToken ct) =>
            server == ServerName ? Task.FromResult<IMcpSession>(this) : Servers.GetClientAsync(server, ct);
        public async Task<McpCallResult> CallToolAsync(string method, JsonNode? args, CancellationToken ct)
        {
            if (method == "copilot_task_contract") return new() { Content = await tasks.ContractAsync(ct, args?["hostToolsOnly"]?.GetValue<bool>() == true) };
            if (method == "copilot_task_exchange")
                return new() { Content = await _exchange!.ExchangeAsync(args!["receiptJson"] is { } receipt ? JsonNode.Parse(receipt.GetValue<string>())!.AsObject() : null, ct) };
            var context = JsonSerializer.Deserialize(args!["contextJson"]!.GetValue<string>(), AgentTaskJsonContext.Default.AgentTaskContext)!;
            AgentTaskResult result;
            var recovered = false;
            if (method == "copilot_task_inspect") result = await tasks.InspectAsync(context, ct);
            else
            {
                var manifest = CopilotTaskToolExchange.ParseManifest(context, args["toolsJson"]!.GetValue<string>());
                if (method == "copilot_task_validate")
                    return new() { Content = new JsonObject { ["errors"] = new JsonArray((await tasks.ValidateAsync(context, ct, manifest)).Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) } };
                Assert.Equal("copilot_task_run", method);
                using var exchange = _exchange = new(manifest);
                result = await tasks.RunAsync(context, ct, exchange);
                recovered = exchange.Recovered;
            }
            return new() { Content = new JsonObject { ["schemaVersion"] = 9, ["recovered"] = recovered, ["result"] = JsonSerializer.SerializeToNode(result, AgentTaskJsonContext.Default.AgentTaskResult) } };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<McpGetPromptResult> GetPromptAsync(string name, JsonNode? arguments, CancellationToken ct) => throw new NotSupportedException();
    }
}
