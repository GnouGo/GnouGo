# Canonical declaration identity and bounded duplicate correction

Canonical public IDs now hash a versioned JSON tuple of exact name, workflow scope
and direction (serialized as direction, scope, name). Candidate IDs, lexical name
references, evidence coordinates and baseline fingerprints do not participate.
Baseline and new ports share the same calculation; spelling and case remain ordinal.
Different directions/scopes remain different identities.

After individually validating initial root assignments, the coordinator separates
compatible duplicate identities from incompatible contracts. Compatible conflicts get
one correction inside `intent_declarations`, owned by `$plan` at `response_contract`.
Only conflicting candidate assignments appear in its schema. Established direction,
scope and presence stay fixed; only issued source-name references may establish an
alternative distinct name. Frozen neighboring/baseline identities are excluded.
Overlapping evidence can defer to the existing attachment stage or retire as a
non-declaration. The engine does not merge unproven declarations silently.

Coupled conflicts sharing an original clause decision stay in one bounded decision.
The page retains the original decision IDs and evidence fingerprint, preventing
another correction or singleton escalation through regrouping, gates or restart.
No new phase or runtime interface is introduced. Initial receipts are immutable;
correction receipts form a scoped delta. Complete root validation runs before any
attachment targets or behavior ports are authorized. Remaining conflicts, unchanged
or invalid corrections and incompatible contracts stop without another correction.

Internal declaration proof is version 5; source proof stays 4 and encrypted storage
stays Schema-5. Historical proofs require explicit reassessment. No archive is
migrated, recharged, resumed or silently granted current authority. Token ceilings,
reasoning, omission defaults, attachment constraints and behavior assembly are unchanged.

## Retained and synthetic evidence

Before the change, strict replay of session `a6fb28068c654132909534280e487410`,
revision 20, reproduced `DECLARATION_GROUNDING_UNRESOLVED`: two candidates selected
required input `input` in `main`. Replay used one retained receipt, no provider
calls, and left the saved session and budget unchanged.

Current-code strict replay cannot reuse the historical request under the changed
proof/schema identity. It stops during preparation with the existing preflight
wrapper diagnostics, consumes no receipts and makes no provider calls. This is a
replay limitation, not corrected live evidence.

`Fixtures/duplicate-roots-stage1.json` retains only public frozen classifier source,
owned references/classifications and the original root candidate; it is not a model
receipt. `CanonicalRootTests` explicitly supplies synthetic correction and attachment
answers. The winner selects the source-owned `record` name; its overlap becomes an
alias, while the original threshold/output roots remain frozen. Materialization and
behavior validation produce exactly required `record`, optional `threshold` with
omission default `100`, and required `classifiedResult`, with enum and preservation
modifiers retained. These synthetic answers never enter historical replay.

## Regression coverage

Generic renamed fixtures cover candidate-independent IDs, distinct exact-name refs,
baseline identity, scope/direction separation and the legitimate public name `input`.
They also cover cross-page duplicates, frozen neighbors, alternate evidenced names,
retirement/attachment, presence/default conflicts, response-schema restrictions,
one-correction exhaustion, zero allowance, request bounds and unverifiable stopping.
Restart tests preserve initial pages, exact correction identity, receipt/accounting
and original decision ownership. Grouped/original decisions share output-escalation
consumption in both directions. Existing omission, constraint and review safeguards
remain covered.

The runtime smoke persists the correction receipt before dependent attachment,
reopens encrypted storage, finishes attachment without redispatching completed roots
or correction, and verifies a second zero-call restart in the Native AOT binary.
Offline measurements and binary hashes are recorded in
[the validation record](planner-canonical-roots-offline.json).

## Single Stage-1 campaign

`schema5-canonical-roots-stage1-1` authorizes exactly one fresh session after offline
checks and a corrected commit/binary freeze. The comparison campaign remains untouched.
The configured model, all-low reasoning, frozen inputs/policy/catalog, global budgets,
12,000 input ceiling, 9,600 target, 8,192 normal output and bounded singleton 16,384
escalation remain unchanged.

Correct exact behavior acceptance permits continuation of the same session. Complete
success requires all six independent execution cases and exact artifact-hash approval.
The first meaningful blocker stops the campaign without a production patch,
replacement session, provider retry or Stage 2. Live results are recorded separately
from the synthetic and historical evidence above.
