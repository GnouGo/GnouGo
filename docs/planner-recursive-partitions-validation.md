# Recursive decision-page output partitioning

The change starts from `8bcbb789c23e2ded23ae1ecdbc623afe26e145d2` on
`feat/deterministic-planner-v2`. Declaration semantics, Schema-5 storage, runtime
interfaces, reasoning profiles and token limits are unchanged.

## Captured failure and strict replay

The archived omission-default campaign stopped in session
`3d41ac712ef24278a8b655cd52c8beff`. Replay from revision 23 reproduced
`DECISION_OUTPUT_LIMIT` with one retained receipt and zero provider dispatches.
The original five-decision declaration page truncated, its two-decision child
completed, and its three-decision child truncated. The old mechanism incorrectly
classified both children as semantic corrections and refused the second split.
The session, original repair charges and encrypted journal remain unchanged.
See [the original evidence](planner-omission-defaults-live-validation.md).

Corrected strict replay stops at `REPLAY_EVIDENCE_REQUIRED`: the first new
`confirmation_scope` page identity has no historical receipt. It consumes zero
receipts and makes zero provider dispatches, verifying unchanged source state and
budget. Historical responses are not relabeled to authorize new page origins.
Synthetic partition fixtures are separate evidence, not replacement model receipts.

## Implementation and regressions

Pages record an origin, effective gate and ordered child identities. Truncation
recursively partitions canonical decision IDs at `floor(count / 2)`. Both child
identities exist before the first child reservation checkpoint. Completed children
are revalidated and merged in stable order against the original parent schema.
Corrupted, overlapping, missing or foreign membership fails closed.

Output partitions retain the parent's phase and semantic restriction. They consume
global model budgets but no semantic correction identity or repair allowance.
An original semantic correction is charged once; its partitions cannot obtain a
second correction for an invalid completed answer. Malformed response accounting
continues to use the response-contract gate. Terminal singleton truncation retains
the decision, page, request identity and canonical diagnostic location.

The synthetic `5 → 2+3 → 1+2` regression requires exactly five request scopes:
`abcde`, `ab`, `cde`, `c`, `de`. Both zero and five repair allowances succeed with
zero correction records or charges. The old implementation failed both variants.
Restart tests cover every reservation, receipt and acceptance checkpoint, including
the completed first sibling and nested split, with identical request/tree identities
and no duplicated calls or token accounting.

Additional cases cover semantic-correction partitions and their one original charge,
invalid correction stopping, singleton truncation, zero repair allowance, runtime
budget exhaustion, cancellation, unverifiable dispatch identity, corrupted scope,
redacted reporting and unknown historical origin. EF/KeyVault tests retain tenant
isolation and encrypted page state. The Native AOT smoke resumes a persisted partial
recursive tree and completes its remaining requests without redispatching siblings.

## Offline validation

- Solution build: zero warnings and errors.
- Solution tests: 3,155 passed, one optional provider test skipped; 754 planner and
  364 Agent.Server tests included.
- Benchmark harness build: zero warnings and errors.
- Harness selfcheck: six classifier/batch reference cases and all 18 CodeReview
  fixture checks passed with no model or business transport calls.
- Native AOT planning and encrypted persistence smoke: passed, including recursive
  partition restart, with the existing documented publish exceptions.
- Trimmed Agent.Server publish and EF/SQLite encrypted persistence smoke: passed.
- Core and Planning NuGet packages: built successfully. Both publishes and packages
  have zero warning/error diagnostics under the existing documented exceptions.

The frozen harness's 33 campaign tests also passed. Its first rebuild encountered
a transient reference-assembly file lock; a sequential rebuild passed without
source changes. Invoked frontend builds were warning-free. No production files
changed after the freeze.

## Single live Stage-1 result

**Accepted correct behavior; stopped during executable repair. Not `ValidWorkflow`.**

- Production commit: `0f4ebfdbf733687c0dc4d1dc9c4b89ca702df95e`.
- Harness freeze: `c0114d8`; campaign `schema5-recursive-partitions-20260913`.
- Session: `c7585c61b6de481e9ac72ffbf6cf5927`; final revision **105**.
- KeyVault-configured model: `gpt-5.5-2026-04-24`; every request used `low`.
- Unchanged input/output ceilings: 12,000/8,192; input dispatch target: 9,600.
- All 23 frozen binary hashes still matched after the run. The complete manifest,
  request fingerprints, page lineage and redacted counts are in the
  [machine-readable report](planner-recursive-partitions-live-report.json).

At revision **41**, independent behavior review verified exactly two inputs:
required `record`, and optional non-nullable `threshold` with source-owned omission
default `100`. There is exactly one required output, `classifiedResult`. The
rejected/high/standard classification rules and preservation of original `id` and
`amount` are retained. There are no invented write operations or extra outputs.
The three canonical declarations have zero aliases; three modifier assignments
retain six modifier evidence references, and one candidate is retired as a
non-declaration.

