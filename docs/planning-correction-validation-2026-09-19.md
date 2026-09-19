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

## Instruction and repair-context correction

Sanitized regressions first demonstrated that repair prompts lacked business scope, finalizer placement and usable predecessor choices. Interpretation and repair now share short examples for bound calculation parameters and main work followed by resource cleanup. Dependency-error context includes operation kind, business scope, placement and structurally eligible predecessor IDs. The scope predicate is shared with structural validation; choices exclude self/cross-scope/cyclic dependencies and host identifiers. It also explains that depending on successful later work can skip finalization after failure. No candidate domains or mapping state are persisted.

All replacements remain model-supplied, atomic and subject to complete rebuild and validation. Tests verify bounded correction, schema/binding errors, repeated IDs in separate branches, confirmation insertion, guarded finalizers, cycles through captured branch/loop results, invalid replacement edges, cleanup on main-work failure/cancellation, and denial/unavailability of permission. The original bad dependencies are never silently removed.

A second sanitized form places primary work inside the cleanup block, as in the recorded protected-cleanup response. Fixing its structural dependency alone still fails the independent failure and cancellation expectations. The test deliberately preserves those negative outcomes: executable validity is not proof of requested business behavior, and changing the graph builder to reinterpret the intent would hide the mistake. The new examples explicitly place primary work in the main operations. Live-model improvement has not been measured.
