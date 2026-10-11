# Incomplete browsing and verified Copilot receipts

For the subsequent paid validation, authorized budget extension and retained failures,
see [fresh live validation, 4 October](fresh-live-validation-2026-10-04.md).
The results and budget snapshot below describe this correction's original candidate.

These are separate corrections on PR #117. The business TaskPlan/compiler pipeline and
`mapping.dynamic` are unchanged. No paid inference or historical invocation is replayed.
The retained [observations4 cohort](evidence/compact-observations/observations4.json)
remains **0/6**; its provider-backed results are not replaced by deterministic fixtures.

## Browsing and explicit review

Compact Browser observations retain DOM grouping for visible dialogs and native or
ARIA button controls. Reading a page never accepts consent implicitly. A workflow
observes the page, conditionally clicks an unambiguous observed cookie control when
that action is authorized, and reads again. Clicking/navigation invalidates cursors.
Unknown controls and unrelated blockers require a limitation or clarification.

`truncated`, a remaining `nextCursor`, or `captureTruncated` means partial data. Follow
bounded continuation or narrow the selector; a capture limit remains incomplete even
without a cursor. Finish reading a snapshot before navigation. A bounded read that
cannot establish completeness must not become an empty collection or successful report.
Requested per-item visits and extraction belong in the iteration, with extraction
bound to the actual visited content. These rules are generic generation/review guidance;
there is no site-specific planner rule or model-authored proof layer.

New approval commands carry optional `reviewedRequirementIds` with every accepted
requirement ID exactly once, plus the existing expected revision and artifact hash.
Missing, duplicate, unknown or stale acknowledgments are rejected atomically. Designer
checkboxes and `workflow.plan` human-input forms start without acknowledgments. An
incomplete or uncertain requirement requires revision. The actual TaskPlan shows
operations, loop collections, conditions and data dependencies; resolved contracts
remain visible alongside it. Without a human-input provider the session stays waiting.
Originating chat does not acquire a new approval endpoint.

Successful acknowledgments are stored in existing validation/history records as
`human_reviewed`. Revision clears them. They do not change historical artifact hashes,
issued model schemas, TaskPlan/PlanningGraph formats or storage format 10. Existing
approved artifacts remain approved. API clients and evaluation harnesses must obtain
explicit review; they must not derive acknowledgments automatically from requirements.
Compilation and human review are not independent proof of execution or prose completeness.

## Copilot session completion

The previous interruption path treated only continuation-eligible context-limit stops
as verified. A verified terminal failure, such as an admission refusal after commands
finished, could consequently become reconciliation despite an observed idle boundary.
Final SDK disposal also preceded durable task completion, and a failed receipt write
released ownership prematurely.

Completion classification now checks ordered SDK events and outstanding tool executions
separately from continuation eligibility. Structured shell metadata must establish exit;
assistant claims, timeout and abort acknowledgments do not suffice. Identical events
are idempotent; conflicting or unfinished operations remain uncertain. Existing bounded
idle waiting also covers late terminal events without another send. Failure snapshots
retain progress and safe termination/admission facts, without raw exception messages.

A verified failure returns a **completed MCP transport task containing an error result**,
with `completed: false` and partial execution evidence. The exact receipt is encrypted
and committed before ownership release and final SDK disposal. A disposal failure
cannot invalidate it. Restart/polling returns the committed receipt without inference
or command replay. A failed receipt write retains ownership; a crash without a receipt
still requires reconciliation. No host ceiling, permission, `agent.run` restriction or
unknown-completion rule is relaxed.

## Deterministic checks

- `RequirementsReviewTests`: exact/stale review submission, atomic rejection, persisted
  review, unchanged artifact identity and invalidation after revision.
- `PlannerChoiceUiTests` and `PlanningClarificationBrowserTests`: explicit controls,
  reload, keyboard/mobile behavior and separate approval.
