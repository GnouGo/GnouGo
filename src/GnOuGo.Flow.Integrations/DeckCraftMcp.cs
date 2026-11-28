using GnOuGo.AI.Core;

namespace GnOuGo.Flow.Integrations;

/// <summary>External stdio configuration for a single owned presentation workspace.</summary>
public static class DeckCraftMcp
{
    public static McpServerOptions ForWorkspace(string cliPath, string workspace)
    {
        if (!Path.IsPathFullyQualified(cliPath) || !File.Exists(cliPath))
            throw new FileNotFoundException("Supply an absolute path to the installed DeckCraft CLI.", cliPath);
        if (!Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace))
            throw new DirectoryNotFoundException("Supply an existing absolute run workspace.");
        return new McpServerOptions { Type = "stdio", Command = Path.GetFullPath(cliPath),
            Args = ["mcp", "--automation-read-root", workspace, "--automation-write-root", workspace],
            DiscoveryTimeoutSeconds = 30, CallTimeoutSeconds = 60 };
    }
}
