# Compact bindings v10 and cancellation ownership

Corrections were implemented and validated sequentially. No paid planning, live
workflow execution, campaign reservation changes or historical workflow execution occurred.
The final candidate is `5a8afbfc`; its production tree is identical to the published
`8371eed1` build. A stale fresh-session test assertion was corrected separately.
PR #117 remains draft.

## Composition, separately from compilation

The retained local composition had 19 tasks, 67 steps and 40 sets. Removing the
identity-forwarding loop and narrowing the cross-scope capture produces **18 tasks,
65 steps and 38 sets** under unchanged v9. YAML falls from **200,410 to 195,783 UTF-8
bytes**. The 17-task attempt failed the accepted output cardinality check: flatten
cannot establish the public ten-product bound. The necessary bounded row projection
was retained rather than weakening that contract. Remaining technical tasks and
rejected attempts are documented in [Gate 1](gate-1.md).

## Compiler and readability

On the same 18-task composition, v10 produces **189,337 bytes**, with unchanged
65 steps/38 sets. It removes unobserved synthetic pure-loop call envelopes, uses
port-derived local names and formats JavaScript by syntax. Whole-envelope/presence
observers retain their representation; strict lookup, first-present selection and
ordered intermediate schemas remain. Native AOT caught an over-aggressive capture
optimization; it was removed and both nullable alternatives now compile and execute.
[Initial gate](gate-2.md), [correction and final local measurements](gate-2-follow-up.md).

An isolated 1,602-record projection reduced total managed allocations from about
218.5 to 156.7 MB for one export and 188.6 to 173.2 MB for multiple exports. These
include engine/recovery infrastructure, not a larger sandbox allowance. Per-record
workflow invocations remain zero. Historical v9 replay preserves its exact 75,217
bytes and hash; no saved record changed. Omitted/v1–v9 lowering remains available.

The frozen-catalog helper comparison retains 7 first-present selects and 4 strict
lookups; both profiles have zero checkedMapping wrappers, 13 native maps and 9
flatMaps. Maximum line length is 268 in both. [Measurements](helper-measurements.json)
use the same composition and original captured contracts (small metadata differences
explain the 12-byte difference from actual local discovery). They claim no reduction
in helpers whose semantics still require them.

## Cancellation and initial generation

[Test-only execution-scope ownership](gate-3.md) joins terminal SDK bookkeeping
before fixture disposal. Controlled success, cancellation and failure barriers prove
that partial publication cannot permit storage deletion while an owner can write.
Unknown completion still blocks cleanup and redispatch; 59 owning checks pass.

[Generation guidance](gate-4.md) uses bound each input names, assembled nested arrays,
direct flatten, compact ID selection and offered/original lookup. Typed copying and
rows/status remain deterministic. The actual compact-schema request and discovery
budget regressions pass. The fresh local fixture reaches review in **one deterministic
call, zero repairs**, then executes actual Browser/Document MCP transport.

## Independent local result

The final local fixture visits ten products in order, processes 29 complete pages
and offers all 12 candidates, including candidates outside generation examples.
Independent inspection verifies exact XLSX strings, missing-field blanks, output
path and Browser closure. Repeated selections, unoffered IDs, both description
paths and completed-run recovery are covered. No “report a problem” affordance is
invented into a business error. Typed row/status assembly uses zero inference.

Execution uses **329 invocations (328 normal, one cleanup)** versus 331 previously.
The complete selection request stays **2,597 bytes**, with 11 deterministic mapping
calls and 12 other deterministic decision/conversion calls. The logical journal is
about **31.14 MB**, compared with 31.51 MB before; this is not physical storage.
No mapping algorithm, limits, permissions, executor or repair authority changed.

All 18 local composition/execution variants pass on the corrected fresh profile.
[Fresh-profile follow-up](fresh-profile-follow-up.md), [all local oracles](all-local-oracles.txt).

## Final gates and live boundary

The complete solution passes with `-warnaserror`: **5,239 passed, zero failed,
13 skipped across 33 projects** ([results](solution-results.json), [log](solution.txt)).
Release packages, Planning/Document Native AOT, Document published transport and
published encrypted recovery pass ([checks](published-checks.json)). The planning
skill validates. All **29 non-skipped CI checks pass on frozen candidate `5a8afbfc`**,
with four publication/optional jobs skipped ([snapshot](ci-results.json)). The two
superseded failures—nullable capture lowering and a stale fresh-session profile
assertion—remain recorded, with their corrections and successful full validation.

Configured pricing/currency and zero-inference local readiness pass. Browser local
navigation/closure and an independently read XLSX are verified ([provider readiness](provider-readiness.jsonl),
[local readiness](local-readiness.jsonl)). The campaign remains **€132.713049 / €150**,
including all unknown reservations, leaving approximately **€17.286951**. Readiness
adds no provider call and changes no financial reservation.

One fresh identity, `compactv1020261011a-amazon-1`, is prepared for at most ten
products; it is **not started**. Source, binaries, configuration, exact evaluation
prompt and independent oracle are frozen in [prepared-live.json](prepared-live.json)
and the existing encrypted campaign manifest. Its six-run template metadata does
not authorize or start additional runs. New explicit GO is required before paid
planning; artifact revision/hash approval and all requirement acknowledgments
remain necessary before execution. No live acceptance is claimed.

The final report is an evidence-only commit after the validated candidate. Use the
clean frozen checkout for the future planning session; do not substitute the evidence
commit or rebuild its binaries silently.
