# GnOuGo.Git.Mcp

MCP stdio server for safe Git repository operations on local projects.

## Features

- Inspect the active policy with `git_get_policy`.
- Read repository metadata with `git_repository_info`.
- Inspect working tree status, diffs, branches, and logs with `git_status`, `git_diff`, `git_branches`, and `git_log`.
- Perform guarded local mutations with `git_stage`, `git_unstage`, `git_commit`, `git_create_branch`, `git_delete_branch`, `git_checkout`, `git_merge`, and `git_resolve_conflict` when `Git:AllowMutations=true`.
- Perform guarded network operations with `git_clone`, `git_fetch`, `git_pull`, `git_push`, and `git_delete_remote_branch` when `Git:AllowNetworkOperations=true`.

When `git_stage` is called without explicit paths, it stages all current changes except the repository-root `.GnOuGo/` directory. This keeps temporary Copilot SDK working files out of commits by default. Passing explicit paths still stages only the requested paths.

## Policy and authentication

The tool resolves project roots under `Git:DefaultWorkingDirectory` and `Git:AllowedWorkingRoots`. Relative paths are resolved below the default working directory, which defaults to `GnOuGo` on the current user's Desktop for local desktop usage.

Git credentials are optional and are resolved in this order:

1. `Git:Token` in configuration.
2. Environment variables listed in `Git:TokenEnvironmentVariables`, by default `GITHUB_TOKEN` then `COPILOT_API_KEY`.

Do not commit real tokens.

## Run

```powershell
dotnet run --project "C:\github\GnouGo\src\GnOuGo.Git.Mcp\GnOuGo.Git.Mcp.csproj"
```

## Build

```powershell
dotnet build "C:\github\GnouGo\src\GnOuGo.Git.Mcp\GnOuGo.Git.Mcp.csproj" -p:SkipModelMetadataGeneration=true
```

## Test

```powershell
dotnet test "C:\github\GnouGo\tests\GnOuGo.Git.Mcp.Tests\GnOuGo.Git.Mcp.Tests.csproj" -p:SkipModelMetadataGeneration=true
```

## Native AOT publish

The project is configured for Native AOT and trimming analysis. `IL2026`, `IL3050`, `IL3053`, and `IL3055` are treated as build errors. Publish a self-contained native binary for a runtime identifier with:

```powershell
dotnet publish "C:\github\GnouGo\src\GnOuGo.Git.Mcp\GnOuGo.Git.Mcp.csproj" -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:InvariantGlobalization=false -p:SkipModelMetadataGeneration=true
```

CI also validates a dedicated `win-x64` Native AOT publish for `GnOuGo.Git.Mcp` in `.github/workflows/build-agent-desktop-trimmed.yml`.
