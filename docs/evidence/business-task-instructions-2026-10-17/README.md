# Business-task instructions and observed extraction

Candidate implementation: `e9162a1` on draft PR #117. This work is offline. No provider call, live review, historical advancement, approval or financial change was made.

## Request responsibilities

Fresh planner requests now say:

> Return executable business tasks for the requested outcomes. Generate artifacts only when explicitly requested. Do not add unrequested actions, filtering or deduplication.

The Agent.Server policy now says:

> Express the requested business actions. The host manages approval of the generated plan before execution; do not add that approval as a business task.

The injected instructions contain no explanation of the compilation format. User requests, document content and tool contracts are not filtered. The actual-request regression deliberately requests a YAML document and retains that request and its original content. Business human-input tasks remain supported. Configuration, confinement, permission, output and cleanup rules are unchanged. Previously issued requests are reused; these edits only affect fresh requests.

## Complete request admission

The first CI pass caught a stale prompt assertion and two real request-size regressions. The correction shortened redundant instructions and removed a schema annotation that repeated the accepted transform-mode enumeration; the enumeration and validation rules remain intact. No discovery algorithm, context format, limit or admission margin changed.

[Measured request estimates](request-admission.json) compare the same retained seven-call discovery fixture: the final request is **23,889 / 24,000**, versus **23,890 / 24,000** before. All six discovery requests remain below the **21,600** admission margin. All 74 discovered identities, ten requested contracts, 19 metadata reads and 20 exact resolutions remain; no unresolved contract is hidden. Total conservative estimates are 139,517 versus 139,514 across those seven requests, so no overall token saving is claimed. Actual provider usage is zero.

The mapping fixture again admits a complete example under **12,000**, while the oversized global decision still fails before dispatch. The first failing candidate attempted 12,165; that failure remains recorded in CI. Current owning suites pass: **1,562 Planning checks and 199 mapping checks** under `-warnaserror`. No assertion was loosened to accept an over-budget request.

## Independent retained failure

Run `338d861b810ad788539ca715b0e6cbcd` and its matching saved planning session were read through the public encrypted-storage APIs. Both mapping responses satisfy their original strict JSON schemas. That guarantees their response structure, not executable JavaScript or provenance.

| Saved response | Replayed failure | New bounded details |
| --- | --- | --- |
| First | A single JavaScript backslash before `.` is consumed before the regex helper receives its pattern. | `program`, `lost_regex_escape`, helper `text`, offset 53, length 2 |
| Second | Its repaired regex passes preflight, but `+` constructs an unobserved suffix and Git reference strings. | `program`, `unsupported_operator`, `BinaryExpression`, operator `+`, offset 305, length 89 |

Offsets are zero-based UTF-16 ranges in the script; the second range identifies the rejected binary expression. Diagnostics include no script or observation. The existing error code/message remain stable, including the generic unsupported-operation message used in historical adaptive assignment identities. Fresh repair context, final two-attempt errors and existing traces receive the bounded structural details.

The unchanged sandbox still rejects both programs. No unsupported operator or invented scalar was enabled, and no retry allowance was added. The failed run reached no clone, command checks or review publication. Its recorded finalizer did complete `cmd_run` and `delete_directory`; it would be incorrect to claim that the run had no effects.

[Machine-readable replay results](retained-replay.json) retain response hashes, exact error ranges, artifact hashes and the unchanged encrypted run-head hash. The original sensitive requests/scripts are not republished here.

## Disposable composition

One existing explicit structural revision on a new disposable session reaches final review using one deterministic proposal and zero repairs. Its complete serialized request is 163,764 bytes. There are zero provider/MCP calls during this retained-data replay, and no approval is issued.

Only these differences were accepted:

| Task | Difference and reason |
| --- | --- |
| `parse_pr_url` | Remove `prHeadRefSpec` and unused `headFetchRef` from the extraction target. Owner, repository, change number and remote URL remain observed URL facts. |
| `clone_repository_once` | Bind the existing `branch` argument to `extract_pr_refs.headSha`; add that producer dependency. The existing clone implementation supports exact commit IDs. |
| `fetch_pr_head` | Omit the constructed `refSpec`, using the existing declared default. Update its objective to describe that behavior; the clone and checkout own exact-commit selection. |

All four requirements, public inputs and outputs, operation identities/order, unrelated arguments, assertions and finalizers remain unchanged. The corrected composition still fails if the requested commit is unavailable; it does not fall back to the default branch.

The original second script was also reduced to its four observed fields, removing the concatenated suffix and reference fields. All four results match an independent `System.Uri` decomposition of the saved source, under unchanged mapping provenance validation. This is an explicit offline composition correction, not a runtime rewrite of a model response.

The exact retained TaskPlans and v15 definitions are written to the operator-selected private evidence directory by [the replay tool](replay/Program.cs). Their hashes are published in `retained-replay.json`. Reproduce with:

```sh
dotnet build docs/evidence/business-task-instructions-2026-10-17/replay/Replay.csproj -m:1 -warnaserror
dotnet docs/evidence/business-task-instructions-2026-10-17/replay/bin/Debug/net10.0/Replay.dll /path/to/existing/workspace /path/to/private/evidence
```

The tool reads historical records only. It creates an independent in-memory revision, not a reset or resumption of the saved session. Its deterministic adapter refuses any second model request.

## Local execution proof

The generic [TaskPlan proposal](local-review-proposal.json) and [actual compiled v15 definition](local-review.yaml) run real local Git cloning and check commands, with deterministic metadata/review adapters. The repository starts on a different commit: the oracle verifies the requested commit's detached HEAD and its exact file content, not merely a successful clone response.

[Five outcome reports](local-review-results.json) cover approval, exact findings including repeated comments, failed checks, denied cloning and uncertain completion. Each uses one deterministic planning response, one mapping response and zero mapping repairs. Successful cases clone once, execute all four local checks, submit the truthful decision and clean up once. Failed checks produce a change request. Denial stops before clone; uncertainty prohibits publication/cleanup. Restart repeats no committed inference or effects.

All 33 code-review integration checks and eight clone checks passed with `-warnaserror`, including an unavailable exact commit. Earlier owning checks passed: 189 mapping tests, 42 planning checks and six host policy/failure-isolation checks. The host HTTP and revision paths stay usable after the workflow failure.

## Validation status and limits

Full serial solution, candidate CI, package/AOT/published recovery and independent Browser/Document artifact results are reported separately in the completion report on PR #117. The targeted results above do not substitute for those gates. The planning skill validates.

The corrected saved-workflow copy is a review artifact, not a newly approved workflow. The local fixture proves extraction, exact commit propagation, execution and recovery mechanics; it does not prove the retained host's remote permissions, commit availability or every business decision in the larger saved composition. Prompt improvements do not establish future model compliance. PR #117 remains draft pending real business acceptance.
