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

## Controlled local editing

This separate smoke requires only the configured encrypted model provider and `python3`.
It creates disposable Python fixtures in the workspace helper's `workflows/e2e` directory.
The MCP client allows only fixture reads, writes to `calculator.py`, and the exact
`python3 -m unittest -v` command, each with an explicit allow-once answer. Other requests
are refused. It checks real file changes and distinct SDK command receipts with exit
codes 1 then 0, output and working directory; it also disconnects/resumes a managed
session and confirms a refused write leaves `refused.py` unchanged. No GitHub mutations
or real-project edits are performed.

```bash
dotnet build src/GnOuGo.GithubCopilot.Mcp/GnOuGo.GithubCopilot.Mcp.csproj
GNOU_GO_LIVE_COPILOT_EDIT=1 \
dotnet test tests/GnOuGo.GithubCopilot.E2E.Tests/GnOuGo.GithubCopilot.E2E.Tests.csproj \
  --filter FullyQualifiedName~LiveControlledEditingTests --logger 'console;verbosity=normal'
```

Set `GNOU_GO_COPILOT_SMOKE_PROVIDER` to override the default `OpenAi` configuration name.
For published-binary acceptance, set `GNOU_GO_COPILOT_SMOKE_BINARY` to the absolute path
of the published MCP executable. Credentials are obtained by the MCP through KeyVault;
only bounded fixture permission descriptions and assertion summaries are logged.

Sanitized acceptance results: [2026-09-24 verification](../../docs/evidence/copilot-core-unification-2026-09-24.json).
