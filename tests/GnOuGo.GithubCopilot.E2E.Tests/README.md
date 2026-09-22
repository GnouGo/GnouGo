# GnOuGo GitHub Copilot live E2E tests

This opt-in suite checks that a configured KeyVault-backed `OpenAi` provider can review an exact pull-request diff through the GnOuGo Copilot and Git MCP servers. It does not publish reviews. Agent.Server publication is covered by its host integration tests and requires runtime confirmation.

The test creates a draft PR named `[E2E] GnOuGo automated PR review fixture`, verifies validated findings without changing its reviews, closes the PR, deletes its remote branch, and removes both isolated workspaces. The closed PR remains in GitHub history; the fixture is never merged. This is a low-level reviewer fixture, separate from the single-clone Agent.Server planning and execution flow.

The test is skipped unless explicitly enabled:

```bash
GNOU_GO_LIVE_PR_REVIEW_E2E=1 \
dotnet test tests/GnOuGo.GithubCopilot.E2E.Tests/GnOuGo.GithubCopilot.E2E.Tests.csproj \
  -c Release --logger "console;verbosity=normal"
```

Prerequisites:

- default-tenant KeyVault secrets `LLM--Models--OpenAi` and `LLM--McpServers--Github`;
- a Git token in `LLM--McpServerOverrides--GnOuGo.Git.Mcp--Git--Token`, or the GitHub MCP API key as fallback;
- permission to push a temporary branch and create, review, close, and delete a branch in the current origin repository.

Secrets are decrypted in memory only and are never included in MCP arguments, test output, review bodies, or telemetry. The GitHub endpoint is rejected if it is not the official server or if it selects an insiders/preview route.
