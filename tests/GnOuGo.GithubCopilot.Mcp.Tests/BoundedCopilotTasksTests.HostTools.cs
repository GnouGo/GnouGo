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
    public async Task DiscoveryDescribesConditionalToolsWithoutStartingTheSdk()
    {
        await using var fixture = new Fixture();
        var contract = await fixture.Tasks.ContractAsync(TestContext.Current.CancellationToken, inspectRuntime: false);
        Assert.Contains("project.native_read", contract.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(0, fixture.Host.PolicyReads);
        Assert.Equal(0, fixture.Host.SessionsCreated);
        Assert.Equal(0, fixture.Host.Sends);
    }

    [Theory]
    [InlineData(true, true, "completed")]
    [InlineData(false, true, "failed")]
    [InlineData(true, false, "failed")]
    public void ReviewRequiresPinnedRevisionsAndActualReadsEvenWithNoFindings(bool pinned, bool read, string status)
    {
        var basis = new string('a', 40);
        var head = new string('b', 40);
        var task = new AgentTaskDefinition
        {
            Verification = [new("review", "review.diff", head, new())],
            Inputs = new() { ["review"] = new JsonObject { ["base"] = basis, ["head"] = head, ["files"] = new JsonArray(
                new JsonObject { ["path"] = "sample.cs", ["status"] = "modified", ["patch"] = "@@ -1 +1 @@\n-old\n+new" }) } }
        };
        var output = new JsonObject { ["base"] = basis, ["head"] = pinned ? head : basis, ["findings"] = new JsonArray(),
            ["reviewed_files"] = new JsonArray("sample.cs"), ["limitations"] = new JsonArray() };
        var result = new AgentTaskResult("completed", output, read ? [new("read-1", "file.read", "sample.cs", new())] : [], [], new(1, 50, 100));
        var verified = CodeReviewEvidence.Verify(task, result);
        Assert.Equal(status, verified.Status);
        Assert.Equal(status == "completed", verified.Evidence.Any(e => e.Kind == "review.diff" && e.Subject == head));
    }

    [Fact]
    public async Task SameApprovedScopeUsesCurrentObservationAcrossServersAndInspectsWithoutRewriting()
    {
        await using var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        var transport = new InMemoryMcpClientFactory();
        var observe = new McpToolInfo { Name = "observe", InputSchema = new JsonObject { ["type"] = "object" }, EffectKind = "read" };
        var resolve = new McpToolInfo { Name = "resolve", InputSchema = JsonNode.Parse("""{"type":"object","required":["cursor"],"properties":{"cursor":{"type":"string"}},"additionalProperties":false}"""), EffectKind = "read" };
        var write = new McpToolInfo { Name = "write", InputSchema = JsonNode.Parse("""{"type":"object","required":["value"],"properties":{"value":{"type":"string"}},"additionalProperties":false}"""), EffectKind = "write" };
        var writes = new List<string>();
        var cursorReads = new List<string>();
        var upstream = "first";
        transport.RegisterServer("source", new() { Tools = [observe, resolve], ToolHandlers = {
            ["observe"] = _ => new() { Content = upstream == "changed" ? new JsonObject { ["cursor"] = "page-2" } : new JsonObject { ["value"] = upstream } },
            ["resolve"] = input =>
            {
                cursorReads.Add(input!["cursor"]!.GetValue<string>());
                return new() { Content = new JsonObject { ["value"] = upstream } };
            }
        } });
        transport.RegisterServer("destination", new() { Tools = [write], ToolHandlers = { ["write"] = input =>
        {
            writes.Add(input!["value"]!.GetValue<string>());
            if (upstream == "interrupted") throw new IOException("The write receipt was lost.");
            return new() { Content = new JsonObject { ["saved"] = writes[^1] } };
        } } });
        var runner = new BusinessAgentTaskRunner(transport, [new("source", "observe"), new("source", "resolve"), new("destination", "write")]);
        var contract = await runner.DescribeAsync(ct);
        var tools = contract.InputSchema["properties"]!["capabilities"]!["items"]!["enum"]!.AsArray()
            .Select(v => v!.GetValue<string>()).Where(id => id.StartsWith("mcp_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, tools.Length);
        Assert.DoesNotContain("project.read", contract.InputSchema["properties"]!["capabilities"]!["items"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.NotEmpty(await runner.ValidateAsync(fixture.Context with {
            Task = fixture.Context.Task with { Capabilities = ["command.execute"] } }, ct));
        var task = fixture.Context.Task with { Capabilities = [.. tools], Budget = new() { MaxActions = 3 },
            Verification = [new("saved", "mcp.result", tools[2], JsonNode.Parse("""{"type":"object","required":["result"],"properties":{"result":{"type":"object","required":["saved"]}}}""")!.AsObject())] };
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
        foreach (var variant in new[] { "first", "changed", "interrupted" })
        {
            upstream = variant;
            var engine = new WorkflowEngine { McpClientFactory = transport, LLMClient = new ObservationModel(tools),
                LlmDefaults = new() { Model = "fixture" }, RunStore = store, Limits = new() { TenantId = "tenant", RunId = variant } };
            engine.AgentTaskRunners[task.Runner] = runner;
            var executed = await engine.ExecuteAsync(workflow, null, ct);
            Assert.Equal(variant != "interrupted", executed.Success);
            var invocation = Assert.Single((await store.ReadAsync("tenant", variant, ct))!.Invocations.Values);
            var recorded = invocation.Observation!.Deserialize(AgentTaskJsonContext.Default.AgentTaskResult)!;
            var context = fixture.Context with { RunId = variant, InvocationId = invocation.Id, Task = task, LastObservation = recorded };
            var replay = await runner.ReconcileAsync(context, ct);
            Assert.Equal(variant == "interrupted" ? "needs_reconciliation" : "completed", replay.Status);
            Assert.Equal(variant == "changed" ? 3 : 2, replay.Usage.Actions);
            if (variant != "interrupted")
                Assert.True(Assert.Single(await new EvidenceAgentTaskVerifier().VerifyAsync(context, replay, ct)).Passed);
        }
        Assert.Equal(new[] { "first", "changed", "interrupted" }, writes);
        Assert.Equal(new[] { "page-2" }, cursorReads);
        write.InputSchema!["additionalProperties"] = true;
        Assert.NotEmpty(await runner.ValidateAsync(fixture.Context with { Task = task }, ct));
        Assert.Equal(0, fixture.Host.Sends);
    }

    private sealed class ObservationModel(string[] tools) : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var observations = JsonNode.Parse(request.Prompt!.Split("\nObservations:\n", StringSplitOptions.None)[^1])!.AsArray();
            var latest = observations.LastOrDefault()?["text"]?.GetValue<string>();
            var value = latest is null ? null : JsonNode.Parse(latest);
            var action = value is null ? tools[0] : value["cursor"] is not null ? tools[1] : value["value"] is not null ? tools[2] : "finish";
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["action"] = action,
                ["payload"] = action == "finish" ? "{}" : latest ?? "{}" },
                Usage = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 8, ["total_tokens"] = 18 } });
        }
    }
}
