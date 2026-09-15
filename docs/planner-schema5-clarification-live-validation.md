# Schema-5 live validation after last-resort clarification

Stage 1 stopped technically with `CONFIRMATION_POLICY_CONFLICT` before behavior
construction. It produced no typed business outcome or approved artifact. Stages 2
and 3 were not run. Exactly one fresh session was started; no replacement session,
provider retry, production patch or reasoning A/B request followed the failure.

## Frozen implementation and prerequisites

- Production commit: `9a3b3de8ce1e4baa228c636471bc41ea7c9bf061`.
- Campaign: `schema5-clarification-20260912`; tenant: `planner-progressive`.
- Model: `OpenAi / gpt-5.5-2026-04-24`, through Agent.Server's
  `PlanningSessionService`, injected capability resolver and KeyVault configuration,
  including Agent's persisted defaults and model metadata overrides.
- Every request used `low`. Limits remained 12,000 input / 8,192 output tokens,
  with a 9,600 estimated-input dispatch target, concurrency four and five repairs
  per workflow/gate. Global budgets and background-advancement settings were unchanged.
- The only harness code changes pinned the production commit and allocated the
  fresh campaign identity. Production sources and public interfaces were unchanged.

Before dispatch, the full solution suite passed **3,018 tests**, with one existing
opt-in test skipped, zero failures and zero warnings. The benchmark harness built
with zero warnings/errors. The six retained classifier/batch reference families
and all 18 frozen CodeReview fixture checks passed without model or business
transport calls. These checks validate prerequisites, not a newly generated workflow.

The campaign manifest records exact binary hashes, configured model metadata,
budgets, and scenario/catalog/policy/fixture fingerprints. It is preserved in the
encrypted `agent-planning-progressive-campaigns-v5` collection. The workspace-resolved
EF index is `.GnOuGo/data/planner-progressive/schema5-clarification-20260912/gnougo-planning-v5.db`.
Both earlier campaign namespaces and databases remain unchanged.

See the [machine-readable report](planner-schema5-clarification-live-report.json)
for the complete redacted manifest, request accounting and replay result.

## Results

| Measure | Stage 1 — standalone classifier | Stage 2 — batch | Stage 3 — CodeReview |
|---|---|---|---|
| Typed outcome | None: technical stop | Not run | Not run |
| Persisted status / revision | `stopped` / 19 | — | — |
| Verifiable model calls / reservations | 5 / 5 | — | — |
| Unverifiable dispatches / missing receipts | 0 / 0 | — | — |
| Decision pages | 5: three intent, two intent-relations | — | — |
| Correction / split pages | 0 / 0 | — | — |
| Engine-resolved executable decisions | 0; other engine decisions unknown | — | — |
| Model decision IDs | 38 distinct issued IDs covered by receipts | — | — |
| Model executable decisions / hole exposures | 0 / 0 | — | — |
| User clarifications / answers | 0 / 0 | — | — |
| Repairs | Intent 0; intent-relations 0 | — | — |
| Largest estimated / actual input | 2,817 / 2,066 tokens | — | — |
| Actual input / output tokens | 7,352 / 1,417 | — | — |
| Independent generated-workflow executions | 0: not reached | — | — |
| Approved artifact hash | None | — | — |

Session: `9978a0711f644ddfb9fabd4026bb681b`. Intent interpretation used three
calls and 5,044 input / 824 output tokens. Intent relations used two calls and
2,308 input / 593 output tokens. Every receipt completed; no truncation, provider,
metadata, or request-sizing failure occurred. The largest estimated input used
23.5% of the 12,000-token ceiling.

Model decision IDs count interpretation units and relation assignments, not a claim
of 38 validated business choices. Executable construction never began. The absence
of a clarification in this run does not establish successful end-to-end validation
of the clarification change, because the earlier capability gate stopped progress.

## First blocker and receipt evidence

The first production diagnostic is `CONFIRMATION_POLICY_CONFLICT`, phase
`capabilities`, canonical location `/preparation`. Its message says that governing
sources disagree about confirmation. The actual captured evidence contains two
compatible rules from the same host-policy source:

1. Unless explicitly requested otherwise, obtain runtime human confirmation before
   the first external write, with zero writes after rejection.
2. Do not request review of the workflow's own YAML during execution.

These rules govern different actions. They do not express contradictory business
intent, and neither requires the user to resolve a choice for the local classifier.

The third intent page, `page_dec8feab83347617897bd3fc629eb341361de8e63f611e26ad85333ad5f2b7c1`,
has a completed encrypted receipt. It classified:

- `interpret_eeac9f57afde0035b32eaaa0` as `confirmation_required`, producing
  obligation `ob_b623613876b7ce97` with source reference `r_82186c24dc02e3b88ceecb1c`.
  The selected host span starts at 436, length 59, and ends at “first external”,
  omitting the following word “write” and the rejection condition.
- `interpret_e38c03ed002cd38e09f1728b` as `confirmation_forbidden`, producing
  obligation `ob_f8ed01559cc43b45` with source reference `r_031a1a7220bdc856f44c306c`.
  The selected host span starts at 537, length 55, and describes prohibiting YAML
  review, ending before “execution”.

Root-cause classification: **confirmation-policy scope conflation**.
[PlanningSourceDecisions](../src/GnOuGo.Flow.Planning/PlanningSourceDecisions.cs)
accepts these issued-span classifications without an action-specific confirmation
scope. [CapabilityInventoryDecisions](../src/GnOuGo.Flow.Planning/Capabilities/CapabilityInventoryDecisions.cs)
then rejects any coexistence of `confirmation_required` and
`confirmation_forbidden`, before checking whether they govern the same action or
apply to the requested operations. The source text is authentic; authenticity alone
does not establish a policy conflict. The planner correctly granted no execution
authority after that failed check, but the conflict itself is false.

No confirmation safeguard was bypassed, no scenario or policy was weakened, and no
answer was invented. A production correction is outside this live-validation run.

## Offline reproduction and stop boundary

Receipt-only replay from revision **14** reproduced the same stop in one advance,
reusing one completed receipt with **zero provider dispatches**. It verified that
the latest session, durable call rows and cumulative budget remained unchanged.
The frozen planner and harness binaries were not rebuilt after live dispatch.

Exact requests, receipts, source references, revision snapshots and accounting stay
in encrypted storage. The report contains only redacted identifiers, counts and
public host-policy evidence. No private scenario payload was exported to plaintext.

No `medium` diagnostic ran. The available failing path involves intent interpretation
and intent relations; it is not the authorized single bounded behavior or semantic-
review decision. Increasing reasoning would not demonstrate that policy scopes are
validated correctly.

The campaign remains blocked at Stage 1. No behavior approval, final review,
independent generated-artifact execution or exact-hash approval was reached.
