# Frozen declaration-constraint Stage-1 rerun

Campaign `schema5-declaration-constraints-stage1-rerun-1` consumed its single
authorized fresh start. Production remained frozen at
`9667f600135ee0fd13dba6c5cc901f545ee79452`; harness commit `1d6e1ad` changed only
campaign/comparison identity and documentation. No production or fixture logic changed.

The [manifest](planner-declaration-constraints-rerun-manifest.json) preserves the
configured `gpt-5.5-2026-04-24`, all-low profiles, scenario, catalog, host policy and
budgets: 12,000 input, 9,600 dispatch target, 8,192 normal output, bounded 16,384
singleton escalation, concurrency four and five repairs per workflow/gate.
Only the harness assembly and its derived fixture fingerprint changed. Production
DLLs and fixture-source hashes match the prior campaign.

## First meaningful blocker

Session `a6fb28068c654132909534280e487410` stopped at revision **23** with
`DECLARATION_GROUNDING_UNRESOLVED` at `/declarations/@$plan`, before BehaviorReview.
This run produced six verifiable model responses and no provider transport failure.
The failure is **planner/model convergence**, not infrastructure reliability.

Two candidates from the clause declaring `record` both selected `distinct_input`,
scope `main`, required presence and the same public-name reference. That reference
resolves exactly to **`input`**, rather than `record`. One candidate's selected
interpretation span was only the word `input`; the other covered a longer part of
the same declaration. Combined root validation rejected the duplicate public name:
“Distinct declarations claim the same public name and scope. Identity must be
adjudicated explicitly.”

The completed root page also proposed optional `threshold`, required output
`classifiedResult`, and one deferred threshold attachment. These are staged
proposals, not committed canonical declarations or reviewed ports. No omission
default or constraint attachment was materialized.

The enum clause and preservation clause were classified as `declaration_constraint`.
This is positive interpretation evidence for the preceding correction, but attachment
and canonical behavior were not reached. It does not establish successful declaration
convergence. No root, attachment, schema, prompt or repair was patched in this task.

The [redacted blocker evidence](planner-declaration-constraints-rerun-blocker.json)
records the issued IDs, staged names, exact request/receipt fingerprints and relevant
constraint references. Full source requests and responses remain in encrypted storage.

## Measurements

| Measurement | Result |
|---|---|
| Typed outcome / execution status | No typed business outcome / stopped |
| Verified calls / reservations / unverifiable dispatches | 6 / 6 / 0 |
| Input / output tokens | 17,952 / 3,023 |
| Reasoning tokens | 1,212, included in output tokens |
| Largest estimated / actual input request | 8,103 / 5,644 |
| Decision pages / distinct model decision IDs | 6 / 28 |
| Partitions / singleton escalations | 0 / 0 |
| Semantic repairs by gate | 0 at every gate |
| Deterministic / model-resolved executable holes | 0 / 0; construction not reached |
| Other engine decisions | Unknown; uninstrumented |
| User clarifications | 0 |
| Canonical declarations / committed attachments | None |
| Accepted behavior / skeleton / final / approved hashes | Absent |

| Phase; owner `$plan`; gate `response_contract` | Calls | Input | Output | Repairs |
|---|---:|---:|---:|---:|
| Intent | 3 | 5,521 | 1,416 | 0 |
| Confirmation scope | 2 | 7,021 | 642 | 0 |
| Declaration roots | 1 | 5,410 | 965 | 0 |

All six independent execution cases—accepted, rejected, boundary, omitted default,
invalid input and null threshold—are **not run**. No behavior or artifact was approved.
The [campaign report](planner-declaration-constraints-rerun-report.json) contains
per-request fingerprints, usage, page lineage and individual fixture statuses.

## Offline validation and archive integrity

Before dispatch, all **3,264 solution tests** passed across 29 projects, with one
optional live-provider test skipped. The 58 focused harness tests and both reference
selfchecks passed without model calls. The harness build was warning-free and used
`BuildProjectReferences=false`. Existing package, Native AOT planning/encrypted
restart and trimmed Agent.Server persistence evidence remains applicable to the
unchanged production binaries. See the [offline evidence](planner-declaration-constraints-rerun-offline.json).

Strict replay from revision **20** reproduced the same declaration stop using
**one retained receipt, zero provider dispatches and no synthetic substitution**.
Replay verified that the source snapshot, journal rows and budget remained unchanged.

After the run and replay, every frozen production/harness binary and all archived
campaign accounting matched their pre-run fingerprints, including the prior
unverifiable transport request. No request was retried or imported. No replacement
session, Stage 2, production patch or reasoning change followed this blocker.
Complete Stage-1 success remains unproven.
