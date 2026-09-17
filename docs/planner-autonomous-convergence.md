# Autonomous deterministic planner convergence

## Mission and baseline

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
