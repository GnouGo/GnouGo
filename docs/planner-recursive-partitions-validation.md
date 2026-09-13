# Recursive decision-page output partitioning

The change starts from `8bcbb789c23e2ded23ae1ecdbc623afe26e145d2` on
`feat/deterministic-planner-v2`. Declaration semantics, Schema-5 storage, runtime
interfaces, reasoning profiles and token limits are unchanged.

## Captured failure and strict replay

The archived omission-default campaign stopped in session
`3d41ac712ef24278a8b655cd52c8beff`. Replay from revision 23 reproduced
`DECISION_OUTPUT_LIMIT` with one retained receipt and zero provider dispatches.
The original five-decision declaration page truncated, its two-decision child
completed, and its three-decision child truncated. The old mechanism incorrectly
classified both children as semantic corrections and refused the second split.
The session, original repair charges and encrypted journal remain unchanged.
See [the original evidence](planner-omission-defaults-live-validation.md).

Corrected strict replay stops at `REPLAY_EVIDENCE_REQUIRED`: the first new
`confirmation_scope` page identity has no historical receipt. It consumes zero
receipts and makes zero provider dispatches, verifying unchanged source state and
budget. Historical responses are not relabeled to authorize new page origins.
Synthetic partition fixtures are separate evidence, not replacement model receipts.

## Implementation and regressions

Pages record an origin, effective gate and ordered child identities. Truncation
recursively partitions canonical decision IDs at `floor(count / 2)`. Both child
identities exist before the first child reservation checkpoint. Completed children
are revalidated and merged in stable order against the original parent schema.
Corrupted, overlapping, missing or foreign membership fails closed.

Output partitions retain the parent's phase and semantic restriction. They consume
global model budgets but no semantic correction identity or repair allowance.
An original semantic correction is charged once; its partitions cannot obtain a
second correction for an invalid completed answer. Malformed response accounting
continues to use the response-contract gate. Terminal singleton truncation retains
the decision, page, request identity and canonical diagnostic location.

The synthetic `5 → 2+3 → 1+2` regression requires exactly five request scopes:
`abcde`, `ab`, `cde`, `c`, `de`. Both zero and five repair allowances succeed with
zero correction records or charges. The old implementation failed both variants.
Restart tests cover every reservation, receipt and acceptance checkpoint, including
the completed first sibling and nested split, with identical request/tree identities
and no duplicated calls or token accounting.

Additional cases cover semantic-correction partitions and their one original charge,
invalid correction stopping, singleton truncation, zero repair allowance, runtime
budget exhaustion, cancellation, unverifiable dispatch identity, corrupted scope,
redacted reporting and unknown historical origin. EF/KeyVault tests retain tenant
isolation and encrypted page state. The Native AOT smoke resumes a persisted partial
recursive tree and completes its remaining requests without redispatching siblings.

## Offline validation

- Solution build: zero warnings and errors.
- Solution tests: 3,155 passed, one optional provider test skipped; 754 planner and
  364 Agent.Server tests included.
- Benchmark harness build: zero warnings and errors.
- Harness selfcheck: six classifier/batch reference cases and all 18 CodeReview
  fixture checks passed with no model or business transport calls.
- Native AOT planning and encrypted persistence smoke: passed, including recursive
  partition restart, with the existing documented publish exceptions.
- Trimmed Agent.Server publish and EF/SQLite encrypted persistence smoke: passed.
- Core and Planning NuGet packages: built successfully. Both publishes and packages
  have zero warning/error diagnostics under the existing documented exceptions.

The single authorized live Stage-1 result is recorded below after completion.
No Stage 2 or replacement start is authorized.
