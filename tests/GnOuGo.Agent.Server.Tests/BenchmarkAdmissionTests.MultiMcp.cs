using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using System.Text.RegularExpressions;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Copilot;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerBusinessRunnerReadsTheFileReportedByGitWithoutAnSdk(bool rateLimited)
    {
        using var fixture = new Fixture();
        var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "workspace")).FullName;
        var project = Path.Combine(workspace, "project");
        LibGit2Sharp.Repository.Init(project);
        var file = $"note-{Guid.NewGuid():N}.txt";
        var marker = $"sample-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(Path.Combine(project, file), marker, Ct);

        var options = RuntimeOptions();
        options.ModelOverrides["openai/pinned"].Capabilities = new() { SupportsStructuredOutput = true };
        const string upstream = "http://127.0.0.1:1/v1";
        options.Models["fixture"].Url = upstream;
        using var provider = new GitDocumentProvider(rateLimited);
        using var http = new HttpClient(provider);
        using var model = RuntimeModel(fixture, options, http);
        const string git = "GnOuGo.Git.Mcp", documents = "GnOuGo.Document.Mcp";
        McpServerOptions Server(string name, string section) => new()
        {
            Type = "stdio", Command = Path.Combine(AppContext.BaseDirectory, "tools", name, name + (OperatingSystem.IsWindows() ? ".exe" : "")),
            EnvironmentVariables = new()
            {
                [section + "__DefaultWorkingDirectory"] = workspace, [section + "__AllowedWorkingRoots__0"] = workspace,
                ["KeyVault__DatabasePath"] = Path.Combine(fixture.Root, name + ".db"), ["OpenTelemetry__Enabled"] = "false"
            }
        };
        options.McpServers[git] = Server(git, "Git");
        options.McpServers[documents] = Server(documents, "Document");
        var factory = new SecureWorkflowRuntimeFactory(SmartFlowTestFactory.CreateRuntimeOptionsStore(options),
            new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options),
            copilotRunners: new Dictionary<string, string> { ["observe"] = "business" },
            copilotTools: new Dictionary<string, IReadOnlyList<CopilotToolBinding>>
                { ["observe"] = [new(git, "git_status"), new(documents, "document_read")] });
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = limit.Token;
        await using var runtime = await factory.CreateAsync(ct);
        var store = new InMemoryWorkflowRunStore();
        var engine = runtime.ConfigureAgentRunners(new()
        {
            McpClientFactory = runtime.McpClientFactory, LLMClient = new LiveWorkflowEvaluation.ExecutionModel(model, "multi"),
            LlmDefaults = new() { Provider = "fixture", Model = "pinned" },
            RunStore = store, ModelUsageCostEstimator = new Price(), Limits = new() { TenantId = "fixture", RunId = "multi" }
        });
        var runner = engine.AgentTaskRunners["observe"];
        var contract = await runner.DescribeAsync(ct);
        var capabilities = contract.InputSchema["properties"]!["capabilities"]!["items"]!["enum"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(2, capabilities.Count);
        provider.Capabilities = capabilities;
        var task = new AgentTaskDefinition
        {
            Runner = "observe", Workspace = workspace,
            Objective = "Read Git status in the supplied project, read the changed text file and return its value.",
            Inputs = new() { ["project"] = "project" }, Capabilities = capabilities,
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")!.AsObject(),
            Budget = new() { MaxActions = 2, MaxModelCalls = 4, MaxElapsedMilliseconds = 60_000, MaxTotalTokens = 300_000 },
            Verification = capabilities.Select((id, i) => new AgentVerificationRequirement("read-" + i, "mcp.result", id,
                JsonNode.Parse("""{"type":"object","required":["request","result"]}""")!.AsObject())).ToList()
        };
        var context = new AgentTaskContext("fixture", "multi", "main/observe", task);
        Assert.Empty(await runner.ValidateAsync(context, ct));
        Assert.NotEmpty(await runner.ValidateAsync(context with { Task = task with { Capabilities = [.. capabilities, "command.execute"] } }, ct));
        var yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - id: observe
                    type: agent.run
                    input:
            """.TrimEnd() + " " + JsonSerializer.Serialize(task, AgentTaskJsonContext.Default.AgentTaskDefinition);
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"];
        var executed = await engine.ExecuteAsync(workflow, null, ct);
        var invocation = Assert.Single((await store.ReadAsync("fixture", "multi", ct))!.Invocations.Values);
        var result = invocation.Observation!.Deserialize(AgentTaskJsonContext.Default.AgentTaskResult)!;
        Assert.True(executed.Success, $"{result.Status}: {result.Failure?.Code} {result.Failure?.Message} {result.Failure?.Details} {result.Message}");
        Assert.True(result.Status == "completed", result.Message ?? result.Failure?.Message ?? result.Status);
        Assert.Equal(marker, result.Output!["value"]!.GetValue<string>());
        Assert.Equal(2, result.Usage.Actions);
        Assert.All(await new EvidenceAgentTaskVerifier().VerifyAsync(context, result, ct), finding => Assert.True(finding.Passed));
        Assert.Equal(capabilities, result.Evidence.Select(e => e.Subject));
        Assert.Equal("project/" + file, result.Evidence[1].Facts["request"]!["filePath"]!.GetValue<string>());
        Assert.Contains(marker, result.Evidence[1].Facts["result"]!.ToJsonString());
        var recovered = await runner.ReconcileAsync(context with { InvocationId = invocation.Id, LastObservation = result }, ct);
        Assert.Equal(result.Evidence.Select(e => e.Id), recovered.Evidence.Select(e => e.Id));
        var expectedCalls = rateLimited ? 4 : 3;
        Assert.Equal(expectedCalls, result.Usage.ModelCalls);
        Assert.Equal(expectedCalls, provider.Requests.Count);
        if (rateLimited) Assert.Equal(provider.Requests[1], provider.Requests[2]);
        Assert.Equal((long)expectedCalls, (await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: ct))["calls"]!.GetValue<long>());
    }

    private sealed class GitDocumentProvider(bool rateLimited) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        internal IReadOnlyList<string> Capabilities { get; set; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var wire = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(wire);
            if (rateLimited && Requests.Count == 2)
            {
                var rejected = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    { Content = new StringContent("""{"error":{"code":"rate_limit_exceeded"}}""") };
                rejected.Headers.TryAddWithoutValidation("retry-after-ms", "10");
                return rejected;
            }
            var body = JsonNode.Parse(wire)!;
            var prompt = body["messages"]!.AsArray().Last(m => m!["role"]?.ToString() == "user")!["content"]!.ToString();
            var observations = JsonNode.Parse(prompt.Split("\nObservations:\n", StringSplitOptions.None)[^1])!.AsArray();
            string Observed(string pattern)
            {
                var text = observations[^1]!["text"]!.GetValue<string>();
                var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
                Assert.True(match.Success, "The decision requires a value from the preceding real tool receipt.");
                return match.Value;
            }
            var (action, payload) = observations.Count switch
            {
                0 => (Capabilities[0], new JsonObject { ["projectRoot"] = "project" }),
                1 => (Capabilities[1], new JsonObject { ["filePath"] = "project/" + Observed(@"note-[a-f0-9]{32}\.txt") }),
                2 => ("finish", new JsonObject { ["value"] = Observed("sample-[a-f0-9]{32}") }),
                _ => throw new InvalidOperationException("Unexpected business decision.")
            };
            var reply = new JsonObject { ["role"] = "assistant", ["content"] = new JsonObject
                { ["action"] = action, ["payload"] = payload.ToJsonString() }.ToJsonString() };
            return ChatReply(body, reply, TimeProvider.System);
        }
    }
}
