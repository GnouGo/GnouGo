# Omission defaults and runtime fallbacks

The existing interpretation ontology separates `omission_default` from
`runtime_fallback`. The former supplies a public input when absent; the latter
selects an executable result when other runtime conditions do not match. A defaulted
input contributes both a declaration candidate and an omission-default candidate
in the existing interpretation response. No new planning phase or runtime interface
is introduced.

Declaration response schemas permit new non-null default references only on
`omission_default` / `modifier_of` assignments with optional input targets. Ordinary
input/output declarations permit only a null reference slot. A non-null reference
resolving to JSON `null` still denotes an explicit omission default. Literal domains
come from the selected omission evidence, not every literal in its containing clause.
The complete clause remains context; missing value/subject proof has the existing
located unresolved outcome. Baseline defaults can only be reaffirmed identically.

Aliases and modifiers use direction-compatible targets. Forward references remain
possible; the coordinator resolves their complete graph and checks requiredness,
conflicting defaults, ownership, baseline contracts and canonical scopes before
committing. Runtime conditions/fallbacks do not enter this graph; their original
evidence remains available in governing behavior and executable/semantic context.
No language, provider or scenario keywords drive admission.

The omission correction introduced internal proof version 2; current neutral declaration adjudication uses version 3 (see [canonical declarations](planner-business-declarations.md)). Encrypted
snapshot/request/receipt/budget namespaces remain Schema-5. There is no compatibility
alias for `default_value`: historical proofs cannot authorize current assignments.
Explicit intent revision reassesses preparation without resetting cumulative
budgets or replacing archived receipts. Confirmation adjudication and guards,
reasoning profiles, limits and deterministic lowering are unchanged.

## Regression and replay boundaries

The historical session `b8c6201e646247a1bcdf25aa5ab1d991` classified `standard otherwise.`
as `default_value` and selected a condition literal `false` as a required-output
default. Baseline replay from revision 18 reproduced its declaration failure with
one receipt, zero provider dispatches and unchanged persisted state. Its encrypted
evidence and [original report](planner-canonical-declarations-live-validation.md)
remain untouched.

`OmissionDefaultTests` supplies separately labeled synthetic corrected semantics and
checks the response schemas directly. It covers the correct two-input/one-output
behavior, preservation and runtime fallback evidence, default 100, conditional
literals, incompatible direction/requiredness, baseline targets, missing literals,
receipt-safe reuse and rejection of the historical ambiguous kind. Existing tests
retain null-versus-omission, cross-page modifiers, canonical review/default guards
and encrypted persistence coverage.

The new single-session campaign continues past exact behavior acceptance only on
explicit advancement. Final success requires mandatory validations, six independent
execution cases and exact artifact-hash approval. No result from synthetic replay
substitutes for live evidence. Live outcomes and final check totals are recorded
separately after the frozen run.

## Offline validation before freeze

The final solution pass has **3,136 passing tests**, including **736 planner**,
**841 Core** and **363 Agent.Server** tests; one optional provider test is skipped.
The 32 focused harness tests pass. Solution/harness builds, Core/Planning NuGet
packages, the osx-arm64 Native AOT planning/encrypted-runtime persistence smoke
and trimmed Agent.Server EF persistence smoke pass without warning diagnostics
under the existing documented publish exceptions. No frontend code changed.
The harness also checks six retained reference cases and 18 frozen CodeReview
fixture contracts with zero model or business transport calls.

Before the correction, the first 16 new omission regressions failed. The completed
suite includes 17 omission cases plus runtime-fallback source-authority coverage.
An older clarification fixture now supplies separate synthetic omission evidence
instead of directly defaulting a declaration. These changes do not relabel old
receipts as corrected evidence.

After rebuilding the harness with current production, strict replay of the archived
revision 18 stops with `INTENT_SOURCE_AUTHORITY_UNPROVEN` on the obsolete source
proof, with zero receipts replayed and zero provider requests. It verifies unchanged
session, journal and budget. The baseline replay with the frozen prior binaries
reproduced the original declaration failure using one retained receipt.

The [single frozen live run](planner-omission-defaults-live-validation.md) stopped
on verified declaration-response truncation before BehaviorReview. Its partial
classification modifier has no default; no complete canonical declaration set was
committed. The exact receipt replays offline. No post-live production patch or
replacement session was made.
