# Realizations before governing effect evidence

`intent_operations` now resolves realizations, selects their canonical operation
identities, and only then issues governing requests. This changes the ordering
inside the existing phase; it adds no graph, authoritative collection or runtime
interface. Declaration semantics, necessity, confirmation, scoped dataflow,
partitioning, singleton escalation, reasoning and budgets remain unchanged.

Action evidence can establish a realization, defer to governing assessment,
establish no effect, or remain unresolved. Deferral carries owned evidence only:
it cannot select an invocation, result slot or producer. Evidence already marked
governing does not participate in realization decisions.

The coordinator reconstructs selected realizations from completed grounding and
identity pages. Possible but unselected anchors are excluded from governing and
producer domains. A governing mapping selects one compatible realized effect;
an explicitly shared rule can select several compatible realized effects in one
scope. Zero compatible realizations stops with `INTENT_OPERATION_UNRESOLVED`.
One target resolves without generation when the same owned clause or exact
baseline authority already proves applicability. Kind compatibility alone does
not prove the meaning of a separate descriptive clause.

Governing proofs attach to the staged operations and cannot create roots. Existing
necessity, source authority and dependency checks run before atomic commit. Valid
independent outputless invocations still originate from realization evidence.

Effect proof **2** binds governing schemas and durable pages to a stable
fingerprint of the selected realization assignments. Restart rebuilds and validates
that domain before using a completed receipt. A changed realized set cannot reuse
an old governing page. Operation proof **6**, runtime evidence **4**, declarations
**5**, source proof **4** and encrypted storage **5** remain unchanged. Historical
proofs and receipts are not converted, and allowances are not reset.

## Offline evidence

The archived `schema5-effect-scoped-domains-diagnostics-1:local` checkpoint
reproduced the unrealized-governing-target stop without provider dispatches.
All 12 original receipts were checked using their original response schemas.
After the change, strict replay stops at `REPLAY_EVIDENCE_REQUIRED`; checkpoint
and budget fingerprints remain unchanged.

A separate explicitly synthetic replay over retained interpretation and frozen
canonical declarations establishes one required local operation:
`record + threshold -> classifiedResult`. Classification rules/fallbacks and the
descriptive local statement govern it; preservation remains declaration evidence.
Two realization decisions and one governing decision use two grounding requests.
Standalone identity decisions are zero. Three downstream policy relationships
use one separate request. Restart makes no further requests. The largest estimated
input is 6,355 tokens. This is not historical or live model evidence, and no call
savings are claimed.

Seven new regression cases cover deferred descriptive evidence, exclusion of
unrealized anchors, deterministic applicability, bounded single/shared realized
targets, zero-realization stopping, governing-before-realization source order,
and restart/stale domains. Existing tests retain independent invocations,
cross-workflow interfaces, external/resource safety, necessity, producer-cycle,
declaration, correction and receipt guarantees. Published persistence smokes now
exercise the two-step grounding flow and the updated effect-proof version.

Exact offline check results and immutable replay fingerprints are recorded in
[the offline report](planner-realized-governing-offline.json). Fresh isolated
diagnostic results are reported separately; this change does not authorize Stage 1.
