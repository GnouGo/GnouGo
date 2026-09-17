# Autonomous deterministic planner convergence

## Mission and baseline

Current outcome: **HARD STOP — repeated source-only fallback ownership failure
after redesign**. Intent remains open; no workflow tier is accepted. See checkpoint 2.

Branch: `feat/deterministic-planner-v2`. Starting and accepted production commit:
`741b071a45df7a0b6554bd42bae946b980642c8e`. The checkout was clean and synchronized
with origin at implementation startup. Existing PR: https://github.com/GnouGo/GnouGo/pull/99.

The user authorized autonomous LOCAL → MIXED → local classifier → batch processor
→ CodeReview convergence, using independent harness behavior review and exact
artifact approval. The three complete workflow tiers require 6, 9 and 18 execution
cases. No merge, release, package publication or external review publication is
part of this mission; MCP execution effects remain isolated fixture observations.

The current implementation is the canonical execution-request redesign documented
in [its accepted report](planner-execution-request-authority.md). The previously
stopped fallback-ownership LOCAL remains historical evidence. A verified recurrence
of the same execution-authority failure after this redesign is a mandatory hard
stop, not permission for another targeted fix.

Current proof versions: execution request 1; contribution 7; admission 17;
coverage 3; governing applicability 2; effect 7; runtime 7; source 6; declaration 5;
occurrence/dependency/baseline projection 1. Storage remains Schema-5.

Limits remain: 16 calls per Intent diagnostic, 100 calls per full planning session,
unchanged execution-case budgets, `gpt-5.5-2026-04-24`, all-low reasoning,
12,000 input ceiling, 9,600 dispatch target, 8,192 normal output and the existing
single 16,384 singleton escalation. No blind retries or synthetic live receipts.

## Checkpoint 1 — mission harness preparation

Outcome: **PASS — harness implemented and offline validated**. Pipeline stage: preflight.
Last accepted gate: production offline validation; no live gate on this architecture.

The harness now supports explicit `--campaign` selection with build-bound accepted
production evidence. Historical identities, schemas, journals and allowances remain
unchanged. New frozen manifests bind both binary and configuration evidence.
MIXED requires accepted current LOCAL and zero-call/zero-write restart proof;
full planning requires accepted LOCAL and MIXED, and stages remain sequential.

Independent review checks use current admission authority instead of obsolete v1
assertions. Mission success cannot use historical Stage-1 justification paths for
unsupported or clarification outcomes. The current Flow.Planning README now
describes request proof 1, contribution 7 and admission 17 consistently.

Authority analysis: these are harness lifecycle and acceptance changes. They add
no semantic model decisions, change no planner response schemas, and confer no
production authority. Typed policy comparison uses the same validated in-memory
overlay as diagnostics without rewriting historical source settings.

Offline evidence so far: all 120 accepted production-source hashes, 22 production
DLL hashes and 29 retained validation artifact hashes verified. The earlier focused
execution-request rerun passed 8 tests. Harness and host-test builds passed with
zero warnings/errors and production reference rebuilding disabled. The full solution rerun passed 3,768 tests with zero failures and one optional provider
test skipped, including all 1,136 planner tests. The final host/harness component
passed 597 tests, superseding its earlier 577-test suite result: **3,788 current
component tests**, zero failures, one optional skip. The initial focused harness
run passed 221 tests. Six classifier/batch reference cases, eighteen frozen
CodeReview cases, both synthetic Intent/restart fixtures, published Native AOT
and trimmed EF persistence smokes, and the updated planning package passed.
Existing full-build/frontend/package evidence was retained after hash verification.
The trimmed smoke was rerun with its required isolated directory after an initial
invocation omitted that argument; the temporary host was terminated. Archived
planner evidence remains unchanged.

The original-schema audit checked eleven schema-valid answers and two output-limit
receipts. Current strict replay stops at `REPLAY_EVIDENCE_REQUIRED`, with zero
provider calls and unchanged checkpoint/accounting.

[Machine-readable validation and hashes](planner-autonomous-harness.json).

