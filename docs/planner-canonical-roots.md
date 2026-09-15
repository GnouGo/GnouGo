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

## Fresh Stage-1 result: stopped after canonical declarations

Production was frozen at `d8e3f068df55b1996f7d6fd8a1fdebbba26b334f`, with harness
pin commit `57650d4`. Offline validation passed **3,294 solution tests** across 29
projects, including **846 planner**, **855 Core**, **67 integrations** and **395
Agent.Server** tests; the optional live-provider test remained skipped. The 30 new
planner regression cases, 58 focused harness tests, reference selfchecks, package
builds, Agent frontend build, Native AOT planning/encrypted restart and trimmed
Agent.Server persistence all passed. Final builds/publishes were warning-free under
the existing exceptions; no new suppression was added. An initial AOT overload
warning was fixed before the final checks and production freeze.

Exactly one fresh session, `d1d249a22d754ef680c646f00881ee32`, stopped at revision
**30** in capability preparation. Its typed outcome is absent: this is a technical
stop, not `ValidWorkflow`, `NeedUserClarification` or demonstrated `Unsupported`.
The first blocker is **`INTENT_OPERATION_UNRESOLVED`**, at **`/preparation`**.

Canonical declaration adjudication completed correctly:

| Name | Direction/scope | Presence/default | Canonical ID |
|---|---|---|---|
| `record` | input / main | required; no omission default | `decl_89cf60869b3c67ae8d948cde` |
| `threshold` | input / main | optional; omission default `100` | `decl_d9dced873ca13886d395895e` |
| `classifiedResult` | output / main | required; no declaration default | `decl_e10c86997aef941cbd55d2f1` |

Threshold's non-nullable numeric requirement is retained in its governing evidence;
no executable schema has yet been constructed. The output has attached member/schema,
`category` enum (`rejected`, `high`, `standard`) and original `id`/`amount` preservation
evidence. There are six modifier assignments and no extra ports or aliases. This run
needed **no duplicate-root correction**. Frozen-neighbor correction behavior is proved
by the offline fixtures, not exercised by this live run.

The saved interpretation classified both workflow creation and the classification
rule as `workflow_policy`, with associated runtime conditions/value evidence. It
admitted **zero runtime operations**. The unchanged capability inventory requires an
evidenced operation and therefore stopped after declarations. This is an upstream
planner/model interpretation convergence blocker; no provider, token-limit or
duplicate-root failure occurred. It does not prove the requested workflow unsupported.
No production change was made after observing it.

| Phase / owner / gate | Verified calls | Input tokens | Output tokens | Repairs |
|---|---:|---:|---:|---:|
| intent / `$plan` / response_contract | 3 | 5,533 | 803 | 0 |
| confirmation_scope / `$plan` / response_contract | 2 | 7,313 | 1,761 | 0 |
| intent_declarations / `$plan` / response_contract | 2 | 6,588 | 1,001 | 0 |
| Total | **7** | **19,434** | **3,565** | **0** |

All seven reservations have verified receipts; there are zero unverifiable requests.
Output usage includes **569 reasoning tokens**. All requests used `low`. Seven initial
decision pages completed, containing 34 distinct issued model-decision IDs. There
were **zero output partitions, singleton escalations or semantic corrections**.
The largest input was **7,532 estimated / 5,250 actual tokens**, below the unchanged
9,600 target and 12,000 ceiling. No user clarification occurred.

Executable-hole counts are **0 deterministic / 0 model**, with zero exposures:
construction was not reached. Other uninstrumented engine decisions remain unknown.
Accepted behavior, skeleton, final artifact and approved artifact hashes are all
absent. There was no behavior acceptance or workflow approval.

| Independent execution case | Result |
|---|---|
| accepted | not run |
| rejected | not run |
| boundary | not run |
| omitted default | not run |
| invalid input | not run |
| null threshold | not run |

Independent execution made zero calls. Current-code strict replay from revision 22
reused the exact attachment receipt and reproduced `INTENT_OPERATION_UNRESOLVED`
with **one retained receipt, zero provider calls and unchanged persisted state**.
The source-derived classification evidence and exact request/receipt fingerprints
are retained in the [blocker record](planner-canonical-roots-blocker.json) and
[full redacted report](planner-canonical-roots-report.json); private payloads remain
in encrypted storage. The [frozen manifest](planner-canonical-roots-manifest.json)
records binaries, model, settings and benchmark fingerprints.

Post-run checks confirm unchanged production/harness binaries, fixture sources and
all archived campaign/snapshot/request/receipt/budget fingerprints. This campaign is
blocked and has consumed its only authorized start. No patch, replacement session,
provider retry or Stage 2 was performed. This result proves live declaration
convergence, **not end-to-end workflow success**.
