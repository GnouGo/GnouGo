# GnOuGo.GithubCopilot.Mcp

MCP stdio server for safe code operations on a local project.

## Features

- Inspect the active policy with `code_get_policy`.
- Summarize a project with `code_project_summary`.
- Read allowlisted text/code files with `code_read_file`.
- Search text with `code_search_text`.
- Ask GitHub Copilot for implementation guidance with `code_suggest_change` through `GitHub.Copilot.SDK`.
- Optionally write files with `code_write_file` when `Code:AllowWrites=true`.

## Authentication

`code_suggest_change` uses `GitHub.Copilot.SDK` and can authenticate with the locally signed-in GitHub user or with an explicit token when supported by the Copilot runtime.
Token resolution order is:

1. `Code:Copilot:ApiKey` in `appsettings.json` or another configuration provider.
2. Environment variables listed in `Code:Copilot:TokenEnvironmentVariables`, by default `GITHUB_TOKEN` then `COPILOT_API_KEY`.

For local desktop usage, prefer `Code:Copilot:UseLoggedInUser=true` and keep `ApiKey` empty. Do not commit real tokens.

Relevant Copilot settings:

- `Code:Copilot:Model`: model passed to the SDK session, default `gpt-4.1`.
- `Code:Copilot:Mode`: Copilot mode, one of `ask`, `edit`, or `agent`; legacy `plan` is accepted as an alias for `ask`.
- `Code:Copilot:ReasoningEffort`: optional reasoning effort, default `high`.
- `Code:Copilot:UseLoggedInUser`: whether the SDK may use an already logged-in user when no explicit token is provided, default `false` in code defaults and `true` in the local appsettings template.
- `Code:Copilot:RequestTimeoutSeconds`: wait timeout for a Copilot response, default `120`.

`code_suggest_change` also accepts an optional `provider` parameter. When omitted, the default GitHub Copilot SDK behavior above is unchanged.
When provided, the MCP reads the matching Agent Server LLM provider secret from the shared KeyVault database and passes it as a custom Copilot SDK provider for that call.
Supported KeyVault secret names follow the Agent Server conventions:

- `LLM--Models--<provider>`
- legacy fallback: `gnougo_llm_<provider>`

The secret value must be a JSON object containing at least `url`; `model` is recommended and falls back to `Code:Copilot:Model` when omitted. Supported provider fields include `type`, `wireApi`, `wireModel`, `authType`, `apiKey`, `bearerToken`, and OIDC fields such as `oidcIssuer`, `oidcClientId`, `oidcScopes`, `oidcClientSecret`, or `oidcPrivateKeyPem`.
If the requested provider does not exist, the tool returns a standard MCP tool error.

PowerShell example:

```powershell
dotnet run --project "C:\github\GnouGo\src\GnOuGo.GithubCopilot.Mcp\GnOuGo.GithubCopilot.Mcp.csproj"
```

The first build may download the Copilot CLI binary through the `GitHub.Copilot.SDK` package targets.

## Build

```powershell
dotnet build "C:\github\GnouGo\src\GnOuGo.GithubCopilot.Mcp\GnOuGo.GithubCopilot.Mcp.csproj" -p:SkipModelMetadataGeneration=true
```

## Test

```powershell
dotnet test "C:\github\GnouGo\tests\GnOuGo.GithubCopilot.Mcp.Tests\GnOuGo.GithubCopilot.Mcp.Tests.csproj" -p:SkipModelMetadataGeneration=true
```


