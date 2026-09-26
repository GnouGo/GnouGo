# Constrained business bindings and bounded repair context

## Retained failure

Starting revision: `1cb406cd66bca2f0fcc794afda7d372a790b3174`, branch
`feat/flow-hybrid-planning-v9`, issue #112, draft PR #113.
Session `d37d71ef4d7f478dae13743acb97bb3e` stopped at revision 12 with three
completed model calls, no repair dispatch and no pending request. Its saved
input/output ceilings were 60,397 / 32,768 tokens.

The four `TASK_INPUT_TYPE` findings were valid:

- Unrestricted transform strings supplied `side` and `startSide`, whose actual
  contracts permit only `LEFT` or `RIGHT`.
- Another unrestricted string supplied `event`, whose actual contract permits
  `APPROVE`, `REQUEST_CHANGES` or `COMMENT`.
- An object supplied `parametersJson`, whose actual contract requires JSON text.

The old TaskType could not declare those finite string domains. Repair authorized
only the four consumer bindings, excluding the necessary producer constraints.
The repair prompt also repeated all 98 discovered operations even though its
scope fixed the ten selected operations. The conservative full-request estimate
was 66,621 tokens, so dispatch stopped locally without consuming another call or
repair. Raising the ceiling alone would not correct the contract failures.

## Generic corrections

String business types can explicitly declare 1–256 distinct enum values. The
compiler emits the same domain in strict structured results; runtime validation
rejects out-of-domain responses before dependent operations. Nullability remains
separate. Semantic diagnostics report produced/required types and unambiguous
producer constraint locations. Repair includes the three exact producer enum
slots with the original four consumer bindings, preserving unrelated content.

A `json` business value encodes exactly one value through existing typed `set`
stages and the existing `json` function. It provides deterministic escaping
without inference or model-authored expressions. Opaque values stay opaque,
cleanup guards and permission checks remain enforced, and approval recompilation
covers the generated stages.

Full-plan validation exposed a second deterministic gap: final YAML validation
did not recognize finite selector domains enforced by structured `llm.call`
results. It now recognizes static, valid, executor-checked schemas, with the same
safe-fallback requirements as checked `set` results. Optional, nullable,
unvalidated and dynamically defined selectors remain insufficient.

Binding repairs with fixed operation selections receive complete selected
metadata. All discovery pages and receipts remain saved. Repairs authorized to
change operations retain catalog alternatives. Baseline prompt serialization
omits only irrelevant DTO defaults and round-trips to the identical TaskPlan.
Recovery derives new producer diagnostics before a new request; pending requests
retain their original scope, schema, identity and accounting.

No new planning phase, IR, executor, provider-specific rule or storage migration
was introduced. Planning format 10 and execution journal schema 9 are unchanged.
Existing session limits and cumulative budgets are unchanged.

## Offline measurements and validation

The original encrypted session was read through public KeyVault record APIs.
No session was updated or resumed. Rebuilding its repair request in memory gives:

| Measure | Before | After |
|---|---:|---:|
| Discovered operations included | 98 | 10 selected |
| Operation metadata, UTF-8 bytes | 153,477 | 33,407 |
| TaskPlan prompt view, UTF-8 bytes | 24,185 | 13,851 |
| Conservative complete-request input estimate | 66,621 | 23,568 |

The full 21-task plan with explicit enum/JSON corrections passes semantic
compilation, generated graph validation and final YAML validation, including the
original host confirmation gate. This in-memory check is not a model-generated
repair and does not prove real workflow execution.

The sanitized fixture first reproduced all four errors; the minimal-repair and
repair-scope tests failed before implementation. Deterministic coverage now
includes constrained responses, JSON escaping, opaque values, nested captures,
rejected unrelated edits, approval invalidation, cancellation, failure, absent
producers, permission refusal, renamed operations, reordered distractors,
recovery and unchanged pending requests. Existing independent oracles remain.

Local validation:

- Full .NET solution: 3,095 passed, zero failed, five opt-in live Copilot tests
  skipped across 33 test projects; planner suite: 324 passed.
- Python core: 287 passed; Python CLI: 27 passed.
- Five independently publishable Flow packages built without warnings.
- Native AOT on macOS ARM64: all eight business scenarios, typed product
  transformations, constrained result contracts and JSON encoding passed.
- No warnings in the final .NET test, package or AOT build logs.

Raw local logs, including unsuccessful intermediate checks, remain under ignored
`artifacts/constrained-bindings-2026-09-26/`. Branch CI results and the exact final
revision are recorded on [draft PR #113](https://github.com/GnouGo/GnouGo/pull/113).

## Operational limits

Deploy/restart Agent.Server with this revision, then explicitly resume the saved
session through Designer. The rebuilt request fits its saved ceiling; no limit
increase is necessary for this retained request. Future requests remain subject
to their saved limits, remaining repairs and cumulative spending admission.
The model must still produce an authorized valid repair; it is not applied
automatically to the saved session.

No paid inference, benchmark, real Copilot task or external workflow operation
was dispatched. Historical live evidence is unchanged, including the 8/9 cohort;
this pass makes no new model-reliability claim. Real Copilot command edit/test
execution remains unverified under the available sandbox policy. The PR remains
draft and is not merged.
