# Compact construction evaluation

This record separates deterministic implementation validation from real business
outcomes. The compact candidate has not yet established live acceptance. Prior
failures remain in [the incremental evaluation record](incremental-taskplan-live-2026-10-19.md).

## Financial and comparison boundary

Before the first compact pilot, read-only provider readiness verified the existing encrypted campaign
`schema-portability-20261019-incremental`: 14 requests and 14 receipts, no uncertain
attempts, no outstanding reservations, and a known spending upper bound of
€1.2371723096063272016351195234. The campaign ceiling remains
€240.0301038327063045424449473; available allowance is
€238.7929315230999773408098277766. Inspection evidence hash:
`ad7841f51ba598a4f635640bd9aec6670140ef8a83fa8535f75807fdd4258cd2`.
This balance must be checked again before paid dispatch.

The lost historical ledger is not recreated. Its separately retained conservative
upper bound of €159.96989616729369545755505266 remains part of the original €400
exposure boundary. No previous request, reservation or uncertain execution is
replayed or treated as free spending.

The comparison baseline is `56942f5bdfad69ee6f05ccd793b712eaf9f33ca5`.
Both revisions use the same configured `OpenAi / gpt-5.5-2026-04-24` deployment,
medium reasoning, 64 calls, four total corrections, three corrections per draft,
8,192 generation/correction output tokens, 2,048 Judge output tokens and ordinary
graph limits. The baseline retains its 32,000-token complete input ceiling. The
candidate captures 3,000 for intent/selection/discovery, 8,000 for generation and
correction, and 4,000 for scoped Judges. These limits include response schemas
and serialized request overhead; no automatic allowance or model increase is
permitted.

The frozen baseline MCP producer bundle contains 568 files and 517,387,459 bytes.
Its manifest hash is
`ee150322910e5995ece8c1b514a5a26b7b07c988095e3f87225a185851dce46e`.
Both arms must use these exact producer binaries and unchanged independent
business oracles. Run-specific confined output roots are recorded separately;
only those exact authored root occurrences may be normalized when comparing
otherwise identical scenario inputs.

## Acceptance protocol

First run candidate diagnostics for Collections, Amazon and Review PR. Retain
failed attempts and costs; a source correction requires a fresh frozen identity.
Only after all three complete successfully, run three alternating paired
repetitions per scenario: 18 measured runs, nine on each revision. All nine
candidate runs must pass their independent business oracles, with no observed
omissions or unwanted effects, no compact request over its captured ceiling,
and reduced peak generation/Judge request sizes where comparable baseline
evidence exists. Compilation and favorable scoped reviews do not establish
business success or authorize execution.

Reports retain complete-request bytes, versioned component estimates, actual
whole-call provider usage, calls, corrections, cost, planning/runtime latency,
failures and uncertainty. Component estimates are not billed token usage.
Artifact-bound delegated review and exact individual runtime confirmations remain
mandatory for authorized effects. Financial limits, unsafe uncertainty,
environmental blocking or failed convergence leave the result incomplete.

## Current results

The first compact Collections pilot failed before accepting a draft. It is
retained under source `41faf8ce4529f2a2514b6366e83ddce89f525bbe`, run
`41faf8ce4529f2a2514b6366e83ddce89f525bbe:pilot:collections:1`, and session
`21bd4fe7a1cc25b243bf9b3da42f43ab2f0880137bbd4605bc5b18a96bc930ca`.
Its measurement manifest is
`fd43756584a76b2fd9103d0735c6ce27a97525d636bf6462adcbcd3b03fd26c5`;
its original harness comparison manifest is
`ee874502ccdf642150ccec8d1c8cf0b2c73f3cf8ddcc46e2a4f150a59b9e5748`.
Neither identifies a completed paired comparison.

One intent-indexing call was followed by five rejected substep selections. The
selection responses supplied internal group/input output names where accepted
public output names were required. The selector context omitted the public-output
directory, its schema allowed arbitrary strings, and the repeated compound
diagnostic did not identify `outputNames`. Deterministic validation rejected them with
`CONSTRUCTION_STEP_INVALID` at `/step`. Four corrections exhausted the captured
allowance, ending with `CONSTRUCTION_CORRECTION_ALLOWANCE`. All five invalid
selection events and the allowance-stop event remain in the failure history.
There were zero accepted drafts, no scoped Judge calls, no FinalReview artifact
and no execution or external effects. Business success is **not established**.

| Pilot measurement | Retained result |
| --- | ---: |
| Model calls / corrections | 6 / 4 |
| Peak complete serialized request | 6,967 bytes |
| Peak conservative input estimate | 2,579 tokens, selection phase |
| Provider input / output tokens | 6,658 / 4,039 |
| Planning elapsed time | 62.411 seconds |
| Known cost | €0.1372611747978316893272905003 |

All six requests stayed below the captured 3,000-token phase ceiling. This
demonstrates bounded request size for this failed attempt only; it does not
demonstrate a complete workflow, business correctness or improvement over the
baseline. The corpus summary's `approved_execution_correct=true` was computed
with no executed variants and must not be read as a successful business result.
Its coverage was one of eight historical pilot cases and its gates failed.

Fresh post-pilot readiness independently reconciled 20 requests with 20 receipts,
no uncertain requests, zero unknown attempts and zero outstanding reservations.
The verified campaign spending upper bound is
€1.3744334844041588909624100237. Against the unchanged campaign ceiling, the
remaining allowance is €238.6556703483021456514825372763. The pinned model and
configuration passed readiness without model calls. This inspection's evidence
hash is `416d1a861ac928d131dc181df972ce890066ce1df46f07b192da208d4566c38a`.
Further spending must remain reconciled against this existing ledger. The
sanitized pilot log has SHA-256
`88989e2c34e7948b5b92a0c83e65b403b6565b0e52fa6710981f80b89f3049f5`.

A generic selection-contract correction is in progress. Further diagnostics
require a fresh committed source and fresh run identities; the failed pilot and
its spending remain retained. Amazon, Review PR and the measured paired cohort
have not established compact live acceptance at this checkpoint.
