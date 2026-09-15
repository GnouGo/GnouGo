# Scoped confirmation: one Stage-1 live validation

The single authorized Stage-1 session stopped technically. It did not produce a
valid terminal business outcome. No Stage 2, replacement session or reasoning A/B
request was started. Production remained frozen after the failure.

Production commit: `554374c21e00ed85af1e1b69daf15ba1d537e3d3`.
Campaign: `schema5-scoped-confirmation-20260913`.
Session: `78c92e2dedab4017bde7fefcca37c24e`, final revision **15**.
The [redacted report](planner-schema5-scoped-confirmation-live-report.json) includes
the frozen binary, scenario, catalog, policy and fixture fingerprints, page identities
and accounting. Private requests, receipts and snapshots remain encrypted.

| Measure | Result |
|---|---|
| Typed outcome / execution status | None / `stopped` |
| First blocker | `CONFIRMATION_POLICY_CONFLICT`, capabilities |
| Canonical location | `/preparation/policies/@ob_4da13e7cace3eb0b` |
| Model | KeyVault-configured OpenAi / `gpt-5.5-2026-04-24` |
| Effective reasoning | `low` for every request |
| Completed model calls / reservations | 4 / 4 |
| Unverifiable dispatches | 0 |
| Decision pages | 4 completed: 3 intent, 1 confirmation scope |
| Correction / split pages | 0 / 0 |
| Model decision IDs | 25 distinct; no executable-hole exposures |
| Engine-resolved executable decisions | 0; other engine decisions unknown |
| User clarification questions / answers | 0 / 0 |
| Repairs | 0 in intent and confirmation scope; no later phase reached |
| Actual input / output tokens | 9,156 / 1,849 |
| Largest estimated / actual input request | 6,765 / 4,090 tokens |
| Input ceiling / dispatch target / output ceiling | 12,000 / 9,600 / 8,192 |
| Independent execution | Not run: final review was not reached |
| Approved artifact hash | None |
| Stages 2 and 3 | Not run |

Intent used three calls, 5,066 input tokens and 1,428 output tokens. Confirmation
scope used one call, 4,090 input tokens and 421 output tokens. The response contracts
passed. The failure arose in deterministic preparation consistency validation;
the existing per-gate failure ledger has no entry for that technical stop, so no
failure-gate attribution is inferred. No executable or independent-execution model
requests occurred.

## First blocker

The unrelated YAML-review prohibition is now represented as `forbid_interaction`
with no matching operation targets. It neither conflicts with write confirmation
nor denies the shared human-input executor.

The new interpretation introduced two different errors in the other host clause:

- It promoted the policy fragment “external write” into a required operation,
  although the classifier scenario requests only local computation.
- It classified “with zero writes after rejection” as `confirmation_forbidden`,
  alongside the same clause's correctly classified confirmation requirement.

The subsequent completed scope receipt assigned both permission rules to the write
effect class, with identical applicability. Both therefore targeted the invented
operation `ob_4da13e7cace3eb0b`. The conflict checker correctly detected an overlap
between these stored rules, but those rules do not faithfully represent the policy.
This is a technical interpretation/grounding failure, not a genuine user-policy
contradiction and not evidence of unsupported business behavior.

The response domain for an initial `confirmation_forbidden` classification permits
forbidding confirmation or an interaction. It cannot represent the rejected branch's
zero-write condition or retire the incorrect classification. Complete-clause context
alone did not prevent that failure. A future correction needs to distinguish a policy's
conditional subject from a requested operation and preserve rejection semantics before
granting classification authority. No further production patch was applied in this run.

## Replay and validation boundary

Strict offline replay from revision **11** consumed the exact completed scope receipt
and reproduced the same code and location in one advance. It made **zero provider
dispatches**, substituted no response, and verified that the saved session, journal
and cumulative budget remained unchanged.

Before the live start, the solution build and all offline tests passed: **3,049 passed,
0 failed, 1 optional provider-backed test skipped**, including 660 planner, 841 Core
and 352 Agent.Server tests. Core and Planning packages built. The published osx-arm64
Native AOT planning/encrypted-persistence smoke and trimmed Agent.Server EF persistence
smoke passed without warnings under the documented existing exceptions. The harness
verified six retained classifier/batch families and all 18 CodeReview fixture contracts
offline. These prerequisite checks are not execution of the failed live artifact.

The original archived failure also remains unchanged. Its baseline replay reproduced
the former global conflict; corrected strict replay stops at `REPLAY_EVIDENCE_REQUIRED`
because the historical run has no scope receipt. New synthetic regression answers are
explicitly labeled and were never substituted into that archive.

This campaign has consumed its only authorized start. Further live validation requires
separate authorization and a new frozen campaign.
