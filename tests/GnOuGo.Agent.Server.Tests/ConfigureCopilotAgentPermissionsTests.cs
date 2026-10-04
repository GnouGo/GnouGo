using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class ConfigureProvidersServiceTests
{
    [Theory]
    [InlineData("Keep current", false, false)]
    [InlineData("Remove persistent approval", false, false)]
    [InlineData("Allow All including sandbox bypass", false, false)]
    [InlineData("Allow All including sandbox bypass", true, false)]
    [InlineData("Allow All including sandbox bypass", false, true)]
    public async Task McpEdit_SelectedAgentPermissionsPreserveIdentityAndRequireConfirmation(string action, bool cancel, bool deleted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var human = new AgentHumanInputProvider();
        var vault = new FakeKeyVaultRuntimeConfigStore();
        var llm = new RecordingLlmClient();
        var grants = new JsonArray(new JsonObject
        {
            ["id"] = "unrelated-grant", ["agentId"] = "other-agent", ["tenantId"] = "tenant-test", ["allowSandboxBypass"] = false
        });
        var listCount = 0;
        var created = 0;
        var revoked = 0;
        string? runId = null;
        var agentSession = new FakeMcpSession("GnOuGo.Agent.Mcp").OnTool("agent_list", (_, _) =>
        {
            listCount++;
            return Task.FromResult(new McpCallResult { Content = new JsonObject
            {
                ["success"] = true,
                ["agents"] = deleted && listCount > 1 ? new JsonArray() : new JsonArray(
                    new JsonObject { ["id"] = "selected-id", ["name"] = listCount == 1 ? "Reviewer" : "Renamed reviewer" },
                    new JsonObject { ["id"] = "other-agent", ["name"] = "Reviewer" })
            } });
        });
        var copilot = new FakeMcpSession("GnOuGo.GithubCopilot.Mcp")
            .OnTool("copilot_permission_grants_list", (_, _) => Task.FromResult(new McpCallResult
                { Content = new JsonObject { ["success"] = true, ["grants"] = grants.DeepClone() } }))
            .OnTool("copilot_permission_grants_revoke_agent", (args, _) =>
            {
                Assert.Equal("selected-id", args!["agentId"]!.ToString());
                Assert.Null(args["tenantId"]);
                revoked++;
                return Task.FromResult(new McpCallResult { Content = new JsonObject { ["success"] = true, ["revokedCount"] = 0 } });
            })
            .OnTool("copilot_permission_grant_create", async (args, token) =>
            {
                created++;
                Assert.Equal("selected-id", args!["agentId"]!.ToString());
                Assert.True(args["allowSandboxBypass"]!.GetValue<bool>());
                Assert.Null(args["tenantId"]);
                Assert.Equal("true", await vault.GetSecretValueAsync(CopilotOverrideKey("EnableApproveAll"), token));
                Assert.Equal("true", await vault.GetSecretValueAsync(CopilotOverrideKey("EnableSandboxBypassGrants"), token));
                var request = new HumanInputRequest { RunId = runId!, StepId = "grant-confirmation", Prompt = "Confirm persistent sandbox-bypass approval", Mode = "choice", Choices = ["Confirm persistent sandbox-bypass approval", "Cancel"] };
                var responseTask = human.RequestInputAsync(request, token);
                Assert.True(ConfiguredMcpClientFactory.PublishHumanInput(new(new McpCorrelationContext
                {
                    TenantId = "tenant-test", CorrelationId = runId, RunId = runId,
                    StepId = "configure/copilot_permission_grant_create", ServerName = "GnOuGo.GithubCopilot.Mcp",
                    MethodName = "copilot_permission_grant_create", Kind = "tool"
                }, request, McpHumanInputSignalPhase.Waiting)));
                var response = await responseTask;
                var accepted = response?["response"]?.ToString() == "Confirm persistent sandbox-bypass approval";
                if (accepted) grants.Add(new JsonObject { ["id"] = "new-grant", ["agentId"] = "selected-id", ["tenantId"] = "tenant-test", ["allowSandboxBypass"] = true });
                return new McpCallResult { Content = new JsonObject { ["success"] = accepted, ["grantId"] = accepted ? "new-grant" : null, ["errorMessage"] = accepted ? null : "Cancelled" } };
            });
        var service = new ConfigureProvidersService(llm, human, new FakeModelCatalog(), vault,
            SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions()), SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance, bundledMcpSettings: Options.Create(CreateBundledCopilotMcpSettings()),
            mcpFactory: new FakeMcpClientFactory(agentSession, copilot),
            openTelemetrySettings: Options.Create(new OpenTelemetrySettings { TenantId = "tenant-test" }));
        var responder = Task.Run(async () =>
        {
            await foreach (var request in human.PendingRequests.ReadAllAsync(ct))
            {
                runId = request.RunId;
                JsonObject answer;
                switch (request.StepId)
                {
                    case "mcp_edit.bundled_fields":
                        Assert.Equal("false", Assert.Single(request.Fields!, f => f.Name == "enable_approve_all").Default);
                        answer = new() { ["agent_permissions"] = "Configure agent permissions" };
                        break;
                    case "mcp_edit.permission_agent":
                        Assert.Contains("Reviewer (selected-id)", request.Choices!);
                        Assert.Contains("Reviewer (other-agent)", request.Choices!);
                        answer = new() { ["response"] = "Reviewer (selected-id)" };
                        break;
                    case "mcp_edit.permission_policy":
                        Assert.Contains("No persistent approval", request.Context!.ToString());
                        answer = new() { ["response"] = action };
                        break;
                    case "grant-confirmation": answer = new() { ["response"] = cancel ? "Cancel" : "Confirm persistent sandbox-bypass approval" }; break;
                    default: throw new InvalidOperationException(request.StepId);
                }
                await human.TrySubmitResponseAsync(request.RunId, request.StepId, answer);
                if (request.StepId == "grant-confirmation" || request.StepId == "mcp_edit.permission_policy" && (deleted || action != "Allow All including sandbox bypass")) break;
            }
        }, ct);
        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/mcp edit GnOuGo.GithubCopilot.Mcp", ct), ct);
        await responder;
        Assert.Equal(0, llm.CallCount);
        Assert.Equal(action == "Remove persistent approval" ? 1 : 0, revoked);
        Assert.Equal(action == "Allow All including sandbox bypass" && !deleted ? 1 : 0, created);
        Assert.Equal(created == 1 && !cancel ? 2 : 1, grants.Count);
        Assert.Equal("other-agent", grants[0]!["agentId"]!.ToString());
        if (created > 0)
        {
            Assert.Contains(events, e => e.Type == "human_input_request" && e.Text!.Contains("grant-confirmation", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Text?.Contains(cancel ? "not saved" : "is saved", StringComparison.Ordinal) == true);
        }
        if (deleted) Assert.Contains(events, e => e.Text?.Contains("no longer available", StringComparison.Ordinal) == true);
        if (created == 0) Assert.Null(await vault.GetSecretValueAsync(CopilotOverrideKey("EnableApproveAll"), ct));
    }
}
