# Retained plan correction — 2026-10-09

This correction continues the unexecuted `consumerbindings20261009b-amazon-1`
session. Its original candidate and harness are
`fb07a92c888a07358ed12d1ce92fdcd24db83937`; regression and evidence changes are on
PR #117. Production code, compiler/mapping profiles, limits and oracles are unchanged.

## Correction boundaries

The retained proposal declared nullable product records despite the accepted
non-null record contract. Missing name, description, price and URL fields remain
nullable. The first explicit revision authorizes only
`/tasks/normalize_products/resultType`, and the intended delta is only the
product item `nullable: true → false`.

A separate explicit structural revision may restructure data adaptation while
preserving accepted requirements, operations, scopes, order, writing and cleanup.
Targeted patch permissions are not expanded to authorize structural changes.
Complete observations remain separate from narrow decision inputs. Existing
bounded `extract.each`, typed projection, one-level `flatten` and ID `lookup`
provide the composition; global decisions must not receive raw snapshots.
Normalization and copying use typed bindings. The selected bounded TSV formatting
transform remains explicit because the current writer takes textual content.
The public workbook path must come from the successful writer's output.

The retained proposal additionally mentions excluding sponsored products. This
criterion is absent from accepted requirements and must not survive review.
Product order and the maximum of ten are unchanged.

## Regression

`Fixtures/TargetedRevisions/nullable-records.json` is a sanitized reproduction with
arbitrary task, field and operation names. `RetainedOutputRevisionTests` verifies:

- The accepted output rejects nullable records before approval or execution.
- A targeted declaration correction preserves the complete remaining TaskPlan.
- The same authority rejects substituting a value task for the transform.
- A separate explicit structural revision preserves requirements, writing and
  cleanup, invalidates approval, and retains cumulative calls across restart.
- Real Flow executes deterministic normalization without an LLM client, preserves
  null fields and duplicate records exactly, and writes before cleanup.
- A null record fails input validation before either external operation.

Existing compact-view, lookup, flatten, targeted-revision and local Browser/Document
execution regressions remain the coverage for complete observations, request
admission, exact action arguments, actual XLSX cells and recovery.

The structural response reproduced another existing validation failure:
`flatten(field("matches", pages))` accesses an array as an object. The retained
`array-field-flatten.json` fixture rejects this form. Its corrected deterministic
execution uses an existing pure `foreach` to select each object's declared array,
then flattens that array-of-arrays exactly once. Sequential and parallel declarations
preserve original observations, empty pages, duplicates, null fields and order,
with no runtime loop or inference required after compilation.

## Validation and live status

The exact targeted revision reached review at revision 5, artifact
`6f80c50fffa968d9561853a221408566300beff335b44a5a5713329470405855`.
An independent JSON comparison found exactly one changed boolean and unchanged
requirements. It consumed one additional call, no repair, and 17.546 seconds.
This intermediate artifact was not approved or executed because the broader
data-adaptation correction was still required.

The explicit structural revision preserved all eight external operation identities,
their scopes/order and accepted requirements. It failed with eight `TASK_FIELD_TYPE`
diagnostics at the page-object-array projections. The remaining automatic repair
returned a clarification because its issued value slots cannot insert the required
typed projection tasks. No broader repair authority was granted. Review also found
reintroduced nullable product records, unnecessary extraction for typed normalization,
and candidate reconnection to extracted records instead of original producer data.
See `structural-revision-review.json`; the proposal is **not execution-ready**.

The retained session is now revision **8**, awaiting clarification, with **six logical
calls, eight physical attempts, both repairs consumed, two discovery reads and
994.912 seconds cumulative planning time**. All six logical responses have receipts.
Verified run usage is **53,277 input / 23,246 output tokens**; the original unknown
transport reservation is retained separately rather than counted as verified usage.
No further dispatch was attempted after the existing physical-attempt ceiling was
reached. The campaign upper bound is **€116.779639 / €150**; unknown reservations
remain **€6.501615**. This turn added approximately **€0.503461** of verified cost and
no unknown reservation. The initial frozen-environment mismatch caused zero dispatch;
the exact original PATH and unchanged executable resolutions were restored before
continuation, with the original manifest checks intact.

**Execution:** not started; zero live workflow mapping/interpretation calls and zero
Browser or Document operations. **Independent live oracle:** untested; no XLSX was
created. No artifact approval was requested for the invalid proposal. The six-slot
cohort remains incomplete and PR #117 stays draft. Historical invocations, receipts,
approvals and reservations are untouched.

The remaining correction uses existing constructs: typed per-page projection before
flattening, direct normalization of already typed product records, and lookup against
retained originals. It needs an explicitly authorized planning continuation with an
available allowance, or a separately authorized fresh session; it cannot be achieved
by resetting this exhausted session or widening automatic patch permissions.

Deterministic request-size measurements are in `deterministic-request-measurements.json`:
the 53-page / 1,504-record fixture processes all pages with one mapping generation and
a 2,110-byte global request (812-token existing estimate). The 54-page / 1,578-record
selection fixture offers all 147 candidates in 17,318 bytes including its conservative
framing allowance, while reconnecting exact original action data. Oversized necessary
views and unoffered selections remain rejected. These are local adapter measurements,
not live-provider execution evidence.

Validation commands:

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror \
  --filter 'FullyQualifiedName~RetainedOutputRevisionTests|FullyQualifiedName~AcceptedOutputTests|FullyQualifiedName~TargetedRevisionTests|FullyQualifiedName~CompactObservationTests|FullyQualifiedName~LookupCompilationTests|FullyQualifiedName~LookupSelectionTests|FullyQualifiedName~FlattenCompilationTests|FullyQualifiedName~IndexedProjectionCompilationTests'
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests|FullyQualifiedName~PlanningPersistenceTests|FullyQualifiedName~PlanningModelRecoveryTests|FullyQualifiedName~BrowserSnapshotReceiptTests'
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
```

The focused planning run passed 122 tests; the subsequently added page-projection
reproduction passed both declarations together with the two revision tests (4/4).
The affected host/local execution run passed **62/62**, including real Browser and
Document transport, independently inspected XLSX contents and encrypted recovery.
The full warning-as-error solution run passed **4,976 tests, with 13 skipped and
zero failures across 33 assemblies**, including all four new regression cases.
The planning assembly passed 1,197 tests; the full host assembly passed 717 with
one existing UI skip. Result counts, skipped cases and TRX hashes are in `validation.json`.
Production, package, AOT and storage code is unchanged; no additional publish checks
are required for these test/evidence-only changes.
