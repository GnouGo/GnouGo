using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class TransportTenantTests
{
    private static CodeMcpTraceContext Meta(string tenant) => CodeMcpTraceContext.FromMcpMeta(new JsonObject { ["gnougo"] = new JsonObject { ["tenantId"] = tenant } })!;

    [Fact]
    public async Task OwnershipIsRequestScopedAndNeverFallsBackToBusinessOrAmbientIdentity()
    {
        var trace = new CodeMcpTraceContextAccessor();
        // Context lookup does not need configuration or filesystem access.
        var configuration = new CopilotMcpConfiguration(null!, Options.Create(new CodeServerSettings()), trace);
        using var activity = new System.Diagnostics.Activity("untrusted-ambient").Start();
        activity.SetTag("tenant.id", "ambient");
        Assert.Throws<McpException>(() => configuration.Context());
        await Task.WhenAll(new[] { "first", "second" }.Select(async tenant =>
        {
            using var request = trace.Push(Meta(tenant));
            await Task.Yield();
            Assert.Equal(tenant, configuration.Context().TenantId);
        }));
        Assert.Throws<McpException>(() => configuration.Context());
        using (trace.Push(Meta("outer")))
        {
            using (trace.Push(Meta("inner"))) Assert.Equal("inner", configuration.Context().TenantId);
            Assert.Equal("outer", configuration.Context().TenantId);
        }
        Assert.Throws<McpException>(() => configuration.Context());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other")]
    public async Task BoundedEnvelopeCannotSupplyOrOverrideTransportOwnership(string? tenant)
    {
        var trace = new CodeMcpTraceContextAccessor();
        using var request = trace.Push(tenant is null ? null : Meta(tenant));
        var tools = new BoundedCopilotTools(null!, null!, trace);
        var context = new AgentTaskContext("owner", "run", "invocation", new());
        var json = JsonSerializer.Serialize(context, AgentTaskJsonContext.Default.AgentTaskContext);
        await Assert.ThrowsAsync<McpException>(() => tools.ValidateAsync(json, TestContext.Current.CancellationToken));
    }
}
