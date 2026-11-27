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
    public void NativeDirectoryListingDoesNotSatisfyAFileRead()
    {
        var bounds = new CopilotExecutionBounds(1, 1000, DateTimeOffset.UtcNow.AddMinutes(1),
            new HashSet<string> { "view" }, (_, _, _) => Task.CompletedTask);
        var directory = new CopilotToolExecutionObservation("dir", null, "view", """{"path":"src"}""", true, true, false, [], null)
            { StartedSequence = 1, CompletedSequence = 2 };
        var file = directory with { ToolCallId = "file", ArgumentsJson = """{"path":"src/sample.cs"}""" };
        bounds.RecordDirectoryView(directory.ToolCallId);
        var sent = new CopilotSendResult("handle", "session", "", null, []) { ToolExecutions = [directory, file] };
        var read = Assert.Single(BoundedCopilotTasks.ReadEvidence(sent, Path.GetTempPath(), bounds));
        Assert.Equal("src/sample.cs", read.Subject);
    }

    [Fact]
    public async Task ConcurrentSdkDialogsRegisterInOrderAndReceiveIndependentAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = new ConcurrentDialogProvider();
        var owner = new AgentTaskContext("tenant", "run", "review", new() { Runner = "coding" });
        var adapter = new WorkflowCopilotHumanInput(owner, journal, ct);
        var request = new CopilotHumanInputRequest(new("tenant", RunId: "run", StepId: "review"), "permission", "Read one", ["Allow once", "Refuse"], false);
        var first = adapter.RequestAsync(request, ct);
        var second = adapter.RequestAsync(request with { Prompt = "Read two" }, ct);
        Assert.Equal(1, journal.Registrations);
        journal.ReleaseRegistration.SetResult();
        await journal.BothPending.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(2, journal.Registrations);
        Assert.False(first.IsCompleted);
        journal.Replies["Read two"].SetResult(new JsonObject { ["response"] = "Refuse" });
        journal.Replies["Read one"].SetResult(new JsonObject { ["response"] = "Allow once" });
        Assert.Equal("Allow once", (await first).Answer);
        Assert.Equal("Refuse", (await second).Answer);
    }

    private sealed class ConcurrentDialogProvider : IHumanInputProvider
    {
        internal int Registrations;
        internal TaskCompletionSource ReleaseRegistration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource BothPending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> Replies { get; } = new();

        public async Task PrepareAsync(HumanInputRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Registrations);
            await ReleaseRegistration.Task.WaitAsync(ct);
        }

        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(Replies.TryAdd(request.Prompt, answer));
            if (Replies.Count == 2) BothPending.TrySetResult();
            return answer.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task LargeApprovedInputsArePagedInsideTheOriginalSdkInvocation()
    {
        await using var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        var data = new string('x', 5000) + "verified-input-tail";
        var task = fixture.Context.Task with { Inputs = new() { ["data"] = data } };
        await fixture.Tasks.RunAsync(fixture.Context with { Task = task }, ct);
        var config = fixture.Host.Configuration!.Request.Configuration;
        Assert.DoesNotContain("verified-input-tail", config.ExecutionBounds!.ApprovedPrompt);
        Assert.Contains("next_offset", config.ExecutionBounds.ApprovedPrompt);
        var reader = Assert.Single(config.HostTools);
        Assert.Contains(reader.Name, config.AvailableTools!);
        using var address = JsonDocument.Parse("""{"offset":4096}""");
        var page = await reader.Invoke(address.RootElement, ct);
        Assert.Contains("verified-input-tail", page.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next_offset").ValueKind);
        using var invalid = JsonDocument.Parse("""{"offset":-1}""");
        await Assert.ThrowsAsync<ArgumentException>(() => reader.Invoke(invalid.RootElement, ct));
    }

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
        if (!pinned || !read) return;
        output["findings"] = new JsonArray(new JsonObject { ["severity"] = "P1" });
        var malformed = CodeReviewEvidence.Verify(task, result);
        Assert.Equal("failed", malformed.Status);
        Assert.Contains("severity", malformed.Message);
        Assert.Contains(ReviewValidation.FindingFieldContract, malformed.Message);
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
