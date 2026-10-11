using GnOuGo.AI.Core;
using GnOuGo.Flow.Integrations;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LiveProducerEffectTests
{
    [Theory]
    [InlineData("Git", "git_clone", "write")]
    [InlineData("Git", "git_compare_refs", "read")]
    [InlineData("Cmd", "cmd_run", "execute")]
    [InlineData("GithubCopilot", "copilot_interactive_one_shot", "execute")]
    [InlineData("GithubCopilot", "copilot_review", "read")]
    public async Task RealDiscoveryTransportsEffectsWithoutLosingArtifactMetadata(string component, string operation, string effect)
    {
        var server = "GnOuGo." + component + ".Mcp";
        var executable = Path.Combine(AppContext.BaseDirectory, "tools", server, server + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(executable)) executable = Path.Combine(AppContext.BaseDirectory, server + (OperatingSystem.IsWindows() ? ".exe" : ""));
        await using var transport = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>
        { [server] = new() { Type = "stdio", Command = executable, EnvironmentVariables = new() { ["OpenTelemetry__Enabled"] = "false" } } });
        var session = await transport.GetClientAsync(server, TestContext.Current.CancellationToken);
        var tools = await session.ListToolsAsync(TestContext.Current.CancellationToken);
        var tool = Assert.Single(tools, t => t.Name == operation);
        Assert.Equal(effect, tool.EffectKind);
        Assert.Equal(1, tool.Meta!["gnougo"]!["effect"]!["version"]!.GetValue<int>());
        if (component != "Cmd") Assert.NotNull(tool.ArtifactContract);
        if (operation == "git_clone") Assert.Contains("(?!", tool.InputSchema!["properties"]!["targetDirectory"]!["pattern"]!.ToString());
    }
}
