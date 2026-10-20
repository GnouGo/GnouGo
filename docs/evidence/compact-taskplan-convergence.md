# Compact TaskPlan convergence

Business acceptance is incomplete. PR #120 stays Draft. None of the latest
Amazon, Collections or Review PR diagnostics reached execution. Checkpoints,
compilation and scoped reviews do not establish business success. The gated
18-run baseline/candidate comparison has not started.

## Latest attempt and retained diagnostics

Exact authorization permanently abandoned the retained Review PR HTTP 500,
Amazon HTTP 504 and Collections HTTP 500 requests and their runs. All records and
full unknown-usage reservations remain intact; none was replayed or continued.
The prior Amazon attempt is preserved in [its archive](compact-taskplan-cd174ea.json).

Frozen cohort `compactd585203a` kept the same pinned model, medium reasoning,
6k/16k/8k input ceilings and navigation/draft/Judge output reservations
16,384/8,192/2,048. Collections stopped on its second intent call with HTTP 500;
its exact authorized closure retains €0.4634497467342042122100773127. Fresh Review
PR then received all ten completions, but its correction exhausted 8,192 reasoning
tokens with no visible answer. No artifact or execution resulted. Amazon was
**not started** in this cohort.

| Scenario | Calls / corrections / checkpoints | Planning time | Known cost | Observed blocker |
| --- | ---: | ---: | ---: | --- |
| Collections | 2 / 0 / 0 | 314.015 s | €0.017644 | HTTP 500; one unknown completion permanently retained |
| Review PR | 10 / 1 / 0 | 341.672 s | €0.539829 | Correction returned an output-limit receipt: 8,192 reasoning tokens, zero content |

All 102,915 constructed request bytes reconcile. Review PR's generation, Judge
and correction requests used respectively 5,637, 4,638 and 7,854 conservative input
tokens. The Judge exceeded its target but stayed within its ceiling. Known cohort
usage is 18,881 input, 17,764 output and 14,734 reasoning tokens; Collections' total
usage remains unknown. [Safe terminal evidence](compact-taskplan-585203a.json)
retains phase distributions, components, receipts, transport and closure hashes.

The first Review PR Judge correctly rejected an array contract that prohibited
reporting discrepancies. It also incorrectly required a separate expression for
an interpretation task: the shared task contract uses its objective as the runtime
LLM instruction. The follow-up replaces that misleading wording while preserving
the valid constraint finding and all approval guards. Empty execution aggregates
now report correctness as unknown instead of vacuous success; existing acceptance
gates and retained run records remain unchanged. Neither change has live acceptance
yet. Output exhaustion is observed, but its cause and the benefit of more output
headroom are not established.

Source `08ad5db6ee96736452d7dda56dab3c4de92ebe1c`, cohort `compactd08ad5db`,
retained the pinned model and medium reasoning. Input targets remain 3k/8k/4k,
hard complete-request ceilings 6k/16k/8k, and navigation/draft/Judge output
reservations 16,384/8,192/2,048. This is the same profile as the preceding
navigation-output experiment; no setting changed during a run.

| Scenario | Calls / corrections / checkpoints | Planning time | Known cost | Observed blocker |
| --- | ---: | ---: | ---: | --- |
| Amazon | 13 / 3 / 0 | 192.646 s | €0.475024 | New malformed-evidence errors were hidden by the retained Judge finding; three repair prompts were identical |
| Collections | 11 / 1 / 1 | 118.271 s | €0.239305 | Requested root-scope evidence omitted the caller input declaration needed to review its closure |
| Review PR | 3 / 0 / 0 | 340.679 s | €0.087501 | Request 3 returned HTTP 500 without a completion receipt or verified usage |

All constructed requests fit their captured ceilings and their component bytes
reconcile. The largest generation, repair and Judge estimates were respectively
6,274, 6,901 and 3,264 tokens. There were no empty completed responses. Review PR's
unknown call is not an empty response: its full **€0.4634497467342042122100773127**
reservation remains held. Exact prompts, contracts and receipts stay encrypted.
[Safe phase measurements](compact-taskplan-08ad5db.json) retain p50/p95/maximum
sizes, component totals, known provider tokens, costs, latency and evidence hashes.

