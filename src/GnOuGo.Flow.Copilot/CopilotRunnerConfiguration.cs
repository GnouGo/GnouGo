using GnOuGo.AI.Core;
using Microsoft.Extensions.Configuration;

namespace GnOuGo.Flow.Copilot;

/// <summary>The same runner and tool scopes apply to discovery, execution and recovery in each host.</summary>
public sealed record CopilotRunnerConfiguration(
    IReadOnlyDictionary<string, string> Runners,
    IReadOnlyDictionary<string, IReadOnlyList<CopilotToolBinding>> Tools)
{
    public IConfiguration? Host { get; init; }
    public static CopilotRunnerConfiguration Read(IConfiguration configuration)
    {
        var current = ReadSections(configuration.GetSection("Flow:AgentRunners"), configuration.GetSection("Flow:AgentTools"));
        var legacy = ReadSections(configuration.GetSection("Flow:CopilotRunners"), configuration.GetSection("Flow:CopilotTools"));
        var runners = new Dictionary<string, string>(current.Runners, StringComparer.Ordinal);
        var tools = new Dictionary<string, IReadOnlyList<CopilotToolBinding>>(current.Tools, StringComparer.Ordinal);
        foreach (var (name, profile) in legacy.Runners)
            if (runners.TryAdd(name, profile) && legacy.Tools.TryGetValue(name, out var scope))
                tools.Add(name, scope);
        return new(runners, tools) { Host = configuration };
    }

    public static Dictionary<string, McpServerOptions> WorkflowMcpServers(Dictionary<string, McpServerOptions> servers) =>
        servers.Where(pair => !string.Equals(pair.Key, "GnOuGo.GithubCopilot.Mcp", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, servers.Comparer);

    private static CopilotRunnerConfiguration ReadSections(IConfigurationSection profiles, IConfigurationSection toolSection)
    {
        var runners = profiles.GetChildren().ToDictionary(runner => runner.Key, RequiredValue, StringComparer.Ordinal);
        var tools = new Dictionary<string, IReadOnlyList<CopilotToolBinding>>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(toolSection.Value))
            throw new InvalidOperationException($"Declare MCP tool scopes per runner under '{toolSection.Path}'.");
        foreach (var scope in toolSection.GetChildren())
        {
            if (!runners.ContainsKey(scope.Key))
                throw new InvalidOperationException($"No agent runner is configured for '{scope.Path}'.");
            var bindings = new List<CopilotToolBinding>();
            foreach (var server in scope.GetChildren())
            {
                var methods = server.GetChildren().Select(RequiredValue).ToArray();
                if (methods.Length == 0)
                    throw new InvalidOperationException($"Declare at least one MCP method at '{server.Path}'.");
                bindings.AddRange(methods.Select(method => new CopilotToolBinding(server.Key, method)));
            }
            if (bindings.Count == 0)
                throw new InvalidOperationException($"Declare at least one MCP tool at '{scope.Path}'.");
            tools.Add(scope.Key, bindings);
        }
        return new(runners, tools);
    }

    private static string RequiredValue(IConfigurationSection section) => !string.IsNullOrWhiteSpace(section.Value)
        ? section.Value : throw new InvalidOperationException($"A name is required at '{section.Path}'.");
}
