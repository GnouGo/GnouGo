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
