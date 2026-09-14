# Canonical business declarations

Schema-5 interpretation produces direction-neutral `declaration_candidate` obligations.
They retain owned evidence and complete clauses, but establish neither public ports
nor input/output direction. `omission_default` remains a separate modifier candidate;
runtime conditions and fallbacks remain executable evidence.

`declaration_constraint` identifies evidence governing an already declared public
value or its members: type, enum/value domain, nullability/schema restrictions and
preservation. It enters attachment pages directly, never root pages or root presence
evidence. Its only responses are `modifier_of` a canonical declaration and
`unresolved`; it cannot become a port, alias a root, assign a default, change public
presence, or retire silently. `explicit_value` still denotes a supplied business or
runtime value and is not automatically reclassified. Host-owned constraints may
attach to established declarations but grant no operation or public-port authority.

The existing `intent_declarations` phase has two dependent sets of bounded pages:

1. Root decisions select `distinct_input`, `distinct_output`, `deferred_attachment`,
   `not_a_declaration`, or `unresolved`. New roots require source-owned public names,
   scope, declaration evidence and presence evidence. Presence must be `required`
   or `optional`; preliminary obligation necessity and member requiredness cannot
   establish it. Baseline ports are already established; policies cannot create ports.
2. Attachment pages are built only after roots validate. Deferred candidates select
   `same_as`, `modifier_of`, `not_a_declaration`, or `unresolved`. Targets are issued
   canonical declaration IDs, never preliminary candidates or alias chains. A member
   constraint can attach to a proven output without an interpretation-direction lock.

Root pages include complete declaration and omission evidence before choosing port
presence. Ordinary attachments inherit direction, scope and presence, carry only
`presence: unspecified` and a null default-reference slot, and cannot rename ports.
Omission modifiers require optional canonical input targets and exact issued JSON
literal references within omission evidence. Baseline defaults can only be reaffirmed
unchanged. No default and a literal null default remain distinct. Runtime fallback
values and conditional literals cannot authorize output defaults.

Repeated spans, matching names and shared clauses do not prove identity. Multiple
subjects in a clause may establish distinct ports. Duplicate public identities,
unknown references, unresolved subjects, conflicting defaults or unsupported evidence
stop with located `DECLARATION_GROUNDING_UNRESOLVED` before behavior review. Reference
validation proves ownership and structural consistency; arbitrary natural-language
meaning remains the responsibility of bounded adjudication and existing review.

Completed decision pages are the staging area. No roots or partial ports enter the
canonical declaration collection before all required assignments validate. Attachment
requests include the validated root-set fingerprint. Restart revalidates and reuses
completed root and attachment pages without duplicate requests, events or charges.
The existing phase, routine reasoning profile, bounded partitions/escalation,
correction policy and global budgets are unchanged.

Behavior assembly consumes exactly one port per canonical declaration, preserving
source names, declaration IDs, defaults and deduplicated governing clauses. Only
canonical inputs and their aliases become business data producers; modifiers do not.
Initial/restored behavior, repairs and exact acceptance enforce this same proof.

`PlanningDeclarationAssignment` retains reference-only declaration/presence evidence.
Canonical `PlanningBusinessDeclaration` holds the proven direction. Internal source
and declaration proofs are version 4; storage and encrypted namespaces remain 5.
Historical directional interpretations are audit evidence, not automatically upgraded
assignments. Explicit revision/reassessment must establish current proof while keeping
cumulative budgets and durable request history. Archived campaigns remain untouched.

See [neutral-declaration offline validation](planner-neutral-declarations.md) for the
retained failure and [declaration-constraint validation](planner-declaration-constraints.md)
for the attachment-only boundary and separately labelled fixture evidence.

## Offline regression evidence

`DeclarationGroundingTests` contains the sanitized declaration fragments from
session `3f11a9cfe79c41ee85383c81649d0923`, revision 27, separately labeled synthetic
adjudication responses. The previous assembler admitted three inputs and four
outputs. The corrected fixture admits required `record`, optional `threshold` with
omission default `100`, and one `classifiedResult` output retaining classification
and preservation clauses. This proves deterministic assembly with supplied semantic
assignments; it does not replace missing historical model evidence.

Tests also cover independent subjects in one clause, cross-page forward links,
baseline reuse, conflicts, null/omission, quoted names, stale/foreign evidence,
canonical dependencies, review rejection and default preservation. Agent tests and
published smokes cover encrypted/source-generated persistence and the single-session
behavior-acceptance checkpoint. Live results are reported separately.

## Validation before the fresh campaign

The final offline pass has **3,114 passing solution tests**, including **718 planner**,
**841 Core** and **359 Agent.Server** tests; one optional provider test is skipped.
Solution and harness builds, Core/Planning packages, the osx-arm64 Native AOT planning
and encrypted runtime-persistence smoke, and trimmed Agent.Server EF persistence smoke
pass without warnings under the existing documented publish exceptions. The harness
also verifies six classifier/batch reference cases and 18 frozen CodeReview fixture
contracts, without model calls. No frontend implementation changed.

Strict replay of revision 13 of the retained session reuses one receipt, then stops
at the first new `intent_declarations` request with `REPLAY_EVIDENCE_REQUIRED`.
Revision 27 stops with `DECLARATION_GROUNDING_UNRESOLVED` before reconstructing the
unproven behavior. Both replays dispatch zero provider requests and verify unchanged
session, journal and budget. The corrected synthetic fixture separately reaches
exact behavior acceptance with the expected two inputs, one output and default 100.

The initial AOT run exposed an outdated synthetic preparation stub that bypassed
adjudication. The updated stub exercises the production pass; the published rerun
passes. Restored-behavior and UI fixtures now use established baseline ports or
explicitly assert rejection of ungrounded historical ports. No production policy,
reasoning setting or token ceiling changed.

The [single fresh Stage-1 campaign](planner-canonical-declarations-live-validation.md) stopped on a runtime-fallback/omission-default scope error before review. Its exact receipt reproduces offline; no post-live production fix or replacement session was made.
