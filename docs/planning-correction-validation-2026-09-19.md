# Compact intent correction validation

This follow-up keeps the architecture, schema-7 sessions and planning budgets unchanged. The uncertain request in `compact-intent-candidate-20260919-2d15b1` remains stopped. Previous live results and accounting are preserved.

## Recorded-response replay

The existing benchmark runner now supports read-only replay of the original interpretation receipt under its reserved response schema. The current builder, compiler, scenarios, approval checks and independent execution oracle run with mocked integrations. No live model is constructed; no campaign record is written. Replay results are distinct from live reliability results.

The initial pilot responses at `8b63d5e` reproduce:

| Recorded case | Construction outcome | Independent execution | Classification |
| --- | --- | --- | --- |
| local | Rejected: undeclared computation binding | Not reached | Invalid intent |
| protected_cleanup | Rejected: dependency on cleanup group | Not reached | Invalid intent |
| review_distractors | Reaches FinalReview with the existing nested-input correction | 5/8 variants pass | Semantic misunderstanding of cleanup prerequisites |

The distractor response binds cleanup to successful publication. It passes nominal, failed-check, incomplete-check and denied/unavailable workflow-confirmation variants, but skips resource removal on publication rejection, a changed head and cancellation. The frozen prompt requires unconditional cleanup after acquiring the workspace. The oracle agrees with that requirement: publication remains prevented, but cleanup is missing. This is a business mismatch, not a capability/policy bypass. The saved response and engine semantics are unchanged.

The next correction will provide explicit generic calculation/finalizer examples and dependency scope context, backed by sanitized regressions. It will not rewrite model dependencies automatically.
