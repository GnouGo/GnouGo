# Consumer-specific views and explicit flatten

Production candidate: `ad9e151422b485f6f181ffdd3e41669fd90f5b97`, baseline `93d1ffa87300f85f2236e0f40b37ef60a0bf951b`. PR #117 remains draft. No paid calls or fresh external workflow execution occurred in this correction.

## Correction

Extraction uses existing closed `resultType` contracts to describe each immediate consumer's observed business view. Explicit selected inputs form the decision view, while complete sources and exact action references remain available separately. Optional candidates are arrays per examined source item; an explicit `flatten` binding joins one typed array-of-arrays level before the global decision. It preserves ordering, duplicates, nullable elements and deeper nesting; null inner arrays and insufficiently typed values fail.

Compilation checks original source constraints before concatenation and downstream constraints afterward. New flatten compositions assemble a checked consumer object. Scripts appear only during final lowering. There is no new executor, consumer-contract framework, planning phase or compilation profile. Adaptive mapping, request ceilings and runtime behavior are unchanged. Shape checks establish neither semantic relevance nor factual completeness; independent oracles remain required. A broad necessary view still fails request admission before dispatch.

Generation shares the existing one-operand schema shape for `json` and `flatten`, narrowing its operation-specific domain to the expected type. Repairs retain their original definitions and permissions, including the retained version-seven authority regression. No existing request schema is rewritten. Independent baseline compilation confirms byte-identical historical YAML for omitted, v1 and v2 lowering; no `compact-bindings-v3` is introduced.

## Deterministic results

| Check | Result |
| --- | --- |
| Initial focused baseline | 38 passed |
| Final full solution, `-m:1 -warnaserror` | 4,801 passed, zero failures, 12 existing skips; 33 suites |
| Planning suite within full solution | 1,089 passed |
| Final focused flatten/consumer checks | 26 passed |
| Real local Browser/Document fixture | 29 passed, including two new flattened consent/decision variants; independent visits, XLSX cells and cleanup |
| Release packages | Core and Planning passed |
| Planning Native AOT | Passed; explicit flatten, null elements, duplicates, zero flatten inference, existing adaptive mapping/replay checks |
| Published encrypted recovery | Trimmed self-contained Agent.Server smoke passed; public journal schema 9 unchanged |
| Planning skill | Validated |

The full solution contains the final production source and tests. Frontend production build ran as part of the solution checks. No test assertions, permissions, recovery behavior or limits were weakened.

| Synthetic complete source | Items processed | Mapping calls | Global calls | Complete serialized global request |
| --- | ---: | ---: | ---: | ---: |
| 52 pages / 1,474 records | 52 | 1 | 1 | 2,113 bytes |
| 53 pages / 1,504 records | 53 | 1 | 1 | 2,110 bytes |
| 52 pages / 1,474 records, no candidates | 52 | 1 | 1 | 846 bytes |

The regression adds a conservative 4,096-byte framing allowance; each complete request remains below the existing 12,000-unit smaller allowance, without raising the retained 96,000 live limit. Relevant duplicate records occur outside generation examples. Exact references and all original observations remain available to the separate downstream consumer. Broad raw inputs are rejected before provider dispatch. Typed flatten tests require no model client and execute with zero inference. Additional checks cover source and consumer bounds, both branches/captures, missing versus null, deeper nesting, immutable approvals, provenance and historical repair recovery.

The [prior failed composition](../adaptive-mappings-2026-10-07/amazon-r4-plan.json) and [execution evidence](../adaptive-mappings-2026-10-07/live-execution.json) remain unchanged. Its 594,458-byte mapped result and rejected oversized global interpretation are historical live evidence, not measurements from this synthetic fixture. No success rates are pooled.

## Live status and CI limitation

Configured pricing/currency readiness passed with zero inference. A disposable readiness probe exercised actual Browser and Document transport, independently read an XLSX and verified Browser cleanup. Campaign upper bound remains **EUR 99.356868 / 150**, including EUR 3.905040 of retained unknown reservations. No reservation was released.

On the frozen production candidate, **28 CI checks passed, four were skipped, and one remained in progress**. The [dedicated planner/runtime/host validation job](https://github.com/GnouGo/GnouGo/actions/runs/37669256528) passed. The separate [Agent.Server job](https://github.com/GnouGo/GnouGo/actions/runs/37669257236/job/112956428688), labelled non-blocking by the repository, remained in `Prepare local browser execution` without reaching its tests after more than 40 minutes. GitHub did not expose its running logs. This is a pending environment/setup check, not a demonstrated production defect; no timeout or assertion was changed.

The fresh Amazon E2E was **not dispatched while that CI gate remained unresolved**. No new planning or execution identity has been consumed; the proposed `consumerflat20261007a-amazon-1` remains unused. Planning success, live mapping, product visits, XLSX oracle success and cleanup for a fresh Amazon run remain unverified. The implementation is complete; the requested live evaluation is incomplete. No code-review run, cohort expansion, historical replay or GitHub feedback publication occurred.

## Reproduction and next permitted action

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests/GnOuGo.Flow.Planning.Tests.csproj -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj -m:1 -warnaserror -p:SkipClientBuild=true --filter FullyQualifiedName~LocalProductOutcomeExecutionTests
PLAYWRIGHT_MODULE_PATH="$PWD/src/GnOuGo.Browser.Mcp/bin/Debug/net10.0/.playwright/package/index.mjs" dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
```

Once the pending CI result is available, recheck pricing/accounting, freeze a fresh clean source/harness/configuration manifest, generate one new Amazon workflow with the ten-product maximum, manually review the concrete requirements and artifact, and execute once through the existing revision/hash-bound approval command. The user's single-run authorization remains recorded in the conversation; historical artifact approvals must not be reused. Preserve all existing completeness, product-visit, observed-value, XLSX and cleanup oracles. Full six-run acceptance is still unmet.
