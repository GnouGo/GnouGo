# Fresh typed-bindings planning after explicit GO

The user authorized fresh paid planning with `YES GO`. Frozen production/harness
candidate: `7a76fc951d31dec26978f6c01a07b76ae71ea154`. Campaign:
`schema-portability-20261002`; cohort/run: `typedbindings20261009b` /
`typedbindings20261009b-amazon-1`; ten products maximum. No production source was
changed during this planning/review work.

## Retained attempts

| Revision | Result | Review and next action |
| --- | --- | --- |
| 3 | Compiler passed, artifact not approved | Whole observation snapshots still entered four interpretations; product selection deduplicated; final normalization could synthesize a missing-result row. The new guidance did not reliably produce the required composition on this provider response. Retained [plan](plan-r3.json), [YAML](amazon-r3.yaml) and [explicit structural revision](revision-r3.json). |
| 5 | Stopped with `REQUIREMENTS_OUTPUTS_CHANGED`, `/outputs/rows` | Data adaptation now uses compact page views, item-scope projection, flatten and double lookup. However, flattening per-product singleton row arrays removed the accepted `maxItems: 10` from the inferred public output. The compiler conservatively retained its existing rules. [Plan](plan-r5.json), [targeted revision](revision-r5.json). |
| 7 | Compiler and accepted-interface validation passed | One object per product and direct loop-output copying retain the bound; a local non-null original-URL assertion precedes product navigation. A remaining selector-copy interpretation needed source grounding. [Plan](plan-r7.json), [YAML](amazon-r7.yaml), [targeted revision](revision-r7.json). |
| 9 | `final_review`, no diagnostics | Only selector adaptation mode/inputs changed: existing source-grounded extraction, observed selector array and the already-intended explicit null for no selection. Requirements, remaining task identities and bindings stayed unchanged. Awaiting concrete approval; [review](amazon-review.md), [plan](plan-r9.json), [YAML](amazon-r9.yaml). |

The three review revisions used existing authority and cumulative accounting. They
were not automatic repairs and did not reset the session. No validator, compiler,
runtime limit, mapping algorithm, repair permission or producer contract changed.
The retired session `consumerbindings20261009b-amazon-1` and historical unknown
invocations remain untouched.

The structural revision preserves the eight external operation contracts and their
business ordering. Its selected-consent adapter introduces an explicit loop over
the array-valued lookup, with the observed-reference click inside it; it does not
claim that every task identity/scope stayed byte-identical during that authorized
structural revision. Subsequent targeted patches changed only their named fields.

## Offline checks of the paid proposals

Read-only compilation reproduced revision 5's missing output bound. A local clone
returning one row object from each product iteration, followed by direct copying,
produced the unchanged non-null row item contract and `maxItems: 10`. This justified
the two contract/binding edits, instead of weakening the accepted output interface.

A zero-inference sandbox probe checked the selector-copy adaptation with an observed
selector, an empty selection and an explicit null. The existing sandbox accepts
the original tokens and the explicit business null; no generated string selector
or undeclared default was introduced. The actual runtime will generate and validate
its own mapping, so this check is not reported as live provider execution.

[Final read-only replay](replay-r9.json) passes current compilation and semantic
validation with zero model calls and no saved-session mutation. The full local
Browser/Document execution and independently inspected workbooks were completed
in the [preceding offline correction](../typed-bindings-offline-2026-10-09/README.md).
That fixture evidence does not establish that this fresh provider-authored artifact
has executed successfully.

The actual retained revision checks are summarized in [review checks](review-checks.json).
The new artifact has 47 TaskPlan tasks, 50 compiled steps, 16 sets and 153,302 UTF-8
YAML bytes (2,199 lines). No runtime invocation/journal-size reduction is claimed
from those static counts.

## Accounting and execution boundary

Verified live planning: **6 logical requests, 7 physical attempts, 0 automatic
repairs, 3 explicit revisions, 2 discovery reads; 67,285 input / 23,042 output
tokens; €0.911958; 652,335 ms**. The extra physical attempt belongs to the existing
transport retry path; this run has no unknown attempt. All six logical requests
have retained responses.

[Campaign accounting](accounting.json): **€117.691596 / €150**, including unchanged
**€6.501615** historical unknown reservations. Readiness resolved the pinned model
and pricing/currency configuration without inference. The 96,000-token allowance
and all permissions remain unchanged.

**Live execution: not started. Product visits: not performed. XLSX: not produced.
Independent live execution oracle: not run.** The [revision-9 review](amazon-review.md)
requires explicit acknowledgment of all five accepted requirements and this exact
artifact hash before one execution. No approval command has been populated from
the planning GO. No code-review run or cohort expansion occurred.

The full prior offline validation results and their initial failures remain in the
preceding evidence directory. CI still had running checks at this review capture;
it is not reported as green. PR #117 remains draft.

## Reproduction

Run the frozen executable from its matching `7a76fc95` checkout. `inspect-run` and
`replay-compile` are read-only. The saved run must not be restarted with `plan`.

```sh
tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark \
  --schema-portability inspect-run --workspace /path/to/GnOuGo \
  --campaign schema-portability-20261002 --run typedbindings20261009b-amazon-1
tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark \
  --schema-portability replay-compile --workspace /path/to/GnOuGo \
  --campaign schema-portability-20261002 --run typedbindings20261009b-amazon-1
```

After explicit approval only, the existing `execute` command takes
`--case amazon --cohort typedbindings20261009b --run typedbindings20261009b-amazon-1
--max-products 10 --review-command <explicit-approval-file>`. It must retain revision
9, hash `cbcd077db834ebe6da54b25bb838e5b6cca897d845a52a29484fcbb21b6cdb89` and the
user's five requirement acknowledgments. The harness refuses an already-started
execution, changed frozen source, invalid approval or failed budget admission.