- `BrowserObservationTests`: real Chromium pagination, dialog grouping, cursor expiry,
  capture truncation without continuation, narrowed reads and genuinely empty data.
- `LocalProductOutcomeExecutionTests`: real Flow, Browser and Document processes,
  immediate/delayed/absent consent, refusal of an unrelated modal, product visits and
  independently inspected XLSX cells; exhausted observation bounds stop before product
  visits or document writing. Existing failure/denial oracles stay intact.
- `CopilotCompletionReceiptTests`: bounded local commands and real MCP Tasks transport
  with deterministic SDK events, final failures, late idle, admission limits, pending
  commands/shells, encrypted restart, tenant isolation and interrupted receipt writes.
  Real Flow receives error receipts and runs cleanup only after verified completion.

Run focused checks with `dotnet test <owning-test-project> -m:1 -warnaserror`.
Browser review smoke additionally needs `PLAYWRIGHT_MODULE_PATH` pointing to an installed
Playwright `index.mjs`. The full gate is `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror`,
plus the affected Release packages, frontend production build, planning/Copilot Native
AOT publication and published encrypted planning-recovery smoke.

Local publication commands (replace `osx-arm64` with a supported host RID):

```sh
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 --self-contained true -warnaserror -o /tmp/planning-smoke
/tmp/planning-smoke/GnOuGo.Flow.Planning.Smoke
dotnet publish src/GnOuGo.GithubCopilot.Mcp -c Release -r osx-arm64 --self-contained true -warnaserror -o /tmp/copilot-smoke
/tmp/copilot-smoke/GnOuGo.GithubCopilot.Mcp --copilot-task-persistence-smoke /tmp/copilot-smoke-vault
GNOUGO_COPILOT_LIST_SMOKE_EXECUTABLE=/tmp/copilot-smoke/GnOuGo.GithubCopilot.Mcp dotnet test tests/GnOuGo.GithubCopilot.Mcp.Tests -m:1 -warnaserror --filter FullyQualifiedName~CopilotListStdioTests
dotnet publish src/GnOuGo.Browser.Mcp -c Release -r osx-arm64 --self-contained true -warnaserror -o /tmp/browser-smoke
GNOU_GO_BROWSER_MCP_TEST_EXECUTABLE=/tmp/browser-smoke/GnOuGo.Browser.Mcp dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter FullyQualifiedName~LocalProductOutcomeExecutionTests
```

Use disposable vault/workspace directories. Browser publication bundles Chromium;
its Playwright dependency is not published as Native AOT. The Server encrypted recovery
check uses its supported trimmed publication and `--planning-persistence-smoke <directory>`.

Validated source candidate `269b8ac9`:

- Full solution: **4,278 passed, zero failures, 12 skips**, with `-m:1 -warnaserror`.
  Skips are seven Windows-only cases and five opt-in paid Copilot cases.
- **14/14 local product execution cases** passed; the published Release Browser repeated
  all fourteen, plus encrypted approval restart (**15/15**). Expected failures remain failures
  with their original oracles. No external website or provider was used.
- Four Release library packages, planning and Copilot Native AOT, published MCP boundary,
  encrypted Copilot/Server recovery, frontend production build, Designer desktop/mobile
  checks and planning-skill validation passed on macOS arm64. The Server publish skipped
  bundled tools; Browser and Copilot were published and exercised separately.
- The retained ten-contract discovery regression still fits its **24,000-token** limit
  (largest issued estimate **23,931**, seven scripted calls, zero repairs). These are
  deterministic estimates, not provider usage or new live benchmark results.

[Sanitized validation, source fingerprints and log hashes](evidence/browsing-review-receipts/validation.json)
retain the test and publication evidence.
Paid dispatch remains stopped. The retained campaign upper bound is €48.634552/€50,
including €1.303376 reserved for historical unknown completion. Reservations are not
released to fund another cohort. PR #117 stays draft until its live execution gates pass.
