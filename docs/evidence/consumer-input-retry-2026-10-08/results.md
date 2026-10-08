# Fresh Amazon retry: mappings succeed, global selection remains oversized

**One fresh execution failed before product selection.** Run `consumerretry20261008b-amazon-1` used production/harness `bb6b6bb4f52e69f4c26eb49c5ca16ffd26d58a3d`, ten products maximum and unchanged execution oracles. The previous initial navigation timeout did not recur. Complete acquisition and both independent mappings succeeded, but the global selection request exceeded the existing input allowance. No production source, mapping behavior, permission, timeout or limit changed.

## Readiness and concrete review

The benchmark rebuilt without warnings. Fresh deterministic checks passed **110 Browser tests** and **23 flatten/consumer-input tests**. All **29 non-skipped CI checks passed**, with four skips, before execution. Relevant planning/runtime CI passed before generation; separate desktop packaging finished during generation. The previous full solution result remains **4,801 passed, zero failures, 12 skips**; it was not repeated for this evidence-only update. [Validation](validation.json), [CI](ci.json).

Configured pricing/currency readiness passed using the admitted ECB quote within the existing freshness policy. The disposable local probe exercised real Browser/Document transport, independently checked XLSX contents and verified closure, with zero model calls. [Readiness](readiness.json), [provider configuration fingerprints](provider-readiness.json), [frozen manifest](manifest.json), [artifact/oracle hashes](artifact-manifest.json).

The user's retry authorization was bound manually to revision **6**, artifact **74dcc283cc848642f5ade605b69f613818c1aba5e546c682d7255c73f92f226a**, after checking all five requirements individually against the actual plan. Approval advanced the session to revision 7. No requirement was acknowledged automatically from compilation. [Review](amazon-review.md), [approval command](amazon-approval.json), [TaskPlan](amazon-r6-plan.json), [executed YAML](amazon-r6.yaml). This execution is finished and must not be replayed.

## Planning

| Measure | Result |
| --- | ---: |
| Logical calls / physical attempts | 4 / 5 |
| Discovery reads | 2 |
| Explicit review revisions / automatic repairs | 2 / 0 |
| Verified input / output tokens | 57,009 / 15,206 |
| Verified planning cost | EUR 0.657756 |
| Additional unknown-attempt reservation | EUR 1.298287 |
| Cumulative planning latency | 450.181 seconds |
| Executed YAML | 91,464 bytes; 2,549 lines |

Initial generation passed compiler checks but sent raw observations to interpretation and made consent mandatory. The first explicit revision introduced independent extraction, flattening, optional observed-control handling and a fresh acquisition after interaction. The second removed invented classification enums from extract contracts, guarded the nullable consent reference explicitly, removed the search-title fallback from product interpretation and used the declared collected product port for workbook formatting. Accepted requirements remained identical across all three proposals. [Original responses](provider-responses.json), [proposal index](proposal-index.json), [first revision](revision-r2.json), [second revision](revision-r4.json).

One initial planning transport attempt has unknown usage. The configured transport retry proceeded; its conservative reservation remains charged to the campaign. This was not a manual replay or reconciliation. Four logical calls and two manual revisions still miss the minimal-call objective.

## Execution and request sizes

Execution started at **2026-10-08 06:31:31 UTC**. Initial Browser acquisition completed in **8.123 seconds**, returning a coherent snapshot of **54 pages / 1,578 records**. It restarted once after navigation: two capture attempts, one recorded invalidation, no capture or manifest truncation. Buffered data from the invalidated generation was discarded. No consent interaction occurred on this observed path.

| Stage | Original pages validated | Generation examples | Result |
| --- | ---: | ---: | --- |
| Consent/blocker extraction | 54/54 | 3 | 3 candidates; 705 output bytes |
| Initial global decision | — | Full extracted view | Completed; 5,923 serialized bytes including framing |
| Product-candidate extraction | 54/54 | 3 | 147 candidates; 138,848 output bytes |
| Global product selection | — | Full extracted view | Rejected before dispatch: 99,456 estimated input tokens > 96,000 |

Each mapping needed **one generation, zero specializations and zero cache hits**. The 51 pages omitted from each generation request were still processed and validated by its script. Generation requests measured **95,797** and **70,797 bytes**, respectively, including the harness's 4,096-byte framing allowance. These are serialized byte measurements, not token counts. The retained mapping `request_bytes` telemetry measures prompt bytes only; `failure_groups: 11` records the initial unmapped shape groups, not eleven failed mappings. [Mapping receipts](mapping-receipts.json), [telemetry](mapping-telemetry.json), [request estimates](request-estimates.json), [journal measurements](journal-metrics.json).

The second extraction remained too broad for its immediate global consumer:

| Extracted field | Sum of serialized value bytes, excluding property names |
| --- | ---: |
| `url` | 103,028 |
| `reference` | 6,646 |
| `pageId` | 4,998 |
| `text` | 4,738 |
| `observedName` | 4,738 |
| `actions` | 2,646 |
| `kind` | 882 |

URLs account for roughly 74% of the complete extraction result; the longest is 1,215 characters. `observedName` duplicates `text` for these candidates. Flattening preserves those fields rather than reducing them. The final request contained **139,331 prompt characters / 167,112 serialized bytes including framing**. The existing admission estimator returned **99,456 input tokens** and durably rejected it with `LLM_BUDGET_EXCEEDED`, `dispatch_status: not_started`. This is an admission estimate, not provider-measured usage: the rejected request was never dispatched.

The retained composition excludes raw snapshots from global interpretation, but its declared candidate contract still includes extensive action data. A generic next correction should define a smaller decision view with stable identities, retaining exact action arguments separately and reconnecting selected identities deterministically. It must preserve every observed candidate, ordering, duplicates and coverage; no automatic truncation, URL shortening or limit increase is justified. This retry made no such source change. Schema and provenance validation do not prove that extraction preserved every semantically relevant fact.

## Oracle, receipts and cleanup

| Check | Result |
| --- | --- |
| Workflow / independent oracle | Failed / failed |
| Oracle findings | `workflow_execution_failed`, `workbook_missing` |
| Product visits / XLSX files produced | 0 / 0 |
| Paid runtime logical calls / physical attempts | 4 / 4 |
| Runtime verified input / output tokens | 54,305 / 5,839 |
| Runtime verified cost | EUR 0.396393 |
| Mapping generation / specialization calls | 2 / 0 |
| Workflow Browser cleanup | Passed, 1.036 seconds |
| Independent cleanup verification | Passed: subsequent read found no active page |
| Normal / finalization steps started | 34 / 1 |
| Finalization complete / durable failed-call receipt | Yes / yes |
| Execution / total latency | 135.911 / 586.092 seconds |

The fifth prospective runtime inference was refused before dispatch and is not counted as a paid call. The failed invocation has verified completion, so normal failure cleanup ran. No false reconciliation error occurred. The oracle's later idempotent close is separate from the workflow finalizer. Product detail extraction, observed XLSX values and the successful nominal end-to-end path remain unverified. [Sanitized execution events](live-execution.json).

The six-slot report remains **0/6: one failed execution, five unexecuted slots**. No code-review evaluation, cohort expansion or uncertain-invocation replay occurred. PR #117 remains draft. [Report](live-report.json).

## Campaign accounting

This retry adds **EUR 1.054149 verified cost**, plus **EUR 1.298287 retained reservation** for the unknown planning attempt. Campaign `schema-portability-20261002` increases from **EUR 100.544321** to **EUR 102.896757 / 150** committed or reserved: **EUR 97.693429 known + EUR 5.203327 reserved**, leaving approximately **EUR 47.103243**. All historical reservations remain intact; four physical attempts now have unknown usage. [Before](accounting-before.json), [after](accounting-after.json).

Previous failed cohorts and the historical 33/33 benchmark remain unchanged and are not pooled with this run. The earlier repair-schema nesting defect remains separate; it was not exercised here.

## Reproduction and evidence boundaries

Commands used the frozen clean candidate. The following inspection commands do not dispatch inference or replay execution:

```sh
dotnet test tests/GnOuGo.Browser.Mcp.Tests/GnOuGo.Browser.Mcp.Tests.csproj -m:1 -warnaserror --no-build --no-restore
dotnet test tests/GnOuGo.Flow.Planning.Tests/GnOuGo.Flow.Planning.Tests.csproj -m:1 -warnaserror --no-build --no-restore --filter 'FullyQualifiedName~FlattenCompilationTests|FullyQualifiedName~ConsumerInputContractTests'
dotnet build tests/GnOuGo.Agent.Planning.Benchmark/GnOuGo.Agent.Planning.Benchmark.csproj -m:1 -warnaserror -p:SkipClientBuild=true
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --workspace /Users/a115vc/Desktop/GnOuGo --campaign schema-portability-20261002 --run consumerretry20261008b-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --workspace /Users/a115vc/Desktop/GnOuGo --campaign schema-portability-20261002 --cohort consumerretry20261008b
```

Fresh `plan`, two `revise` phases and the single `execute` phase used `--case amazon --max-products 10 --cohort consumerretry20261008b --run consumerretry20261008b-amazon-1` with the existing campaign/workspace. Revision commands and the concrete execution approval are retained. A new evaluation must use a new run identity and review its own artifact.

Published evidence excludes raw page content, tracked navigation URLs, credentials and complete resolved inference inputs. Original observations, responses and receipts remain encrypted. Read-only journal measurements were obtained through existing public KeyVault/persistence APIs. No saved workflow, approval, receipt or reservation was edited.