Exact behavior hash
`b9bf7b91998f31f2b4f267853cb3b8e4a2dddb25a756e131971401b02e57ab1e`
was accepted once, creating revision **42** and skeleton hash
`801c76d5c4893020567b603e28aafbc41108c630a843b0953eae3b8d0ad7469f`.
Acceptance made no new model call. A separate explicit advance continued the same
session into construction.

| Phase / gate | Verifiable calls | Reservations | Verified input / output tokens | Semantic repair reservations |
| --- | ---: | ---: | ---: | ---: |
| Intent / response contract | 5 | 5 | 7,099 / 9,164 | 0 |
| Confirmation scope / response contract | 2 | 2 | 6,824 / 722 | 0 |
| Declarations / response contract | 1 | 1 | 6,154 / 847 | 0 |
| Relationships / response contract | 1 | 1 | 470 / 288 | 0 |
| Construction schemas / response contract | 13 | 13 | 5,289 / 1,538 | 0 |
| Executable assignments / response contract | 3 | 3 | 1,504 / 132 | 0 |
| Repair / typed dataflow | 0 | 1 | Unknown for the failed dispatch | 1 |

There are **25 verifiable calls**, **26 reservations** and **one unverifiable
dispatch**. Known receipt usage totals **27,340 input / 12,691 output tokens**;
usage for the failed request remains unknown. The largest request was **8,059
estimated / 6,154 actual input tokens**, below the unchanged dispatch target.

The 23 decision pages comprise 20 initial pages, two output partitions and one
pending semantic-correction page. The report records 47 distinct exposed model
decision IDs, six executable holes resolved by model, and 16 executable-hole
exposures. Zero active executable holes have a deterministic resolution origin;
other engine decisions remain uninstrumented/unknown. There were no clarification
questions or answers. One typed/dataflow gate evaluation failed, consuming one
genuine semantic repair reservation; other phases consumed none.

### Partition result

One five-decision intent page reached 8,192 output tokens and split into **2 + 3**.
Both children completed; each has one request and receipt, with no detected
redispatch or semantic repair charge. The parent completed after validated stable
merging. No isolated decision reached the output ceiling. This live run exercised
one partition level; the nested `5 → 2+3 → 1+2` behavior is proven by the separate
synthetic regression and encrypted Native AOT restart smoke. An exact count of
live sibling-cache reuses is not instrumented and is not inferred.

### First blocker and offline replay

The last executable assignment failed typed/dataflow validation at
`/assignments/h_475858de62b51ed8` with `HOLE_BINDING_INELIGIBLE` and
`BUSINESS_INPUT_BINDING_MISSING` (`input:threshold`). Its computation did not
satisfy the operation's required dynamic threshold dependency. The staged
candidate and valid neighboring fields are retained. These are deterministic
construction findings, independent of the output-partition mechanism.

The existing exact-field repair reserved decision **`f_479f4d9a3d5288f1`** on page
`page_20131631a4b0d32408bfe6aa849e5398e58eac66d39c0eb521584efad552c3f4`.
The provider returned **`LLM_PROVIDER_SERVICEUNAVAILABLE`** before a verifiable
receipt was recorded. The terminal technical stop is classified as provider
execution failure with unknown usage, not business unsupportedness. It occurred
on the original semantic repair, not an output partition.

- Request fingerprint: `c5f4dcb05a902600b0cb21286d0472435f191880481ac796eb0b1e10842e727d`.
- Schema fingerprint: `2ead16004e599914ca96ec7932eaa512d96a90a87a5495ef003314f9021512db`.
- Context fingerprint: `687cd826234794ddc0b30257dd426b73c731f824799029ef28919d95d75e03f2`.
- Estimated input: 1,273; configured output ceiling: 8,192; actual usage: unknown.
- Exact request identity, payload and pending receipt state remain encrypted.

Replay from revision **102** reproduces the typed findings, reconstructs the exact
reserved repair identity and stops with `LLM_BUDGET_UNVERIFIABLE`: no receipt exists.
Replay from revision **101** consumes the last retained construction receipt and
reproduces the same findings, then stops at `REPLAY_EVIDENCE_REQUIRED` because the
audit-generated next reservation has a different revision. Neither audit
substitutes receipts; both verify unchanged source session, journal and budget,
and make zero provider dispatches. A separate attempted replay from revision 9 is
refused before replay because that intent checkpoint predates captured catalog
discovery; it is not counted as successful restart evidence.

No final artifact was produced or approved. The six independent execution cases
were **not run against this candidate**, since `FinalReview` was not reached.
Reference selfchecks are not candidate execution evidence. No further production
patch, provider retry, replacement session, reasoning change or Stage 2 occurred.
