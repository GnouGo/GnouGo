# Source authority: one Stage-1 live validation

The correction passed capability/policy preparation. The single live session reached
behavior review, where independent review rejected a new declaration-grounding defect.
It did **not** reach a valid terminal business outcome. Behavior was not accepted;
no executable construction, execution fixtures, Stage 2, replacement session or
reasoning A/B request followed. Production code and frozen binaries stayed unchanged.

Production commit: `25566657965b39774fae16f7a553622db66597dc`.
Campaign: `schema5-source-authority-2556665`.
Session: `3f11a9cfe79c41ee85383c81649d0923`, revision **32**.
The [redacted report](planner-schema5-source-authority-live-report.json) contains
binary hashes, frozen inputs, page/request identities, accounting and replay evidence.
Private snapshots, requests, receipts and the independent review remain encrypted.

| Measure | Result |
|---|---|
| Typed outcome | None; Stage 1 did not pass |
| Planner status / campaign status | `behavior_review` / `blocked` |
| Review finding | `BENCHMARK_BEHAVIOR_DECLARATION_MISMATCH` |
| Finding authority | Independent benchmark behavior review; not a production diagnostic |
| Model / reasoning | KeyVault OpenAi `gpt-5.5-2026-04-24`; every request `low` |
| Verifiable calls / reservations / unverifiable dispatches | 6 / 6 / 0 |
| Decision pages | 6 completed: intent 3, confirmation scope 2, intent relations 1 |
| Correction / split pages | 0 / 0 |
| Model page decisions | 39 distinct IDs |
| Executable model decisions / hole exposures | 0 / 0 |
| Engine-resolved executable decisions | 0; other engine decisions unknown |
| User clarification questions / answers | 0 / 0 |
| Repairs | 0 in every reached phase |
| Actual input / output tokens | 13,189 / 1,638 total |
| Largest estimated / actual input request | 8,053 / 5,546 tokens |
| Input ceiling / dispatch target / output ceiling | 12,000 / 9,600 / 8,192 |
| Independent execution / approved artifact hash | Not run / none |
| Stages 2 and 3 | Not run |

Intent used three calls and 4,936 input / 831 output tokens. Confirmation scope used
two calls and 6,871 / 455 tokens. Intent relations used one call and 1,382 / 352 tokens.
No behavior-model, construction, scenario, semantic-review or independent-execution
model request occurred. The production failure ledger has no finding for the independent
review, so the report attributes it separately instead of fabricating a planner gate failure.

## Verified correction

No host-policy source introduced an operation. The host clause's required confirmation
and rejection consequence produced one `require_confirmation` policy, retaining both
governing obligations and their references. The rejection classification is explicitly
`rejection_condition`, linked to that permission. The write scope has zero targets
because this classifier requests no external writes; no confirmation step was created.

The unrelated YAML-review prohibition became an independently scoped `forbid_interaction`
with zero operation targets. It did not deny the shared human-input executor. There was
no confirmation conflict. The conflict checker itself is byte-for-byte unchanged from
`2a9b0d2`; the fix changes which grounded facts reach it.

## First new blocker

The frozen classifier declares two inputs: required `record` and optional `threshold`,
whose omission supplies 100. The reviewed behavior contains three input ports. Two
obligations select overlapping fragments of the same threshold clause:

- `ob_574949bb388aa4bc`: boundaries `b0`–`b10`.
- `ob_a6abe9c566a3340e`: boundaries `b2`–`b6`.

Both were returned as `business_input` in the completed intent decision
`interpret_8b4cf54bde1749d88726cc5b`. They share governing clause
`r_b1de367e616ace3dcd1a6963`, but their distinct span identities became distinct ports.
The first located review finding is
`/behavior/workflows/@main/inputs/@input_a6abe9c566a3340e`.

The same reviewed plan also exposes four outputs instead of the one declared
`classifiedResult`: descriptive clauses and preservation requirements became additional
output declarations. Source authority and reference validity passed, but they do not
prove that every selected fragment defines a distinct business declaration.
`PlanningBehaviorDecisions.Assemble` currently materializes one port per declaration
obligation; its validation did not identify this semantic duplication before review.
This is a declaration-grounding/admission gap, not a confirmation conflict, missing
user decision, unsupported business operation, provider failure or token-limit failure.

Acceptance was withheld for exact behavior hash
`6f5fd95ea2ff393e14f25de6ba19eed8931990a9e853708cdc16926897fde88e`.
This is a **behavior-review hash**, not an approved executable artifact hash.
No revision prompt, fabricated answer or production correction was submitted.

## Offline replay and preservation

Receipt-only replay from revision **27** reaches the same behavior-review checkpoint
in one local advance. It needs zero new/replayed receipts because the required decision
pages are already complete; it dispatches zero provider requests and verifies unchanged
session, journal and budget.

An isolated audit invokes the frozen pure behavior assembler with the retained snapshot
and completed decisions. It reproduces the exact behavior JSON/hash, three inputs and
four outputs. Its review finding is stored through the public encrypted KeyVault record
API under the campaign's `:review:1` key. Only campaign review metadata changed to
`blocked`; revision 32, requests, receipts, budgets and the frozen binaries remain intact.
The original harness now refuses further advancement. Two local audit dependency-loading
failures occurred before any storage mutation; neither involved a planner/provider
request. The audit loader was corrected outside the frozen binaries.

The prior archived campaign remains untouched. Before correction, replay of session
`78c92e2dedab4017bde7fefcca37c24e`, revision 11, reproduced its confirmation conflict
using one retained receipt. Corrected replay stops before that receipt with
`INTENT_SOURCE_AUTHORITY_UNPROVEN`: historical obligations lack current grounding proof.
No synthetic corrected response was substituted into either replay.

## Validation and freeze

Before live dispatch, **3,077 solution tests passed**, including **688 planner**,
**841 Core**, and **352 Agent.Server** tests. One optional provider-backed test was
skipped. Solution/harness builds, Core and Planning packages, the osx-arm64 Native AOT
planning/encrypted-persistence smoke and trimmed Agent.Server EF persistence smoke
passed without warnings under existing documented exceptions. The offline harness
verified its six reference cases and all 18 CodeReview fixture contracts without model
calls. Those checks are not execution of an artifact from this failed campaign.

The scenario, catalog, policy, configured model, evidence and budgets match the prior
campaign. The execution-fixture fingerprint incorporates the benchmark assembly MVID,
so rebuilding its campaign pin changes that derived hash; fixture code and cases are
unchanged. All new hashes are preserved in the campaign manifest. Schema and encrypted
namespaces remain 5; internal preparation proof is version 2.

This campaign's single authorized start is consumed. Further production changes or
live validation require a new instruction.
