# Omission-default Stage-1 live validation

The single authorized session **stopped before BehaviorReview** with
`DECISION_OUTPUT_LIMIT`. There is no accepted behavior, executable skeleton, generated
artifact or typed business outcome. The campaign is blocked. Stage 2 and Stage 3
were not run; no production patch or replacement session followed the failure.

Production is frozen at `28a057c53a47360fd8fe5ad369f734e33c1e8403`, with harness pin
`9db24c3` and campaign `schema5-omission-defaults-20260913`. The encrypted manifest
contains binary, scenario, catalog and policy fingerprints. All frozen binary hashes
were reverified after the run. The configured model remained
`OpenAi / gpt-5.5-2026-04-24`, with **low** reasoning throughout. Input/output ceilings
remain **12,000 / 8,192**, the input dispatch target **9,600**, concurrency four and
five repairs per workflow/gate. No reasoning A/B request was made.

## Outcome and accounting

Session `3d41ac712ef24278a8b655cd52c8beff`, final revision **24**.

| Measure | Result |
|---|---|
| Typed outcome | None; technical stop |
| Status / phase | `stopped` / capability preparation |
| Behavior review / artifact approval hashes | None |
| Verifiable calls / reservations | 7 / 7 |
| Unverifiable dispatches / pending requests | 0 / 0 |
| Input / output tokens | 20,425 / 18,099 |
| Largest estimated / actual input | 6,896 / 5,128 |
| Decision pages | 7: 5 completed, 1 split parent, 1 stopped child |
| Distinct decision IDs exposed to verified calls | 28 |
| Distinct IDs on completed pages | 25; three declaration decisions remain incomplete |
| Engine-resolved executable decisions | 0; other uninstrumented engine decisions unknown |
| Executable model decisions / hole exposures | 0 / 0 |
| User clarifications / answers | 0 / 0 |
| Repairs | 2 declaration correction reservations, `$plan` / `response_contract` |
| Terminal gate failures / truncated receipts | 1 / 2 |
| Canonical declarations / aliases / modifiers committed | 0 / 0 / 0 |
| Estimated cost from retained budget accounting | EUR 0.5565 |

| Phase | Calls | Input tokens | Output tokens | Repair reservations |
|---|---:|---:|---:|---:|
| Intent | 3 | 5,215 | 693 | 0 |
| Confirmation scope | 1 | 4,591 | 667 | 0 |
| Declaration adjudication | 1 | 5,128 | 8,192 | 0 |
| Declaration corrections | 2 | 5,491 | 8,547 | 2 |

Planning usage is separate from independent execution: no generated artifact reached
execution, so no execution fixture or execution-model request ran in this session.
The preflight reference-fixture checks are offline checks, not live workflow success.

## First blocker and retained partial evidence

The original five-decision declaration page returned a verified `output_limit`
receipt at 8,192 output tokens. Its estimated answer size was 1,219 tokens. The
existing finite correction policy split it into two pages without changing model
settings or limits. The first child completed two decisions using 355 output tokens.
The three-decision child then also returned `output_limit` at 8,192 tokens, against
an estimated answer size of 785. It used 3,003 actual input tokens.

The planner stopped instead of splitting the correction again. The terminal code is
`DECISION_OUTPUT_LIMIT`; the outer preparation diagnostic reports `/preparation`.
The retained page records the more precise location:

`/decisions/declarations_r_45d7e0e056ae6f7ae21f5136`

Terminal page:
`page_dcd9cec622c3ec8ba1151d25f78a66a9cfa77957672fff7167197f1689d5802f`.

This establishes **verified model response truncation during declaration
adjudication**, including an already split correction. The receipts do not establish
why the model exhausted its allocation; no reasoning comparison or additional
provider request was made. This is not an input-sizing failure, unverifiable
dispatch, confirmation conflict or demonstrated missing business choice.

The live interpretation emitted a threshold `omission_default` candidate containing
`100`. It did not emit a separate `runtime_fallback` obligation: the complete
classification clause remained a preliminary output candidate. The completed
correction resolved that clause as `modifier_of classifiedResult` with a **null
default reference**, and selected the required public input name **record**.
These are durable partial decisions, not canonical declarations. The other three
decisions have no usable candidate, so the coordinator committed no declaration
set and could not offer behavior review.

The new schema exclusions and synthetic regressions prove that output defaults and
condition-literal references cannot take the former invalid path. This live run
provides partial corroboration, not complete declaration or end-to-end convergence.

## Replay and offline validation

Strict replay from revision **23** reproduces the terminal code using **one retained
receipt**, one local advance and **zero provider dispatches**. It verifies unchanged
persisted session, request journal and budget. No synthetic response was supplied.
The blocked final revision has no pending requests.

Before the frozen run, **3,136 solution tests passed**: **736 planner**, **841 Core**
and **363 Agent.Server** tests are included, with one optional provider test skipped.
All **32 focused harness tests** passed again after pinning the campaign. Solution
and harness builds, Core/Planning packages, Native AOT planning/encrypted-runtime
persistence and trimmed Agent.Server EF persistence smokes passed without warning
diagnostics under the unchanged documented publish exceptions. The harness also
checked six retained reference cases and 18 frozen CodeReview fixture contracts
without model or business transport calls. No frontend code changed.

The [redacted machine report](planner-omission-defaults-live-report.json) contains
the exact request identities, page lineage, partial reference assignments, persisted
accounting and freeze fingerprints. Full request/receipt payloads remain encrypted
in Schema-5 storage. The original failed campaign remains untouched.
