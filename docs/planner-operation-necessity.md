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

## One isolated LOCAL diagnostic

Production and binaries were frozen at `ca3e98c119ef7cb3abdde984cb95df43d2ee2e4c`
under `schema5-operation-necessity-diagnostics-1`. The model, all-low reasoning,
scenario, declaration ports/attachments, catalog, policy, budgets and token limits
match the previous campaign. LOCAL ran once. MIXED is **not run**.

The verified first stop was `INTENT_OPERATION_UNRESOLVED` at
`/operations/@r_0f2cc62e8b0e92527445190d`, during the second occurrence-identity
decision. This was not a provider failure, budget exhaustion or necessity conflict.

The short classification action and detailed rules both carried explicit required
evidence in this run. The existing classification root was correctly available to
the detailed-rules decision through `same_as`, but the response selected:

```json
{"operation_runtime_2b1184a1fd772a1ccc416754":{"status":"distinct"}}
```

This left two staged required local roots:

| ID | Kind | Required | Identity origin |
|---|---|---|---|
| `operation_82f2ecf1465dc8cb2cc923ba` | `local_processing` | true | Deterministic first root |
| `operation_d1c539152a0977221010c69e` | `local_processing` | true | Model `distinct` |

The clause “This is deterministic, local, in-memory business processing.” was
interpreted as action evidence with unspecified necessity. Both roots remained
eligible despite that necessity difference. Its identity response was:

```json
{"operation_runtime_923cb1ef4c0242322b592e47":{"status":"unresolved"}}
```

The canonical operation set was not committed. Consequently there are no committed
operation IDs or governing attachments to report as success. The durable pages
retain one model `distinct` and one model `unresolved`; committed-identity counters
remain zero. There was one deterministically established staged root. This run
demonstrates the corrected target eligibility, but does not prove one-occurrence
live convergence.

Runtime interpretation had zero unresolved execution scopes and no external,
lifecycle or human action evidence. A governing candidate covering the output
declaration was excluded by exact canonical declaration coverage, as confirmed by
read-only deterministic inspection. Preservation remained non-executable. The
fixture still supplied required `record`, optional `threshold` with default `100`,
and required `classifiedResult` with its enum/preservation modifiers. These are
frozen fixture preconditions, not fresh declaration convergence.

| Measurement | LOCAL | MIXED |
|---|---:|---|
| Verified provider calls / durable reservations | 15 / 15 | Not run |
| Unverifiable requests | 0 | Not run |
| Input / output tokens | 27,837 / 11,749 | Not run |
| Reasoning tokens, included in output | 10,034 | Not run |
| Largest estimated / actual input | 4,819 / 3,175 | Not run |
| Partition children / singleton escalations | 2 / 0 | Not run |
| Semantic repair reservations | 0 | Not run |
| Committed governing attachments | None; admission incomplete | Not run |

Interpretation consumed 13 calls (26,924 input / 11,380 output tokens); occurrence
identity consumed two (913 input / 369 output). Fourteen responses passed their
original schemas, and one verified truncation was handled through partitioning.
All dispatch usage is known. Limits remained 12,000 input, 9,600 dispatch target,
8,192 normal output, bounded 16,384 singleton escalation and 16 reservations.

Read-only replay reused the completed decision pages and reproduced the exact
stop with zero provider calls and unchanged checkpoint/budget records. All 23
frozen DLL hashes, production/harness sources and archived accounting remain
unchanged after the run. No production patch, retry, replacement diagnostic,
MIXED execution or Stage-1 campaign followed the failure.

[Redacted live report, original request/receipt fingerprints and replay evidence](planner-operation-necessity-live-report.json).
