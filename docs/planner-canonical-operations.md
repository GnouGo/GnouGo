# Canonical runtime-operation admission

This document retains the operation-proof-v1 implementation and its failed frozen campaign as historical evidence. The current operation-proof-v2 boundary and its separate diagnostics are documented in [runtime admission](planner-runtime-admission.md). The campaign below must not be resumed.

Operation interpretation labels are preliminary evidence. The existing bounded decision-page coordinator runs `intent_operations` before confirmation scoping in both inferred and explicit capability preparation. It examines complete requested clauses even when initial interpretation labelled them only as policies.

Each clause selects issued action boundaries and typed kinds: local processing, external read/write/execute, resource lifecycle, cleanup, or human interaction. It may instead establish no operation or stop unresolved. Up to four actions can be represented in one clause decision; insufficient capacity must stop unresolved rather than omit actions. Responses contain no invented descriptions, operation identities or contracts.

Only requested behavior can establish new actions. Constraints-only sources have no action-creation alternative. Existing behavior requires an issued baseline node with compatible Flow executor semantics. Baseline source references remain tied to the supplied baseline when new behavior is approved.

Clauses run in stable source order. Later clauses can reuse validated staged canonical actions while keeping their additional governing evidence. Reuse fixes kind, requiredness and baseline ownership. Matching kinds or descriptions alone do not establish identity. Distinct requested occurrences remain distinct. Primary action evidence and cross-clause governing references are stored separately, then resolved from source for capability and behavior contexts. Conditions, fallbacks, declaration constraints and policy obligations retain their own authority.

Requested action identity derives from a versioned source anchor, independent of preliminary candidate IDs. Baseline identity derives from its workflow/node coordinates; evidence and contract fingerprints remain separate. All assignments and revision/supersession decisions must validate before admitted obligations commit. Partial pages grant no execution authority. Zero admitted operations retain `INTENT_OPERATION_UNRESOLVED`, including explicit capability preparation; ambiguity does not become a clarification or Unsupported.

Operation proof version **1** joins source proof **4** and declaration proof **5** in Schema-5 storage. Missing or stale proof requires explicit reassessment without resetting budgets or modifying archives. Existing reservations, receipts, finite corrections, recursive partitions and singleton escalation remain authoritative. Completed pages are reused after restart; unverifiable requests never redispatch.

## Evidence and validation

The retained session `d1d249a22d754ef680c646f00881ee32`, revision 22, reproduces the original zero-operation blocker using one receipt, zero provider calls and unchanged encrypted state. The sanitized `operation-admission-stage1.json` fixture retains its classifications and correct declaration assignments. New admission responses in regression tests are explicitly synthetic and are never substituted for historical receipts.

The fixture establishes one classification action, reuses it for the runtime classification rules, and retains exactly `record`, optional `threshold` with omission default `100`, and `classifiedResult` with enum/preservation attachments. Generic tests cover multiple actions, policy authority, baseline proof, malformed responses, finite correction charging, source/revision changes, restart and unverifiable dispatch.

The campaign `schema5-canonical-operations-stage1-1` authorizes one fresh Stage-1 session only after offline checks. It retains the configured model, all-low profiles, 12,000/9,600 input ceilings, 8,192 normal output and bounded 16,384 singleton escalation. Full success requires exact behavior acceptance, current mandatory validation, six independent execution cases and exact artifact-hash approval. A new meaningful blocker ends the campaign without patches, replacement sessions or Stage 2.

## Frozen Stage-1 result

Production was frozen at `4941985d0d41b8a4e8d9596621a8f202dd2bfa86`, with harness pin `862cd49`. Offline validation passed: **3,324 tests**, including **875 planner**, **855 Core**, **396 Agent.Server** and **67 integration** tests; one optional live test was skipped. All **59** focused harness checks, reference fixture selfchecks, package builds, Native AOT planning/encrypted-restart and trimmed Agent.Server persistence smokes passed. Builds/publishes were warning-free under the existing exceptions. The final Agent publish used the documented persistence-only options; an earlier unrelated browser-bundle download was interrupted.

Exactly one fresh session started: `984756f1099448a3856e6280d53ebba3`.
It stopped automatically at revision **34**, before complete operation admission,
with **`DECISION_OUTPUT_LIMIT`**. No typed business outcome was emitted.

| Measurement | Result |
|---|---:|
| Verified calls / reservations / unverifiable dispatches | 10 / 10 / 0 |
| Input / output / reasoning tokens | 11,036 / 42,619 / 41,564 |
| Decision pages / distinct exposed decision IDs | 10 / 21 |
| Output partitions / singleton escalations | 0 / 3 |
| Successful escalations / terminal escalation | 2 / 1 |
| Semantic repairs, every workflow/gate | 0 |
| Executable holes resolved deterministically / by model | 0 / 0 |
| Other engine decisions | Unknown |
| User clarifications | 0 |
| Largest estimated / actual input | 3,224 / 2,031 |

The terminal singleton, `operation_r_8f9f6d01e24b42f645c7795e`, assessed:

> Return classifiedResult:{id:string,amount:number,category:string}, all members required.

Its estimated input was **1,623** tokens, actual input **890**, and estimated answer **512**. Both the 8,192 and 16,384 attempts exhausted their output ceilings entirely in provider-reported reasoning tokens, returning no assignment. This is verified output exhaustion, not an unverifiable transport failure. No higher limit or reasoning change was attempted.

Inspection also found an earlier **staged semantic error**: the complete request
“Create one reusable workflow classifying a single record.” was assigned
`resource_lifecycle`, required, with no reuse target. This treats authoring the
workflow as an action performed by that workflow. Source ownership alone does
not establish runtime execution scope. The response passed its bounded schema,
but remained staged; no operation set was committed. The omission-default clause
also received a staged `local_processing` assignment. These are retained model
responses, not synthetic corrected evidence.

Canonical operations and declarations were **not committed** in this live run.
Behavior review/acceptance, construction and final review were not reached.
Behavior, skeleton, final-artifact and approval hashes are absent. All six
independent generated-workflow cases—accepted, rejected, boundary, omitted
default, invalid input and null threshold—are **not run**. Passing reference
selfchecks are separate offline evidence and do not constitute live success.

Strict terminal replay from revision 30 reproduced the stop using **two retained
receipts**, **zero provider dispatches**, and unchanged source state. Earlier
admission replay from revision 12 reused two receipts, then stopped at
`REPLAY_EVIDENCE_REQUIRED` because the next request identity did not match retained
evidence; it substituted no response. All 22 frozen
production DLLs, production sources and archived campaign accounting were
rechecked unchanged. No production patch, replacement session or Stage 2 followed.

Exact lineage, requests, receipts and usage are retained in the encrypted journal.
Redacted artifacts: [campaign report](planner-canonical-operations-report.json),
[manifest](planner-canonical-operations-manifest.json),
[blocker and replay evidence](planner-canonical-operations-blocker.json), and
[offline validation](planner-canonical-operations-offline.json).
