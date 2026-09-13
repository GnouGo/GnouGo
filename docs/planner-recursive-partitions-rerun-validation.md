# Frozen Stage-1 rerun

Campaign `schema5-recursive-partitions-rerun-1` consumed its single authorized
start. It stopped before BehaviorReview on a verified singleton
`DECISION_OUTPUT_LIMIT`. No replacement session, Stage 2, production fix,
reasoning experiment or provider retry followed.

This is a technical stop, not `ValidWorkflow`, `NeedUserClarification` or
`Unsupported`. The prior threshold construction finding and its repair were
not reached. The new result therefore supplies no evidence about whether that
repair now converges.

## Frozen implementation and prerequisites

- Production commit: `0f4ebfdbf733687c0dc4d1dc9c4b89ca702df95e`.
- Harness commit: `eef5823` (campaign identity, observation, reporting and tests only).
- Harness DLL SHA-256: `d2a58aa1540e21edf790a54c3806534762007c4ca01c8ca3da2a01395a462ae7`.
- KeyVault-configured model: `gpt-5.5-2026-04-24`; every request used `low`.
- Limits unchanged: 12,000 input, 9,600 dispatch target, 8,192 output,
  concurrency four, five repairs per workflow/gate; global budgets unchanged.
- Scenario, catalog, policy and frozen evidence fingerprints match the previous
  manifest. All 22 production DLLs match their previous hashes before and after
  execution; the production source diff is empty.
- Fixture source, inputs and assertions are unchanged. The Stage-1 fixture hash
  changed from `75a5b7757705044dc774d561f4c7ecd2eee976128aa14b612ea8e4ebae183bfe`
  to `179f0f54aa279419ea4c4b52cfb57a79dda1b1f496c7d17c21f460933f5e30b0`
  because its derivation includes the harness assembly identity.

The offline solution suite passed **3,164 tests**, with one optional test skipped,
zero failures and no warning lines. All **42 focused harness tests** passed.
The harness was built without rebuilding project references, with zero warnings
or errors. The reference selfcheck verified six classifier/batch cases and 18
CodeReview fixture cases without model or business transport calls. These are
reference checks, not execution of the new session's nonexistent artifact.

The existing successful package, Native AOT planning/encrypted persistence and
trimmed Agent.Server EF persistence evidence remains applicable to the unchanged
production binaries; those publishes were not repeated. See the
[previous validation record](planner-recursive-partitions-validation.md).

The new observer tests cover permitting the initial threshold repair, continuing
after validated resolution, stopping after a verified unresolved/rejected repair,
receipt recovery, partition waiting, unverifiable stopping, persisted observation
and matching by diagnostic rule/canonical field rather than message. Existing
harness tests retain single-start, Stage-2 refusal and exact-hash/fixture guards.

## Live result

Session: `7a27ef2963ae49589c9b6c4196644fe7`.

| Measurement | Result |
|---|---|
| Campaign / planner status | Blocked / stopped |
| Terminal revision / phase | 21 / capabilities |
| Typed business outcome | None; technical stop |
| Accepted behavior / skeleton hashes | None; review not reached |
| Final / approved artifact hashes | None |
| Verified calls / reservations | 6 / 6 |
| Unverifiable dispatches | 0 |
| Input / output tokens | 17,887 / 16,771; all dispatch usage known |
| Largest input request, estimated / actual | 8,773 / 7,577 tokens |
| Decision pages | 6 initial: 5 completed, 1 stopped |
| Output partitions / semantic repairs | 0 / 0 |
| Distinct model decision IDs exposed | 29; 28 on completed pages, 1 truncated |
| Deterministic / model-resolved executable holes | 0 / 0; construction not reached |
| Other engine decisions | Unknown; not instrumented |
| Canonical declarations | Not committed; adjudication incomplete |
| User clarifications / answers | 0 / 0 |

All requests belong to `$plan` and the `response_contract` gate:

| Phase | Calls | Input tokens | Output tokens | Repair reservations |
|---|---:|---:|---:|---:|
| Intent | 3 | 5,223 | 1,859 | 0 |
| Confirmation scope | 1 | 3,813 | 517 | 0 |
| Declaration adjudication | 2 | 8,851 | 14,395 | 0 |

## First blocker and receipt evidence

The final request adjudicates the candidate declaration in the clause defining
the category enumeration: `category has exactly the values rejected, high, standard.`
Its preliminary interpretation is `business_output`. No completed assignment was
returned, so this run cannot establish whether its final semantic classification
would be correct.

- Code: `DECISION_OUTPUT_LIMIT`.
- Canonical location: `/decisions/declarations_r_f62567b1da3aa9193fdaad62`.
- Decision phase: `intent_declarations`; owning planner phase: `capabilities`.
- Page: `page_8671f487763176cf6eaa82f66798fc545290df8f0ec661a18756da0d6ed18018`.
- Origin: initial page; one decision, no parent or partition children.
- Request revision: 18; durable replay checkpoint: 20; stopped revision: 21.
- Input: 1,914 estimated / 1,274 actual tokens; estimated answer: 218 tokens.
- Receipt: `output_limit`, 8,192 output tokens. Provider usage attributes all
  8,192 to reasoning tokens. The page contains no usable candidate.
- Request fingerprint: `2d289a0514c308703e35bae582087c247d2e1b66b8f81097fcd42c978b7abba7`.
- Response-schema fingerprint: `6b07a8952c358aa5914da3068b42ea214a24324d11a77f4739df9a2299498f00`.
- Context fingerprint: `e0f44c563c65ed59d0fb5cb672bcb36a89f59f34ea209daa707b3fdaa5bfb35e`.
- Receipt fingerprint: `c851b7af0a1be7d527a54a54d77901fad3cd1bde5525f55086f4e7079b741b3c`.

Classification: **model output exhaustion on an indivisible semantic decision**.
This is neither an input-sizing failure nor an unverifiable service failure.
The recursive partition mechanism correctly has no smaller issued decision to
dispatch. The exact request and receipt remain in encrypted Schema-5 storage.
No output ceiling or reasoning setting was changed.

Receipt-only replay from revision 20 reproduced the same code and canonical
location in one local advance, consuming one retained receipt with **zero provider
dispatches** and unchanged persisted state. No synthetic response was substituted.

## Execution and preservation

| Independent case | Result |
|---|---|
| accepted | Not run |
| rejected | Not run |
| boundary | Not run |
| omitted default | Not run |
| invalid input | Not run |
| null threshold | Not run |

No artifact existed to execute or approve. The campaign remains blocked.

The prior campaign and session `c7585c61b6de481e9ac72ffbf6cf5927` were inspected
through public KeyVault record APIs. Before/after comparison found all **159**
owned records identical, including timestamps: 106 snapshots, one budget,
26 requests, 25 receipts and one campaign manifest. No historical request was
resumed and no allowance was refunded. Source, fixture and production DLL checks
also passed after the new run and offline replay.

The [redacted machine-readable report](planner-recursive-partitions-rerun-live-report.json)
contains the full manifest, all request identities/usage/fingerprints, replay result,
fixture-source hashes and archive audit fingerprint. Private prompts, responses,
configuration and credentials remain outside the report.
