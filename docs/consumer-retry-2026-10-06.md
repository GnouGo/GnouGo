# Fresh Amazon retry after console cancellation

The user authorized one fresh live retry on October 6. The prior `consumerjournal20261006a-amazon-1` attempt remains cancelled and is not reset, revised or replayed. Its [failure and console correction](consumer-compaction-and-journals-2026-10-06.md#execution-outcome-and-narrow-harness-correction) remain separate evidence.

## Frozen candidate and readiness

This retry uses clean isolated candidate/harness **`716d4b5510a3759fb54283eb1e9d6476b0adcc5e`**, cohort **`consumerretry20261006b`**, run **`consumerretry20261006b-amazon-1`**. No production code, planner/mapping/runtime architecture, permissions, limits, prompt or execution oracle changed during this retry. The only harness difference from the preceding live candidate is the tested console-cancellation correction. The original artifact is untouched.

A fresh benchmark/MCP build passes with `-warnaserror` (zero warnings/errors). Actual local Browser navigation/cleanup and Document workbook writing with independent XLSX reading pass without inference. The exact configured model and limits are resolved through existing configuration. Starting campaign upper bound: **EUR 86.696331 / 150**, including unchanged **EUR 2.606753** unknown reservations. The new cohort and all six run labels were absent before collection.

CI originally failed in `CopilotTasksTests.CancellationReachesTaskOwnerAndRetainsPartialCheckpoint` during temporary-directory teardown (`Directory not empty`). Planner and persistence suites passed. The original failure is [retained](evidence/consumer-retry-2026-10-06/ci-retained-failure.json); the failed job [passed on rerun](evidence/consumer-retry-2026-10-06/ci-rerun-final.json) without code/assertion changes. These frozen-candidate checks are separate from validation of the subsequent Browser correction.

## Planning and explicit review

The initial proposal compiled after two calls, but review rejected whole raw observations flowing into global interpretation and completeness expressed only in prose. Revision 4 added real completeness guards and compact views, but implemented typed field copies as whole-array learned extraction and duplicated search records. Both proposals and implementation-only feedback are retained. No rejected artifact was executed or approved.

**Revision 6**, artifact **`e4229b3d32b7375133feb9962e00a0bf0d517ee451d1fa15db887d445cb6ab0d`**, preserves accepted requirements and uses direct typed record exports, original manifest/page completeness guards, conditional observed consent, fresh post-consent capture, real bounded per-product navigation, the Document workbook write and root cleanup. No dynamic mapping or per-record inference is emitted. The normal three-product path contains seven explicit interpretations; actual observations and request sizes remain subject to unchanged admission checks and the independent oracle.

[Exact artifact, requirements and review](evidence/consumer-retry-2026-10-06/amazon-review.md). The user explicitly approved all four requirements. The [approval command](evidence/consumer-retry-2026-10-06/approved-r6-command.json) binds revision 6 and the exact hash. It advanced the saved revision to 7 without changing the YAML. **Execution completed once and failed**, as detailed below. Runtime confirmation was answered under the same authority with the terminal attached.

Planning totals: **4 logical calls / 4 physical attempts, 0 automatic repairs, 2 review revisions, 2 discovery reads**; **53,750 input / 19,354 output tokens**, **EUR 0.758095**, **200,439 ms**. All new usage is verified. Campaign upper bound: **EUR 87.454426 / 150**, leaving **EUR 62.545574**, with historical unknown reservations unchanged. [Frozen manifest, per-run accounting and six-slot report](evidence/consumer-retry-2026-10-06/cohort-awaiting-approval.json).

## Reproduction and live boundary

Use the clean frozen checkout and the existing campaign/workspace configuration:

```sh
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror -p:SkipClientBuild=true -p:SkipModelMetadataGeneration=true
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --campaign schema-portability-20261002 --workspace /path/to/workspace --run consumerretry20261006b-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --campaign schema-portability-20261002 --workspace /path/to/workspace --cohort consumerretry20261006b
```

The existing `execute` command was used once with `--case amazon --cohort consumerretry20261006b --run consumerretry20261006b-amazon-1 --review-command <approval.json>`, plus the same campaign/workspace. **Do not reissue it.** The commands above only inspect retained evidence.

## Live failure and generic producer correction

The first capture returned a complete 83-record snapshot in three manifest pages (62, 10 and 11 records), with both truncation flags false. Reading its first cursor with `format: observation` immediately returned `INVALID_INPUT`: “The observation cursor is invalid or expired.” The workflow stopped with `MCP_CALL_ERROR`. It had not navigated or interacted between capture and continuation.

The producer selected its cursor parser from the requested format: `observation_pages` expected a page cursor, while `observation` expected a legacy offset. A valid page cursor was therefore rejected before checking its actual saved snapshot. A local arbitrary-page regression reproduced the same failure without inference or external navigation.

Browser now resolves cursor layout from the immutable stored snapshot. Both observation formats can read that snapshot, retaining its original returned format and frozen page bounds. HTML/text continuation, new URL/selector arguments, frozen-limit overrides, malformed cursors and navigation-invalidated cursors remain rejected. No automatic recapture, hidden click, planner rule, mapping change or site-specific behavior was added. Producer documentation and discovery descriptions expose the behavior.

The live failure remains in the denominator: **0/1 attempted execution oracles passed**; the other five cohort slots remain unexecuted. The oracle reports `workflow_execution_failed` and `workbook_missing`. Workflow Browser cleanup completed and an independent read confirmed no active page. The failed external call has a durable observed-completion receipt; no reconciliation bypass or new unknown reservation was involved. Product visits, XLSX values and the large global consumer request remain unverified. The cursor correction has not been evaluated in another paid execution.

[Exact events and runtime inference observations](evidence/consumer-retry-2026-10-06/execution-observations.json) · [Oracle](evidence/consumer-retry-2026-10-06/execution-oracle.json) · [Final cohort](evidence/consumer-retry-2026-10-06/cohort-final.json).

| Stage | Calls / attempts | Input / output tokens | Cost EUR | Latency |
| --- | --- | --- | --- | --- |
| Planning, including two review revisions | 4 / 4 | 53,750 / 19,354 | 0.758095 | 200,439 ms |
| Execution, before first page read failed | 1 / 1 | 113 / 102 | 0.003235 | 36,769 ms |

No automatic repairs or mapping inference occurred. Campaign **known cost EUR 84.850909 + retained unknown reservations EUR 2.606753 = upper bound EUR 87.457661 / 150**, leaving EUR 62.542339. [Exact decimal accounting](evidence/consumer-retry-2026-10-06/execution-result.json).

Read-only public KeyVault inspection [reconstructed the failed schema-9 journal](evidence/consumer-retry-2026-10-06/journal-measurement.json): 20 invocations, 70 events, 753,323 logical JSON bytes, a 3,865-byte checkpoint and 245 retained blocks totaling 762,086 JSON bytes. Blocks include superseded immutable records and exclude encryption/database overhead. This early failure is not representative collection-workload performance; the earlier growing-collection measurements remain separate deterministic evidence.

No code-review live or cohort expansion is included. PR #117 remains draft. Deploying the producer correction requires rebuilding/restarting Browser MCP and refreshing discovery. Preserve this artifact, approval and history; any subsequent live execution uses a fresh identity and separately reviewed artifact approval.

## Deterministic correction checks

The [regression](evidence/consumer-retry-2026-10-06/cursor-reproduction.json) fails with the exact live error before the fix. After correction, all 50 Browser tests pass, including byte-equivalent continuation across both observation formats and the unchanged rejection boundaries. The existing local Flow/Browser/Document fixture now reads manifest cursors as `observation`, exercising the production MCP transport with its independent XLSX, visit, consent, completeness and cleanup assertions unchanged.

The [full solution run](evidence/consumer-retry-2026-10-06/cursor-validation.json) passes **4,590 tests**, zero failures and zero warnings with `-warnaserror`, including all **23 local Flow/Browser/Document cases**. The 12 existing skips are seven Windows-only command cases and five opt-in paid Copilot cases. No new paid dispatch occurred during these checks.

Release self-contained Browser publish passes with warnings as errors. The [published MCP smoke](evidence/consumer-retry-2026-10-06/published-cursor-result.json) reads all **60 local records across 15 pages**, verifies identical results through both observation formats, rejects frozen-limit overrides and navigation-invalidated cursors, and verifies Browser closure. Zero model calls. The [initial smoke setup](evidence/consumer-retry-2026-10-06/published-probe-initial.json) requested 30 two-record pages whose manifest exceeded its 2,400-character allowance; the completion assertion correctly failed. The script now explicitly verifies that bound, then recaptures the same 60 records in four-record pages within the same allowance. No production code, existing oracle or host ceiling changed for that probe correction.

Reproduce from the corrected checkout:

```sh
dotnet test tests/GnOuGo.Browser.Mcp.Tests -m:1 -warnaserror -p:SkipModelMetadataGeneration=true
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipModelMetadataGeneration=true --filter FullyQualifiedName~LocalProductOutcomeExecutionTests
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -p:SkipModelMetadataGeneration=true
dotnet publish src/GnOuGo.Browser.Mcp -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror -p:SkipModelMetadataGeneration=true -o /tmp/gnougo-browser-cursor-publish
python3 docs/evidence/consumer-retry-2026-10-06/published-cursor-smoke.py /tmp/gnougo-browser-cursor-publish/GnOuGo.Browser.Mcp /tmp/gnougo-browser-cursor-smoke.log
```

Browser uses its supported self-contained publish profile; Playwright's existing file-based assembly discovery does not support Native AOT. The preceding planning AOT and encrypted-recovery evidence applies to unchanged components and is not presented as a new live result. No frontend or planning skill change is needed for this producer-only correction.
