using System.Reflection;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Mcp.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using GnOuGo.GithubCopilot.Core;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

[Collection("Copilot tool discovery")]
public sealed class CodeToolsStructuredOutputTests : IDisposable
{
    [Fact]
    public void CommandResultsRetainOrderedReceiptsAndLeaveConflictsOrMissingCommandsUnverified()
    {
        var terminal = new CopilotTerminalObservation("/repo", 1, new string('x', 50000));
        var completed = new CopilotToolExecutionObservation("install", null, "shell", """{"command":"npm ci"}""",
            true, true, false, [terminal], null) { IsShellCommand = true };
        var conflict = completed with { ToolCallId = "conflict", ConflictingCompletion = true };
        var waiting = completed with { ToolCallId = "waiting", CompletionObserved = false };
        var invalid = completed with { ToolCallId = "missing", ArgumentsJson = "{" };
        var read = completed with { ToolCallId = "read", IsShellCommand = false, ArgumentsJson = """{"path":"file.txt"}""" };

        var rows = CodeCommandResult.FromObservations([completed, read, conflict, waiting, invalid]);

        Assert.Collection(rows,
            row => Assert.Equal(new CodeCommandResult("install", "npm ci", "/repo", 1, null), row),
            row => Assert.Equal(new CodeCommandResult("conflict", "npm ci", null, null, null), row),
            row => Assert.Equal(new CodeCommandResult("waiting", "npm ci", null, null, null), row),
            row => Assert.Equal(new CodeCommandResult("missing", null, null, null, null), row));
        Assert.Equal(50000, terminal.Text!.Length);
    }

    [Fact]
    public void CompactDeferredExitRequiresOneLaterSourceInTheSameSession()
    {
        var terminal = new CopilotTerminalObservation("/repo", 2, "observed") { ShellId = "shell" };
        var read = new CopilotToolExecutionObservation("read", null, "read_terminal", "{}", true, true, false, [terminal], null)
            { CopilotSessionId = "s", StartedSequence = 3, CompletedSequence = 4 };
        var cmd = new CopilotToolExecutionObservation("test", null, "shell", """{"cmd":"run-test"}""", true, true, false,
            [terminal with { SourceToolCallId = "read" }], null)
            { IsShellCommand = true, CopilotSessionId = "s", StartedSequence = 1, CompletedSequence = 2 };
        Assert.Equal(new CodeCommandResult("test", "run-test", "/repo", 2, "read"), Assert.Single(CodeCommandResult.FromObservations([cmd, read])));
        Assert.Null(Assert.Single(CodeCommandResult.FromObservations([cmd, read, read])).ExitCode);
        Assert.Null(Assert.Single(CodeCommandResult.FromObservations([cmd, read with { CopilotSessionId = "other" }])).ExitCode);
    }

