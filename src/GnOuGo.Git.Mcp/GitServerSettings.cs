namespace GnOuGo.Git.Mcp;

public sealed class GitServerSettings
{
    public const string SectionName = "Git";

    public string DefaultWorkingDirectory { get; set; } = "GnOuGo";
    public List<string> AllowedWorkingRoots { get; set; } = [];
    public bool AllowMutations { get; set; }
    public bool AllowNetworkOperations { get; set; }
    public bool RequireCleanWorkingTreeForMerge { get; set; } = true;
    public int MaxDiffCharacters { get; set; } = 120_000;
    public int MaxLogCount { get; set; } = 100;
    public string DefaultAuthorName { get; set; } = "GnOuGo Agent";
    public string DefaultAuthorEmail { get; set; } = "gnougo-agent@localhost";
    public string DefaultRemoteName { get; set; } = "origin";
    public string Username { get; set; } = "x-access-token";
    public string? Token { get; set; }
    public List<string> TokenEnvironmentVariables { get; set; } = ["GITHUB_TOKEN", "COPILOT_API_KEY"];
}

