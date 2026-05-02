using System.Text;
using System.Text.Json;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GnOuGo.Code.Mcp;

internal sealed class GitHubCopilotCodeClient : ICodeAssistantClient
{
    private readonly CodeServerSettings _settings;
    private readonly CodePolicy _policy;
    private readonly ILogger<GitHubCopilotCodeClient> _logger;

    public GitHubCopilotCodeClient(
        IOptions<CodeServerSettings> settings,
        CodePolicy policy,
        ILogger<GitHubCopilotCodeClient> logger)
    {
        _settings = settings.Value;
        _policy = policy;
        _logger = logger;
    }

    public async Task<CodeSuggestionResult> SuggestChangeAsync(
        string task,
        string projectRoot,
        IReadOnlyList<CodeFileContent> contextFiles,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task))
            throw new InvalidOperationException("task must not be empty.");
        _policy.EnsurePromptWithinLimit(task, nameof(task));

        var prompt = BuildPrompt(task, projectRoot, contextFiles);

        var token = _policy.ResolveConfiguredToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("A GitHub token is required. Configure Code:Copilot:ApiKey or one of Code:Copilot:TokenEnvironmentVariables.");

        await using var client = CreateClient(projectRoot, token);
        await client.StartAsync(cancellationToken);

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            ClientName = "GnOuGo.Code.Mcp",
            Model = _settings.Copilot.Model,
            ReasoningEffort = NormalizeNullable(_settings.Copilot.ReasoningEffort),
            WorkingDirectory = projectRoot,
            Streaming = false,
            OnPermissionRequest = PermissionHandler.ApproveAll
        }, cancellationToken);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.Copilot.RequestTimeoutSeconds));
        var response = await session.SendAndWaitAsync(new MessageOptions
        {
            Prompt = prompt,
            Mode = "plan"
        }, timeout, cancellationToken);

        var data = response?.Data;
        var suggestion = data?.Content;
        if (string.IsNullOrWhiteSpace(suggestion))
            throw new InvalidOperationException("GitHub Copilot returned an empty response.");

        _logger.LogDebug("GitHub Copilot SDK completed code suggestion for {FileCount} context files using model {Model}.", contextFiles.Count, _settings.Copilot.Model);

        return new CodeSuggestionResult(
            Task: task,
            Files: contextFiles.Select(static file => file.Path).ToArray(),
            Suggestion: suggestion,
            Model: _settings.Copilot.Model,
            UsageJson: data is null ? null : BuildUsageJson(data));
    }

    internal CopilotClient CreateClient(string projectRoot, string token)
        => new(BuildClientOptions(_settings, projectRoot, token, _logger));

    internal static CopilotClientOptions BuildClientOptions(
        CodeServerSettings settings,
        string projectRoot,
        string token,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A GitHub token is required.", nameof(token));

        return new CopilotClientOptions
        {
            Cwd = projectRoot,
            GitHubToken = token,
            UseLoggedInUser = settings.Copilot.UseLoggedInUser,
            LogLevel = string.IsNullOrWhiteSpace(settings.Copilot.LogLevel) ? "warn" : settings.Copilot.LogLevel,
            Logger = logger
        };
    }

    private static string? BuildUsageJson(AssistantMessageData data)
    {
        if (data.OutputTokens is null && string.IsNullOrWhiteSpace(data.RequestId) && string.IsNullOrWhiteSpace(data.InteractionId))
            return null;

        return JsonSerializer.Serialize(new
        {
            data.OutputTokens,
            data.RequestId,
            data.InteractionId
        });
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildPrompt(string task, string projectRoot, IReadOnlyList<CodeFileContent> contextFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a coding assistant operating on a local project.");
        sb.AppendLine("Return a concise implementation plan and unified-diff style patches when changes are needed.");
        sb.AppendLine("Do not invent files that are not mentioned unless clearly necessary; explain assumptions.");
        sb.AppendLine();
        sb.AppendLine("[PROJECT ROOT]");
        sb.AppendLine(projectRoot);
        sb.AppendLine();
        sb.AppendLine("[TASK]");
        sb.AppendLine(task);
        sb.AppendLine();
        sb.AppendLine("[CONTEXT FILES]");
        if (contextFiles.Count == 0)
        {
            sb.AppendLine("No file context was provided.");
        }
        else
        {
            foreach (var file in contextFiles)
            {
                sb.AppendLine($"--- {file.Path} ({file.LengthBytes} bytes) ---");
                sb.AppendLine(file.Content);
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}



