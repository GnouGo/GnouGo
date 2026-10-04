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
