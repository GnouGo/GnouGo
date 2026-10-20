# Compact TaskPlan convergence

Business acceptance is incomplete. PR #120 stays Draft. None of the latest
Amazon, Collections or Review PR diagnostics reached execution. Checkpoints,
compilation and scoped reviews do not establish business success. The gated
18-run baseline/candidate comparison has not started.

## Latest diagnostic and corrections

Frozen cohort `compactd41077d0` retained the pinned model, medium reasoning,
3k/8k/4k input targets, 6k/16k/8k complete-request ceilings and
16,384/8,192/2,048 navigation/draft/Judge output reservations. Amazon accepted one
locally reviewed URL producer, then stopped on repeated evidence requests.
Collections stopped without a receipt for its second intent call. Review PR was
**not started** in this cohort because unresolved uncertainty blocked paid admission.

| Diagnostic | Calls / corrections / checkpoints | Planning time | Known cost | Outcome |
| --- | ---: | ---: | ---: | --- |
| Amazon `41077d0` | 28 / 4 / 1 | 341.726 s | €0.758984 | Repeated evidence after altered page addresses; no execution |
| Collections `41077d0` | 2 / 0 / 0 | 318.774 s | €0.018017 | Transport failure without HTTP response or completion receipt |
| Review PR `585203a` | 10 / 1 / 0 | 341.672 s | €0.539829 | Correction exhausted 8,192 reasoning tokens with zero visible content |

Amazon used 66,057 provider input, 17,460 output and 13,225 reasoning tokens, with
28 known receipts and no empty response. Generation peaked at 30,946 serialized
bytes / 10,572 estimated input tokens; its one Judge used 7,791 bytes / 2,853
estimated tokens. Two prospective discovery batches reached 6,048/6,049 estimates
and were rejected before dispatch against the captured 6,000 ceiling. All
350,534 bytes across both scenarios' 32 constructed envelopes reconcile.
[Safe phase measurements](compact-taskplan-41077d0.json) retain p50/p95/maximum,
components, known provider usage, costs and latency. Collections' total usage is
unknown; its full €0.4634497467342042122100773127 reservation remains charged.

The reselection clarification produced a substantive missing-prerequisite reason,
preserved through recovery. The next selection still chose `value` for unsupported
string construction, which drafting correctly rejected. Fresh selection now reuses
the existing value-task semantics, avoiding contradictory phase guidance.
Discovery wording explains that a page's hash and offset must be copied unchanged
and supplied evidence used directly. The trace showed changed offsets and repeated
pages, with no host cache or identity defect. These are presentation corrections,
with no new phase, state, runtime or relaxed validation; live convergence remains
unproved.

The [preceding Amazon diagnostic](compact-taskplan-ba4ed26.json) retains its
missing-query failure and genuine Judge findings. No accepted fragment, inspection,
compilation or favorable review authorizes execution.

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

Read-only inspection after Collections `41077d0` verified encrypted campaign
`schema-portability-20261019-incremental`:

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 21.655011996800853105838443082 |
| Unknown-usage reservations | 2.3491202346041055718475073313 |
| Spending/reservation upper bound | 24.004132231404958677685950413 |
| Available allowance | 216.025971601301345864758996887 |

Evidence hash:
`26ca280c75cc3a8b8519526d475e096a08ab87e1fefeadd9886bc06bbd725a8a`.
All 771 request identities and 764 receipts remain. All seven receiptless identities
are permanently retained; six attempts retain unknown usage. The separately
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
