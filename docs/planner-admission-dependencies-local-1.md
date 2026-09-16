# Admission-owned dependencies: one frozen LOCAL

## Outcome

**VERIFIED BLOCKER — category C.** Campaign `schema5-admission-dependencies-diagnostics-1` ran LOCAL exactly once against production `1b5513c28032c718eeb9ea4179e5c7981c51bbba`. It stopped during interpretation with `INTENT_OPERATION_UNRESOLVED`. No typed business outcome or canonical operation set was established.

MIXED and Stage 1 were **not run**. There was no production patch, replacement diagnostic, isolated model retry or provider probe. Detailed redacted evidence is in [the JSON report](planner-admission-dependencies-local-1.json).

## First meaningful blocker

- Decision: `interpret_a211f8afaa7bca2154080a84`.
- Location: `/operations/@runtime_ea48b9c2d364d80145d7e033`.
- Finding: “The source does not establish its runtime execution boundary.”
- Source authority: `ExistingBehavior`, source `existing`, reference `r_23fee5d3349dfc28181248f5`.
- Origin: request **6**. Interpretation gathered all **11** receipts before combined runtime validation detected the stop.

The selected source scope is a 49-character structural tail of the supplied baseline JSON, at offset 1024:

```text
,"value":{"kind":"null"}}]}],"entrypoint":"main"}
```

Its exact returned assignment was:

```json
{
  "obligations": [
    { "start": "b0", "end": "b1", "kind": "information", "required": true }
  ],
  "runtime": [{ "role": "unresolved" }]
}
```

The original schema accepts this assignment. The request fingerprint is `d72406d24fb71cfd5bcf6cc70e9f006b506f57226366ae3a7063d335653efa2d`; its receipt fingerprint is `e28a411c24910899d457fdf6339ed18d2a15ee04343bb85a2f90f8512067f16a`. Exact request identity, schema/context fingerprints and owned coordinates are retained in the JSON report and encrypted journal.

## Root cause

The existing baseline is an engine-owned, port-only graph. `IntentSources` serializes it as `existing_workflow` text, and interpretation indexes that serialization with the general source-span mechanism. The final fragment was offered a model-owned runtime-role domain containing `unresolved`.

The model abstained on this structural fragment; deterministic runtime validation then failed closed. This is an interpretation-domain blocker involving already available structural information. It is not provider unavailability, output exhaustion, or a verified failure of the new admission dependency proof. Effect grounding and dependency resolution were never reached.

## Authority analysis

The engine already owns the baseline ports, entrypoint and absence of executable baseline nodes. Asking a model to infer an execution boundary from a JSON tail delegates structural authority unnecessarily. The validator correctly refused unresolved evidence; weakening that check is not justified.

All nine host-policy runtime scopes remained engine-owned. Source interpretation produced three preliminary local-behavior entries, but those are evidence candidates, not admitted operations. Their canonical identity, dataflow and governing attachments were not assessed.

The public declarations and attachments were validated as frozen diagnostic preconditions before dispatch. Live interpretation stopped before the harness installed that projection. This run therefore proves neither fresh declaration convergence nor preservation of the expected canonical classifier operation.

## Proposed action

Stop this campaign. The next gate is an architecture review of how existing structural baseline contracts enter source interpretation. The protocol threshold applies because a model still selects among choices for engine-owned structural facts. No targeted patch or new run is included here.

Keep the admission-proof implementation frozen. This run does not justify a dependency filter or dependency-proof redesign. Another verified dependency-authority blocker, if reached later, still requires its own architecture review.

## Validation performed

**Offline checks rerun before dispatch:** 3,494 solution tests passed across 29 projects, including 993 planner tests; one existing optional provider test skipped. The focused harness/persistence selection passed 124 tests. Three added cases reject a LOCAL dependency-model decision, an operation-producer edge and legacy effect-producer authority. Builds completed with zero warnings/errors and without rebuilding production references.

The synthetic admission selfchecks passed LOCAL and MIXED without model calls. All six classifier/batch reference cases and 18 CodeReview reference cases passed. The existing published Native AOT planning/encrypted-restart and trimmed Agent.Server persistence smokes passed. Prior package/frontend/production-build evidence was retained after verifying its recorded log hashes; those builds were not rerun over frozen production.

**Prerequisite:** one existing-provider currency check returned a valid USD→EUR quote dated September 15, 2026. No quote was injected into planning. Effective model, policy, catalog, budgets, prompt and declaration-fixture fingerprints matched the previous campaign. LOCAL preflight required 11 interpretation pages, within the unchanged sixteen-reservation allowance.

**Historical replay:** all 12 previous receipts validated against their original schemas. Changed-request replay stopped at `REPLAY_EVIDENCE_REQUIRED`, with zero provider calls and unchanged archive state. Historical receipts were not substituted for fresh evidence.

**Fresh evidence and replay:** all 11 new responses validated against their original schemas. Completed-page offline replay reproduced `INTENT_OPERATION_UNRESOLVED` at the same location, with zero provider dispatches and zero additional receipt dispatches. Read-only KeyVault inspection identified the exact source fragment and response. No synthetic answer replaced any live response.

All 22 production DLLs and unchanged fixture sources matched their pre-run hashes afterward. The harness hash is `ad1bd3d547254d1aee8a4fcb51a39fdbe1e8cf39cfe782c6a4b2a0a2b7c33e67`. Archived accounting fingerprint remained `aee7057d62b71f10999fcc3be41ed9e38bb32e147ab6e6201cd4332d58d3e638`. The failed diagnostic checkpoint and budget also remained unchanged during inspection/replay.

## Decision accounting

| Class | Issued model decisions | Verified calls | Assessment |
|---|---:|---:|---|
| Interpretation | 22 | 11 | One unresolved runtime scope |
| Effect realizations | 0 | 0 | Not reached |
| Effect governing | 0 | 0 | Not reached |
| Occurrence identity | 0 | 0 | Not reached |
| Operation dependencies | 0 | 0 | Not reached |
| Later relationships | 0 | 0 | Not reached |

- Eleven coordinator reservations, eleven journal requests and eleven verified receipts; zero unverifiable requests. All transport retry flags were disabled.
- Input: **23,527** tokens. Output: **4,011**, including **2,340 reasoning**. Final-answer tokens: **1,671**, derived from compatible output-minus-reasoning counters. No receipt usage is missing.
- Largest input: **4,735 estimated / 3,213 actual** tokens. All issued requests used `low` reasoning and an 8,192 output ceiling.
- Eleven completed initial decision pages; **zero partitions, escalations or semantic repairs**. No repair allowance was consumed.
- Nine model-owned policy runtime facets remained eliminated by existing source-authority handling; this iteration removed **zero new planner decisions** and introduced **zero planner decisions**.
- Canonical operations/dataflow, deterministic operation assignments, governing attachments and declaration-coverage exclusions are **not established**, rather than inferred from preliminary fields. Zero identity/dependency calls here reflect an early stop, not convergence or measured call savings.

## Next gate

**Architecture review — structural baseline interpretation.** Stop here. MIXED and Stage 1 remain not run and are not authorized by this result.
