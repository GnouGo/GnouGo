# Runtime admission from execution evidence

Local computation retains the existing internal `local_processing` representation. Moving it entirely out of preparation would change behavior ownership, routing and dependency contracts without eliminating further capability decisions: capability matching already resolves local operations without a model call. The retained representation uses a native behavior node and null external capability binding.

The existing intent response now has two independent facets: semantic obligations and runtime execution evidence. Declaration alternatives are unchanged. Runtime evidence distinguishes planning directives, public contracts, policies, local behavior and requested external/human/resource actions. A clause can contain more than one role. Responses select owned spans and subject references; the coordinator owns identities and resolves source text.

Canonical declarations resolve before admission. Contract-only evidence, omission defaults and workflow-authoring directives are excluded from admission. A separate runtime action in a shared declaration clause remains eligible. Preliminary operation labels confer no authority. Local evidence has no external kind alternatives during admission, while runtime resources require explicit ownership and create/acquire/release/delete semantics. Constraints-only sources cannot create actions; baselines require current exact node authority.

Distinct evidenced runtime occurrences resolve deterministically. Governing conditions and fallbacks attach to an established occurrence only with the same issued subject, kind, presence and baseline authority. A unique eligible occurrence resolves without a call. Ambiguous reuse exposes only issued compatible targets through the existing bounded `intent_operations` pages. Missing identity or execution proof stops with a located diagnostic; it does not reopen broad kinds or ask the user a technical question.

Both preparation paths require at least one proven local executable behavior or external/human/resource action. `INTENT_OPERATION_UNRESOLVED` remains when neither exists. Partial evidence grants no execution authority. All source scopes must be accounted for, all proofs must remain current, and revisions apply before the combined operation set commits. Mixed local/external dependency analysis, confirmations, behavior assembly, typed construction and deterministic lowering are unchanged.

Current operation proof version **3** binds runtime evidence and canonical declaration coverage; runtime evidence proof is version **2**. ConstraintsOnly runtime policy scope is engine-owned, and exact canonical contract coverage excludes preliminary local occurrences. See [the runtime-evidence correction](planner-runtime-evidence-convergence.md). The results below retain the historical version-2 campaign unchanged. Adding derived admitted obligations no longer invalidates declaration proofs. Source proof **4**, declaration proof **5**, snapshot schema **5**, encrypted namespaces, receipts and cumulative budgets remain unchanged. Historical admission proofs require explicit reassessment; archived sessions are never upgraded or resumed automatically.

## Offline evidence

The retained failure from session `984756f1099448a3856e6280d53ebba3` is described in the [frozen operation campaign](planner-canonical-operations.md). Read-only replay reproduced it with two historical receipts, zero provider requests and unchanged storage. Changed request schemas cannot reuse those responses as fresh evidence.

Generic synthetic fixtures separately cover local native execution, directive/contract exclusion, mixed read/local ordering, explicit runtime resource ownership, policy authority, baseline coordinates, complete coverage, ambiguous reuse, revisions, cancellation and replay. The retained declaration fixture remains exactly `record`, optional `threshold` with omission default `100`, and `classifiedResult` with enum and preservation evidence. Native execution checks exercise accepted, rejected, boundary, omitted default, invalid input and null threshold. These are offline fixtures, not a successful live Stage-1 campaign.

## Isolated diagnostics

`schema5-runtime-admission-diagnostics-1` permits the pure-local classifier followed by a generic explicit external read plus local classification. Each case supplies a clearly identified canonical declaration fixture and obtains fresh runtime interpretation through production builders and the injected KeyVault-configured transport. It stops after operation admission; it creates no planning session, behavior acceptance or workflow approval.

Each case has at most eight provider requests, including existing corrections, partitions and singleton escalation. Profiles stay `low`; input limits remain 12,000/9,600, normal output 8,192 and bounded singleton output 16,384. Separate encrypted checkpoints, requests, receipts, budgets and an EF index preserve restart safety. Completed receipts are reused; unverifiable reservations and stopped cases never redispatch. The first meaningful blocker stops the two-case sequence.

