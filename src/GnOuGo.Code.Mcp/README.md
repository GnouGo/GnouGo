# GnOuGo.Code.Mcp

MCP stdio server for safe code operations on a local project.

## Features

- Inspect the active policy with `code_get_policy`.
- Summarize a project with `code_project_summary`.
- Read allowlisted text/code files with `code_read_file`.
- Search text with `code_search_text`.
- Ask GitHub Copilot for implementation guidance with `code_suggest_change` through `GitHub.Copilot.SDK`.
- Optionally write files with `code_write_file` when `Code:AllowWrites=true`.

## Authentication

`code_suggest_change` uses `GitHub.Copilot.SDK` and requires a GitHub token for the Copilot CLI runtime.
Token resolution order is:

1. `Code:Copilot:ApiKey` in `appsettings.json` or another configuration provider.
2. Environment variables listed in `Code:Copilot:TokenEnvironmentVariables`, by default `GITHUB_TOKEN` then `COPILOT_API_KEY`.

For local experiments, `ApiKey` can be set in `appsettings.json`; do not commit real tokens. The default `appsettings.json` intentionally leaves `ApiKey` empty.

Relevant Copilot settings:

- `Code:Copilot:Model`: model passed to the SDK session, default `gpt-4.1`.
- `Code:Copilot:ReasoningEffort`: optional reasoning effort, default `high`.
- `Code:Copilot:UseLoggedInUser`: whether the SDK may use an already logged-in user when no explicit token is provided, default `false`.
- `Code:Copilot:RequestTimeoutSeconds`: wait timeout for a Copilot response, default `120`.

PowerShell example:

```powershell
$env:GITHUB_TOKEN = "ghp_your_token_here"
dotnet run --project "C:\github\GnouGo\src\GnOuGo.Code.Mcp\GnOuGo.Code.Mcp.csproj"
```

The first build may download the Copilot CLI binary through the `GitHub.Copilot.SDK` package targets.

## Build

```powershell
dotnet build "C:\github\GnouGo\src\GnOuGo.Code.Mcp\GnOuGo.Code.Mcp.csproj" -p:SkipModelMetadataGeneration=true
```

## Test

```powershell
dotnet test "C:\github\GnouGo\tests\GnOuGo.Code.Mcp.Tests\GnOuGo.Code.Mcp.Tests.csproj" -p:SkipModelMetadataGeneration=true
```


