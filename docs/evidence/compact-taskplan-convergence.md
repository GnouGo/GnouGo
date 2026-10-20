# Compact TaskPlan convergence

Business acceptance is incomplete. PR #120 stays Draft. None of the latest
Amazon, Collections or Review PR diagnostics reached execution. Checkpoints,
compilation and scoped reviews do not establish business success. The gated
18-run baseline/candidate comparison has not started.

## Latest diagnostic and corrections

Frozen cohort `compactdba4ed26` retained the pinned model, medium reasoning,
3k/8k/4k input targets, 6k/16k/8k complete-request ceilings and
16,384/8,192/2,048 navigation/draft/Judge output reservations. Amazon stopped at
four corrections after 25 known completions, with no checkpoint or execution.
Its final operation URL still omitted the caller's search input; the local Judge
correctly rejected that omission. Collections and Review PR were **not started**
in this cohort.

| Diagnostic | Calls / corrections / checkpoints | Planning time | Known cost | Outcome |
| --- | ---: | ---: | ---: | --- |
| Amazon `ba4ed26` | 25 / 4 / 0 | 443.791 s | €1.026837 | Correction allowance exhausted; query absent from operation arguments |
| Collections `585203a` | 2 / 0 / 0 | 314.015 s | €0.017644 | HTTP 500; exact request/run permanently abandoned with full reservation |
| Review PR `585203a` | 10 / 1 / 0 | 341.672 s | €0.539829 | Correction exhausted 8,192 reasoning tokens with zero visible content |

Amazon used 71,056 provider input, 26,674 output and 21,180 reasoning tokens. No
response was empty and no new uncertainty was introduced. All 371,096 constructed
request bytes reconcile: 25 admitted calls plus one prepared-only envelope, not
an explicit admission rejection. Generation, Judge and correction peaks were
11,115, 4,548 and 11,305 conservative input tokens. All fit their captured ceilings.
[Safe phase measurements](compact-taskplan-ba4ed26.json) retain complete-request
p50/p95/maximum, reconciled components, receipt usage, costs and latency.

The trace exposed an ambiguous existing action: `reselectStep` is a reason string,
but its fresh schema did not describe it and the model returned the old step ID.
The host faithfully preserved that value, the original requirement and rejected
step evidence; no context loss was found. The follow-up clarifies the fresh field
with a precise obstacle and any missing prerequisite. It adds no state, protocol
or validation rule and does not guarantee convergence.

MCP retrieval guards worked: a malformed address was rejected, and the corrected
batch expanded the exact required input grammar. Empty tool arguments and the
later missing search input were genuine defects, not false positives from open
scopes. These guards remain unchanged.

Earlier retained evidence led to targeted corrections for repair diagnostic
visibility, caller declarations in requested root evidence and interpretation
semantics. Interpretation uses its objective/input/result-type contract and needs
no separate expression. A genuine result-bound contradiction still prevents
checkpoint acceptance. Empty execution aggregates now report unknown correctness;
existing gates and stored run records remain unchanged. These corrections still
need complete live business acceptance. [The preceding cohort](compact-taskplan-585203a.json)
retains its failures and exact authorized Collections closure.

## Retained experiments and acceptance limits

The [08ad5db diagnostics](compact-taskplan-08ad5db.json) stopped on missing context,
evidence corrections and an uncertain HTTP 500. The next
[Amazon attempt](compact-taskplan-cd174ea.json) returned HTTP 504 on a small first
intent request. Exact user authorizations permanently abandoned those requests
and runs, retaining every record and full reservation. Neither was replayed or
continued, and provider termination was not inferred.

The [5c01f98 controls and separate output experiments](compact-taskplan-5c01f98.json)
retain three reasoning-only 8,192-token failures. A draft-16k trial stopped before
drafting; a navigation-16k trial never consumed more than 8,192 output tokens.
Neither establishes a causal benefit from more headroom. Review PR `585203a`
adds a verified correction output-exhaustion receipt, without establishing its
cause. New output experiments must freeze a separate configuration; no session
escalates mid-run. The configured deployment supports medium/high/max, not low.
Earlier [663cf72](compact-taskplan-663cf72.json), [1984bdc](compact-taskplan-1984bdc.json)
and [afc342c](compact-taskplan-afc342c.json) archives remain intact.

All three scenarios must execute and pass independent oracles before the
18 alternating runs against `56942f5`. Collections requires both ordered,
duplicate-preserving variants and reusable parallel structure. Amazon v4 requires
complete observations, exact products/workbook data and browser closure. Review
PR requires pinned provenance, actual command results, independent local report
assessment and cleanup. Artifact-bound delegated review and individual runtime
confirmations remain mandatory. Historical two-call efficiency gates are legacy
measures; fresh acceptance evaluates business outcomes and reports calls, tokens,
cost and latency separately. Frozen arm differences support configuration-level
comparisons, not architecture-only causality.

## Campaign reconciliation

Read-only inspection after Amazon `ba4ed26` verified encrypted campaign
`schema-portability-20261019-incremental`:

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 20.878010308362214520572291834 |
| Unknown-usage reservations | 1.8856704878699013596374300186 |
| Spending/reservation upper bound | 22.763680796232115880209721853 |
| Available allowance | 217.266423036474188662235225447 |

Evidence hash:
`f863fe35729522e3ffb4d4e206ace0dff2a2bc1287da58569c46f8b293e94fb1`.
All 741 request identities and 735 receipts remain. All six receiptless identities
are permanently retained; five attempts retain unknown usage. No unresolved
uncertainty currently blocks fresh admission. No funds were released. Separate
lost historical exposure of €159.96989616729369545755505266 remains accounted for
within the original €400 authorization. No ceiling increased.

## Independent validation

At `ba4ed26`, the complete Planning project passes 2,354/2,354 tests with warnings
as errors; the benchmark builds with zero warnings/errors. The reselection
follow-up passes all 11 focused cases, including exact recovery and historical
unannotated schemas. At `041c69c`, all five Flow package builds, Native AOT planning
smoke and published Git contract check passed. The publication verifier passed
14 profiles, eight frontend builds, encrypted planning/Flow recovery, tenant
isolation and health checks, with six documented EF experimental notices and no
unexpected warnings. Files checks cover persistence, isolation and TTL, without
separately proving payload encryption.

The full solution run exposed eight fixture failures: fresh phase metadata was
incorrectly retained in historical version-one sessions, and financial tests
expected the old ceiling. `cd174ea` corrects only those fixtures; all ten affected
cases pass with warnings as errors. The original full run and CI remain in
progress. No final full-solution/CI pass or business acceptance is claimed.

## Immutable evidence index

These reports are retained archives. Their historical balances and intermediate
results are not current acceptance claims.

| Archive | SHA-256 |
| --- | --- |
| [October 19 diagnostics](compact-taskplan-live-2026-10-19.md) | `f270b2b0f96ca6930d9f8c603153f6a1fbe0bfa1519309603998f71ad6b1f7e6` |
| [October 20 diagnostics through `0e76414`](compact-taskplan-live-2026-10-20.md) | `2853a1c5bf337a25826a58b6b22ff88b32b009c492cd19f3b4dbb3a2c3f690db` |
| [Historical request components](compact-taskplan-request-components-2026-10-20.json) | `48fd3dab8c923e6c307445d1a99295cfea554227f8df015b5eddb5ba2d34c286` |

Private source text, complete contracts, exact requests and receipts remain in
encrypted storage. This summary and linked metadata contain no execution authority.
