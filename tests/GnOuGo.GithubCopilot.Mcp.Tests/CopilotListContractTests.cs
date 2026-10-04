using GnOuGo.Flow.Integrations;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.Mcp.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

[Collection("Copilot tool discovery")]
public sealed class CopilotListContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static TheoryData<string, string> Parameters => new()
    {
        { "copilot_session_create", "permissionAllowlist" }, { "copilot_session_create", "availableTools" },
        { "copilot_session_create", "excludedTools" }, { "copilot_session_create", "skillDirectories" },
        { "copilot_session_create", "disabledSkills" }, { "copilot_one_shot", "permissionAllowlist" },
        { "copilot_interactive_one_shot", "permissionAllowlist" },
        { "code_suggest_change", "contextFiles" }, { "code_agent_edit", "contextFiles" }
    };

    [Theory]
    [MemberData(nameof(Parameters))]
    public async Task DiscoveryAndTransportRejectInvalidListsBeforeSessionCreation(string tool, string parameter)
    {
        await using var fixture = await Fixture.CreateAsync();
        var schema = fixture.Schemas[tool]["properties"]![parameter];
        Assert.NotNull(schema);
        foreach (var json in new[] { "{}", "\"[]\"", "true", "42", "[null]", "[false]", "[1]", "[{}]", "[[]]", "[\"\"]", "[\" \\t\\n\"]", "[\"private-data\",null]" })
        {
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonNode.Parse(json), schema));
            var result = await fixture.Call(tool, new() { [parameter] = JsonSerializer.Deserialize<JsonElement>(json) });
            AssertInvalid(result, parameter + (json.StartsWith('[') ? json.Contains("private-data", StringComparison.Ordinal) ? "[1]" : "[0]" : ""));
        }
        Assert.Equal(0, fixture.Host.SessionsCreated); Assert.Equal(0, fixture.Host.Sends);
    }

    [Theory]
    [MemberData(nameof(Parameters))]
    public async Task LegacyArgumentsAreRejectedEvenWhenNullOrCombinedWithTypedReplacement(string tool, string parameter)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Null(fixture.Schemas[tool]["properties"]![parameter + "Json"]);
        foreach (var legacy in new object?[] { null, "[]", "{\"commands\":[\"npm\"],\"scope\":\"projectRoot only\"}" })
        foreach (var includeReplacement in new[] { false, true })
        {
            var args = new Dictionary<string, object?> { [parameter + "Json"] = legacy };
            if (includeReplacement) args[parameter] = new[] { "readme.md" };
            var result = await fixture.Call(tool, args);
            AssertInvalid(result, parameter + "Json");
            Assert.Contains("regenerate/reapprove", result.StructuredContent!.Value.GetProperty("message").GetString());
        }
        Assert.Equal(0, fixture.Host.SessionsCreated); Assert.Equal(0, fixture.Host.Sends);
    }

    [Theory]
    [MemberData(nameof(Parameters))]
    public async Task TypedListsPreserveNullEmptyOrderAndDeduplication(string tool, string parameter)
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var json in new string?[] { null, "null", "[]", "[\"readme.md\",\"readme.md\",\"README.md\"]" })
        {
            var args = new Dictionary<string, object?> { ["projectRoot"] = "workflows/project", ["prompt"] = "Inspect supplied context", ["task"] = "Inspect supplied context" };
            if (json is not null)
            {
                args[parameter] = JsonSerializer.Deserialize<JsonElement>(json);
                Assert.Empty(PlanningContractValidation.ValidateInstance(JsonNode.Parse(json), fixture.Schemas[tool]["properties"]![parameter]!));
            }
            var result = await fixture.Call(tool, args, "fixture-tenant");
            Assert.False(result.IsError == true, string.Join(";", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
            Assert.Equal("fixture-tenant", fixture.Host.Configuration!.Request.Context.TenantId);
            var configuration = fixture.Host.Configuration.Request.Configuration;
            if (parameter == "contextFiles")
            {
                var files = result.StructuredContent!.Value.GetProperty(tool == "code_agent_edit" ? "contextFiles" : "files");
                Assert.Equal(json is null or "null" or "[]" ? 0 : 1, files.GetArrayLength());
            }
            else
            {
                var actual = parameter switch
                {
                    "permissionAllowlist" => configuration.PermissionAllowlist,
                    "availableTools" => configuration.AvailableTools,
                    "excludedTools" => configuration.ExcludedTools,
                    "skillDirectories" => configuration.SkillDirectories,
                    "disabledSkills" => configuration.DisabledSkills,
                    _ => throw new InvalidOperationException(parameter)
                };
                if (json is null or "null") Assert.Null(actual);
                else Assert.Equal(json == "[]" ? [] : new[] { "readme.md", "README.md" }, actual);
            }
        }
        Assert.Equal(4, fixture.Host.SessionsCreated);
        Assert.Equal(tool == "copilot_session_create" ? 0 : 4, fixture.Host.Sends);
    }

    private static void AssertInvalid(CallToolResult result, string location)
    {
        Assert.True(result.IsError);
        Assert.Equal("INVALID_INPUT", result.StructuredContent!.Value.GetProperty("code").GetString());
        var message = result.StructuredContent.Value.GetProperty("message").GetString()!;
        Assert.StartsWith(location + ":", message);
        Assert.DoesNotContain("private-data", message); Assert.DoesNotContain("System.", message); Assert.DoesNotContain("BytePosition", message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("copilot-list-contract-").FullName;
        internal CopilotTestHost Host = null!;
        internal readonly Dictionary<string, JsonNode> Schemas = [];
        private KeyVaultCopilotTaskStore tasks = null!;
        private ServiceProvider services = null!;
        private McpServer server = null!; private McpClient client = null!; private Task running = null!;
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        internal static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            var project = Path.Combine(f.root, "workflows", "project"); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "readme.md"), "Deterministic context");
            var settings = new CodeServerSettings { DefaultWorkingDirectory = f.root, AllowedWorkingRoots = [f.root], AllowWrites = true, Copilot = new() { Model = "mock" } };
            var policy = new CodePolicy(settings, f.root); var options = Options.Create(settings);
            f.Host = new(settings, f.root, policy);
            f.Host.OnSend = (_, handle, _, _) => Task.FromResult(new CopilotSendResult(handle, "mock-session", "mock result", "mock", []));
            f.tasks = new(KeyVaultRecordStoreFactory.CreateWorkspaceStore(Path.Combine(f.root, "vault.db"), f.root), f.Host.Trace, Path.Combine(f.root, "leases"));
            var logical = new CopilotLogicalOperations(f.Host.Manager, f.tasks, f.Host.Human, options);
            var copilot = new CopilotTools(f.Host.Manager, new(f.Host.Manager), policy, options, f.Host.Trace, f.Host.Human,
                new(f.Host.Trace), null!, new(policy, options, f.Host.Trace), logical);
            var code = new CodeTools(new(policy, options), f.Host.Service, NullLogger<CodeTools>.Instance, f.Host.Human);
            var serverOptions = new McpServerOptions { ServerInfo = new() { Name = "lists-fixture", Version = "1" }, ToolCollection = [] };
            serverOptions.AddGnOuGoToolErrorNormalizer(); CopilotAttachmentContract.Configure(serverOptions); CopilotListContract.Configure(serverOptions);
            // Same boundary registrations as the published MCP host.
            serverOptions.Filters.Request.CallToolFilters.Add(next => async (request, ct) =>
            {
                using var scope = f.Host.Trace.Push(CodeMcpTraceContext.FromMcpMeta(request.Params.Meta));
                return await next(request, ct);
            });
            foreach (var target in new object[] { copilot, code })
            foreach (var method in target.GetType().GetMethods().Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
                serverOptions.ToolCollection.Add(McpServerTool.Create(method, target, new() { SerializerOptions = CodeMcpJson.SerializerOptions }));
            var incoming = new Pipe(); var outgoing = new Pipe();
            var registrations = new ServiceCollection(); registrations.AddLogging();
            registrations.AddMcpServer().WithCopilotTasks(f.tasks, f.Host.Trace);
            f.services = registrations.BuildServiceProvider();
            foreach (var configure in f.services.GetServices<IConfigureOptions<McpServerOptions>>()) configure.Configure(serverOptions);
            f.server = McpServer.Create(new StreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream()), serverOptions, serviceProvider: f.services);
            f.running = f.server.RunAsync(f.stop.Token);
            f.client = await McpClient.CreateAsync(new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()), cancellationToken: Ct);
            foreach (var tool in await f.client.ListToolsAsync(cancellationToken: Ct)) f.Schemas.Add(tool.Name, JsonNode.Parse(tool.JsonSchema.GetRawText())!);
            return f;
        }

        internal Task<CallToolResult> Call(string name, Dictionary<string, object?> args, string? tenant = null)
            => McpTaskPolling.CallAsync(client, name, JsonSerializer.SerializeToNode(args),
                tenant is null ? null : new JsonObject { ["gnougo"] = new JsonObject { ["tenantId"] = tenant } }, Ct);

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync(); await stop.CancelAsync(); await running; await server.DisposeAsync();
            await Host.Manager.DisposeAsync(); await tasks.DisposeAsync(); await services.DisposeAsync(); stop.Dispose(); Directory.Delete(root, true);
        }
    }
}
