# Frozen stabilization candidate

Source: `4cb8586440d22adf02bc0496565c4f9cc6064ef0`. All 24 outcomes are retained. The comparison is **inconclusive** because it reports `usage_bounded: false`; correctness also regressed: 21/24 correct, versus parent 14/24 and previous candidate 22/24. Median physical planning calls is 2, versus 4 and 2.5. All approved executions passed the independent variants; zero safety violations were recorded.

| Case | Parent | Previous candidate | Stabilization |
| --- | ---: | ---: | ---: |
| local | 3/3 | 3/3 | 3/3 |
| read_transform | 3/3 | 3/3 | 3/3 |
| nullable_defaults | 0/3 | 3/3 | 3/3 |
| conditional | 3/3 | 3/3 | 2/3 |
| collections | 2/3 | 3/3 | 3/3 |
| protected_cleanup | 3/3 | 3/3 | 3/3 |
| review_french | 0/3 | 2/3 | 3/3 |
| review_distractors | 0/3 | 2/3 | 1/3 |

## Retained failures

- `conditional`, repetition 2: an undeclared producer reference remained invalid; both bounded repairs attempted changes outside the allowed workflow scope. Five physical attempts, two repairs. This is a per-case regression against both baselines.
- `review_distractors`, repetition 1: cleanup availability was repaired, but a required nested evidence object did not satisfy its output contract. Seven physical attempts, two repairs; never approved.
- `review_distractors`, repetition 3: input-contract findings remained; the session exhausted eight physical attempts during its second repair. The separate permanent closure preserves the failure, pending request, absent receipt and all reservations. It permits no redispatch of that identity.

`live.jsonl` retains all 24 original rows and the collection summary. `comparison.json` compares both baselines and includes the unmodified pre-audit comparison and admission audit proofs. The audit does not improve an outcome. A startup provider-spelling refusal before any dispatch is retained separately; it is not an extra cohort outcome.

## Accounting and validation

Campaign `flow-v9-112` used 68 additional physical attempts. Known cumulative cost is EUR 27.8121452481; reservations are EUR 10.2769019878; the cumulative upper bound is EUR 38.0890472358 under EUR 50. Eight uncertain attempts retain reservations. The campaign-wide ledger remains bounded, but the retained cohort comparison does not establish bounded per-session usage. These are distinct claims; no acceptance pass is inferred from the ledger. Estimates use recorded usage and FX, not invoices.

Frozen-commit CI completed with 32 successful checks and four release-only skips. Local .NET validation: 2,814 passed, five opt-in live skips. Python: 287 core and 27 CLI passed. Frontends, five Flow packages, Native AOT smoke and published encrypted persistence checks passed under the existing documented framework-exception policy. Exact CI checks and publish-attempt accounting are retained in the manifests.

Changes: literal agent workspace approval; shared generated MCP option validation with locked-binding checks; typed result presence and valid null comparisons; terminal-format-independent CLI refusal assertion. The planner architecture remained unchanged. Planning source measured 40 files / 4,435 lines, versus 55 / 7,190 in the parent.

Native validation can still reject presence checks on conditional producers. Real Copilot command edit/test execution remains unverified because host sandbox enforcement is unavailable; mocked workflow benchmarks do not resolve it. The PR remains draft.

No further stabilization implementation or paid stabilization runs followed this cohort. The subsequent TaskPlan replacement is separately authorized; it must retain these results and use the same cumulative campaign ceiling.
