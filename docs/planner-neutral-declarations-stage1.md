# Frozen direction-neutral declaration Stage-1 campaign

Campaign `schema5-neutral-declarations-stage1-1` started exactly one fresh session,
`36cc6d8ce339488184bd30ea22c6c04f`, on production commit
`87ed861284729dfa2475bd5c25546b76339d7453`. It stopped at independent behavior
review, revision 44. The planner remains in `BehaviorReview`, with no terminal
typed outcome and no production technical diagnostic. This is not `ValidWorkflow`.

The campaign rejection is `BENCHMARK_OUTPUT_MEMBER_CONSTRAINT_UNATTACHED` at
`/declarations/@decl_0ff6237704faf7629de8caa1`. The requested category enum was
classified as an `explicit_value` obligation (`ob_4222548667785c98`), outside
declaration adjudication. Its clause reference
`r_c9324efd644832192a32f24b` was not attached to the canonical `classifiedResult`
output. That output has no modifier references and only its original declaration
clause. The independent campaign check requires this association before acceptance.

The enum remains present in the obligation list and overall behavior text. The
rejection establishes a missing canonical association under the campaign's review
criterion; it does not establish that executable generation would lose the enum
or produce incorrect runtime results. No executable construction was authorized.

| Canonical declaration | Direction | Presence | Omission default |
|---|---|---|---|
| `record` | Input | Required | None |
| `threshold` | Input | Optional, non-nullable evidence retained | `100` |
| `classifiedResult` | Output | Required | None |

There are exactly two inputs and one output. No `category` or descriptive/member
port was created. The default is attached through one omission-default modifier.
The runtime classification/fallback and original-field preservation remain in
the governing source evidence and behavior text.

The unaccepted behavior candidate hash is
`571136c54466231d8741f458abb27e96b8035aee0e78e2ce878adfa0eb0bece1`.
Accepted behavior, skeleton, final artifact and approved artifact hashes are all
absent. The harness recorded the rejection without modifying the planning revision.

| Measurement | Observed |
|---|---:|
| Verified model calls / reservations | 9 / 9 |
| Unverifiable dispatches | 0 |
| Input tokens | 21,024 |
| Output tokens, including reasoning | 13,284 |
| Provider-reported reasoning tokens | 9,454 |
| Decision pages / distinct model decision IDs | 9 / 45 |
| Output partition pages | 0 |
| Singleton output escalations | 1 |
| Semantic corrections / repair reservations | 0 / 0 |
| Deterministic / model-resolved executable holes | 0 / 0; construction not reached |
| Uninstrumented engine decisions | Unknown |
| User clarifications | 0 |
| Largest estimated / actual input | 8,234 / 5,819 |

| Phase; all work owned by `$plan`, response-contract gate | Calls | Input | Output | Repairs |
|---|---:|---:|---:|---:|
| Intent | 3 | 5,309 | 1,244 | 0 |
| Confirmation scope | 2 | 8,797 | 883 | 0 |
| Declarations | 3 | 5,420 | 10,243 | 0 |
| Relationships | 1 | 1,498 | 914 | 0 |

The singleton threshold omission-default attachment exhausted 8,192 tokens, all
reported as reasoning, at 836 actual input tokens. Its one 16,384-ceiling request
returned a valid assignment using 1,732 output tokens. It retained the original
decision and evidence and consumed no semantic repair allowance. Recursive output
partitioning was not exercised in this live session.

All six independent generated-workflow cases are **not run**: accepted, rejected,
boundary, omitted default, invalid input and null threshold. Construction,
threshold executable repair, mandatory executable validation and final approval
were not reached. Stage 2 and replacement sessions were not started.

Before dispatch, 3,233 offline tests across 29 projects and all 52 focused harness
tests passed. Solution and harness builds were warning-free. The unchanged
classifier/batch reference selfcheck passed six cases, and the frozen CodeReview
reference selfcheck passed 18 cases, with no model/business calls. These reference
checks are not the six generated-workflow live execution cases. Existing package
and published smoke evidence is retained in
[the offline validation report](planner-neutral-declarations-offline.json).

Strict offline replay from revision 18 reused one receipt, then stopped with
`REPLAY_EVIDENCE_REQUIRED` when subsequent request revision identity differed.
Replay from the exact later checkpoint at revision 30 reused one retained receipt
and reproduced `BehaviorReview`, with no graph or production diagnostics. Both
made zero provider calls and verified unchanged saved state, budget and journal
rows. No synthetic answer replaced historical evidence.

Production, frozen production/harness binaries and all four fixture sources were
verified unchanged after the campaign. Archived campaign, snapshot, request,
receipt and budget record fingerprints also match their pre-run values. Only
harness controls/tests and this redacted report were changed; no production fix
or post-failure harness patch was made.

The [campaign manifest](planner-neutral-declarations-stage1-manifest.json) records
binary/source fingerprints, model, unchanged policy/scenario/catalog hashes and
budgets. The fixture's derived hash changed because it includes the harness
assembly identity; its source, inputs and assertions did not change. The
[detailed report](planner-neutral-declarations-stage1-report.json) contains exact
request/receipt fingerprints, escalation lineage, canonical proof IDs, usage and
both replay results. Private request/response payloads remain encrypted in the
existing KeyVault storage.
