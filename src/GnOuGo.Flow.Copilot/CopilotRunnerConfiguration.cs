using Microsoft.Extensions.Configuration;

namespace GnOuGo.Flow.Copilot;

/// <summary>The same runner and tool scopes apply to discovery, execution and recovery in each host.</summary>
public sealed record CopilotRunnerConfiguration(
    IReadOnlyDictionary<string, string> Runners,
    IReadOnlyDictionary<string, IReadOnlyList<CopilotToolBinding>> Tools)
{
    public static CopilotRunnerConfiguration Read(IConfiguration configuration)
    {
        var runners = configuration.GetSection("Flow:CopilotRunners").GetChildren()
            .ToDictionary(runner => runner.Key, runner => RequiredValue(runner), StringComparer.Ordinal);
        var tools = new Dictionary<string, IReadOnlyList<CopilotToolBinding>>(StringComparer.Ordinal);
        var toolSection = configuration.GetSection("Flow:CopilotTools");
        if (!string.IsNullOrEmpty(toolSection.Value))
            throw new InvalidOperationException($"Declare MCP tool scopes per runner under '{toolSection.Path}'.");
        foreach (var scope in toolSection.GetChildren())
        {
            if (!runners.ContainsKey(scope.Key))
                throw new InvalidOperationException($"No Copilot runner is configured for '{scope.Path}'.");
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
