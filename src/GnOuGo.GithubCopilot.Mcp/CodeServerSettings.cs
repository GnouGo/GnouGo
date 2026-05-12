namespace GnOuGo.GithubCopilot.Mcp;

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
}

public sealed class CodeCopilotSettings
{
    public string Provider { get; set; } = "Copilot";
    public string Model { get; set; } = "gpt-5.4";
    public string Mode { get; set; } = "ask";
    public string? ReasoningEffort { get; set; } = "high";
    public string Endpoint { get; set; } = "https://models.github.ai/inference";
    public string? ApiKey { get; set; }
    public bool UseLoggedInUser { get; set; }
    public bool ForwardTraceContext { get; set; } = true;
    public string LogLevel { get; set; } = "warning";
    public int RequestTimeoutSeconds { get; set; } = 120;
    public List<string> TokenEnvironmentVariables { get; set; } = ["GITHUB_TOKEN", "COPILOT_API_KEY"];
    public CodeCopilotTelemetrySettings Telemetry { get; set; } = new();
}

public sealed class CodeCopilotTelemetrySettings
{
    public bool Enabled { get; set; } = true;
    public string ExporterType { get; set; } = "otlp";
    public string? OtlpEndpoint { get; set; } = "http://127.0.0.1:4317";
    public string? FilePath { get; set; }
    public string SourceName { get; set; } = "GnOuGo.GithubCopilot.Mcp.Copilot";
    public bool CaptureContent { get; set; }
}




