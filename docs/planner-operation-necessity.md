# Operation identity and necessity

Canonical runtime admission keeps occurrence identity separate from capability
necessity. Kind, execution scope, role, baseline authority and resource semantics
determine eligible occurrence targets. Requiredness cannot exclude an otherwise
compatible target. The existing bounded identity decision still distinguishes
genuinely separate executions from `same_as` evidence; no automatic semantic merge
is introduced.

Runtime interpretation supplies `necessity: { state, evidence }`. `state` is
`required`, `optional` or `unspecified`. Explicit states require issued source-span
boundaries. Unspecified necessity requires null evidence. Non-executable roles
and host policies have no necessity response field. Conditions describe when an
action executes, not whether its capability is needed to implement the workflow.

Canonical admission resolves necessity from every assigned action and governing
contribution:

| Evidence for one occurrence | Canonical contract |
|---|---|
| Unspecified only | Required |
| Explicit required, with or without unspecified contributions | Required |
| Explicit optional, with or without unspecified contributions | Optional |
| Explicit required and explicit optional | Located `INTENT_OPERATION_UNRESOLVED`; no operation-set commit |

Governing evidence changes necessity only when it carries an explicit owned claim.
Canonical requiredness and source grounding are recalculated together before a
proof is committed. Restored proofs must reproduce the same result. Stable
operation IDs still derive from existing action anchors or baseline coordinates;
neither necessity nor its evidence references enter occurrence identity.

Baseline nodes retain exact node authority. Their representation has no optional
capability flag, so interpretation cannot invent one. Their executor remains
required even for a conditional node. Changing existing behavior continues through
the existing revision and review flow.

Runtime proof version 4 and operation proof version 5 replace the prior boolean
evidence proofs. Schema-5 databases and encrypted namespaces are unchanged.
Historical receipts remain audit evidence; missing necessity proof is not silently
converted from a boolean. Explicit reassessment retains cumulative budgets,
receipts, corrections and escalation accounting. No declaration, confirmation,
reasoning, token-limit, paging, escalation or compiler change is included.

The captured LOCAL checkpoint was first inspected and revalidated with its frozen
implementation: two roots differed only in preliminary requiredness. All 13
retained receipts were audited with zero provider dispatches and unchanged
archived state. Corrected tests use explicitly synthetic necessity/identity
responses; they do not replace historical responses or claim fresh live success.

Offline and isolated diagnostic results are recorded alongside this document.
The live scope permits one LOCAL and, only after it passes, one MIXED case. No
Stage-1 campaign is authorized by this change.

Offline validation passed 3,420 tests across 29 projects (947 planner tests, including 20 new necessity cases); one existing optional live test was skipped. Both frontends, four packages, the Native AOT planning/encrypted restart and trimmed Agent.Server persistence smokes passed under the existing documented exceptions. The two admission selfchecks and reference execution selfchecks passed with no provider calls. [Offline evidence](planner-operation-necessity-offline.json).
