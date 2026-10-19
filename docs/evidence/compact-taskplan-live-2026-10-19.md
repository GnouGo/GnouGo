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

The generic selection-contract correction was frozen in the next source below.
The failed pilot, exact issued requests, receipts and spending remain retained.

### Frozen diagnostic iteration `88a1afe`

Source `88a1afe023486fe2813044c4875a41084242e97f` passed 155 focused
deterministic tests and a warning-free benchmark build before these real-model
diagnostics. All three diagnostics stopped without a FinalReview artifact or
accepted checkpoint. None reached MCP discovery or workflow execution. The
frozen producer bundle was available, but these attempts provide **no real MCP
runtime coverage and no successful business outcome**.

| Scenario | Calls / corrections | Provider input / output tokens | Planning time | Known cost |
| --- | ---: | ---: | ---: | ---: |
| Collections | 4 / 0 | 4,724 / 2,526 | 37.305 s | €0.0883320003554607660179507687 |
| Amazon | 1 / 0 | 864 / 8,192 | 141.203 s | €0.2222340709144228205811783524 |
| Review PR | 2 / 0 | 1,803 / 9,116 | 108.546 s | €0.2510397227406025059984004265 |

Collections now passed intent indexing and substep selection, and generated a
deterministically valid opening of a reusable group. Its local Judge treated
the intentionally open group, whose body and exports were deferred, as a
business omission. The report also appended explanatory text to evidence
pointer strings instead of returning exact reference paths. Reference validation
therefore rejected the report as `CONSTRUCTION_REVIEW_INCONCLUSIVE` at
`/construction/candidate`; the draft was not accepted. This is a failure of the
scoped review protocol, not evidence that the unfinished workflow satisfies the
business request. Its conservative complete-request peaks were 2,695 tokens for
selection, 3,611 for generation and 1,960 for the local Judge (7,316, 10,065 and
5,110 serialized bytes respectively), all within captured limits.

Amazon stopped on its first intent-indexing call with `MODEL_OUTPUT_LIMIT` at
`/phases/construction_intent`. The provider reported all 8,192 output tokens as
reasoning tokens and returned zero content bytes. The complete request was
5,673 bytes, conservatively estimated at 2,147 input tokens. Review PR completed
one indexing call, then stopped on the second with the same diagnostic: that
second response also consumed 8,192 reasoning tokens and returned zero content
bytes. Its peak complete request was 5,856 bytes, estimated at 2,208 input tokens.
The two calls used 9,116 output tokens in total. Both stopped at the existing
output allowance; no limit escalation, model change or uncertain replay was used.
Small input requests alone did not establish useful output or convergence.

The exact retained identities are:

- Collections run:
  `88a1afe023486fe2813044c4875a41084242e97f:pilot:collections:1`;
  session `bc2b8126865c1200ce04d98d9eea9e9adac955bf5811bf1b0c87ed4951c6a90f`;
  failed Judge request
  `bc2b8126865c1200ce04d98d9eea9e9adac955bf5811bf1b0c87ed4951c6a90f:4:988f3e70f84ab4eea672f3440a0d00624bdb88e09eb74ffbaf817e86baacae52`.
- Amazon run/session: `compactdiag88-amazon-1`; failed request
  `compactdiag88-amazon-1:1:af306e988b3122d3ae7c80bef6f0795730c0e8a834b04609d51837c4fb4af824`.
- Review PR run/session: `compactdiag88-code-1`; failed request
  `compactdiag88-code-1:2:c9003b5d9f09d80707dc6517e7ef3ec3547145b5477d32a3797e8090206a7b48`.

The three sanitized diagnostic log hashes are respectively
`8e3cd2b6d7c23c1fc5655dca5beadf3e2d5fa795fb94a0a1fc0ea8e20d84236e`,
`ed8befd92f93b6e254324e824f8911f2f68657d2d50cbf1a5d294b5e1e9823f5`,
and `d414f116b83a9b61ad8ed956e9165d69bcbfc6430df8774ca0bd182188119185`.
These runs retain the original measurement manifest
`fd43756584a76b2fd9103d0735c6ce27a97525d636bf6462adcbcd3b03fd26c5`.

This iteration used seven calls, 7,391 provider input tokens, 19,834 provider
output tokens, 287.054 seconds of planning and
€0.5616057940104860925975295476. Including the earlier `41faf8c` pilot, the
compact diagnostics consumed 13 calls and €0.6988669688083177819248200479.
Fresh post-iteration readiness reconciled 27 cumulative campaign requests with
27 receipts, no uncertain requests, zero unknown attempts and zero outstanding
reservations. The verified spending upper bound is
€1.9360392784146449835599395713, leaving
€238.0940645542916595588850077287 under the unchanged campaign ceiling. The
readiness evidence hash is
`dfc086458751baa04325f2dbb5925508e09ad65c4825296ca494f9ffcfc42bf1`.

An independent comparison manifest,
`9730c90fda7219627edc9e541a2a86ac8c18563648a174ece8fa84477598cc99`,
froze 18 fresh baseline/candidate run identities, model settings, independent
oracles, scenario inputs and producer hashes. All 18 remain unissued at this
checkpoint. The measured paired campaign has not run, and no comparative
business-success, cost or latency improvement is claimed. Further source
corrections require another frozen source and fresh diagnostic identities while
preserving these failures and cumulative accounting.
