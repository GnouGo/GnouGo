namespace GnOuGo.Agent.Server.Tests;

internal static class AgentServerTestEnvironment
{
    public const string RunMountedAgentMcpTestsVariable = "RUN_MOUNTED_AGENT_MCP_TESTS";
    public const string RunMountedMcpHttpTestsVariable = "RUN_MOUNTED_MCP_HTTP_TESTS";

    public static bool RunMountedAgentMcpTests =>
        string.Equals(Environment.GetEnvironmentVariable(RunMountedAgentMcpTestsVariable), "1", StringComparison.Ordinal);

    public static bool RunMountedMcpHttpTests =>
        string.Equals(Environment.GetEnvironmentVariable(RunMountedMcpHttpTestsVariable), "1", StringComparison.Ordinal);

    public static bool HasDevelopmentSettings(string serverContentRoot) =>
        File.Exists(Path.Combine(serverContentRoot, "appsettings.Development.json"));
}

