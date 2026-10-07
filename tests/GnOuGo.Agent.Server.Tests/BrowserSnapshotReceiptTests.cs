using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
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
