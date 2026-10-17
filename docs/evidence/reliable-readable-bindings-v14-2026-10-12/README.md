# Reliable extraction and readable lexical bindings

Baseline `4d43b222`, PR #117. This correction is entirely offline: zero paid inference, zero live-site execution, no campaign or historical run mutation. The completed v13 failure and its verdict remain intact.

## Gate A — extraction

Commits `3e2414af`, `3ad3746b`. [Replay, independent reference and limitations](gate-a/README.md).

- Regex helper preflight rejects a JavaScript-consumed backslash before item execution as a global program failure. It does not rewrite the pattern or specialize a shape.
- Actual generation/repair instructions require observed properties, exact identities and correctly escaped patterns; selectors do not prove DOM ancestry, unfamiliar URLs do not disqualify observed targets, and blank IDs are not selection placeholders.
- Fully validated all-empty independent array outputs emit bounded count-only telemetry, including cache hits. Success, contracts, cache publication and repair behavior are unchanged.
- Public encrypted-storage replay covers all **54 pages / 1,540 records**. The original response remains zero-result under historical behavior; fresh preflight identifies the malformed escape. A generic corrected link view offers 790 observed records and reconnects the ten independently reviewed title identities exactly, in order, including a legitimate redirect. Neither 790 links nor the historical 194-link ablation is claimed as a product oracle.
- Complete decision admission is measured at 79,450 conservative input tokens under the unchanged 96,000 allowance. No candidate truncation, global sampling or fabricated values.

## Gates B/C — lexical readability and equivalence

Compiler commit `f4f9c699`, new approval-fingerprinted profile `compact-bindings-v14`. [Complete YAML/JavaScript examples, measurements and trace proof](gate-b/README.md). [Every retained set and helper](gate-b/retained-sets.md).

| Same frozen TaskPlan | YAML bytes | JS bytes | Wrappers counted | `m.select` | Steps / sets |
|---|---:|---:|---:|---:|---:|
| Retained live r8 | 202,763 → 186,221 | 21,546 → 17,377 | 71 → 9 | 14 → 3 | 35 / 8 unchanged |
| Compiler comparison | 203,908 → 187,214 | 20,785 → 13,892 | 33 → 0 | 7 → 0 | 24 / 3 unchanged |

The compiler simplifies after collecting lexical contracts, preserves alias checks and original origins, and bypasses synthetic branch envelopes only for proved required selections. Conditional checks, strict lookup, first-present semantics and existing durability boundaries remain. The runtime evaluator, public expression metadata and historical profiles are unchanged. Retained v13 approval/YAML reproduces exactly.

Eleven local integration checks use actual Browser/Document operations with deterministic inference, independently inspect real XLSX contents and visits, and verify cleanup and receipt recovery. All 1,089 validation origins/outcomes match in reached order. Completed recovery dispatches no inference or external operation. Resource probes retain exact first failures and do not import 1.2 MB of unrelated observations.

See [validation](validation.md) for full-solution, package, Native AOT, encrypted-recovery, skill and candidate-CI results, including skipped checks and retained failures. Unit-test or workflow success alone does not establish a real live acceptance result. PR #117 remains draft; further paid planning and live execution require new authorization and artifact approval.
