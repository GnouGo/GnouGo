using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Persistence;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BrowserSnapshotReceiptTests
{
    [Theory]
    [InlineData(false, "activate")]
    [InlineData(true, "activate")]
    [InlineData(true, "follow")]
    public async Task ObservedActionChecksSurviveTransportAndEncryptedRecovery(bool compatible, string action)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("action-reference-").FullName;
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        await using var site = builder.Build(); var visits = new System.Collections.Concurrent.ConcurrentQueue<string>();
        site.Run(async context =>
        {
            visits.Enqueue(context.Request.Path);
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body><a id='information' href='/information'>Accept information</a><button id='activate' onclick=\"location.href='/activated'\">Accept</button></body></html>", ct);
        });
        await site.StartAsync(ct);
        try
        {
            await using var transport = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>
            {
                ["browser"] = new() { Type = "stdio", Command = Environment.GetEnvironmentVariable("GNOU_GO_BROWSER_MCP_TEST_EXECUTABLE") ??
                    Path.Combine(AppContext.BaseDirectory, "GnOuGo.Browser.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : "")),
                    EnvironmentVariables = new() { ["Browser__AllowedHosts__0"] = "127.0.0.1", ["Browser__KeepBrowserOpen"] = "false", ["Browser__HoldOpenMs"] = "0", ["OpenTelemetry__Enabled"] = "false" } }
            });
            EncryptedWorkflowRunStore Store() => new(new KeyVaultRecordStore(Path.Combine(root, "vault.db")), Path.Combine(root, "index.db"), Path.Combine(root, "owners"));
            var tools = await (await transport.GetClientAsync("browser", ct)).ListToolsAsync(ct);
            var clickContract = tools.Single(t => t.Name == "browser_click").InputSchema!;
            Assert.NotNull(clickContract["properties"]!["reference"]);
            Assert.NotNull(clickContract["properties"]!["requestedAction"]);
            Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["selector"] = "#known" }, clickContract));
            var conflict = await (await transport.GetClientAsync("browser", ct)).CallToolAsync("browser_click",
                new JsonObject { ["selector"] = "#known", ["reference"] = "untrusted", ["requestedAction"] = "activate" }, ct);
            Assert.True(conflict.IsError); Assert.Contains("INVALID_REFERENCE", conflict.Content!.ToJsonString());
            var engine = new WorkflowEngine { McpClientFactory = transport, RunStore = Store(), Limits = new() { TenantId = "action-tenant", RunId = "action-run" } };
            var yaml = """
                version: 1
                workflows:
                  main:
                    inputs:
                      url: { type: string }
                      index: { type: integer }
                      action: { type: string }
                    steps:
                      - id: capture
                        type: mcp.call
                        input:
                          server: browser
                          method: browser_get_content
                          request: { url: '${data.inputs.url}', format: observation_complete }
                      - id: action
                        type: mcp.call
                        input:
                          server: browser
                          method: browser_click
                          request:
                            reference: '${data.steps.capture.response.observationSnapshot.pages[0].records[data.inputs.index].reference}'
                            requestedAction: '${data.inputs.action}'
                    finally:
                      - id: close
                        type: mcp.call
                        input: { server: browser, method: browser_close, request: {} }
                """;
            var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"];
            var result = await engine.ExecuteAsync(workflow, new JsonObject { ["url"] = site.Urls.Single() + "/", ["index"] = compatible && action == "activate" ? 1 : 0, ["action"] = action }, ct);
            Assert.True(compatible == result.Success, $"Expected success={compatible}; actual={result.Success}; {result.Error?.Code}: {result.Error?.Message}");
            if (!compatible) Assert.Equal("ACTION_MISMATCH", result.Error!.Details!["mcp_error_code"]!.ToString());
            Assert.Equal(action == "follow" ? 1 : 0, visits.Count(v => v == "/information"));
            Assert.Equal(compatible && action == "activate" ? 1 : 0, visits.Count(v => v == "/activated"));
            var saved = (await Store().ReadAsync("action-tenant", "action-run", ct))!;
            Assert.True(saved.FinalizationCompleted);
            var receipt = Assert.Single(saved.Invocations.Values, i => i.Id.EndsWith("/step/action", StringComparison.Ordinal));
            Assert.True(receipt.ExternalCompletionObserved); Assert.NotNull(receipt.CompletedAt);
            Assert.Contains("requestedAction", receipt.Observation!.ToJsonString());
            Assert.Contains(action, receipt.Observation.ToJsonString());
            if (!compatible) Assert.Contains("ACTION_MISMATCH", receipt.Observation.ToJsonString());
            var recovered = (await Store().ReadAsync("action-tenant", "action-run", ct))!;
            Assert.True(JsonNode.DeepEquals(receipt.Observation, recovered.Invocations[receipt.Id].Observation));
            var visitsBeforeRecovery = visits.ToArray();
            var restarted = new WorkflowEngine { McpClientFactory = transport, RunStore = Store() };
            var replayed = await restarted.ResumeAsync("action-tenant", "action-run", recovered.Revision, workflow, ct);
            Assert.Equal(result.Success, replayed.Success);
            Assert.Equal(visitsBeforeRecovery, visits.ToArray());
            Assert.Null(await Store().ReadAsync("another-tenant", "action-run", ct));
            Assert.True((await (await transport.GetClientAsync("browser", ct)).CallToolAsync("browser_get_content", new JsonObject(), ct)).IsError);
        }
        finally { await site.StopAsync(ct); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExpirationSurvivesRealTransportAndEncryptedReceiptRecovery()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("snapshot-receipt-").FullName;
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        await using var site = builder.Build();
        var visits = 0;
        site.MapGet("/", async context =>
        {
            Interlocked.Increment(ref visits);
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body><p>Private observed value</p><button id='change'>Change</button></body></html>", ct);
        });
        await site.StartAsync(ct);
        try
        {
            await using var transport = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>
            {
                ["browser"] = new() { Type = "stdio", Command = Environment.GetEnvironmentVariable("GNOU_GO_BROWSER_MCP_TEST_EXECUTABLE") ??
                    Path.Combine(AppContext.BaseDirectory, "GnOuGo.Browser.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : "")),
                    EnvironmentVariables = new() { ["Browser__AllowedHosts__0"] = "127.0.0.1", ["Browser__KeepBrowserOpen"] = "false", ["Browser__HoldOpenMs"] = "0", ["OpenTelemetry__Enabled"] = "false" } }
            });
            EncryptedWorkflowRunStore Store() => new(new KeyVaultRecordStore(Path.Combine(root, "vault.db")), Path.Combine(root, "index.db"), Path.Combine(root, "owners"));
            var engine = new WorkflowEngine { McpClientFactory = transport, RunStore = Store(), Limits = new() { TenantId = "snapshot-tenant", RunId = "snapshot-run" } };
            var yaml = """
                version: 1
                workflows:
                  main:
                    inputs:
                      url: { type: string }
                    steps:
                      - id: capture
                        type: mcp.call
                        input:
                          server: browser
                          method: browser_get_content
                          request: { url: '${data.inputs.url}', format: observation_pages }
                      - id: change
                        type: mcp.call
                        input:
                          server: browser
                          method: browser_click
                          request: { selector: '#change' }
                      - id: stale
                        type: mcp.call
                        input:
                          server: browser
                          method: browser_get_content
                          request: { format: observation, cursor: '${data.steps.capture.response.observationManifest.pages[0].cursor}' }
                    finally:
                      - id: close
                        type: mcp.call
                        input: { server: browser, method: browser_close, request: {} }
                """;
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
            var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject { ["url"] = site.Urls.Single() + "/" }, ct);
            Assert.False(result.Success);
            Assert.True(result.Error!.Code == "MCP_CALL_ERROR", result.Error.Code + ": " + result.Error.Message);
            Assert.Equal("SNAPSHOT_EXPIRED", result.Error.Details!["mcp_error_code"]!.ToString());
            var restarted = Store();
            var run = await restarted.ReadAsync("snapshot-tenant", "snapshot-run", ct);
            Assert.Equal(WorkflowRunStatus.Failed, run!.Status); Assert.True(run.FinalizationCompleted);
            var failed = Assert.Single(run.Invocations.Values, i => i.Id.EndsWith("/step/stale", StringComparison.Ordinal));
            Assert.True(failed.ExternalCompletionObserved); Assert.NotNull(failed.CompletedAt);
            Assert.Contains("SNAPSHOT_EXPIRED", failed.Observation!.ToJsonString());
            Assert.Contains("interaction", failed.Observation.ToJsonString());
            Assert.DoesNotContain("Private observed value", failed.Observation.ToJsonString());
            var revision = run.Revision;
            var again = await restarted.ReadAsync("snapshot-tenant", "snapshot-run", ct);
            Assert.Equal(revision, again!.Revision);
            Assert.True(JsonNode.DeepEquals(failed.Observation, again.Invocations[failed.Id].Observation));
            Assert.Null(await restarted.ReadAsync("other-tenant", "snapshot-run", ct));
            Assert.Equal(1, visits);
            var browser = await transport.GetClientAsync("browser", ct);
            Assert.True((await browser.CallToolAsync("browser_get_content", new JsonObject(), ct)).IsError);
        }
        finally { await site.StopAsync(ct); Directory.Delete(root, true); }
    }
}