Live validation: not run. New provider requests/tokens/reasoning: **0 / 0 / 0**.
Partitions, escalations and repairs: **0 / 0 / 0**. Added/removed production model
decisions: **0 / 0**. Historical and synthetic accounting remains separate.

Persistence/replay: existing encrypted Schema-5 stores and tenant boundaries are
unchanged; new gate tests require receipt completeness and current restart proofs.
Git: this journal belongs to the stable preparation checkpoint; live dispatch
requires its successful commit and push. The next checkpoint records its exact SHA.
CI: baseline SHA checks were still
running during initial inspection; no final-code CI acceptance is claimed.

Next autonomous action: commit/push this validated harness-only
checkpoint, freeze `schema5-autonomous-execution-request-1`, then run one fresh LOCAL.

## Checkpoint 2 — fresh LOCAL and durable blocker

Preparation was committed and pushed as
`27594dfe454d941cb721f728c5df3d3e73c63ab6`. Its frozen harness ran exactly one fresh
LOCAL in `schema5-autonomous-execution-request-1-intent:local`, against unchanged
production `741b071a45df7a0b6554bd42bae946b980642c8e`. The encrypted manifest binds
23 DLLs, fixture/catalog/configuration fingerprints and the unchanged limits.
The exchange-rate prerequisite passed with one quote check and zero model calls.

Outcome: **STOPPED**, `INTENT_OPERATION_UNRESOLVED`, `intent_operations`,
`/operations/@r_aa723dffce157ed7b0307912`. The first terminal message is
“Clause qualification did not account for all owned source evidence.” All ten
original responses satisfy their issued schemas. Receipt-only re-entry reproduces
the same rejection, with zero provider calls and unchanged checkpoint/accounting.
Completed-admission restart acceptance is not claimed: admission never committed.

The bounded request answer selects execution support `[325,432)`, ending at
`standard`, for the issued classifier effect. The complete clause is `[325,443)`.
Property qualification selects two nested conditions and `standard` at `[424,432)`
as `runtime_fallback`; it leaves `otherwise.` at `[433,443)` unowned. Fixed support
is preserved and nested selections are contained. The descriptive clause is
explicitly non-requested, then receives a descriptive-property selection.
These are proposed selections, not admitted operations or proved applicability.

Classification: **E — missing semantic ownership**, manifested by a **C —
schema-valid, semantically incomplete answer**. This is not a transport, budget,
identity or structured-output incompatibility failure. No provider replay is
justified. The [fallback-ownership redesign](planner-fallback-ownership.md) already
addressed this exact authority class: an action ending at `standard` while a
source-only `otherwise.` remains outside executable support. The current request
redesign retained that complete-clause invariant and source-only property domain.
The new execution-request separation and fixed-parent containment are not shown
to have regressed; the previously redesigned governing-ownership class has recurred.
The user's post-redesign hard-stop rule applies to that recurrence.

No production patch, replacement campaign, retry, MIXED dispatch, full workflow
session, artifact approval or external execution followed. Intent is **not CLOSED**.
All three workflow tiers remain **not run**. CodeReview has only the earlier
18-case isolated fixture selfcheck; there is no live model workflow execution or
live external effect to report.

### Authority review at the stop

1. Failed invariant: every non-whitespace source position needs exact contract,
   fixed request support or qualified governing/exclusion ownership before admission.
2. Owner: clause contribution qualification and its deterministic complete-coverage
   validator in Flow.Planning; runtime extraction and request selection cannot
   silently absorb missing governing text.
3. Engine knowledge: exact clause, support and contract coordinates already prove
   the residual source range. They do not prove its semantic role or applicability.
4. Remaining bad choice: a variable-length property array can be schema-valid while
   omitting a residual range. Completeness is checked only after the answer.
5. Prevention opportunity: require explicit bounded ownership for deterministic
   residual ranges before dispatch; keep semantic roles and applicability separate.
6. Generic boundary: derive ranges from current references/proofs, never fixture
   words, tool names, provider names or domain-specific heuristics.
7. Invariants at risk: declaration exclusions, immutable request support, exact
   provenance, contained children, non-executable fallback, unique dependency
   authority and fail-closed admission must all remain intact.
