# Browser snapshot pagination and Copilot completion

This correction continues PR #117 from `18b6bc0ffd9cf5c498db8f99a2c64fb31a3b4f05`.
TaskPlan, compilation, mapping.dynamic, inference ceilings and execution oracles
are unchanged. Producer contracts and Copilot integration own the changes.

## Reproduced causes

The retained `preconditions20261004b` Amazon execution exhausted three explicitly
unrolled continuation reads while its snapshot still had a cursor. Its required
condition correctly stopped before product visits and workbook publication. The
browser had already captured the DOM; these reads paginate that immutable capture.
Scripts were already excluded by observation mode. HTML reads also already default
`includeScriptContent` to false, before truncation.

The retained code execution received 49 tool completions and an idle event. Two
shell starts had no exit in their original callback, but later `read_bash`
callbacks supplied exits 0 and 2 for those exact shell identities. A third shell
invocation was denied before execution. Requiring an exit on each original
callback incorrectly classified this combination as unresolved. Separately,
virtual SDK output logs were read with host shell commands, which failed and
prompted unnecessary repeats. The operation stopped at its unchanged cumulative
inference reservation ceiling, after 407,664 measured input tokens and 8,320
output tokens. These historical invocations remain untouched.

## Producer changes

Browser offers additive `format=observation_pages`, returning a bounded manifest
of frozen page cursors. Existing foreach consumes the descriptors before navigation.
Capture and manifest truncation remain explicit; no bound establishes invented
completeness. Pages omit duplicate flat content and use DOM-verified shorter
selectors. See [Browser contract](../src/GnOuGo.Browser.Mcp/README.md#compact-observations).

Copilot correlates asynchronous exits by session, command instance and shell ID.
Unknown or conflicting commands still prevent cleanup/replay. A verified failure
commits an error receipt; it is never reported as a successful review. Finalization
can refresh SDK event history once within the existing shutdown allowance.
`project_read` exposes bounded ranges and read-only access to SDK-published virtual
logs; complete logs stay in the existing encrypted task checkpoint, outside tool
results and continuation prompts. See [Copilot contract](../src/GnOuGo.GithubCopilot.Core/README.md#command-completion-and-bounded-output-reads).

## Reproducible checks

```sh
dotnet test tests/GnOuGo.Browser.Mcp.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.GithubCopilot.Core.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.GithubCopilot.Mcp.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  --filter FullyQualifiedName~LocalProductOutcomeExecutionTests
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet pack src/GnOuGo.GithubCopilot.Core -c Release -m:1 -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror
dotnet publish src/GnOuGo.GithubCopilot.Mcp -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror
```

New local product cases consume more than three snapshot pages through sequential
and parallel foreach, use one planning call and zero repairs, visit product pages,
and independently inspect XLSX cells. Other cases retain consent, incomplete
observations, empty/missing data, permission denial and cleanup checks. Copilot
regressions cover later exit callbacks, refused commands, ambiguity, missing event
delivery, virtual-log isolation and durable terminal-error replay without duplicate
commands. Deterministic adapters are not live-provider evidence.

## Live collection

Continue `schema-portability-20261002` under its already authorized EUR 100 ceiling.
The inspected initial upper bound is EUR 62.95914450475299, including all unknown
reservations. Preserve the original prompts, three-product limit, pinned SmartGuide
commits and six-slot execution report. Use new IDs and review each artifact before
execution. Never replay an execution already started or uncertain.

```sh
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability inspect --campaign schema-portability-20261002 --workspace "$GNOU_GO_WORKSPACE"
# On the frozen candidate, replace CASE/RUN/COHORT with the retained fresh IDs.
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability plan --campaign schema-portability-20261002 --workspace "$GNOU_GO_WORKSPACE" \
  --case CASE --run RUN --cohort COHORT
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability inspect-run --campaign schema-portability-20261002 --workspace "$GNOU_GO_WORKSPACE" --run RUN
# Only after comparing every accepted requirement against actual tasks/dependencies:
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability execute --campaign schema-portability-20261002 --workspace "$GNOU_GO_WORKSPACE" \
  --case CASE --run RUN --cohort COHORT --review-command REVIEW.json
```

### Candidate A: retained failed cohort

Candidate `90bf33d84e894f689daa813240846304e287afba` passed the full solution:
4,432 tests, zero failures, 13 existing skips. Release Core packaging, planning
and Copilot Native AOT, published MCP discovery and encrypted recovery passed.
See [validation evidence](evidence/browser-pagination-copilot-completion-2026-10-04/validation-a.json).

Fresh cohort `pagination20261004a` has **0/6 execution oracles**, with four slots
unstarted after a reproducible defect was identified. Amazon repetition one was
rejected during review for missing query binding and incomplete reads. Explicit
revisions retained the original requirements but exhausted repairs on incompatible
control bindings: six calls, two repairs, no execution. Code repetition one reached
execution after seven planning calls, two repairs and two explicit review revisions.
It made eleven runtime inference calls (448,230 input and 5,525 output tokens).

Copilot committed a **verified terminal failure** at its unchanged two-million-token
reservation ceiling. There was no false `RUN_NEEDS_RECONCILIATION`. No final review
report was produced. Clone cleanup was attempted but exceeded the existing 30-second
finalization deadline; it remains a failed oracle and is not replayed. Required-check
coverage also failed the unchanged independent oracle. See the
[six-slot report](evidence/browser-pagination-copilot-completion-2026-10-04/cohort-a.json)
and [failure evidence](evidence/browser-pagination-copilot-completion-2026-10-04/cohort-a-failures.json).

The run revealed a producer serialization defect: `project_read` returned JSON as
a string, so SDK escaping expanded a bounded range to 20.1 KB. The SDK truncated
that response into another virtual output artifact, without shell-exit metadata.
Access to that unregistered artifact was correctly refused; subsequent host-shell
reads and output-only reruns were refused too. Follow-up regressions require native
structured results, a serialized byte cap, exact continuation, and shell rejection
with guidance to the supported reader. Complete registered command logs remain
encrypted; no inference ceiling, permission or oracle is relaxed.

Campaign usage after this cohort is EUR 66.71637835775968 committed or reserved
out of EUR 100, including the same two historical unknown reservations. Historical
33/33 benchmark evidence and failed live cohorts are unchanged. PR #117 remains
draft until six unchanged execution oracles pass on one frozen candidate.

### Candidate B: verified receipts, incomplete execution acceptance

Candidate `b93a5890fb0d15672de1aaaf24f1c2b9dcd675bc` fixes the reproduced
double encoding: `project_read` returns a structured object and bounds its serialized
UTF-8 representation as well as its character range. Continuation preserves exact
text and Unicode boundaries. Native shell access to virtual output paths is rejected
before permission approval, with guidance to the supported reader. Regression tests
failed before these changes and pass afterward.

The full solution passed **4,435 tests, zero failures, 13 existing skips** across
33 projects with `-warnaserror`. Release Core packaging, published Copilot Native AOT
discovery and planning Native AOT smoke passed. Published encrypted recovery had
passed on candidate A; this follow-up changes no persistence implementation. All
22 non-skipped CI checks passed on candidate B (four jobs skipped). See
[validation](evidence/browser-pagination-copilot-completion-2026-10-04/validation-b.json).

The frozen `pagination20261004b` cohort remains **0/6**, with two failed executions,
two unexecuted planning sessions and two unused slots. The
[report](evidence/browser-pagination-copilot-completion-2026-10-04/cohort-b.json)
retains source/harness/configuration hashes and the original prompts/oracle version.
Historical and candidate A failures are not pooled into these results.

| Run suffix | Planning calls / repairs | Execution | Oracle |
| --- | --- | --- | --- |
| amazon-1 | 5 / 1 | 989,519 ms; all 49 search pages consumed | Failed: collection memory, cleanup step limit, missing workbook |
| amazon-2 | 5 / 1 | Not approved/executed after review revisions | Unverified |
| amazon-3 | Not started | Not started | Unverified |
| code-1 | 8 / 2 | Clarification after discovery/planning allowance closed; no commands | Unverified |
| code-2 | 4 / 0 | 698,491 ms; durable verified terminal failure | Failed: inference ceiling, missing report, cleanup timeout, required-check coverage |
| code-3 | Not started | Not started | Unverified |

Amazon read **1,474 records in 49 frozen pages**, with no lost or stale cursors.
Retained result objects serialize to **1,146,115 UTF-8 bytes** in total, at most
23,983 per page, excluding MCP envelopes. The individual page reads took 246 ms
combined; overall execution took much longer. About 289,000 selector characters
and 494,000 URL characters remained alongside 28,594 text characters. Final loop
collection hit the existing checked-mapping memory allowance before the next
inference request; cleanup then hit the persisted step ceiling. No product visit
or workbook was fabricated. The final Browser close recorded by the independent
oracle is not credited as workflow cleanup. See
[Amazon evidence](evidence/browser-pagination-copilot-completion-2026-10-04/cohort-b-amazon-execution.json).

The code run committed `completionVerified=true`, `termination=verified_failure`
and an exact durable MCP error result. All **25 tool invocations** completed without
conflicting evidence, including a pytest exit recovered from a later `read_bash`.
A native shell read of a virtual log was refused before execution; the virtual-log
`project_read` succeeded. Its 4,000-character range arrived as a **4,133-byte JSON
object**, with `nextOffset=4000` and no secondary truncation artifact. There was no
false `RUN_NEEDS_RECONCILIATION` and no repeated command to recover that log.

Copilot nevertheless reached its unchanged reservation limit after **18 inference
calls**, with 381,166 measured input and 4,426 output tokens. The retained conservative
charge was 1,938,576 tokens; the next input reservation could not fit. It returned
`COPILOT_LIMIT_REACHED`, not a successful review. Cleanup was attempted after verified
completion but exceeded the existing 30-second finalization deadline. No final local
review was written. Installation and Python checks did execute with truthful exits,
but their command forms differed from the unchanged oracle's accepted forms; those
coverage checks remain failed. See
[Copilot evidence and serialized input measurements](evidence/browser-pagination-copilot-completion-2026-10-04/cohort-b-code-execution.json).

Compared with candidate A's executed code run, measured input fell from 448,230 to
381,166 tokens and runtime inference increased from 11 to 18 calls. These are
different generated plans, so this is not a controlled causal efficiency comparison.
The bounded structured-read regression and its live payload are direct evidence of
the serialization correction.

Review prevented an empty-array placeholder report, invalid encoding, ignored HTML
truncation and model-authored completeness claims from being executed. Later revisions
preserved the accepted requirements. Amazon repetition two gained authoritative
manifest guards but still aggregates full snapshots; it remains unapproved given the
collection/cleanup bounds demonstrated by repetition one. Code repetition one had
not inspected the durable interactive operation before discovery closed. See
[review evidence](evidence/browser-pagination-copilot-completion-2026-10-04/cohort-b-review.json).

This cohort used **22 logical planning calls / 23 physical attempts**, four repairs,
12 discovery reads and eight explicit review revisions. Planning measured 352,320
input and 52,545 output tokens, costing EUR 2.973675. Execution used 21 calls,
398,993 input and 4,794 output tokens, costing EUR 1.905376. One planning request
used its existing bounded HTTP retry; it left no new unknown receipt.

Final campaign accounting is **EUR 68.988677 verified + EUR 2.606753 reserved =
EUR 71.595430 / EUR 100**. Both historical unknown reservations remain retained;
approximately EUR 28.404570 remains. Budget is not exhausted. Collection stopped
on the failed candidate with unresolved execution bounds, not because those bounds
were raised or treated as success. No uncertain invocation or cleanup was replayed.

Remaining acceptance work includes bounded observation aggregation, finishing checks
and the report within existing inference allowances, and cleanup within its existing
deadline. The live XLSX value oracle was never reached; its legacy flat-content
reader also has not been adapted to the new structured page records. Its assertions
and implementation are unchanged. PR #117 remains draft. No planner, compiler,
mapping.dynamic, executor, permission or oracle change is included in this correction.