The harness freezes commit, DLL hashes, input/configuration and fixture fingerprints, and archived accounting before dispatch. Reports distinguish synthetic preconditions from fresh evidence, record actual decision domains, and preserve unavailable usage as unknown. Neither diagnostic constitutes Stage-1 success.

## Frozen implementation and results

Production and the diagnostic harness were frozen at **`9487b2e28f89b4e6749634761de9eddfd937d430`**. Offline validation passed **3,353 tests**, including **888 planner**, **855 Core**, **412 Agent.Server** and **67 integration** tests; one existing optional live test was skipped. All **75** focused harness checks, frontend/package builds, reference selfchecks, Native AOT planning/encrypted restart and trimmed Agent.Server EF persistence smokes passed. Final builds and publishes were warning-free under the existing documented exceptions, with no added suppressions.

Exactly one isolated case started: **local**. It stopped before interpretation completed, at the harness's eight-provider-request ceiling. The mixed case was **not run**. No planning session, behavior acceptance, construction, artifact approval, replacement case or Stage 2 was started.

| Measurement | Local diagnostic |
|---|---:|
| Verified provider calls / durable journal requests | 8 / 8 |
| Coordinator reservations / blocked before journal dispatch | 9 / 1 |
| Unverifiable dispatches | 0 |
| Input / output / reasoning tokens | 26,932 / 3,696 / 2,515 |
| Largest estimated / actual input | 5,304 / 4,048 |
| Completed pages / pending page | 8 / 1 |
| Completed source decisions | 15 |
| Output partitions / singleton escalations | 0 / 0 |
| Semantic corrections / repair charges | 0 / 0 |
| Operation-admission model calls | 0 |
| Committed runtime evidence / operation set | Neither committed |

All eight responses satisfy their original JSON schemas. The terminal exception is `InvalidOperationException: The isolated case exhausted its eight-request ceiling.` The ninth coordinator reservation never reached the journal or provider. The raw frozen report's `journalReservationsWithoutReceipt` field includes that coordinator-only reservation; it must not be interpreted as an unverifiable dispatch.

The response-size estimator limited page packing under the unchanged 2,048-token structured-answer target. Completed pages held one to three decisions, with estimated answers of 826–1,728 tokens. Requests were comfortably below the 9,600 input target; this was neither input-limit nor output-limit exhaustion. The existing interpreter also processed the supplied port-only baseline and host constraints: the 15 completed decisions covered three baseline, six host and six user scopes. Complete interpretation requires more than the authorized eight calls in this captured layout.

Partial candidate evidence is **not canonical admission**. Its runtime facets contain seven contracts, four policies, two planning directives, two local behaviors and one unresolved classification. There are no staged external-operation kinds in these completed receipts. In particular:

- The authoring clause correctly separated `planning_directive` from the local `classifying a single record` span, without lifecycle authority.
- A host-constraint decision, `interpret_566adf3b8e5bc3101e0a7ebc`, returned `unresolved`. It is schema-admissible but would block runtime-evidence validation. The request ceiling stopped the run before collective validation.
- `Preserve the original id and amount.` was staged as a distinct local behavior. Constraint-versus-occurrence separation therefore remains unproven by this diagnostic.

Increasing the diagnostic allowance alone would not establish convergence. The captured evidence identifies both a diagnostic-isolation/request-volume limitation and unresolved semantic work. No production patch or further provider request followed the stop. No missing receipt was substituted. Read-only audit revalidated the eight schemas through public Core contracts and read encrypted evidence through public KeyVault APIs. Frozen production sources, all 23 diagnostic DLLs and archived accounting remained unchanged.

Artifacts: [offline checks](planner-runtime-admission-offline.json), [frozen manifest](planner-runtime-admission-manifest.json), [raw redacted report](planner-runtime-admission-report.json), and [blocker and partial evidence](planner-runtime-admission-blocker.json). These results do **not** establish live admission convergence or Stage-1 success.
