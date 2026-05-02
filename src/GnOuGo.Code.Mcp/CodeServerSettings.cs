namespace GnOuGo.Code.Mcp;

public sealed class CodeServerSettings
{
    public const string SectionName = "Code";

    public string DefaultWorkingDirectory { get; set; } = "GnOuGo";
    public long MaxFileSizeBytes { get; set; } = 512 * 1024;
    public int MaxSearchResults { get; set; } = 100;
    public int MaxPromptCharacters { get; set; } = 24_000;
    public bool AllowWrites { get; set; }
    public List<string> AllowedWorkingRoots { get; set; } = [];
    public List<string> AllowedExtensions { get; set; } =
    [
        ".cs", ".csproj", ".sln", ".json", ".xml", ".yaml", ".yml", ".md", ".txt", ".ps1", ".sh",
        ".js", ".jsx", ".ts", ".tsx", ".py", ".css", ".html"
    ];
    public CodeCopilotSettings Copilot { get; set; } = new();
    public CodeGitSettings Git { get; set; } = new();
}

public sealed class CodeCopilotSettings
{
    public string Provider { get; set; } = "Copilot";
    public string Model { get; set; } = "gpt-4.1";
    public string? ReasoningEffort { get; set; } = "high";
    public string Endpoint { get; set; } = "https://models.github.ai/inference";
    public string? ApiKey { get; set; }
    public bool UseLoggedInUser { get; set; }
    public string LogLevel { get; set; } = "warn";
    public int RequestTimeoutSeconds { get; set; } = 120;
    public List<string> TokenEnvironmentVariables { get; set; } = ["GITHUB_TOKEN", "COPILOT_API_KEY"];
}

public sealed class CodeGitSettings
{
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



