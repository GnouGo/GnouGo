# Compact TaskPlan convergence

Business acceptance remains incomplete and PR #120 stays Draft. None of the three
latest workflows reached execution. Checkpoints, scoped reviews and green CI do
not establish business success. The gated 18-run baseline/candidate comparison
has not started.

## Latest frozen diagnostics

Source `4a43ea1f1633ef27b9a2d20bb5fda5c62e02794b` retained the medium phase profile: navigation target/ceiling 3k/8k with 16k output, drafting 8k/16k
with 8k output, and Judge 4k/8k with 4k output. Calls/corrections remain 64/4;
model, MCP bundle, inputs, oracles and execution authority were unchanged.

| Scenario | Calls / corrections / checkpoints | Time | Known cost | Terminal outcome |
| --- | ---: | ---: | ---: | --- |
| Amazon | 20 / 0 / 2 | 299.706 s | €0.777393 | Local review inconclusive; Judge request rejected at 11,473 estimated tokens versus 8k |
| Collections | 14 / 1 / 2 | 199.293 s | €0.432049 | Business-review findings followed by repeated-candidate stop |
| Review PR | 15 / 2 / 0 | 161.028 s | €0.417195 | Discovery follow-ups rejected at 8,451, 8,972 and 8,086 estimated tokens versus 8k; repeated batch stopped |

All 49 receipts have known usage: 123,499 input / 40,432 output tokens, including
31,649 reasoning tokens, costing €1.6266373411534701857282502441 over 660.027 seconds. There were no empty responses or new uncertain attempts. All
734,543 bytes across 54 constructed requests reconcile; four were explicitly
rejected before dispatch and one was an unissued repair preview. These five
requests have no provider usage. [The cohort archive](compact-taskplan-4a43ea1.json)
retains phase distributions, reconciled components, receipt accounting and every
non-admitted envelope, with hashes of the complete retained measurement records.

Two Collections Judges used 2,161 and 2,495 total output tokens, including reasoning,
consuming headroom above the former 2,048 reservation. This changed-source
observation is not a controlled causal result. Code retained distinct original
command-description pages carrying alias, parameter and workspace constraints;
even a singleton continuation exceeded its ceiling. Repeating the rejected batch
correctly stopped. A separate navigation-12k configuration and focused follow-up
fixes have not yet supplied new live business evidence.

The focused follow-up removes automatic duplicate export schemas from ordinary
nonclosing reviews, preserving exact consumed interfaces and versioned evidence
retrieval. A synthetic complete-request regression falls from 12,910 to 3,294
estimated tokens under the unchanged 8,000 review ceiling. Closure claims,
explicit revisions, mandatory evidence and saved historical requests retain their
exact interfaces and admission checks. Close-step guidance also explains existing
reselection to withdraw a premature requirement claim while closing its completed
scope. All 593 combined compact-planning cases pass with warnings as errors;
this deterministic result does not establish live acceptance.

## Campaign accounting and retained uncertainty

The terminal read-only audit verified 939 request identities / 931 receipts.
All eight receiptless identities are permanently retained; seven attempts have unknown
usage and none remains unclosed. Historical unknown usage stays reserved.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 26.424704523238247578423531502 |
| Unknown-usage reservations | 2.8214565004887585532746823068 |
| Spending/reservation upper bound | 29.246161023727006131698213809 |
| Available allowance | 210.783942808979298410746733491 |

Audit evidence: `7b3294d3a368d51044c730ede518c074aeb0ba59aa4edd06fec127ef0308c321`.
The user-authorized `743e4a3` HTTP-500 abandonment was applied and verified; its
full €0.4723362658846529814271749755 reservation remains. The cohort archive binds
the exact authorization, evidence and closure hashes. The original
[743 preauthorization snapshot](compact-taskplan-743e4a3.json) remains unchanged,
as do the earlier authorized [410 closure records](compact-taskplan-41077d0.json).
Abandoned requests and runs cannot be replayed or continued. No receipt, provider
termination or released funds was inferred. Separate lost historical exposure of
€159.96989616729369545755505266 remains included within the original €400
permission; no ceiling increased.

## Validation and remaining acceptance

The complete solution at `8952a0a` passed 7,329 tests, with zero failures and
13 skips across 33 projects, in 1h30m34.515s with warnings as errors and no warnings.
Its safe result SHA-256 is
`eca8d7cd4384953c4a5516a48f30ad6254a7801e90eb6d362b876affdf54973f`.
[CI at exact `4a43ea1`](https://github.com/GnouGo/GnouGo/actions/runs/39499927185)
succeeded: 13 steps, 6,315 tests with zero failures and one browser-environment
skip, a separate published Git test, five Release packages, benchmark build, and
Linux Native AOT publish/run; no compiler warnings. Safe result SHA-256:
`09a9b0ed187103bb3a3306d90fc1b722a298ffec1c13e3e4de883892958cb3a1`.
The original local `743e4a3` AOT execution failure is retained. Its fixture-only
follow-up uses the issued requirement directory and verifies exact clause/source
coverage; native publish/run passes while preserving six recovered calls,
accounting, approval checks and the expected execution result. This does not
constitute a complete solution or complete publication matrix on `4a43ea1`.

Final source full-solution/publication validation and all three independent
business oracles remain required. Collections must execute both ordered,
duplicate-preserving variants with reusable parallel structure. Amazon v4 requires
complete observations, exact products/workbook data and browser closure. Review
PR requires pinned provenance, actual command results, independent report
assessment and cleanup. Artifact-bound review and individual runtime confirmations
remain mandatory. Only then may the 18 alternating runs against `56942f5` begin,
using disclosed best-validated arm settings rather than architecture-only claims.

## Immutable evidence

[8952 controls and phase experiments](compact-taskplan-8952a0a.json) retain all
five runs and 117 receipts (SHA-256
`aabd4815978e8be0f56d1f8d2e7f82626d5b97308acd358de841aef43fbd8ca6`).
Earlier [663](compact-taskplan-663cf72.json), [1984](compact-taskplan-1984bdc.json),
[afc](compact-taskplan-afc342c.json), [5c controls](compact-taskplan-5c01f98.json),
[08ad](compact-taskplan-08ad5db.json) and [cd174ea](compact-taskplan-cd174ea.json)
archives retain their original failures, balances and validation limitations.

| Archive | SHA-256 |
| --- | --- |
| [October 19](compact-taskplan-live-2026-10-19.md) | `f270b2b0f96ca6930d9f8c603153f6a1fbe0bfa1519309603998f71ad6b1f7e6` |
| [October 20 through `0e76414`](compact-taskplan-live-2026-10-20.md) | `2853a1c5bf337a25826a58b6b22ff88b32b009c492cd19f3b4dbb3a2c3f690db` |
| [Historical components](compact-taskplan-request-components-2026-10-20.json) | `48fd3dab8c923e6c307445d1a99295cfea554227f8df015b5eddb5ba2d34c286` |

Original prompts, contracts, requests and receipts remain encrypted. These reports
contain safe metadata and grant no execution authority.
