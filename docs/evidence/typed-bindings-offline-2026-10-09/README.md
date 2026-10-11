# Typed bindings: complete offline correction

Implementation: `eb79c99b`; final prompt candidate: `7a76fc95`, following `eaba0f57` on PR #117.
Only generation guidance changes in production. Compiler acceptance, runtime,
mapping, response schemas, public contracts, compilation profiles and repair
authority are unchanged. No paid planning or execution request was made.

## Why generation repeated the errors

The retained structural revision received the corrected baseline contract. It
nevertheless authored a new plan containing array-field access, nullable product
records and learned extraction for typed copying. The compact response schema
removes descriptions, and the short instructions previously said to flatten without
showing the required item-scope projection. A singleton lookup was also treated as
a scalar, although lookup always returns an array. A scoped value patch cannot
insert the missing projection tasks; spending further repairs cannot fix that
structural omission.

The updated emitted prompt explicitly demonstrates `foreach` item projection
before `flatten`, direct `value` copying, array-valued lookup and preservation of
accepted nullability. Instructions grow from 865 to 868 UTF-8 bytes. The retained
discovery regression still reaches review in seven calls, zero repairs and 19
metadata reads, with request estimates `[9521,21208,21579,21397,21316,21530,23941]`
under the unchanged 24,000-token ceiling. Existing issued requests are retained.

## Complete corrected composition

The paired [failed and corrected fixtures](../../../tests/GnOuGo.Agent.Server.Tests/Fixtures/RetainedProductBindings/README.md)
retain the accepted output contracts and all eight external operation identities
in business order. Data adaptation uses existing constructs only:

1. Extract one compact view per complete page and immediate decision stage.
2. Project related arrays inside a pure `foreach`, then flatten one level.
3. Interpret compact offered candidates and return observed IDs.
4. Check IDs against offered candidates, then reconnect them to original records.
5. Iterate array-valued lookups for actual reference-based interactions and visits.
6. Copy typed product records with `value`, retain the bounded TSV formatter, write
   the actual workbook and return the successful writer's path.

Original observations and action arguments remain separate. Product objects are
non-null, permitted missing fields remain null, and selection preserves ordering
and repetitions. No sponsored filtering, deduplication or new ranking is added.
Optional action adapters use existing sequence scopes. Completeness assertions
stay on extraction; pure copying does not repeat them or require runtime iteration.

Initial offline attempts retained their real failures: redundant guarded copy loops
caused expensive nested-state validation; four-record examples and a broad product
extraction contract exceeded the default mapping request allowance. The fixture now
uses smaller complete producer pages and one product-facts view. No observation was
discarded, no limit increased, and no runtime or validator was changed. The scope
and projection corrections use the explicit structural-revision boundary already
authorized for this offline plan.

## Planning, execution and oracles

The complete fixture discovers Browser and Document contracts over real stdio MCP,
passes a deterministic response through the actual issued planning schema, reaches
review in **one planning call and zero repairs**, and explicitly approves the
concrete artifact and requirements. No external action occurs before approval.

Both consent-present and consent-absent cases submit the query, visit ten local
product pages, preserve duplicate names and a missing price, write an actual XLSX,
and pass independent cell/path/visit/cleanup checks. Committed-result recovery from
a fresh encrypted-store instance causes no new navigation, inference or write.

Negative cases pass their expected-failure tests:

- An incomplete snapshot stops before any runtime model call and closes Browser.
- An unoffered ID fails lookup before any product visit or write.
- A denied document path fails at the producer, creates no workbook and permits
  normal cleanup through its verified terminal receipt.
- A plausible fabricated workbook is rejected by the independent cell oracle.
- Repeated selected IDs remain valid and preserve repeated visits/rows; the
  independent first-ten business oracle rejects that deliberately wrong selection.

These last two cases deliberately show that workflow success alone is insufficient.
They are fault-injected deterministic cases, not successful business evaluations.

[Local measurements](local-measurements.json) separate planning and execution.
The positive cases use 13 mapping generations and 15 interpretations; typed
projection and normalization use zero inference. Every original page validates,
including pages omitted from generation examples: 43 pages / 77 records without
consent, 44 pages / 79 records with consent. All 12 candidates remain offered,
including a sponsored label and candidates outside generation examples. All complete serialized model
request estimates remain below the fixture's 12,000-token admission allowance;
the retained normal run's largest estimate is 7,491. No raw snapshot or action-only
URL enters candidate selection. The measured YAML is 195,479 bytes; actual execution
uses 232 normal steps without consent, 236 with consent, and one cleanup step.
The logical reconstructed journals are approximately 45–52 MB. These are measured
costs, not a claim that compilation or journal size has been optimized in this change.

## Validation and next live boundary

Focused validation passed **127 planning tests and eight complete-composition tests**.
Planning Release packaging, Native AOT smoke (including encrypted adaptive receipt
replay), and skill validation passed. The full solution ran all 33 test assemblies: 4,984 passed, 13 skipped, and one
existing discovery-size regression failed. The correction then passed the complete
1,198-test planning suite, including that regression. This gives 4,985 passing
tests after the affected-suite rerun; the initial full command itself exited 1.
See [validation](validation.json) and [initial full-run evidence](full-solution-initial.json).

CI initially caught a removed compatible-alternatives instruction. Restoring it
also exposed a tight retained discovery budget. The final wording keeps both rules
and the projection example, with the same schemas, limits and assertions. Both
initial CI failures and the discovery failure are retained; no check was weakened.

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  --filter 'FullyQualifiedName~RetainedComposition|FullyQualifiedName~RetainedWholeProposal'
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 \
  --self-contained true -warnaserror -o /tmp/typed-bindings-aot
/tmp/typed-bindings-aot/GnOuGo.Flow.Planning.Smoke
```

Configured [provider/currency readiness](provider-readiness.json) passes with zero
model calls. [Campaign accounting](accounting.json) remains **€116.779639 / €150**,
including **€6.501615** in retained unknown reservations. The exhausted
`consumerbindings20261009b-amazon-1` session stays at revision 8, with six saved
responses, no execution and no artifact hash. Nothing is reset or replayed.

The [next independent session](fresh-session.json) is prepared under a fresh identity and must wait for
the user's explicit GO before its first paid planning call. Its generated artifact
will then need its own revision/hash-bound approval and requirement acknowledgments
before execution. The ten-product maximum, existing permissions, limits and live
oracles remain unchanged. No Amazon/provider success or six-run acceptance is
claimed; PR #117 remains draft.

The unused first preparation is retained in [unused-preparation-a.json](unused-preparation-a.json).
Its manifest is unchanged; the final guidance uses a separate candidate/cohort.
[History preservation](history-preservation.json) records byte-identical inspection
of the exhausted run and unchanged campaign accounting.

Final-candidate GitHub CI is still running at evidence capture; it is not reported
as green. The frozen local readiness check passes pricing/currency configuration,
local Browser navigation and closure, and independent XLSX reading with zero model calls.
