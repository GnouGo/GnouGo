# Effect-grounding domains by workflow scope

`intent_operations` now issues one `mapped` alternative per workflow scope. Each alternative restricts effect IDs, direct public inputs/outputs and producer effects to that scope. The response fields, effect-proof contracts, canonical identity, declaration semantics and necessity rules are unchanged.

The workflow boundary remains available for independently evidenced work, including outputless operations. Multiple compatible effects within one scope remain selectable. Governing and shared contributions cannot combine scopes, even with empty input/output lists.

Cross-workflow results use an established interface effect in the consuming scope. The regression executes an existing `workflow.call`, then proves that its caller-side effect is an admissible producer. Callee declarations and callee effect IDs are not made directly visible. This change does not invent interfaces or infer cross-workflow transfer from shared names.

The parser retains deterministic checks for declaration ownership and now also checks combined effect scopes and producer visibility. It does not drop selected candidates or repair foreign references. The request-domain fingerprint includes `scoped-domain-v1`; old effect proofs require explicit reassessment. Effect proof 1, operation proof 6, Schema-5, budgets and encrypted namespaces remain unchanged. Original request schemas and receipts are not rewritten.

## Offline evidence

Before changing the implementation, read-only replay of `schema5-effect-grounded-admission-diagnostics-rerun-1:local` reproduced `INTENT_OPERATION_UNRESOLVED` at `/operations/@runtime_c8c088269589e8d624438f87`. All 15 retained receipts were inspected under their original schemas; checkpoint and budget were unchanged.

After the change, strict historical replay stops at `REPLAY_EVIDENCE_REQUIRED` because the scoped request has no historical receipt. The old assignment is not reinterpreted under the new schema. A separate, explicitly synthetic fixture uses the retained interpretation and canonical declarations, selects the valid `main` result effect, and establishes one required local operation: `record + threshold -> classifiedResult`. Rules/fallbacks and descriptive evidence remain attached, and preservation remains declaration evidence. Restart adds no requests.

The equivalent grounding checkpoint still has three effect decisions in one request and zero standalone occurrence-identity decisions. No call or token savings are claimed. The corrected synthetic fixture's largest estimated input is 5,305 tokens, below the unchanged 9,600 target.

Ten added generic regression cases cover same-scope mappings, foreign inputs/outputs/producers, governing/shared rules, outputless scopes, same-scope ambiguity, an executable caller/callee interface, and receipt-safe restart. The existing classifier, forged-proof, ownership, necessity and declaration regressions remain in the full suite.

Validation passed: 3,448 tests across 29 projects (968 planner tests; one existing optional provider E2E skipped), solution/harness builds, two frontend builds, four packages, Native AOT planning/encrypted restart, trimmed Agent.Server persistence, and the six/18-case reference selfchecks. Builds and publishes were warning-free under the existing documented publish-local exceptions.

The [offline report](planner-effect-scope-domains-offline.json) preserves replay results, fingerprints and exact counts. These offline results do not claim live admission or Stage-1 success.
