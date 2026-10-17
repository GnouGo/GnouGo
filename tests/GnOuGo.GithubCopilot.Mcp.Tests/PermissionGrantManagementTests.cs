using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

/// <summary>Real stdio MCP and encrypted storage; no Copilot/provider session is started.</summary>
public sealed class PermissionGrantManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-grant-management-" + Guid.NewGuid().ToString("N"));
    private readonly List<ElicitRequestParams> _confirmations = [];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmedGrantSurvivesProcessRestartAndIsScopedToSelectedTenantAndAgent(bool confirm)
    {
        var ct = TestContext.Current.CancellationToken;
        string? grantId;
        await using (var client = await StartAsync(confirm))
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            var tool = Assert.Single(tools, t => t.Name == "copilot_permission_grant_create");
            Assert.Equal("management_only", tool.ProtocolTool.Meta?["gnougo"]?["management"]?["visibility"]?.GetValue<string>());
            Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("tenantId", out _));
            var result = await CallAsync(client, "copilot_permission_grant_create", "tenant-a", new()
            {
                ["agentId"] = "stable-agent-a", ["agentName"] = "Selected reviewer", ["allowSandboxBypass"] = true
            });
            Assert.Equal(confirm, result["success"]!.GetValue<bool>());
            grantId = result["grantId"]?.ToString();
            var question = Assert.Single(_confirmations);
            Assert.Contains("Selected reviewer", question.RequestedSchema!.Properties["answer"].Description);
            Assert.Contains("tenant-a", question.RequestedSchema!.Properties["answer"].Description);
            Assert.Contains("sandbox", question.RequestedSchema!.Properties["answer"].Description);
            if (!confirm) Assert.Equal("PERMISSION_GRANT_CANCELLED", result["errorCode"]?.ToString());
        }
        await using (var restarted = await StartAsync(confirm))
        {
            var listed = await CallAsync(restarted, "copilot_permission_grants_list", "tenant-a");
            var grants = listed["grants"]!.AsArray();
            if (confirm)
            {
                var grant = Assert.Single(grants)!;
                Assert.Equal(grantId, grant["id"]!.ToString());
                Assert.Equal("stable-agent-a", grant["agentId"]!.ToString());
                Assert.Equal("tenant-a", grant["tenantId"]!.ToString());
                Assert.True(grant["allowSandboxBypass"]!.GetValue<bool>());
                // A different tenant cannot see or revoke the selected agent's grant.
                Assert.Empty((await CallAsync(restarted, "copilot_permission_grants_list", "tenant-b"))["grants"]!.AsArray());
                Assert.False((await CallAsync(restarted, "copilot_permission_grant_revoke", "tenant-b", new() { ["grantId"] = grantId }))["success"]!.GetValue<bool>());
                var other = await CallAsync(restarted, "copilot_permission_grants_revoke_agent", "tenant-a", new() { ["agentId"] = "other-agent" });
                Assert.Equal(0, other["revokedCount"]!.GetValue<int>());
                Assert.Single((await CallAsync(restarted, "copilot_permission_grants_list", "tenant-a"))["grants"]!.AsArray());
                var removed = await CallAsync(restarted, "copilot_permission_grants_revoke_agent", "tenant-a", new() { ["agentId"] = "stable-agent-a" });
                Assert.Equal(1, removed["revokedCount"]!.GetValue<int>());
            }
            Assert.Empty((await CallAsync(restarted, "copilot_permission_grants_list", "tenant-a"))["grants"]!.AsArray());
        }
        await using var afterRevocation = await StartAsync(confirm);
        Assert.Empty((await CallAsync(afterRevocation, "copilot_permission_grants_list", "tenant-a"))["grants"]!.AsArray());
        Assert.Single(_confirmations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task HostMustEnableBothGatesBeforeBypassGrantCanBeCreated(bool broad, bool bypass)
    {
        await using var client = await StartAsync(true, broad, bypass);
        var result = await CallAsync(client, "copilot_permission_grant_create", "tenant-a", new()
        {
            ["agentId"] = "agent-a", ["agentName"] = "Reviewer", ["allowSandboxBypass"] = true
        });
        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("PERMISSION_GRANT_DISABLED", result["errorCode"]?.ToString());
        Assert.Empty(_confirmations);
        Assert.Empty((await CallAsync(client, "copilot_permission_grants_list", "tenant-a"))["grants"]!.AsArray());
    }

    private async Task<McpClient> StartAsync(bool confirm, bool broad = true, bool bypass = true)
    {
        Directory.CreateDirectory(_root);
        var executable = Environment.GetEnvironmentVariable("GNOUGO_COPILOT_GRANT_SMOKE_EXECUTABLE")
            ?? Path.Combine(AppContext.BaseDirectory, "GnOuGo.GithubCopilot.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = executable, Name = "grant-management-test", WorkingDirectory = _root,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["Code__DefaultWorkingDirectory"] = _root, ["Code__AllowedWorkingRoots__0"] = _root,
                ["KeyVault__DatabasePath"] = Path.Combine(_root, "encrypted-grants.db"),
                ["Code__Copilot__EnableApproveAll"] = broad.ToString(),
                ["Code__Copilot__EnableSandboxBypassGrants"] = bypass.ToString()
            }
        }), new McpClientOptions
        {
            ClientInfo = new() { Name = "grant-management-test", Version = "1.0" },
            Handlers = new()
            {
                ElicitationHandler = (request, _) =>
                {
                    _confirmations.Add(request!);
                    return ValueTask.FromResult(new ElicitResult
                    {
                        Action = "accept",
                        Content = new Dictionary<string, JsonElement>
                        {
                            ["answer"] = JsonSerializer.SerializeToElement(confirm ? "Confirm persistent sandbox-bypass approval" : "Cancel")
                        }
                    });
                }
            }
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<JsonObject> CallAsync(McpClient client, string name, string tenant, JsonObject? args = null)
    {
        var result = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = name,
            Arguments = args?.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
            Meta = new JsonObject { ["gnougo"] = new JsonObject { ["tenantId"] = tenant, ["runId"] = "configuration-run", ["stepId"] = name } }
        }, TestContext.Current.CancellationToken);
        var body = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.AsObject();
        Assert.Equal(!body["success"]!.GetValue<bool>(), result.IsError == true);
        return body;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
