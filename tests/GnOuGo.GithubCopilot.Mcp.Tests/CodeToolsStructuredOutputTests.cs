using System.Reflection;
using System.Text.Json.Nodes;
using GnOuGo.Mcp.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using GnOuGo.GithubCopilot.Core;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class CodeToolsStructuredOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-code-tools-structured-output-tests-" + Guid.NewGuid().ToString("N"));

    public CodeToolsStructuredOutputTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void AllCodeMcpTools_DeclareStructuredOutputSchemas()
    {
        var toolMethods = typeof(CodeTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttribute<McpServerToolAttribute>()
            })
            .Where(item => item.Attribute != null)
            .ToArray();

        Assert.NotEmpty(toolMethods);

        foreach (var item in toolMethods)
        {
            Assert.True(item.Attribute!.UseStructuredContent, item.Method.Name);
            Assert.NotNull(item.Attribute.OutputSchemaType);
            Assert.NotEqual(typeof(object), item.Method.ReturnType);
            Assert.Equal(UnwrapToolReturnType(item.Method.ReturnType), item.Attribute.OutputSchemaType);
        }
    }

    [Fact]
    public void McpToolRegistration_CreatesToolDescriptorsWithOutputSchemas()
    {
        var settings = CreateSettings();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(Options.Create(settings));
        services.AddSingleton(new CodePolicy(settings, _root));
        services.AddSingleton<CodeProjectService>();
        services.AddSingleton<ICodeAssistantClient, NoopAssistantClient>();
        services.AddTransient<CodeTools>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "GnOuGo.GithubCopilot.Mcp.Tests",
                    Version = "1.0.0"
                };
            })
            .WithTools<CodeTools>(CodeMcpJson.SerializerOptions);

        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToArray();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.NotNull(tool.ProtocolTool.OutputSchema));
    }

    [Fact]
    public void ProjectRootTools_AdvertiseGenericWorkspaceConsumers()
    {
        var toolTypes = new[] { typeof(CodeTools), typeof(CopilotTools) };
        var methods = toolTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null)
            .Where(method => method.GetParameters().Any(parameter =>
                string.Equals(parameter.Name, "projectRoot", StringComparison.Ordinal)))
            .ToArray();

        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            var attribute = Assert.Single(method.GetCustomAttributes<McpMetaAttribute>());
            Assert.Equal(McpArtifactContractMetadata.MetaPropertyName, attribute.Name);
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(McpArtifactContractMetadata.WorkspaceDirectoryConsumerProjectRootJson),
                JsonNode.Parse(attribute.JsonValue!)), method.Name);

            var description = method.GetParameters()
                .Single(parameter => string.Equals(parameter.Name, "projectRoot", StringComparison.Ordinal))
                .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
            Assert.Contains("workspace.directory", description, StringComparison.Ordinal);
            Assert.DoesNotContain("git_clone", description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CopilotPermissionTools_AdvertiseAuthoritativeSnakeCaseEnumsAndDefaults()
    {
        var tools = DiscoverCopilotTools();

        var managed = GetInputSchema(tools["copilot_session_create"]);
        AssertPermissionSchema(
            managed,
            ["interactive", "auto_approve_allowlist", "deny", "approve_all"],
            "interactive");

        var oneShotTool = tools["copilot_one_shot"];
        var oneShot = GetInputSchema(oneShotTool);
        AssertPermissionSchema(
            oneShot,
            ["auto_approve_allowlist", "deny", "approve_all"],
            "deny");
        Assert.NotNull(oneShot["properties"]?["permissionAllowlistJson"]);
        Assert.Contains("copilot_interactive_one_shot", oneShotTool.ProtocolTool.Description, StringComparison.Ordinal);

        var interactive = tools["copilot_interactive_one_shot"];
        var interactiveSchema = GetInputSchema(interactive);
        Assert.Null(interactiveSchema["properties"]?["permissionMode"]);
        Assert.NotNull(interactiveSchema["properties"]?["permissionAllowlistJson"]);
        Assert.Contains("interactive", interactive.ProtocolTool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deletes", interactive.ProtocolTool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(McpArtifactContractMetadata.WorkspaceDirectoryConsumerProjectRootJson),
            interactive.ProtocolTool.Meta?[McpArtifactContractMetadata.MetaPropertyName]));

        foreach (var name in new[]
                 {
                     "copilot_permission_grants_list",
                     "copilot_permission_grant_revoke",
                     "copilot_permission_grants_revoke_agent"
                 })
        {
            Assert.Equal(
                "management_only",
                tools[name].ProtocolTool.Meta?["gnougo"]?["management"]?["visibility"]?.GetValue<string>());
        }
    }

    [Fact]
    public void PermissionActivityMessage_IncludesDecisionScopeAndWorkflowCorrelation()
    {
        var context = new CopilotRequestContext(
            "tenant-a",
            RunId: "run-child",
            StepId: "copilot-step",
            Repository: "org/repository",
            ExecutionId: "execution-a",
            AgentId: "agent-a",
            AgentName: "Reviewer");
        var permissionEvent = new CopilotPermissionEvent(
            "permission.auto_approved",
            "info",
            "Auto-approved shell command.",
            "shell",
            CopilotPermissionGrantScope.WorkflowRun,
            context,
            Automatic: true)
        {
            SandboxBypass = true
        };

        var message = McpCopilotPermissionEventSink.BuildActivityMessage(permissionEvent);

        Assert.Contains("operation=shell", message, StringComparison.Ordinal);
        Assert.Contains("decision=automatic_reuse", message, StringComparison.Ordinal);
        Assert.Contains("scope=workflow_run", message, StringComparison.Ordinal);
        Assert.Contains("sandbox_bypass=true", message, StringComparison.Ordinal);
        Assert.Contains("agent=Reviewer", message, StringComparison.Ordinal);
        Assert.Contains("repository=org/repository", message, StringComparison.Ordinal);
        Assert.Contains("execution=execution-a", message, StringComparison.Ordinal);
        Assert.Contains("run=run-child", message, StringComparison.Ordinal);
        Assert.Contains("step=copilot-step", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveStdioDiscovery_PublishesAuthoritativePermissionSchemas()
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "GnOuGo.GithubCopilot.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(executable), $"The MCP test executable was not found at '{executable}'.");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = executable,
            Name = "GnOuGo.GithubCopilot.Mcp.Tests",
            WorkingDirectory = AppContext.BaseDirectory,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["Code__DefaultWorkingDirectory"] = _root,
                ["Code__AllowedWorkingRoots__0"] = _root
            }
        });
        await using var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = "GnOuGo.GithubCopilot.Mcp.Tests",
                    Version = "1.0.0"
                }
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var tools = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        AssertPermissionSchema(
            Assert.IsType<JsonObject>(JsonNode.Parse(tools["copilot_session_create"].JsonSchema.GetRawText())),
            ["interactive", "auto_approve_allowlist", "deny", "approve_all"],
            "interactive");
        AssertPermissionSchema(
            Assert.IsType<JsonObject>(JsonNode.Parse(tools["copilot_one_shot"].JsonSchema.GetRawText())),
            ["auto_approve_allowlist", "deny", "approve_all"],
            "deny");
        var interactive = tools["copilot_interactive_one_shot"];
        var interactiveSchema = Assert.IsType<JsonObject>(JsonNode.Parse(interactive.JsonSchema.GetRawText()));
        Assert.Null(interactiveSchema["properties"]?["permissionMode"]);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(McpArtifactContractMetadata.WorkspaceDirectoryConsumerProjectRootJson),
            interactive.ProtocolTool.Meta?[McpArtifactContractMetadata.MetaPropertyName]));
        Assert.Equal(
            "management_only",
            tools["copilot_permission_grants_list"].ProtocolTool.Meta?["gnougo"]?["management"]?["visibility"]?.GetValue<string>());
    }

    [Fact]
    public void ExistingReviewComments_AcceptsDirectArraysAndThreadEnvelopes()
    {
        var parser = typeof(CopilotTools).GetMethod(
            "ParseExistingReviewComments",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(parser);

        static IReadOnlyList<ExistingReviewComment> Parse(MethodInfo method, string json) =>
            Assert.IsAssignableFrom<IReadOnlyList<ExistingReviewComment>>(method.Invoke(null, [json]));

        var direct = Parse(parser!, """
            [{"path":"src/a.cs","side":"Right","startLine":4,"endLine":5,"body":"Existing issue","fingerprint":"fp-1"}]
            """);
        var enveloped = Parse(parser!, """
            {
              "review_threads": [
                {
                  "path": "src/b.cs",
                  "side": "RIGHT",
                  "line": 17,
                  "comments": [{"body":"Thread comment"}]
                }
              ],
              "totalCount": 1,
              "pageInfo": {"hasNextPage": false}
            }
            """);
        var emptyEnvelope = Parse(parser!, """
            {"review_threads":[],"totalCount":0,"pageInfo":{"hasNextPage":false}}
            """);
        var absent = Parse(parser!, "null");

        Assert.Single(direct);
        Assert.Equal("fp-1", direct[0].Fingerprint);
        var thread = Assert.Single(enveloped);
        Assert.Equal("src/b.cs", thread.Path);
        Assert.Equal(17, thread.EndLine);
        Assert.Equal(ReviewDiffSide.Right, thread.Side);
        Assert.Equal("Thread comment", thread.Body);
        Assert.Empty(emptyEnvelope);
        Assert.Empty(absent);
    }

    private static Type UnwrapToolReturnType(Type returnType)
        => returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;

    private static Dictionary<string, McpServerTool> DiscoverCopilotTools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "GnOuGo.GithubCopilot.Mcp.Tests",
                    Version = "1.0.0"
                };
            })
            .WithTools<CopilotTools>(CodeMcpJson.SerializerOptions);

        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>()
            .ToDictionary(static tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
    }

    private static JsonObject GetInputSchema(McpServerTool tool)
        => Assert.IsType<JsonObject>(JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()));

    private static void AssertPermissionSchema(
        JsonObject schema,
        IReadOnlyList<string> expectedValues,
        string expectedDefault)
    {
        var permission = Assert.IsType<JsonObject>(schema["properties"]?["permissionMode"]);
        var values = Assert.IsType<JsonArray>(permission["enum"])
            .Select(static node => node!.GetValue<string>())
            .ToArray();

        Assert.Equal(expectedValues, values);
        Assert.Equal(expectedDefault, permission["default"]?.GetValue<string>());
        Assert.DoesNotContain("allow", values, StringComparer.Ordinal);
    }

    private CodeServerSettings CreateSettings() => new()
    {
        DefaultWorkingDirectory = _root,
        AllowedWorkingRoots = [_root],
        AllowedExtensions = [".cs", ".md"],
        MaxFileSizeBytes = 1024 * 1024,
        MaxPromptCharacters = 24_000,
        AllowWrites = false
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class NoopAssistantClient : ICodeAssistantClient
    {
        public Task<CodeSuggestionResult> SuggestChangeAsync(
            string task,
            string projectRoot,
            IReadOnlyList<CodeFileContent> contextFiles,
            string? providerName,
            CancellationToken cancellationToken)
            => Task.FromResult(new CodeSuggestionResult(task, [], "", null, null, []));

        public Task<CodeAgentEditResult> AgentEditAsync(
            string task,
            string projectRoot,
            IReadOnlyList<CodeFileContent> contextFiles,
            string? providerName,
            CancellationToken cancellationToken)
            => Task.FromResult(new CodeAgentEditResult(task, [], [], "", null, null, []));
    }
}
