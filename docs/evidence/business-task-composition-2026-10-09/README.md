# Smaller business TaskPlans

Continue PR #117 from `2663f4ac`. Production changes are confined to generation
and repair guidance. Compiler acceptance/lowering, mapping, public contracts,
repair authority, permissions and budgets are unchanged. Fresh generation uses
the existing `compact-bindings-v5`; historical proposals and approvals are intact.

The actual compact-schema prompt now requests business tasks and inline typed
consumer bindings. Independent extraction can return one candidate array per
complete page, consumed by a direct flatten binding. The skill and executable
fixtures keep only necessary collection projections that existing bindings cannot
express. No model-authored scripts, automatic plan rewriting or new operators are
introduced. Full observations and original action arguments stay separate.

## Compilation and local execution

The [sanitized baseline and compact fixture](../../../tests/GnOuGo.Agent.Server.Tests/Fixtures/BusinessTaskComposition/README.md)
preserve accepted public outputs and the external operation sequence. Both use
the same compiler/profile and current discovered contracts:

| Measure | Baseline | Compact |
| --- | ---: | ---: |
| TaskPlan tasks | 23 | 13 |
| Compiled steps | 61 | 60 |
| `set` steps | 39 | 36 |
| YAML bytes | 92,585 | 77,588 |

Task count falls 43.5%; compiled-step reduction is modest because contract and
short-circuit checks remain. The comparison uses neutral objectives and names;
its byte counts are not the historical French YAML's byte counts. Normal output
bindings remain outside finalization. The two pure projections retain original
record arrays and export IDs only after offered-candidate lookup. URL conversion,
observed-fact reconciliation and bounded TSV formatting remain explicit inference.

The final local execution reaches review in one deterministic call with zero
repairs, then uses actual Browser and Document MCP transport. It visits ten
products, validates all 38 captured pages, offers all 12 candidates to selection,
and writes a workbook whose cells are independently checked with OpenXML. The
complete serialized decision request is 2,597 bytes. The execution has 24 runtime
inference calls, including 11 mapping invocations, zero inference for typed copying,
226 journal invocations, 225 normal steps and one cleanup step. Browser closes;
encrypted-journal recovery repeats no visit, inference or write. The reconstructed
logical journal is 22,702,244 bytes; this is not a physical-storage measurement.

The matrix also preserves repeated selections, duplicate business values, missing
fields and empty results. Unoffered identities and incomplete observations block
affected work; denied writing creates no file. A plausible but fabricated workbook
is deliberately rejected by the independent cell oracle despite workflow success.
These expected failures are regression successes, not successful business runs.

An initial fixture combined guards into more generated steps; the measurement
rejected that composition. Ordered grouping now preserves the actual predicates
and uses established data dependencies instead of redundant completion checks.
The runtime also rejected an invented null per-page error in the test adapter.
The compact target omits that redundant field; reconciliation already receives
the actual producer error metadata. Neither finding changed runtime behavior.

## Validation and live boundary

[Validation measurements](validation.json): 1,209 planning tests, eight new fixture
cases and a final two-case recheck passed with `-warnaserror`. Planning Release
packaging, the published osx-arm64 Native AOT planning/recovery smoke and skill
validation passed. Full-solution validation is tracked separately before delivery.

Reproduce the focused execution:

```sh
dotnet test tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj -m:1 -warnaserror --filter FullyQualifiedName~BusinessComposition
dotnet test tests/GnOuGo.Flow.Planning.Tests/GnOuGo.Flow.Planning.Tests.csproj -m:1 -warnaserror
```

No paid calls occurred during implementation. The rechecked campaign upper bound
is EUR 120.79665899575455349076839816 of EUR 150, including all unknown reservations.
Historical uncertain logical requests retain their existing inconclusive closures;
no invocation is resumed or reclassified. One fresh Amazon validation is authorized,
with a new identity, ten-product maximum and its own concrete artifact approval.
Local deterministic success does not establish live-provider or Amazon acceptance.
The earlier Browser timeout remains unproven as resolved. PR #117 remains draft.