The follow-up makes two focused generic corrections: present unresolved candidate
findings together with the latest rejected-action diagnostics, and include exact
caller declarations in explicitly requested root-scope evidence. Valid evidence
retrieval clears only its resolved errors after atomic admission. Judge guidance
also distinguishes a needed producer from its future consumer or public export.
No execution, permission, contract-validation or accounting guard is relaxed.

## Retained experiments and comparison limits

The [5c01f98 controls and separate output experiments](compact-taskplan-5c01f98.json)
retain all failures. Three controls stopped on known reasoning-only responses
at 8,192 output tokens. A Collections draft-16k trial stopped before drafting,
so it did not test the changed phase. An Amazon navigation-16k trial reached
discovery, but every response used fewer than 8,192 output tokens. Neither trial
establishes a causal benefit from the larger allowance.

Earlier [663cf72](compact-taskplan-663cf72.json),
[1984bdc](compact-taskplan-1984bdc.json) and [afc342c](compact-taskplan-afc342c.json)
reports preserve prompt-size distributions, costs and retained failures. The
current deployment declares medium/high/max reasoning, not low. No model or
metadata substitution enabled the proposed low-navigation experiment.

Fresh diagnostics must execute all three scenarios and pass independent oracles
before the 18 alternating runs against `56942f5`. Collections requires both
ordered duplicate-preserving runtime variants and reusable parallel structure.
Amazon v4 requires complete search observations, exact product/workbook data and
browser closure. Review PR requires pinned provenance, actual command results,
independent report assessment and cleanup. Compilation and Judges grant no effect
authority; exact delegated artifact review and individual confirmations remain
required. Each comparison arm freezes its configuration; intentional differences
must be disclosed as configuration-level outcomes.

## Campaign reconciliation

Read-only inspection after the `585203a` diagnostics verified encrypted campaign
`schema-portability-20261019-incremental`:

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 19.851173020527859237536656894 |
| Unknown-usage reservations | 1.8856704878699013596374300186 |
| Spending/reservation upper bound | 21.736843508397760597174086913 |
| Available allowance | 218.293260324308543945270860387 |

Evidence hash:
`7ce9d581631a7675589b2db2a4dcf0c0c9ed734d62ca6d1d176be87ba2b0d50f`.
There are 716 request identities and 710 receipts. All six receiptless identities
are permanently retained; five attempts retain unknown usage. No unresolved
uncertainty currently blocks fresh admission. The new Collections closure is
`d0c1bfe9b2939b5dbea5032c78a8e9d26aa10f4ef205118392272df68930d342`.
No receipt or provider termination was inferred, and no funds were released.
Separate lost historical exposure of €159.96989616729369545755505266 remains
accounted for within the original €400 authorization. No ceiling increased.

## Independent validation

At `041c69c`, all five Flow package builds, the Native AOT planning smoke and the
published Git contract check passed. The maintained publication verifier passed
14 profiles, eight frontend builds, encrypted planning/Flow recovery, tenant
isolation and health checks. Six documented EF experimental notices were accepted;
there were no unexpected warnings. Files checks cover persistence, isolation and
TTL, without separately proving payload encryption.

The full solution run exposed eight fixture failures: fresh phase metadata was
incorrectly retained in historical version-one test sessions, and financial tests
still expected the old 8k ceiling. `cd174ea` corrects only those fixtures; all ten
affected cases pass with warnings as errors. The original full run and CI are
still in progress. The follow-up Judge/lowering tests pass 20/20 and measurement
tests 48/48 with warnings as errors. Neither focused validation nor packaging
establishes business acceptance. Historical two-call efficiency gates remain
legacy measures; the new acceptance protocol requires independently complete
business outcomes and reports calls, tokens, costs and latency separately.

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
