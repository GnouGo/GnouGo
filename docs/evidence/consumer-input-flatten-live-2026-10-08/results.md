# Fresh consumer-view Amazon validation

**Result: planning reached review; the single execution failed before snapshot capture.** Run `consumerflat20261008a-amazon-1` used frozen production/harness `b46803db4846343806caf1dacf10ce66ac30e4c9`, ten products maximum, and the unchanged execution oracle. No production source, architecture, mapping behavior, permission, timeout or limit changed in this evaluation.

## Readiness and approval

All **29 non-skipped CI checks passed**, with four skips, on the frozen candidate. The earlier pending Browser setup gate is resolved. The previous deterministic correction remains validated by **4,801 passing solution tests**, 29 local Browser/Document execution fixtures, Release packages, planning Native AOT, published encrypted recovery and skill validation; these tests were not rerun for this evidence-only update. The current harness rebuild passed with zero warnings/errors.

Configured pricing/currency readiness passed, retaining the admitted ECB quote within the existing freshness policy. The disposable readiness probe used actual Browser and Document transport, independently checked XLSX contents and verified Browser cleanup, with zero model calls. [Readiness](readiness.json), [CI](ci.json), [frozen manifest](manifest.json), [artifact and oracle hashes](artifact-manifest.json).

The user approved this fresh live validation. Manual review checked all five accepted requirements against the actual operations, nested branches, input bindings and exports. The concrete [approval command](amazon-approval.json) covers revision **12**, artifact **906f064e5ac78b36c38ca9e57cf4608934d574f52357efaf80c4c5ce97282d06**; approval advanced the session to revision 13. Acknowledgments were not inferred from compilation. [Review](amazon-review.md), [TaskPlan](amazon-r12-plan.json), [unchanged executed YAML](amazon-r12.yaml).

## Planning results and retained failures

| Measure | Result |
| --- | ---: |
| Logical planning calls / physical attempts | 7 / 7 |
| Reported MCP discovery reads | 2 |
| Explicit review revisions / automatic repairs | 4 / 0 |
| Verified input / output tokens | 108,688 / 26,490 |
| Planning cost | EUR 1.187452 |
| Cumulative planning latency | 268.817 seconds |
| Executed YAML | 167,234 bytes; 4,799 lines |

The first proposal omitted workbook writing and cleanup and failed the accepted file-output contract. The first revision used nonexistent names in its independent extraction declarations. The next proposal added writing but had invalid cross-scope references and still omitted closure. Its automatic repair was rejected by local structured-output preflight: **12 nesting levels, maximum 10**, before another model call or repair was consumed. This generic repair-schema issue remains open; no provider limit was increased.

An explicit revision corrected the scopes, placed the real writer after collection and added root cleanup. Final review then removed the search-list fallback from product-detail interpretation through the existing input-only revision command. A structural comparison confirms that this last revision changed only that input list; accepted requirements and every other TaskPlan field stayed identical. The resulting plan has no diagnostics, four independent extraction declarations and four explicit one-level flatten bindings. Global interpretation receives selected candidates and original completeness metadata, while original snapshots remain separate.

These are generated composition results, not live evidence that compact prompts or extracted values are correct. Seven planning calls and four review revisions also fall short of the minimal-call objective. All original provider responses, failed proposals, diagnostics and review commands are retained in this directory and in encrypted campaign records.

## Execution and independent oracle

Execution started at **2026-10-08 05:38:19 UTC**. The first `browser_get_content` call requested `https://www.amazon.fr/`, `observation_complete`, `domcontentloaded`, and the artifact's existing **30,000 ms** timeout. It returned a verified MCP `TIMEOUT` after **30,030 ms**:

> Complete snapshot acquisition exceeded its shared timeout.

Acquisition reported **zero capture attempts**, no invalidations, and no title or HTTP status. In the current Browser implementation this places the timeout before the capture loop, during page preparation or initial navigation. The retained evidence does **not** distinguish a network/site delay from a Browser initialization/navigation defect. It does not demonstrate a snapshot-generation, flattening or mapping failure. No additional external probe or replay was performed.

| Execution check | Result |
| --- | --- |
| Workflow success | Failed |
| Independent oracle | Failed: `workflow_execution_failed`, `workbook_missing` |
| Complete snapshots / product visits | 0 / 0 |
| Runtime inference / mapping calls / specializations | 0 / 0 / 0 |
| Cache behavior / final global prompt size | Not exercised |
| Runtime verified tokens / paid cost | 0 / EUR 0 |
| Workflow Browser cleanup | Passed; root finalizer closed the Browser |
| Independent cleanup check | Passed; subsequent read returned “No active page” |
| Durable completion | Verified error receipt and verified cleanup receipt; no reconciliation error |
| Normal / finalization steps started | 1 / 1 |
| Execution / total latency | 32.394 / 301.211 seconds |

The oracle's additional idempotent close is separate from the workflow finalizer; the independent read verified that the workflow had already closed the Browser. No XLSX was produced. Cookie handling, full observation processing, actual product visits, runtime flattening/compaction and workbook values remain unverified in this fresh live.

[Execution and MCP events](live-execution.json), [journal measurements](journal-metrics.json), [six-slot report](live-report.json). The report remains **0/6**, comprising this failed execution and five unexecuted slots. There was no code-review run, cohort expansion, historical replay or uncertain-invocation resumption.

## Accounting and next diagnostic

Campaign `schema-portability-20261002` increased from **EUR 99.356868** to **EUR 100.544321 / 150** committed or reserved. The **EUR 3.905040** historical reservation and three unknown transport attempts remain unchanged. This fresh run has no unknown attempts. [Accounting](accounting-after.json).

The next useful investigation is to distinguish Browser page preparation from initial navigation and reproduce a demonstrated defect locally before changing behavior. Separately, reproduce the retained repair-schema nesting failure without inference. Neither issue justifies larger limits, weaker oracles or replaying this execution. Another live execution requires a fresh identity and its own concrete review. PR #117 remains draft; the historical 33/33 benchmark and all previous failed cohorts remain unchanged.

## Reproducible commands

The commands below were run from the frozen clean candidate. Inspection is read-only. The retained run has already executed and **must not be executed again**.

```sh
dotnet build tests/GnOuGo.Agent.Planning.Benchmark/GnOuGo.Agent.Planning.Benchmark.csproj -m:1 -warnaserror -p:SkipClientBuild=true

dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability provider-readiness --workspace /Users/a115vc/Desktop/GnOuGo --campaign schema-portability-20261002

dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --workspace /Users/a115vc/Desktop/GnOuGo --campaign schema-portability-20261002 --run consumerflat20261008a-amazon-1

dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --workspace /Users/a115vc/Desktop/GnOuGo --campaign schema-portability-20261002 --cohort consumerflat20261008a
```

The fresh `plan`, `revise`, and single `execute` phases used `--case amazon --max-products 10 --cohort consumerflat20261008a --run consumerflat20261008a-amazon-1` with the same workspace/campaign. The four revision commands and revision/hash-bound execution approval are retained. Full execution data and receipt payloads remain encrypted; published evidence excludes configuration secrets and raw page content.
