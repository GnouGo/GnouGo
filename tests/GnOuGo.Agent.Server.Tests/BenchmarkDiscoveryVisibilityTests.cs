using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Copilot;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Integrations;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkDiscoveryVisibilityTests
{
    [Fact]
    public async Task PublishedReviewCmdListsAndExecutesOnlyItsConfiguredCleanupAlias()
    {
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        const string server = "GnOuGo.Cmd.Mcp";
        var root = Directory.CreateTempSubdirectory("review-cmd-").FullName;
        try
        {
            var configured = new[] { "Git", "Cmd", "GithubCopilot", "Document" }
                .ToDictionary(n => $"GnOuGo.{n}.Mcp", _ => new McpServerOptions());
            configured["Github"] = new() { Type = "http" };
            configured[server].EnvironmentVariables = new() { ["Cmd__DefaultWorkingDirectory"] = root };
            var saved = JsonSerializer.Serialize(configured);
            var profile = LiveWorkflowEvaluation.Configuration(configured, PublishedReviewQualification.Scenario, null, build, source);
            const string timeout = "Cmd__AllowedCommands__delete_directory__TimeoutMs";
            Assert.Equal("240000", profile[server].EnvironmentVariables![timeout]);
            var ct = TestContext.Current.CancellationToken;
            await using var factory = new ConfiguredMcpClientFactory(new() { [server] = profile[server] });
            await using var session = await factory.GetClientAsync(server, ct);
            var cmd = Assert.Single(await session.ListToolsAsync(ct), t => t.Name == "cmd_run");
            Assert.Equal("delete_directory", Assert.Single(cmd.InputSchema!["properties"]!["commandName"]!["enum"]!.AsArray())!.ToString());
            Assert.Single(cmd.InputSchema["oneOf"]!.AsArray());
            Assert.DoesNotContain("create_directory", cmd.Description);
            Assert.Contains("delete_directory", cmd.Description);
            var parameters = new JsonObject { ["path"] = "checkout" };
            var denied = await session.CallToolAsync("cmd_run", new JsonObject { ["commandName"] = "create_directory", ["parameters"] = parameters.DeepClone() }, ct);
            Assert.True(denied.IsError);
            var checkout = Path.Combine(root, "checkout");
            Assert.False(Directory.Exists(checkout));
            Directory.CreateDirectory(checkout);
            var cleanup = await session.CallToolAsync("cmd_run", new JsonObject { ["commandName"] = "delete_directory", ["parameters"] = parameters.DeepClone() }, ct);
            Assert.False(cleanup.IsError);
            Assert.False(Directory.Exists(checkout));
            foreach (var absent in new[] { false, true })
            {
                var events = new List<JsonObject>();
                var releases = 0;
                var profileTransport = new LiveWorkflowEvaluation.ObservedMcp(factory,
                    evt => { events.Add(evt); return Task.CompletedTask; }, discoveryScenario: PublishedReviewQualification.Scenario,
                    localKindCleanup: true, releaseCluster: () =>
                    {
                        releases++;
                        Assert.False(Directory.Exists(checkout));
                        return Task.FromResult(new JsonObject { ["absent"] = absent });
                    });
                await using var observed = await profileTransport.GetClientAsync(server, ct);
                var scopedTool = Assert.Single(await observed.ListToolsAsync(ct), t => t.Name == "cmd_run");
                Assert.Contains(SlimFaasReviewProfile.CleanupContract, scopedTool.Description);
                Directory.CreateDirectory(checkout);
                var result = await observed.CallToolAsync("cmd_run", new JsonObject {
                    ["commandName"] = DelegatedTestReviewAuthority.CleanupCommand, ["parameters"] = parameters.DeepClone() }, ct);
                Assert.Equal(!absent, result.IsError);
                Assert.Equal(1, releases);
                var receipt = Assert.Single(events);
                Assert.Equal(absent, receipt["kind_cleanup"]!["absent"]!.GetValue<bool>());
                if (!absent) Assert.True(receipt["directory_release_result"]!["success"]!.GetValue<bool>());
            }
            Assert.Equal(saved, JsonSerializer.Serialize(configured));
            configured[server].EnvironmentVariables![timeout] = "20000";
            Assert.Equal("20000", LiveWorkflowEvaluation.Configuration(configured, PublishedReviewQualification.Scenario, null, build, source)
                [server].EnvironmentVariables![timeout]);
            configured[server].EnvironmentVariables!["GNOUGO_CMD_ONLY_COMMAND"] = "cat_file";
            Assert.Throws<PlanningConflictException>(() =>
                LiveWorkflowEvaluation.Configuration(configured, PublishedReviewQualification.Scenario, null, build, source));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MetadataConfigurationStartsActualCopilotWithoutAnExecutionProxyPlaceholder()
    {
        var root = Directory.CreateTempSubdirectory("benchmark-discovery-").FullName;
        try
        {
            var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            var build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var configured = new Dictionary<string, McpServerOptions>(StringComparer.Ordinal);
            foreach (var name in new[] { "Git", "Cmd", "GithubCopilot", "Document" })
                configured.Add("GnOuGo." + name + ".Mcp", new()
                {
                    Type = "stdio", Command = "original-command", Args = ["original-argument"],
                    EnvironmentVariables = new()
                    {
                        ["Code__DefaultWorkingDirectory"] = root, ["Code__AllowedWorkingRoots__0"] = root,
                        ["KeyVault__DatabasePath"] = Path.Combine(root, "isolated.db")
                    }
                });
            const string server = "GnOuGo.GithubCopilot.Mcp";
            var original = JsonSerializer.Serialize(configured);
            var discovery = LiveWorkflowEvaluation.Configuration(configured, "code", null, build, sourceRoot);
            Assert.False(discovery[server].EnvironmentVariables!.ContainsKey("Code__Copilot__InferenceProxyEndpoint"));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            await using var factory = new ConfiguredMcpClientFactory(new() { [server] = discovery[server] });
            await using var session = await factory.GetClientAsync(server, deadline.Token);
            var tools = await session.ListToolsAsync(deadline.Token);
            Assert.Contains(tools, tool => tool.Name == "code_agent_edit");
            Assert.Contains(tools, tool => tool.Name == "code_project_summary");
            Assert.Equal(original, JsonSerializer.Serialize(configured));

            const string actualProxy = "http://127.0.0.1:12345/inference";
            configured[server].EnvironmentVariables!["Code__Copilot__InferenceProxyEndpoint"] = actualProxy;
            Assert.Equal(actualProxy, LiveWorkflowEvaluation.Configuration(configured, "code", null, build, sourceRoot)
                [server].EnvironmentVariables!["Code__Copilot__InferenceProxyEndpoint"]);
            const string executionProxy = "http://127.0.0.1:23456/inference";
            Assert.Equal(executionProxy, LiveWorkflowEvaluation.Configuration(configured, "code", executionProxy, build, sourceRoot)
                [server].EnvironmentVariables!["Code__Copilot__InferenceProxyEndpoint"]);
            Assert.Equal(actualProxy, configured[server].EnvironmentVariables!["Code__Copilot__InferenceProxyEndpoint"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task WorkbookProfileClosesItsRealPageEvenWhenDesktopKeepsBrowsersOpen()
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var configured = new[] { "Browser", "Document" }.ToDictionary(n => "GnOuGo." + n + ".Mcp", _ => new McpServerOptions());
        using var root = new TemporaryDirectoryForWorkbook();
        configured["GnOuGo.Browser.Mcp"].EnvironmentVariables = new() { ["Browser__KeepBrowserOpen"] = "true", ["Browser__Headless"] = "true",
            ["Browser__AllowedHosts__0"] = "127.0.0.1", ["Browser__HoldOpenMs"] = "0" };
        configured["GnOuGo.Document.Mcp"].EnvironmentVariables = new() { ["Document__DefaultWorkingDirectory"] = root.Path, ["Document__AllowedWorkingRoots__0"] = root.Path };
        var profile = LiveWorkflowEvaluation.Configuration(configured, "observed_workbook", null, build, sourceRoot);
        await using var factory = new ConfiguredMcpClientFactory(profile);
        await LiveExecutionReadiness.VerifyAsync(factory, root.Path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.xlsx", SearchOption.AllDirectories));
        Assert.Equal("true", configured["GnOuGo.Browser.Mcp"].EnvironmentVariables!["Browser__KeepBrowserOpen"]);
    }

    private sealed class TemporaryDirectoryForWorkbook : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory().FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }

    [Fact]
    public void ExplicitBrowserScopeIsFrozenWithoutMutatingOrReplacingSavedRestrictions()
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var configured = new[] {"Browser", "Document"}.ToDictionary(name => "GnOuGo." + name + ".Mcp", _ => new McpServerOptions());
        var original = JsonSerializer.Serialize(configured);
        var strict = LiveWorkflowEvaluation.Configuration(configured, "amazon", null, build, sourceRoot, AmazonObservedRedirectPolicy.Id);
        var browser = strict["GnOuGo.Browser.Mcp"].EnvironmentVariables!;
        Assert.Equal(AmazonObservedRedirectPolicy.UrlPattern, browser["Browser__AllowedNavigationUrlPatterns__0"]);
        Assert.Equal("www.amazon.fr", browser["Browser__AllowedHosts__0"]);
        Assert.Equal("amazon.fr", browser["Browser__AllowedHosts__1"]);
        var allowedRoutes = Assert.IsType<string>(browser["Browser__AllowedNavigationUrlPatterns__0"]);
        var strictPrompt = LiveWorkflowEvaluation.AmazonEvaluationPrompt("workflows/campaign/fresh", 3, AmazonObservedRedirectPolicy.Id);
        Assert.Contains(allowedRoutes, strictPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(allowedRoutes,
            LiveWorkflowEvaluation.AmazonEvaluationPrompt("workflows/campaign/fresh", 3), StringComparison.Ordinal);
        Assert.Equal(original, JsonSerializer.Serialize(configured));
        var legacy = LiveWorkflowEvaluation.Configuration(configured, "amazon", null, build, sourceRoot);
        Assert.False(legacy["GnOuGo.Browser.Mcp"].EnvironmentVariables!.ContainsKey("Browser__AllowedNavigationUrlPatterns__0"));
        configured["GnOuGo.Browser.Mcp"].EnvironmentVariables = new() { ["Browser__AllowedHosts__0"] = "stricter.example" };
        Assert.Throws<InvalidOperationException>(() => LiveWorkflowEvaluation.Configuration(configured, "amazon", null, build, sourceRoot, AmazonObservedRedirectPolicy.Id));
        Assert.Throws<InvalidOperationException>(() => LiveWorkflowEvaluation.Configuration(configured, "amazon", null, build, sourceRoot, "unknown"));
    }

    [Theory]
    [InlineData("amazon", "GnOuGo.Browser.Mcp", "browser_get_content", "browser_click")]
    [InlineData("amazon", "GnOuGo.Document.Mcp", "document_write", "document_delete")]
    [InlineData("code", "GnOuGo.Git.Mcp", "git_clone", "git_push")]
    [InlineData("code", "GnOuGo.GithubCopilot.Mcp", "code_agent_edit", "copilot_interactive_one_shot")]
    [InlineData("code", "GnOuGo.Cmd.Mcp", "cmd_run", "cmd_list_allowed_commands")]
    [InlineData("code", "GnOuGo.Document.Mcp", "document_write", "document_delete")]
    public async Task IsolatedDiscoveryPreservesAllowedOriginalsAndCannotResolveUnsupportedMethods(
        string scenario, string server, string allowed, string denied)
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new OriginalSession(server, allowed, denied);
        var observed = new LiveWorkflowEvaluation.ObservedMcp(transport, _ => Task.CompletedTask, discoveryScenario: scenario);
        await using var session = await observed.GetClientAsync(server, ct);
        Assert.Same(transport.Tools[0], Assert.Single(await session.ListToolsAsync(ct)));
        Assert.Empty(await session.ListPromptsAsync(ct));
        Assert.Same(transport.ServerMetadata, observed.ServerMetadata);

        var original = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = transport }, (_, _) => Task.CompletedTask);
        var filtered = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = observed }, (_, _) => Task.CompletedTask);
        var source = Assert.Single(await filtered.Capabilities.ListSourcesAsync(ct));
        Assert.Equal(Assert.Single(await original.Capabilities.ListSourcesAsync(ct)), source);
        var before = await original.Capabilities.ListAsync(source.Id, null, ct);
        var after = await filtered.Capabilities.ListAsync(source.Id, null, ct);
        var summary = Assert.Single(after.Capabilities);
        var originalAllowed = before.Capabilities.Single(c => c.Name == allowed && c.EffectKind == "execute");
        Assert.Equal(originalAllowed.Id, summary.Id);
        Assert.Equal(originalAllowed.Version, summary.Version);
        var originalContract = await original.Capabilities.ResolveAsync(originalAllowed, ct);
        var filteredContract = await filtered.Capabilities.ResolveAsync(summary, ct);
        Assert.Equal(JsonSerializer.Serialize(originalContract, PlanningJsonContext.Default.PlanningCapability),
            JsonSerializer.Serialize(filteredContract, PlanningJsonContext.Default.PlanningCapability));
        foreach (var unavailable in before.Capabilities.Where(c => c.Id != summary.Id))
            await Assert.ThrowsAsync<PlanningConflictException>(() => filtered.Capabilities.ResolveAsync(unavailable, ct));
        var catalog = await filtered.DiscoverAsync(new(), ct);
        Assert.Contains("set", catalog.AllowedStepTypes);
        Assert.DoesNotContain("agent.run", catalog.AllowedStepTypes);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task UnfilteredHostsRetainToolsPromptsAndExplicitAdaptiveRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new OriginalSession("GnOuGo.GithubCopilot.Mcp", "code_agent_edit", "copilot_interactive_one_shot");
        var observed = new LiveWorkflowEvaluation.ObservedMcp(transport, _ => Task.CompletedTask);
        await using var session = await observed.GetClientAsync(transport.ServerName, ct);
        Assert.Same(transport.Tools, await session.ListToolsAsync(ct));
        Assert.Same(transport.Prompts, await session.ListPromptsAsync(ct));
        var engine = new WorkflowEngine { McpClientFactory = transport }
            .WithCopilotRunners([new("coding", transport.ServerName)]);
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        Assert.Equal(2, (await runtime.Capabilities.ListSourcesAsync(ct)).Count);
        Assert.Contains("agent.run", (await runtime.DiscoverAsync(new(), ct)).AllowedStepTypes);
        Assert.Equal(0, transport.Calls);
    }

    private sealed class OriginalSession(string server, string allowed, string denied) : IMcpClientFactory, IMcpSession
    {
        public string ServerName => server;
        public IReadOnlyList<McpServerMetadata> ServerMetadata { get; } = [new() { Name = server, Description = "Original provider description" }];
        internal IReadOnlyList<McpToolInfo> Tools { get; } = [Tool(allowed), Tool(denied)];
        internal IReadOnlyList<McpPromptInfo> Prompts { get; } = [new() { Name = allowed, Description = "A prompt is not the same named tool" }];
        internal int Calls;
        private static McpToolInfo Tool(string name) => new() { Name = name, Description = "Original normative contract", EffectKind = "execute",
            InputSchema = JsonNode.Parse("""{"type":"object","required":["value"],"properties":{"value":{"type":"string","minLength":1}},"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"complete":{"type":"boolean"}}}"""),
            Meta = JsonNode.Parse("""{"origin":"provider","constraints":"unchanged"}"""), ExampleResponse = new JsonObject { ["complete"] = true } };
        public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct) => Task.FromResult<IMcpSession>(this);
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => Task.FromResult(Tools);
        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => Task.FromResult(Prompts);
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);
        public Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct) { Calls++; throw new InvalidOperationException("Discovery cannot execute tools."); }
        public Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct) { Calls++; throw new InvalidOperationException("Discovery cannot resolve prompts."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
