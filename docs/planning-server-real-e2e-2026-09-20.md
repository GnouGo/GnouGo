# Real Server PR-review validation — 2026-09-20

## Outcome

**The real end-to-end objective remains blocked at planning.** The Server chat
submitted the first read-only review of SmartGuide PR #597 to the configured live
model. Its invalid proposal required correction, but the complete correction
request exceeded the unchanged input limit: **48,483 estimated tokens versus
12,000 allowed**. No correction was dispatched, artifact approved, repository
cloned, check executed, Copilot review performed, or GitHub review published.

Completed read-only reviews: **0/3**. Publications: **0/1**. Neither discovery,
offline tests nor planning scenarios count as real review execution.

Source started at `bc9a618`. One generic MCP integration defect was reproduced,
fixed, tested and pushed as **`fd316fc`** before the live submission. Planner
architecture, bootstrap, public APIs, storage, limits and safety checks remain
unchanged. This report does not claim another benchmark evaluation.

## Actual host and integration preflight

The actual published Release `GnOuGo.Agent.Server` ran at `127.0.0.1:58543` with
planning background processing disabled, dedicated planning-index and telemetry
paths, and collector ports 15317/15318. The existing E2E agent store was reused
through normal configuration; credentials and encrypted planning records remained
in their existing KeyVault storage. The unrelated host on port 5088 was preserved.

Chromium drove the interactive Blazor chat and selected the existing
`desktop-planning-e2e` agent through its normal menu. The name is historical; this
attempt used Server, not Desktop. Public agent management confirmed the unchanged
generic `workflow.plan → workflow.execute` bootstrap, SHA-256
`966478c847defc9afed16b92f4023ff098174017187e553d232d04068d134ed7`.

No generation or approval API shortcut, automatic-approval provider, replacement
workflow or injected execution result was used. The failed-run designer-revision
offer was skipped through its normal UI control, avoiding an unintended session.

Real MCP preflight calls included:

- GitHub `get_me`, `search_pull_requests`, `pull_request_read` and
  `get_file_contents`. The integration authenticated as `guillaume-chervet`.
- Git `git_get_policy`: configured token and allowed network operations; workspace
  permissions inspected. Actual clone/fetch authentication remains untested.
- Cmd `cmd_get_policy`: configured allowlist and workspace restrictions inspected.
  No repository command was submitted.
- Copilot `code_get_policy`, `copilot_auth_status` and `copilot_connectivity`.
  Connectivity returned `pong`; the configured BYO provider/model was OpenAi /
  `gpt-5.5-2026-04-24`. GitHub Copilot login status was unauthenticated; connectivity
  is not proof of paid inference authorization. No Copilot inference was attempted.

The initial build-output launch lacked interactive static assets. Using the
repository's self-contained trimmed publish configuration corrected the launch,
without source changes or warning suppressions. Exploratory incompatible publish
options failed before that successful publish; they are not passing builds.

## Selected PRs and repository safety inspection

These were open, non-draft `gfortaine` PRs when inspected through GitHub MCP:

