# Review evaluation and publication

`Original results → ReviewEvaluation → Stored draft → Runtime confirmation → Fresh head read → One publication attempt`

`ReviewMcpClientFactory` is a host adapter, outside Flow.Core and the planner. It wraps the current KeyVault-backed MCP configuration and exposes the virtual server `GnOuGo.Review` when the configured GitHub integration exists. `ReviewPublicationService` owns encrypted evidence/drafts and publication. `GnOuGo.GithubCopilot.Core.ReviewEvaluation` remains independently testable and publishable.

Host settings select explicit integration identities; they do not inspect prompt keywords or URLs:

```json
{"ReviewPublication":{"GitHubServer":"Github","CopilotServer":"GnOuGo.GithubCopilot.Mcp"}}
```

Server matching is case-insensitive. These defaults use the normal named integrations. Set them to the exact configured names if renamed. Credentials and transport targets stay in the existing KeyVault-backed MCP configuration.

Tool schemas use numeric JSON values and explicit string-enum types. Output schemas describe the serialized result: every emitted property is required, including nullable execution evidence. Optional constructor defaults remain input behavior. These contracts can be reused as typed workflow ports without weakening enum, number or null constraints.

`review_evaluate` accepts repository owner/name, pull number and an evaluation containing the original Copilot review, an absolute working directory, requested check definitions and results. Declare each requested check separately. Command definitions specify exact expected tool arguments as JSON. Results retain original `execution` observations from the command-producing Copilot invocation. The host records these results at the transport boundary and rejects altered, foreign-tenant or foreign-execution observations. Non-execution review evidence remains a probabilistic reviewer judgment; deterministic validation does not prove that natural-language instructions were exhaustively interpreted. Review the declared checks when approving the workflow and publication.

Missing results become explicit blocked outcomes. Command status is derived from matching arguments, observed completion, working directory and exit codes, independent of the model's status claim. One observation cannot establish several requested checks. Optional non-execution checks may be not applicable only when declared optional and explained. Blocked/incomplete verification yields `COMMENT`; established failed checks or blocking findings yield `REQUEST_CHANGES`; a complete passing review can yield `APPROVE` with zero findings. The formatted review contains every declared check, its outcome and evidence, findings and limitations.

`review_publish` accepts only the issued `draftId`. The host loads the immutable draft, displays its exact body and target, obtains separate runtime confirmation, reads the head afterward, and sends one `pull_request_review_write` create request with the derived event and reviewed commit. Findings appear in the review body; publication does not create separate pending or inline-comment writes. This uses the official tool's [create-with-event contract](https://github.com/github/github-mcp-server/blob/main/pkg/github/pullrequests.go). GitHub review submission is not an atomic compare-and-swap of the PR head; the host performs the immediate fresh read and pins the submitted review to the reviewed commit.

The configured GitHub integration exposes declared read-only tools to workflows; writes and unknown effects are removed from discovery and rejected at dispatch. New or renamed write methods therefore cannot bypass the publisher. Old approved workflows that use raw writes require replanning and fresh approval. The host publication operation cannot merge or deploy. This boundary covers the configured MCP integration; it does not sandbox arbitrary shell tools or unrelated integrations. Use a separately configured integration with its own explicit policy for unrelated GitHub mutation workflows.

Evidence and drafts use tenant-scoped encrypted KeyVault record collections `agent-review-evidence-v1` and `agent-review-drafts-v1`. The host never reads configuration or content through SQL. Existing EF/SQLite planning storage and schema 6 are unchanged. Empty OS lock files under the resolved workspace's `.GnOuGo/data/review-locks` serialize competing publishers, including host processes sharing that workspace. They contain no review content.

Rejection/abandonment prevents publication. Cancellation before dispatch can resume with fresh confirmation. A durable `dispatching` receipt is stored before the write. A completed receipt replays without another write; an exception, cancellation or failed response after reservation remains uncertain and is never automatically resent. Inspect GitHub manually before any new publication after an uncertain outcome. Restart replenishes no planning budgets and does not change the stopped live session.

```bash
dotnet test tests/GnOuGo.GithubCopilot.Core.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipClientBuild=true
dotnet build src/GnOuGo.Agent.Server -m:1 -warnaserror -p:SkipClientBuild=true
```

The published `--planning-persistence-smoke` also checks encrypted review drafts, tenant isolation and uncertain-publication replay without external effects. Runtime tests use fake integrations and the actual Agent.Server confirmation channel and signals.