    [Fact]
    public void LegacyAgentContract_HidesInjectedContextAndExposesObservations()
    {
        var method = typeof(CodeTools).GetMethod(nameof(CodeTools.AgentEditAsync))!;
        var settings = CreateSettings();
        var host = new CopilotTestHost(settings, _root);
        var target = new CodeTools(new CodeProjectService(new CodePolicy(settings, _root), Options.Create(settings)), host.Service,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CodeTools>.Instance, host.Human);
        var tool = McpServerTool.Create(method, target, new McpServerToolCreateOptions { SerializerOptions = CodeMcpJson.SerializerOptions });
        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
        Assert.Contains("independently verifiable objective", schema["properties"]!["task"]!["description"]!.ToString());
        Assert.Null(schema["properties"]?["requestContext"]);
        Assert.Null(schema["properties"]?["tenantId"]);
        var output = JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())!;
        Assert.Contains("host-provided package caches",
            tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("task").GetProperty("description").GetString(), StringComparison.Ordinal);
        var fields = output["properties"]!;
        Assert.Contains("Original input", fields["task"]?["description"]?.ToString() ?? "");
        Assert.Contains("returned text", fields["summary"]?["description"]?.ToString() ?? "");
        Assert.Contains("terminal exit codes", fields["toolExecutions"]?["description"]?.ToString() ?? "");
        Assert.Contains("Progress stream", fields["progressEvents"]?["description"]?.ToString() ?? "");
        Assert.NotNull(fields["commandResults"]);
        Assert.Contains("exitCode", fields["commandResults"]!.ToJsonString());
    }

    [Theory]
    [InlineData("copilot_one_shot")]
    [InlineData("copilot_interactive_one_shot")]
    [InlineData("copilot_session_send")]
    public void DiscoveredCompletionContractDoesNotClaimSuccessfulRequestedWork(string tool)
    {
        var schema = JsonNode.Parse(DiscoverCopilotTools()[tool].ProtocolTool.OutputSchema!.Value.GetRawText())!;
        var completed = schema["properties"]!["completed"]!;
        Assert.Equal("boolean", completed["type"]!.ToString());
        Assert.Contains("does not certify", completed["description"]!.ToString());
        Assert.Contains("execution observations", schema["properties"]!["content"]!["description"]!.ToString());
        Assert.Null(schema["properties"]!["success"]);
        var observations = schema["properties"]!["toolExecutions"]!;
        Assert.Equal("array", observations["type"]!.ToString());
        Assert.Contains("captured directly", observations["description"]!.ToString());
        Assert.Contains("exitCode", observations.ToJsonString());
        Assert.Contains("not inferred from text", observations.ToJsonString());
    }

    [Theory]
    [InlineData("copilot_one_shot")]
    [InlineData("copilot_interactive_one_shot")]
    [InlineData("copilot_session_send")]
    public void AttachmentsExposeTypedBusinessObjectsInsteadOfEncodedJson(string tool)
    {
        var schema = GetInputSchema(DiscoverCopilotTools()[tool]);
        Assert.Null(schema["properties"]?["attachmentsJson"]);
        var attachments = Assert.IsType<JsonObject>(schema["properties"]?["attachments"]);
        Assert.Contains("array", attachments["type"]!.ToString());
        Assert.NotNull(attachments["items"]);
        Assert.Contains("file", attachments["items"]!.ToJsonString());
        Assert.Contains("blob", attachments["items"]!.ToJsonString());
        Assert.Contains("additionalProperties\":false", attachments["items"]!.ToJsonString());
    }

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
        var copilot = new CopilotTestHost(settings, _root);
        services.AddSingleton(copilot.Service);
        services.AddSingleton(copilot.Human);
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
        Assert.All(tools, tool => AssertValidRequiredDeclarations(
            JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText()),
            tool.ProtocolTool.Name));
    }

    private static void AssertValidRequiredDeclarations(JsonNode? schema, string path)
    {
        if (schema is not JsonObject obj)
            return;

        var properties = obj["properties"] as JsonObject;
        if (obj["required"] is JsonArray required)
        {
            Assert.NotNull(properties);
            var names = required.Select((node, index) =>
            {
                var value = Assert.IsAssignableFrom<JsonValue>(node);
                Assert.True(value.TryGetValue<string>(out var name), $"{path}.required[{index}] must be a string.");
                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.True(properties!.ContainsKey(name!), $"{path}.required[{index}] references undeclared property '{name}'.");
                return name!;
            }).ToArray();
            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        }

        if (properties != null)
            foreach (var (name, property) in properties)
                AssertValidRequiredDeclarations(property, $"{path}.properties.{name}");
        foreach (var keyword in new[] { "$defs", "definitions" })
            if (obj[keyword] is JsonObject definitions)
                foreach (var (name, definition) in definitions)
                    AssertValidRequiredDeclarations(definition, $"{path}.{keyword}.{name}");
        foreach (var keyword in new[] { "items", "additionalProperties" })
            AssertValidRequiredDeclarations(obj[keyword], $"{path}.{keyword}");
        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
            if (obj[keyword] is JsonArray variants)
                for (var index = 0; index < variants.Count; index++)
                    AssertValidRequiredDeclarations(variants[index], $"{path}.{keyword}[{index}]");
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
            var advertised = Assert.IsType<JsonObject>(JsonNode.Parse(attribute.JsonValue!));
            var artifacts = Assert.IsType<JsonObject>(
                advertised[McpArtifactContractMetadata.ArtifactsPropertyName]);
            Assert.Contains(
                Assert.IsType<JsonArray>(artifacts["consumes"]),
                item => item is JsonObject consume
                        && consume["kind"]?.GetValue<string>() == McpArtifactContractMetadata.WorkspaceDirectoryKind
                        && consume["pointer"]?.GetValue<string>() == "/projectRoot"
                        && consume["required"]?.GetValue<bool>() == true);

            var description = method.GetParameters()
                .Single(parameter => string.Equals(parameter.Name, "projectRoot", StringComparison.Ordinal))
                .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
            Assert.Contains("workspace.directory", description, StringComparison.Ordinal);
            Assert.DoesNotContain("git_clone", description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CopilotSessionAndOneShotTools_AdvertiseComposableLifecycleContracts()
    {
        var tools = DiscoverCopilotTools();
        var create = tools["copilot_session_create"];
        var createArtifacts = McpArtifactContractParser.ParseAndValidate(
            create.ProtocolTool.Meta,
            GetInputSchema(create),
            JsonNode.Parse(create.ProtocolTool.OutputSchema!.Value.GetRawText()));
        Assert.True(createArtifacts.IsValid, string.Join(Environment.NewLine, createArtifacts.Errors));
        Assert.Contains(createArtifacts.Contract!.Produces, static artifact =>
            artifact.Kind == McpArtifactContractMetadata.SessionHandleKind
            && artifact.Pointer == "/handle"
            && artifact.Mode == McpArtifactContractMetadata.MaterializeMode);
        Assert.Contains(createArtifacts.Contract.Consumes, static artifact =>
            artifact.Kind == McpArtifactContractMetadata.WorkspaceDirectoryKind
            && artifact.Pointer == "/projectRoot"
            && artifact.Required);

        foreach (var method in new[]
                 {
                     "copilot_session_send",
                     "copilot_session_disconnect",
                     "copilot_session_delete"
                 })
        {
            var tool = tools[method];
            var artifacts = McpArtifactContractParser.ParseAndValidate(
                tool.ProtocolTool.Meta,
                GetInputSchema(tool),
                JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText()));
            Assert.True(artifacts.IsValid, string.Join(Environment.NewLine, artifacts.Errors));
            var consumed = Assert.Single(artifacts.Contract!.Consumes);
            Assert.Equal(McpArtifactContractMetadata.SessionHandleKind, consumed.Kind);
            Assert.Equal("/handle", consumed.Pointer);
            Assert.True(consumed.Required);
        }

        foreach (var method in new[] { "copilot_one_shot", "copilot_interactive_one_shot" })
        {
            var tool = tools[method];
            var composition = McpCapabilityCompositionParser.ParseAndValidate(tool.ProtocolTool.Meta);
            Assert.True(composition.IsValid, string.Join(Environment.NewLine, composition.Errors));
            Assert.Equal(McpCapabilityCompositionMetadata.CompleteOperationKind, composition.Contract!.Kind);
            Assert.Equal(
                ["copilot_session_create", "copilot_session_send", "copilot_session_disconnect", "copilot_session_delete"],
                composition.Contract.Encapsulates.Select(static capability => capability.Method).ToArray());
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
        Assert.NotNull(oneShot["properties"]?["permissionAllowlist"]);
        Assert.Contains("copilot_interactive_one_shot", oneShotTool.ProtocolTool.Description, StringComparison.Ordinal);

        var interactive = tools["copilot_interactive_one_shot"];
        var interactiveSchema = GetInputSchema(interactive);
        Assert.Null(interactiveSchema["properties"]?["permissionMode"]);
        Assert.NotNull(interactiveSchema["properties"]?["permissionAllowlist"]);
        Assert.Contains("interactive", interactive.ProtocolTool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("saved before", interactive.ProtocolTool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconciliation", interactive.ProtocolTool.Description, StringComparison.OrdinalIgnoreCase);
        var interactiveArtifacts = McpArtifactContractParser.ParseAndValidate(
            interactive.ProtocolTool.Meta,
            interactiveSchema,
            JsonNode.Parse(interactive.ProtocolTool.OutputSchema!.Value.GetRawText()));
        Assert.True(interactiveArtifacts.IsValid, string.Join(Environment.NewLine, interactiveArtifacts.Errors));
        Assert.Contains(interactiveArtifacts.Contract!.Consumes, static artifact =>
            artifact.Kind == McpArtifactContractMetadata.WorkspaceDirectoryKind
            && artifact.Pointer == "/projectRoot"
            && artifact.Required);

        foreach (var name in new[]
                 {
                     "copilot_permission_grant_create",
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
                ["Code__AllowedWorkingRoots__0"] = _root,
                ["KeyVault__DatabasePath"] = Path.Combine(_root, "isolated-keyvault.db")
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
        foreach (var name in new[] { "copilot_session_send", "copilot_one_shot", "copilot_interactive_one_shot" })
        {
            var schema = JsonNode.Parse(tools[name].JsonSchema.GetRawText())!;
            Assert.Null(schema["properties"]?["attachmentsJson"]);
            var attachments = schema["properties"]!["attachments"]!;
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonArray((JsonNode?)null), attachments));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""[{"type":"file","path":null}]"""), attachments));
            var rejected = await client.CallToolAsync(name, new Dictionary<string, object?> { ["attachments"] = new { pullRequestUrl = "fixture" } }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(rejected.IsError);
            Assert.Equal("INVALID_INPUT", rejected.StructuredContent!.Value.GetProperty("code").GetString());
        }
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
        var interactiveArtifacts = McpArtifactContractParser.ParseAndValidate(
            interactive.ProtocolTool.Meta,
            interactiveSchema,
            JsonNode.Parse(interactive.ProtocolTool.OutputSchema!.Value.GetRawText()));
        Assert.True(interactiveArtifacts.IsValid, string.Join(Environment.NewLine, interactiveArtifacts.Errors));
        Assert.Contains(interactiveArtifacts.Contract!.Consumes, static artifact =>
            artifact.Kind == McpArtifactContractMetadata.WorkspaceDirectoryKind
            && artifact.Pointer == "/projectRoot"
            && artifact.Required);
        var oneShotComposition = Assert.IsType<JsonObject>(interactive.ProtocolTool.Meta?["gnougo"]?["composition"]);
        foreach (var name in new[] { "code_suggest_change", "code_agent_edit" })
        {
            var tool = tools[name].ProtocolTool;
            Assert.True(JsonNode.DeepEquals(oneShotComposition, tool.Meta?["gnougo"]?["composition"]), name);
            var artifacts = McpArtifactContractParser.ParseAndValidate(tool.Meta, JsonNode.Parse(tool.InputSchema.GetRawText()),
                JsonNode.Parse(tool.OutputSchema!.Value.GetRawText()));
            Assert.True(artifacts.IsValid);
            var consumed = Assert.Single(artifacts.Contract!.Consumes);
            Assert.Equal(McpArtifactContractMetadata.WorkspaceDirectoryKind, consumed.Kind);
            Assert.Equal("/projectRoot", consumed.Pointer);
            Assert.True(consumed.Required);
        }
        Assert.Equal(
            "management_only",
            tools["copilot_permission_grants_list"].ProtocolTool.Meta?["gnougo"]?["management"]?["visibility"]?.GetValue<string>());
    }

    private static Type UnwrapToolReturnType(Type returnType)
        => returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;

    [Fact]
    public void CopilotBusinessToolsNeverAdvertiseTenantIdentityAsAnArgument()
    {
        foreach (var tool in DiscoverCopilotTools().Values)
            Assert.Null(GetInputSchema(tool)["properties"]?["tenantId"]);
    }

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
        var tools = provider.GetServices<McpServerTool>()
            .ToDictionary(static tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        foreach (var tool in tools.Values)
        {
            CopilotListContract.Publish(tool.ProtocolTool);
            if (CopilotAttachmentContract.IsAttachmentTool(tool.ProtocolTool.Name))
                tool.ProtocolTool.InputSchema = CopilotAttachmentContract.InputSchema(tool.ProtocolTool.InputSchema);
        }
        return tools;
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

}
