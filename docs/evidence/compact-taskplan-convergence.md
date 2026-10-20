# Compact TaskPlan convergence

Business acceptance is incomplete. PR #120 stays Draft. None of the latest
Amazon, Collections or Review PR diagnostics reached execution. Checkpoints,
compilation and scoped reviews do not establish business success. The gated
18-run baseline/candidate comparison has not started.

## Latest diagnostic and corrections

Five diagnostics on source `8952a0a` produced 117 known receipts, cost €3.1193948280
and reached no execution or business acceptance. The model, medium reasoning,
MCPs, oracles and call/correction allowances remained fixed. Separate frozen
experiments changed navigation input 6,000→8,000, then Judge output 2,048→4,096;
no setting changed during a run.

| Diagnostic / configuration | Calls / corrections / checkpoints | Planning time | Known cost | Outcome |
| --- | ---: | ---: | ---: | --- |
| Review PR, navigation input 6k | 11 / 2 / 0 | 120.572 s | €0.241722 | Two 6,257-token evidence batches rejected; stopped without execution |
| Review PR, navigation input 8k | 25 / 1 / 0 | 272.883 s | €0.706909 | Clarification about capability availability; no execution |
| Amazon, navigation input 8k | 15 / 1 / 0 | 214.878 s | €0.548405 | Judge rejected a search using a model-authored empty-string enum; clarification |
| Collections, navigation input 8k / Judge output 2k | 17 / 1 / 3 | 205.051 s | €0.399214 | Intermediate Judge exhausted 2,048 reasoning tokens with no visible content |
| Collections, navigation input 8k / Judge output 4k | 49 / 4 / 10 | 641.732 s | €1.223145 | Closed-scope selection and exhausted correction allowance |

The 8k navigation trial admitted requests above the former 6k ceiling, but none of
the workflows completed. All 17 Judges in the 4k-output trial returned visible
content; their maximum output was 1,629 tokens. The additional headroom was never
consumed, so farther progress does not establish that a larger output allowance
caused it. All 1,182,267 bytes across 119 constructed requests reconcile.
[Safe phase measurements](compact-taskplan-8952a0a.json) retain distributions,
components, actual usage and immutable experiment identities. The gated 18-run
baseline/candidate comparison remains unstarted.

The follow-up restricts selection to displayed open scopes, exposes a bounded
exact requirement page before selecting a step, and explains existing rollback
authority when accepted work needs correction. Provider views distinguish paging
from capability availability; interface extraction explains that enums constrain
accepted values. All 577 compact-planning cases pass with warnings as errors,
including historical request recovery. Fresh live confirmation remains pending.
The [41077d0 archive](compact-taskplan-41077d0.json), earlier experiment archives
and independent-validation references remain retained.

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

Read-only inspection after the final Collections trial verified encrypted campaign
`schema-portability-20261019-incremental`: 888 request identities and 881 receipts.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 24.774406824846707544654758732 |
| Unknown-usage reservations | 2.3491202346041055718475073313 |
| Spending/reservation upper bound | 27.123527059450813116502266063 |
| Available allowance | 212.906576773255491425942681237 |

Evidence hash: `bb73c6f376e8c20c6cf784176ddd386d5e9d1fdc1cc07d377ed3d5630b6e9903`.
All seven receiptless identities are permanently retained; six attempts retain
unknown usage. The separately
authorized Collections closure was applied under the existing transport-retention
mode and verified against evidence
`fb1cbf1b2472374bcceeefb65b9ede893c03ed987a02833fab02031d8dfb580a`;
closure hash:
`a3e5afacbe97d195e8e7a4e9c8adfb11252eab31642985013e631c1e2e4b0dde`.
The abandoned request and run cannot be replayed or continued. Fresh diagnostics
still require normal admission. No receipt or provider termination was inferred,
and no funds were released. Separate
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

The full solution at `041c69c` completed with 7,133 passes, 24 failures and
13 skips. All failures are historical-fixture setup or obsolete ceiling assertions
in Server (1,425 passes, 24 failures and one skip):
`cd174ea` fixes the shared restoration path and financial assertions; a remaining
manual downgrade now uses that same restoration helper. All 16 later failing
cases pass in the final focused Server run; the prior ten-case rerun covers the
earlier failures. The original full run remains failed. The guidance follow-up
passes 14 final focused cases with warnings as
errors. Its regression uses the captured hard ceiling; the former assertion
incorrectly treated the 3,000-token target as a mandatory cap. Final full-solution
and CI success, final publication validation and business acceptance remain unclaimed.

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