## Frozen live candidate

Candidate `1022ed33` is frozen in an isolated clean checkout. The build refreshed
only a generated metadata timestamp; the harness refused readiness before any
action. Restoring the committed file and rebuilding with the existing
`SkipModelMetadataGeneration=true` option preserved the frozen source.
[Pricing/currency readiness](provider-readiness.json) and [local execution readiness](execution-readiness.json)
passed with zero inference. The [freeze](freeze.json) records source, binary,
configuration and unchanged oracle hashes. Fresh generation uses
`businesstasks20261009a-amazon-1`; its concrete artifact review remains required.

Prior CI on `2663f4ac` failed six retained-composition cases because their existing
runtime elapsed budgets had already expired after 135–136 seconds of deterministic
planning, before mapping inference. This is separate from the new compact matrix.
No limit was raised. [Retained CI failure](https://github.com/GnouGo/GnouGo/actions/runs/37985644971)
remains recorded; current full-solution and CI results will be reported separately.

## Fresh planning findings (retained)

The initial live generation stopped after discovery and one proposal (two logical
calls, two physical attempts, no repairs; EUR 0.206367). Three `requires` values
were nullable strings, not booleans. It also forwarded complete snapshots to
interpretation and mixed extraction with selection. Nothing executed.

An explicit composition revision preserved the seven accepted requirements and
public contracts. Revision 4 still selected fields from lookup arrays, supplied
a collection to a scalar Browser argument, and read an undeclared `length` field.
The validator rejected these. It also attempted learned copying of original
records and declared synthetic extraction fields; review did not accept them.
The cumulative three calls cost EUR 0.442790 with no new unknown completion.

The next explicit revision supplies the proven compact reference composition as
a generation example; it does not install YAML or bypass approval/validation.
The retained plans, results and exact revision commands are stored alongside
this report. Paid generation is model-dependent; passing a deterministic adapter
is not evidence that the first real-provider proposal was valid.

## Reviewable live artifact

Revision 8 reached final review with zero diagnostics: five logical/physical
planning calls, two discovery reads, three explicit revisions and zero automatic
repairs. The final targeted patch changed only the extraction result contract,
its consumer input and the visit assertion; a structural diff confirms the
accepted requirements and unrelated work stayed unchanged.

The [concrete review](amazon-review.md) covers thirteen tasks, zero value
forwarders, 56 compiled steps, 33 sets and 79,833 YAML bytes. Both necessary pure
projections remain, full snapshots are absent from global interpretation, and
offered-candidate and original-record lookups are explicit. Independent inference
validates all original pages under existing cumulative limits. The bounded TSV
formatter remains visible. This artifact has not executed and is not approved
automatically. The seven explicit requirement acknowledgments are pending.

Known planning usage is 74,373 input / 16,987 output tokens, EUR 0.782212 and
281,807 ms. Campaign upper bound is EUR 121.578871 / 150 with all historical
reservations retained. Planning success is separate from execution and XLSX
oracle success; neither has been observed for this live yet.

## Final local validation

`dotnet test GnOuGo.Agent.sln -m:1 -warnaserror` completed with exit code 0:
**5,015 passed, zero failed, 13 skipped** across 33 projects. The host suite
passed 739 tests; the planning suite passed 1,209. [Per-project totals](full-solution.json)
retain the exact counts and log fingerprint. The frontend build also passed.
Release Planning packaging, osx-arm64 Native AOT smoke and skill validation passed.
The frozen candidate's published Linux Server passed its encrypted format-10
planning persistence smoke, preserving execution journal schema 9.

Current CI is not claimed green: Docker image jobs encountered Docker Hub
authentication timeouts/504s, and the Linux x64 desktop Copilot CLI checksum
download failed with a connection reset. Those are external acquisition failures.
The prior retained-fixture CI budget failures remain historical; the same host
suite passes locally on this candidate. Remaining remote job status is recorded
separately from completed local checks and the unexecuted live artifact.