8. Independent multiplicity case: separately owned executions with overlapping
   clause evidence must retain distinct effect/occurrence ownership while a
   source-only governing range receives explicit applicability adjudication.
9. Desired authority change: eliminate the model's ability to omit engine-known
   coverage without letting the engine invent the residual range's meaning.
10. Recurrence policy: the source-only ownership architecture was already redesigned;
    another autonomous patch would exceed the mission's authorized loop.

Three structural options for a separately authorized continuation:

| Option | Boundary and tradeoff |
|---|---|
| Exact clause partition before request adjudication | Establish exhaustive source units first, then adjudicate execution/property roles; affects interpretation, multiplicity and several proof domains. |
| Required residual ownership after fixed requests | Derive uncovered ranges from immutable support/contracts and require an explicit bounded outcome for each; directly removes omission authority while preserving the current request boundary. Preferred direction for review, not implemented. |
| Joint exhaustive request/property construction | Encode complete coverage while jointly selecting execution and governing units; larger response domains and risk of reintroducing the competing authority removed by the request redesign. |

Prompt-only repair, widening support, accepting incomplete coverage or importing
synthetic fallback answers cannot satisfy the accepted authority invariants.
No structural option has been authorized past this hard stop or claimed validated.

### Cumulative mission accounting and integrity

| Phase | Calls / reservations | Input | Output | Reasoning |
|---|---:|---:|---:|---:|
| Interpretation | 8 / 8 | 24,804 | 1,573 | 786 |
| Canonical execution request | 1 / 1 | 2,387 | 458 | 272 |
| Property qualification | 1 / 1 | 3,039 | 784 | 512 |
| Coverage, applicability, dependencies, downstream relationships, full tiers | 0 / 0 | 0 | 0 | 0 |
| **Mission total** | **10 / 10** | **30,230** | **2,815** | **1,570** |

Reasoning is the reported output subset, not an additional token total. There are
zero partitions, escalations, semantic repairs, retries, missing receipts or unknown
usage. Six diagnostic calls remain unused; the consumed start and ten reservations
are retained. Preparation, synthetic fixtures and read-only audits used zero model
calls. Historical usage remains in its original campaigns and reports.

All 120 production-source hashes, 22 production DLLs in both working and frozen
directories, all 23 frozen live DLLs, and 29 prior validation artifact hashes were
rechecked unchanged. The live report's archive check is unchanged. Source-coordinate
audit instrumentation was added only to the working harness after the run; the
frozen live harness was not replaced. Its warning-as-error build passed with zero
warnings/errors. All 16 focused execution-request and fallback-ownership tests
passed again with zero failures/skips. The audit exports reference coordinates and bounded selections,
never source text, prompts or reasoning. Encrypted records remain in existing
Schema-5 namespaces behind the public KeyVault API.

[Machine-readable live evidence, original-schema audit and integrity](planner-autonomous-local-1.json).

Git: this blocker journal, audit instrumentation and redacted evidence form the
next semantic checkpoint to commit/push. PR #99 stays open. No production code
changed. The preparation SHA's planner/main CI runs were still in progress when
the blocker was established (runs `35264592533` / `35264592967`); these are not final
mission acceptance. Required final-SHA CI and its attestation are not reached.

Next action: commit/push the durable blocker checkpoint, then request explicit
direction before any further production redesign or live campaign. **MISSION
COMPLETE is not claimed.**

## Completion protocol

At each stable checkpoint record the first meaningful blocker, failure category,
authority owner, fixes, proof changes, offline/live evidence, per-phase accounting,
replay status and commit/push/CI state. Every production change requires the
mission's ten-point pre-patch review and full offline gate. Repeated authority
failures invoke architecture review or the prescribed post-redesign hard stop.

Final acceptance requires all pipeline stages and workflow tiers, current exact
artifact approvals, replay/security/provenance checks, a clean pushed branch and
required CI green on the final exact SHA. The committed journal is accompanied by
a final SHA-bound CI attestation outside tracked source, so recording CI cannot
create a new untested source commit. Until those conditions hold this mission is
not complete.
