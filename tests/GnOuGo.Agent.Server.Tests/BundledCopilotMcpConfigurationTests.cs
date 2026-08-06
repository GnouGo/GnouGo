using GnOuGo.Agent.Server.Configuration;
using Microsoft.Extensions.Configuration;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BundledCopilotMcpConfigurationTests
{
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Desktop.json")]
    public void Configuration_RegistersOnlyApprovedCopilotRuntimeDefaults(string fileName)
    {
        var path = Path.Combine(GetRepositoryRoot(), "src", "GnOuGo.Agent.Server", fileName);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();
        var settings = configuration.GetSection(BundledMcpSettings.SectionName).Get<BundledMcpSettings>();

        Assert.NotNull(settings);
        var server = Assert.Contains("GnOuGo.GithubCopilot.Mcp", settings.Servers);
        Assert.True(server.Listable);
        Assert.Equal(
            ["managed_session_ttl_seconds", "model", "provider", "reasoning_effort", "request_timeout_seconds", "use_logged_in_user"],
            server.EditableFields.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.All(server.EditableFields.Values, field =>
        {
            Assert.False(field.Sensitive);
            Assert.True(field.AllowInherit);
            Assert.StartsWith("LLM--McpServerOverrides--GnOuGo.GithubCopilot.Mcp--Code--Copilot--", field.SecretKey);
            Assert.StartsWith("env:Code__Copilot__", field.Target);
        });
        Assert.Equal("llm_providers", server.EditableFields["provider"].OptionsSource);
        Assert.Equal(["low", "medium", "high", "xhigh"], server.EditableFields["reasoning_effort"].Options);
        Assert.Equal(1, server.EditableFields["request_timeout_seconds"].MinValue);
        Assert.Equal(1, server.EditableFields["managed_session_ttl_seconds"].MinValue);
        Assert.DoesNotContain(server.EditableFields.Keys, key =>
            key.Contains("approve", StringComparison.OrdinalIgnoreCase)
            || key.Contains("write", StringComparison.OrdinalIgnoreCase)
            || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || key is "command" or "args" or "roots" or "extensions");
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GnOuGo.Agent.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
