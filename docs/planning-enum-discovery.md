# Enum generation and discovery repair

This correction follows `b53ac1c` on `feat/flow-hybrid-planning-v9`, issue #112 and
[draft PR #113](https://github.com/GnouGo/GnouGo/pull/113). TaskPlan, executable
compilation, approvals, planning format 10 and execution journal schema 9 are unchanged.

## Retained failure

Session `7e5bbed190234d6e99d2d0e31afd403e` has seven recorded responses and sixteen
revision snapshots. Its first TaskPlan declared unrestricted strings for three
comment fields whose consumers require finite domains. It also supplied `bash`
to two invocations of the filesystem command catalog, which does not declare that
alias. Both operation contracts were resolved after submission; the final prompt
had shown only their names and port names.

The fifth response repeated a normalized discovery query. The sixth corrected it
successfully, but `DISCOVERY_NO_PROGRESS` remained active. Consequently, the first
TaskPlan call was counted as another repair. Planning stopped with seven of eight
calls and both repairs used, before any repair of the TaskPlan.

The original response sequence reproduces five `TASK_INPUT_TYPE` findings and
three exact producer-enum findings. In-memory enum declarations remove the three
comment errors; the unsupported command literals correctly remain invalid.
Encrypted originals were reread and verified unchanged. Sanitized requests,
responses, schemas, contracts and accounting are in the
[regression recordings](../tests/GnOuGo.Agent.Server.Tests/Fixtures/EnumPlanning/README.md).

## Generic changes

- Compact discovery entries include declared `enum`/`const` constraints, without
  descriptions or full schemas. Ranking never invents these constraints.
- Resolved editable inputs constrain scalar literals in new response schemas.
  Operation variants with different domains cannot share an unrestricted bypass.
  References remain available and require semantic type compatibility. Index-only
  selections still resolve their exact contracts before compilation; a compact
  metadata hint does not establish ownership, compatibility or permission.
- Identical schema fragments share definitions. Concise guidance retains explicit
  intent, bounded scopes, cleanup and safety rules. Neither submitted TaskPlans nor
  business values are automatically rewritten or narrowed. Composite contracts
  remain authoritative semantic checks; this pass adds scalar-literal wire checks.
- A completely successful discovery batch clears only resolved discovery-response
  errors. Semantic errors, unavailable-source findings, coverage limitations,
  historical checkpoints and cumulative accounting remain intact. Historical
  pending requests still use their original schemas and reservations.

The synthetic regenerated proposal explicitly chooses a command-execution
capability for project checks, declares the three enums, and assembles JSON cleanup
arguments around the original resource location. It is **not** an authorized scoped
repair of the stopped session. An independent oracle rejects replacing project
checks with an unrelated permitted filesystem command. The execution permission
stays `deny`; missing or refused command observations cannot count as successful
checks. No permission is broadened automatically.

## Deterministic measurements

Fresh presentation of each sanitized retained state uses the existing complete
request estimator, including the response schema. Counters and inspection selections
are preserved, so these are context measurements, not a new model run.

| Call | Input estimate before | After | Prompt bytes before | After | Schema bytes before | After |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 9,531 | 8,650 | 9,006 | 7,586 | 18,817 | 17,595 |
| 2 | 21,372 | 21,509 | 34,259 | 33,631 | 29,088 | 30,126 |
| 3 | 22,129 | 22,146 | 34,125 | 32,705 | 31,493 | 32,965 |
| 4 | 22,088 | 22,106 | 32,564 | 31,144 | 32,932 | 34,404 |
| 5 | 21,690 | 21,707 | 31,369 | 29,949 | 32,932 | 34,404 |
| 6 | 21,758 | 21,513 | 31,572 | 37,899 | 32,932 | 25,871 |
| 7 | 21,588 | 21,574 | 39,132 | 37,616 | 24,864 | 26,336 |

All seven requests fit the unchanged 24,000-token allowance. Cumulative estimates
are **140,156 → 139,205**; the maximum increases **22,129 → 22,146**. Constraint
visibility is the improvement, not a large token reduction. Optional packing and
exact mandatory context explain increases on individual requests. The earlier
index-only probe was 21,588 → 21,925; the table includes the final schema/guidance
changes as well. Output/reasoning usage and live reliability were not measured.

Regression coverage includes enum/constant JSON types, explicit null, typed
references, differing domains on equal port names, host-owned fields, unchanged
receipts, pending-schema recovery and approval verification. A fresh scripted
sequence reproduces repeated discovery, its successful correction, a proposal and
a repair within **eight calls and two repairs**. Historical replay preserves the
original **seven calls and two repairs**, without refunding them.

The full planner suite retains all eight independent business scenarios and its
existing 21,600/24,000 request-budget assertions. Package and Native AOT smoke
checks use deterministic responses. Exact full-suite and branch-CI outcomes are
reported on PR #113; the known Windows Cmd timeout is a separate blocker.

## Recovery and limits

Deploy the updated planner, generate a new workflow and explicitly approve it.
Do not resume the stopped session by refunding its repairs, silently substitute
operations, change the command allowlist or raise token ceilings.

No paid inference, live benchmark, external review or real Copilot task was run.
The real Copilot sandbox command-execution limitation remains unresolved; planning
success and mocked observations are not evidence of successful external checks.
Keep PR #113 draft; do not merge.
