# Canonical business declarations

Schema-5 source interpretation produces **candidate** `business_input` and
`business_output` obligations. Candidates cannot directly create public ports or
become independent data producers.

Preparation validates source authority, then adjudicates complete clauses through
bounded `intent_declarations` pages using the routine reasoning profile. Each
candidate selects one issued assignment: `distinct`, `same_as`, `modifier_of`,
`not_a_declaration`, or `unresolved`. `distinct` selects an exact lexical name
reference and a declared workflow scope. Lexical indexing establishes coordinates;
it does not classify keywords or infer names. Existing sources can reference only
issued baseline ports. Modifiers may precede declarations or point across pages.

The coordinator commits only a completely covered, acyclic assignment graph. Every
alias and modifier must reach one distinct declaration or existing baseline port.
Repeated spans and shared clauses do not prove identity. Conflicting presence,
defaults, duplicate public names, unknown references and unresolved identity produce
located `DECLARATION_GROUNDING_UNRESOLVED` technical stops before review. They do not
claim unsupportedness or ask the user to fix a technical proof gap.

Public names come from source tokens or baseline contracts. Requiredness is resolved
from complete evidence, independently of preliminary classification flags. Only separately interpreted `omission_default` evidence can supply a new default.
Ordinary declarations and outputs expose only a null default-reference slot. Runtime
conditions and `runtime_fallback` obligations retain executable authority without
entering declaration adjudication. Exact JSON literals inside the omission evidence
are parsed locally; no default and a literal null default remain distinct. Input/output
aliases and modifiers cannot cross directions, and omission modifiers require optional
input targets. See [omission semantics](planner-omission-defaults.md). Existing typed schemas remain authoritative.
Unresolved type and preservation evidence stays available to typed construction.
There is no new schema authoring or executable generation path.

`PlanningSnapshot` retains assignments, canonical declaration references and their
proof fingerprints. Behavior ports carry their canonical identity. Initial and
restored behavior, staged repairs and exact acceptance validate one port per public
declaration, its scope, exact name, requiredness and governing description. Dependency
analysis exposes canonical inputs and remaps historical aliases without treating
modifiers as data producers. Executable validation preserves declared defaults.

Schema and encrypted namespaces remain **5**. Source-generated serialization covers
the additive contracts. Old checkpoints lacking declaration proof cannot authorize
review or acceptance; explicit intent revision reassesses preparation. Revisions
invalidate declaration proofs while keeping cumulative budgets, durable pages and
receipts. Archived campaigns are never rewritten or supplied synthetic receipts.

Redacted declaration events count committed dispositions. Replay revalidates the
proof without duplicating adjudication events. Names and source text do not appear
in convergence metrics. Confirmation adjudication and guards are unchanged.

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
