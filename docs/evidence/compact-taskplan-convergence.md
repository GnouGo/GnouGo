# Compact TaskPlan convergence

Business acceptance is incomplete. PR #120 stays Draft. None of the latest
Amazon, Collections or Review PR diagnostics reached execution. Checkpoints,
compilation and scoped reviews do not establish business success. The gated
18-run baseline/candidate comparison has not started.

## Latest frozen diagnostics

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

Read-only inspection after the three diagnostics verified encrypted campaign
`schema-portability-20261019-incremental`:

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 19.293699457922331822625077758 |
| Unknown-usage reservations | 0.9587709944014929352172753932 |
| Spending/reservation upper bound | 20.252470452323824757842353151 |
| Available allowance | 219.777633380382479784602594149 |

Evidence hash:
`25f8c64e278cded3d90bdb2888f93ef0ca4aa533baed70b82f1eacb4a27ef612`.
There are 703 request identities and 699 receipts. Three historical receiptless
identities are permanently retained. The new HTTP 500 remains unresolved and
blocks paid dispatch; it has not been replayed or continued. An explicit
benchmark-only retention operation is prepared for review, preserving every
original record and the full reservation without claiming provider termination.
Separate lost historical exposure of €159.96989616729369545755505266 remains
accounted for within the original €400 authorization. No ceiling increased.

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
