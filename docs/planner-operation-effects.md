# Runtime effect proofs — offline implementation

The implementation starts from `63ca2813872a392fa96d070656c79a2942863614`.
No LOCAL, MIXED or Stage-1 session is included in this validation.

`PlanningOperationAssignment` carries an effect proof and its selected effect ID.
An anchor identifies a workflow owner and a result realization, explicit invocation,
iteration or resource-transition boundary. Existing operations retain their exact
baseline workflow/node identity. Requested IDs hash versioned effect/boundary
coordinates. Source candidate IDs, consumed inputs, necessity, conditions and
descriptions do not participate in that hash.

There is no effect graph or new authoritative collection. Request domains are
derived indexes; intermediate assignments use the existing durable decision pages
inside `intent_operations`. Only complete, validated mappings can establish the
operation set. Baseline node facts resolve without generation. Missing requested
effect ownership is grounded with bounded assignments to issued references.

Grounding identifies whether evidence realizes an effect, governs it, explicitly
governs a shared set, or establishes no effect. A public output is a possible
single-result boundary, not automatic proof that all transformations sharing it
are one occurrence. Separately evidenced invocations can share inputs and outputs.
Governing evidence cannot create an operation; the selected effect must have an
admitted realization. Contradictory execution facts and missing producer proofs
stop before atomic commit.

After grounding, zero proven identities stops, one resolves deterministically,
and multiple identities expose only a bounded enum of canonical operation IDs.
There is no `distinct` response alternative in occurrence selection. Grounding
still requires semantic assessment where source intent does not establish the
effect mapping mechanically; reference/schema validation does not itself prove
the meaning of arbitrary prose. These decisions are measured separately from
occurrence selection, without claiming that renamed work saves calls.

Input and producer mappings project into existing obligation relations. Behavior
assembly reuses workflow and iteration ownership. Already proven data relations
are absent from subsequent model alternatives; independent permission, ownership
and failure relationships remain eligible. Capability matching, confirmation
guards, necessity resolution, human review and executable validation retain their
existing roles.

Effect proof version **1** and operation-admission proof **6** retain runtime
evidence **4**, source **4**, declarations **5**, and storage **5**. Completed
grounding and selection pages are revalidated against their exact current schema,
scope and evidence fingerprint on restart. Old admission proofs require explicit
reassessment. Receipts, budgets, correction/escalation allowances and archived
records are neither migrated nor reset.

The regression corpus covers the captured classifier, separately evidenced
transformations with shared endpoints, repeated external/human/resource effects,
strict identity enums, missing/foreign/stale proofs, necessity conflicts,
producer cycles, declaration exclusions and restart. The published Native AOT
smoke persists completed grounding pages before admission, then recovers and
commits through the encrypted journal without another dispatch. The trimmed
Agent.Server smoke roundtrips the additive effect contracts through Schema-5
persistence.

## Evidence and measurements

Historical replay and synthetic convergence are separate tracks. The archived
necessity diagnostic had two standalone occurrence decisions and no committed
operation set. Its original receipts are validated unchanged; current replay
stops at the first new request without a matching receipt.

The corrected classifier uses labelled synthetic effect mappings with retained
interpretation and frozen canonical declarations. It must produce one required
local operation consuming `record` and `threshold`, producing `classifiedResult`,
with rules/fallbacks and descriptive evidence attached. Preservation remains
declaration evidence. This is offline admission convergence, not live success.

Exact decision counts and validation results are recorded in
[the offline report](planner-operation-effects-offline.json).

| Decision class at the admission checkpoint | Retained attempt | Corrected synthetic fixture |
|---|---:|---:|
| Standalone occurrence identity | 2 | 0 |
| Effect grounding | 0 | 3, packed into one request |

The fixture then verifies downstream projection with four remaining policy
relationship decisions in one separate request. It does not re-ask the two public
input dependencies. Its largest estimated request is 3,683 tokens; provider usage
is unavailable because the responses are synthetic. Restart adds zero requests.

All **3,431 tests** passed across 29 projects, including **958 planner tests**;
one existing optional provider E2E was skipped. The solution and harness builds,
two frontend builds, four packages, Native AOT planning/encrypted-restart and
trimmed Agent.Server persistence smokes passed without warnings under the existing
documented publish exceptions. Reference selfchecks passed six classifier/batch
cases and 18 frozen CodeReview cases without model or business transport calls.
These exercise existing reference workflows, not a newly planned live workflow.

Seven archived diagnostic checkpoints were audited: 77 request records, 75
verifiable receipts and two preserved unverifiable reservations. Original response
schemas remained valid for every completed candidate. Historical proof versions
stop explicitly; the latest checkpoint stops at `REPLAY_EVIDENCE_REQUIRED` for new
effect grounding. Every audited checkpoint and budget remained unchanged.