| PR | Head SHA | This pass |
| --- | --- | --- |
| [#597](https://github.com/AxaFrance/SmartGuide/pull/597) | `0b7899b1944aaab5b742e12c6238825155fa2bac` | Live planning attempted; execution blocked |
| [#595](https://github.com/AxaFrance/SmartGuide/pull/595) | `de5393fdeda3b42bf6d360afd00ac8f79c1ddbe6` | Discovery only |
| [#553](https://github.com/AxaFrance/SmartGuide/pull/553) | `79259fcc773e844daa44ca5e6db680cc15138f65` | Discovery only |

Their reported base SHA was `667b097d205f7a376e7c8fc09f1f88e08a4dc5c6`.
The inspected workflow files defined push/pull-request quality checks, a manually
dispatched release workflow, and reusable build/deployment workflows. No
review-triggered deployment event was found in those files. This inspection is
not publication clearance: PR head/state, auto-merge, automation and publication
permissions must be checked again before any future publication.

The unchanged #597 request required one clone/work directory, complete diff and
surrounding-file inspection, repository-derived dependency installation/checks,
real Copilot review, `review_evaluate`, original command observations, tracked
source-integrity verification and cleanup. In particular it required:

```text
uv run --with pytest-forked pytest production/api
uv run ruff check production/api/src/smartguide_api/red_teaming/routes.py production/api/tests/test_api_red_teaming.py
uv run ruff format --check production/api/tests/test_api_red_teaming.py
```

**None of these commands ran.** They remain unverified, not successful or failed
checks. No source-integrity or workflow cleanup success is claimed: no review
workspace was acquired. No SmartGuide source, branch, PR metadata, merge state or
deployment was changed; all external calls were read-only preflight operations.

## Live attempt and durable evidence

| Field | Recorded value |
| --- | --- |
| Executed source | `fd316fc` |
| Session | `5bcbfd32eb0710114eadb7ae8300e0ee41807e497d48b0456f823b55e7d0820b` |
| Chat correlation | `f74d69e8ec4bed0eb88fb037ef3ca347` |
| Submission | 2026-09-20 16:06:41.706 UTC |
| Stopped record | 2026-09-20 16:09:01.785 UTC, revision 3 |
| Planning step duration | Approximately 140 seconds |
| Provider/model | OpenAi / `gpt-5.5-2026-04-24` |
| Reasoning | Medium |
| Call/repair limits | 8 / 2 |
| Input/output request limits | 12,000 / 32,768 |
| Actual calls / repairs | 1 / 0 |
| Receipt input / output tokens | 8,772 / 10,439 |
| Reasoning tokens | 3,584, included in output tokens |
| Initial estimated request size | 11,827 input tokens |
| Catalog / exposed cards | 129 / 16 |
| FinalReview / YAML / scenarios | No / none / none |

The request text hash is
`6755b57df5804c26b9dca0c1babe62a31b9d2404ae7b5b96ff174cced1611649`.
The original reserved response-schema hash is
`2e2c9feb316bbade87303fea3c3c86d915f8bb3e581c73fa031117721bdc4dfd`.
The request and completed receipt remain encrypted under the original identity:

```text
5bcbfd32eb0710114eadb7ae8300e0ee41807e497d48b0456f823b55e7d0820b:1:f7b208f3a21bd0113c1126516f82dba1f13602d80e2dffb7b351720ab8c6c8fb
```

Usage is known from the completed receipt. Monetary cost is **unknown**, despite
the stored estimator's zero value; zero is not evidence of free usage. There was
no additional Copilot inference. Prior session charges and unknown usage remain
unchanged. The waived provider cap remains waived, and EUR 20 remains a best-effort
target, not a demonstrated aggregate ceiling.

## Failure classification and read-only reproduction

The live failure is **invalid model-authored intent**, followed by an explicit
request-budget stop. Examples include prose instead of executable calculations,
unresolved capabilities with unsupported argument names, and invalid result paths.
Some needed tool cards were absent from the shortlist; this does not establish
that retrieval alone caused the invalid proposal. No deterministic builder defect
or safety violation was demonstrated by this attempt. Strict validation prevented
execution.

A temporary offline inspector read the session, original request and receipt
through public encrypted KeyVault APIs. An in-memory runtime replayed only that
receipt under its original response schema, rejected any further dispatch or
execution, and compared record hashes/timestamps and accounting before and after.
It found 175 blocking diagnostics, 21 typed repair targets, zero false
`PLANNING_HOST_CONTRACT` findings and no YAML. The stopped session additionally
contains `MODEL_INPUT_LIMIT`.

Repair payload measurements, without dropping evidence:

| Section | UTF-8 bytes |
| --- | ---: |
| User request | 2,821 |
| Diagnostics | 33,089 |
| Issued targets and fragments | 24,391 |
| Binding context | 12,412 |
| Authoritative contracts | 59,623 |
| Response schema | 11,117 |

The complete request estimates 48,483 input tokens. Even diagnostics plus affected
fragments exceed the configured allowance under the existing estimator. No repair
was reserved or charged; zero additional provider calls occurred during replay.
Session, request, receipt and budget hashes/timestamps all remained identical.

This reproduces the previously documented oversized correction-context limitation
on a fresh Server proposal. It does not justify weakening validation, raising
limits silently, changing the request, retrying for a lucky sample, or adding a
planner phase. Further live progress needs an explicitly changed input allowance
or separately scoped repair-context work. Both are outside this pass's frozen
settings; no second or third PR was dispatched merely to hide the first failure.

## Generic integration correction

Actual GitHub file reads exposed a separate **MCP response-mapping defect**:
`ConfiguredMcpClientFactory` discarded embedded resource payloads, retaining only
their content type. Two sanitized tests with generic embedded text resources
failed before the correction.

Commit `fd316fc` serializes content blocks with the MCP SDK's source-generated
protocol metadata. Embedded text/binary resources, resource links, images, audio
and annotations retain their payloads. Existing structured-content precedence and
single-text normalization are preserved. This is provider-neutral and introduces
no planner or review-specific branch. A subsequent real GitHub file read returned
its complete embedded resource content.

Ten focused mapping tests passed. Full affected and safety verification is recorded
below. This defect required one correction; no production planner fix was made.

## Offline verification and delivery

The following commands ran on the corrected source, without paid models or real
repository execution:

```bash
dotnet test tests/GnOuGo.Flow.Integrations.Tests/GnOuGo.Flow.Integrations.Tests.csproj -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests/GnOuGo.Flow.Planning.Tests.csproj -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Flow.Tests/GnOuGo.Flow.Tests.csproj -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Mcp.Core.Tests/GnOuGo.Mcp.Core.Tests.csproj -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.GithubCopilot.Core.Tests/GnOuGo.GithubCopilot.Core.Tests.csproj -c Release -m:1 --no-restore -warnaserror
dotnet publish src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj -c Release -r osx-arm64 --self-contained true -m:1 -p:PublishTrimmed=true -p:PublishAot=false -p:SkipBundledServerTools=true -p:UseAppHost=true -o /private/tmp/gnougo-server-e2e-bc9a618/host -warnaserror
git diff --check
```

Results: **1,558 tests passed**: 76 integration, 163 planner, 345 Server, 853 Flow,
31 MCP Core and 90 Copilot Core. No failures or skips occurred. Existing receipt
replay, budgets, cancellation, approval and publication-security regressions remain
enabled. The trimmed Server publish completed without warnings and its real Blazor
chat was exercised. The Server tests invoked the frontend `tsc && vite build`
successfully. Frontend source was unchanged; no synthetic benchmark campaign or
new release-wide validation is claimed.

The isolated browser and published host were stopped after the attempt. Their
ports were released, the unrelated host remained running, and encrypted sessions,
receipts, budgets and retained telemetry were preserved. No review workspace needed
emergency cleanup. The source correction is pushed as `fd316fc`; this report is a
separate documentation commit, not another live-model evaluation.

There is no publication draft, review identifier, review URL or exactly-once
publication result. Those gates remain untested. The immediate blocker is the
correction request limit; successful repository execution, Copilot inference and
publication permissions remain subsequent validation work.
