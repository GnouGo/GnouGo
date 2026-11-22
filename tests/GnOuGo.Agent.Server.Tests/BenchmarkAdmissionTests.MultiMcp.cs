using System.Net;
using System.Text.Json.Nodes;
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
    public async Task ServerRunnerUsesGitObservationToReadDocumentThroughBundledCopilot(bool rateLimited)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GNOUGO_TEST_LOCAL_COPILOT") == "1", "Opt in to the bundled local Copilot runtime.");
        using var fixture = new Fixture();
        var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "workspace")).FullName;
        var project = Path.Combine(workspace, "project");
        LibGit2Sharp.Repository.Init(project);
        var file = $"note-{Guid.NewGuid():N}.txt";
        var marker = $"sample-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(Path.Combine(project, file), marker, Ct);

        var options = RuntimeOptions();
        const string upstream = "http://127.0.0.1:1/v1";
        options.Models["fixture"].Url = upstream;
        using var provider = new GitDocumentProvider(rateLimited);
        using var http = new HttpClient(provider);
        using var model = RuntimeModel(fixture, options, http);
        await using var relay = await CampaignInferenceProxy.StartAsync(model, "multi");
        relay.ExecutionEnabled = true;
        var permission = new ObservationPermissions();
        const string git = "GnOuGo.Git.Mcp", documents = "GnOuGo.Document.Mcp", copilot = "GnOuGo.GithubCopilot.Mcp";
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
        var backend = options.McpServers[copilot] = Server(copilot, "Code");
        var env = backend.EnvironmentVariables!;
        env["Code__AllowWrites"] = "false";
        env["Code__Copilot__Provider"] = "fixture";
        env["Code__Copilot__Model"] = "pinned";
        env["Code__Copilot__UseLoggedInUser"] = "false";
        env["Code__Copilot__Providers__fixture__Provider"] = "openai";
        env["Code__Copilot__Providers__fixture__Url"] = upstream;
        env["Code__Copilot__Providers__fixture__Model"] = "pinned";
        env["Code__Copilot__Providers__fixture__WireApi"] = "completions";
        env["Code__Copilot__Providers__fixture__AuthType"] = "none";
        env["Code__Copilot__Providers__fixture__ApiKey"] = "offline-fixture";
        env["Code__Copilot__Endpoint"] = upstream;
        env["Code__Copilot__InferenceProxyEndpoint"] = relay.Endpoint;
        env["Code__Copilot__Telemetry__Enabled"] = "false";
        env["COPILOT_HOME"] = Path.Combine(fixture.Root, "copilot");
        env["XDG_CONFIG_HOME"] = Path.Combine(fixture.Root, "config");
        var factory = new SecureWorkflowRuntimeFactory(SmartFlowTestFactory.CreateRuntimeOptionsStore(options),
            new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options), humanInputProvider: permission,
            copilotRunners: new Dictionary<string, string> { ["observe"] = copilot },
            copilotTools: new Dictionary<string, IReadOnlyList<CopilotToolBinding>>
                { ["observe"] = [new(git, "git_status"), new(documents, "document_read")] });
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = limit.Token;
        await using var runtime = await factory.CreateAsync(ct);
        var engine = runtime.ConfigureAgentRunners(new() { McpClientFactory = runtime.McpClientFactory });
        var runner = engine.AgentTaskRunners["observe"];
        var contract = await runner.DescribeAsync(ct);
        var capabilities = contract.InputSchema["properties"]!["capabilities"]!["items"]!["enum"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(2, capabilities.Count);
        permission.Approved.UnionWith(capabilities);
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
        var result = await runner.RunAsync(context, ct);
        Assert.True(result.Status == "completed", result.Message ?? result.Failure?.Message ?? result.Status);
        Assert.Equal(marker, result.Output!["value"]!.GetValue<string>());
        Assert.Equal(2, result.Usage.Actions);
        Assert.All(await new EvidenceAgentTaskVerifier().VerifyAsync(context, result, ct), finding => Assert.True(finding.Passed));
        Assert.Equal(capabilities, result.Evidence.Select(e => e.Subject));
        Assert.Equal("project/" + file, result.Evidence[1].Facts["request"]!["filePath"]!.GetValue<string>());
        Assert.Contains(marker, result.Evidence[1].Facts["result"]!.ToJsonString());
        foreach (var recovered in new[] { await runner.ReconcileAsync(context, ct), await runner.RunAsync(context, ct) })
            Assert.Equal(result.Evidence.Select(e => e.Id), recovered.Evidence.Select(e => e.Id));
        Assert.Equal(capabilities, permission.Grants);
        var expectedCalls = rateLimited ? 4 : 3;
        Assert.Equal(expectedCalls, result.Usage.ModelCalls);
        Assert.Equal(expectedCalls, provider.Requests.Count);
        if (rateLimited) Assert.Equal(provider.Requests[1], provider.Requests[2]);
        Assert.Equal((long)expectedCalls, (await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: ct))["calls"]!.GetValue<long>());
    }

    private sealed class ObservationPermissions : IHumanInputProvider
    {
        internal HashSet<string> Approved { get; } = [];
        internal List<string> Grants { get; } = [];
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            var capability = request.Context?["requested_capability"]?.ToString();
            var allow = request.RunId == "multi" && request.ParentInvocationId == "main/observe" &&
                request.Prompt == "Allow this Copilot operation?" && capability is not null && Approved.Remove(capability);
            if (allow) Grants.Add(capability!);
            return Task.FromResult<JsonNode?>(new JsonObject { ["answer"] = allow ? "Allow once" : "Refuse" });
        }
    }

    private sealed class GitDocumentProvider(bool rateLimited) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
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
            var turns = body["messages"]!.AsArray();
            var tools = body["tools"]!.AsArray().Select(t => t!["function"]!).ToList();
            string Observed(string pattern)
            {
                var observation = turns.Last(m => m!["role"]?.ToString() == "tool")!["content"]!.ToString();
                var match = Regex.Match(observation, pattern, RegexOptions.CultureInvariant);
                Assert.True(match.Success, "The scripted provider did not receive the preceding tool's result.");
                return match.Value;
            }
            JsonObject Select(string parameter, string value)
            {
                var tool = tools.Single(t => t["parameters"]!["properties"]!.AsObject().ContainsKey(parameter));
                return new() { ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0, ["id"] = "observe-" + Requests.Count, ["type"] = "function", ["function"] = new JsonObject
                        { ["name"] = tool["name"]!.DeepClone(), ["arguments"] = new JsonObject { [parameter] = value }.ToJsonString() }
                }) };
            }
            var reply = turns.Count(m => m!["role"]?.ToString() == "tool") switch
            {
                0 => Select("projectRoot", "project"),
                1 => Select("filePath", "project/" + Observed(@"note-[a-f0-9]{32}\.txt")),
                2 => new JsonObject { ["role"] = "assistant", ["content"] = new JsonObject { ["value"] = Observed("sample-[a-f0-9]{32}") }.ToJsonString() },
                _ => throw new InvalidOperationException("Unexpected provider turn.")
            };
            return ChatReply(body, reply, TimeProvider.System);
        }
    }
}
