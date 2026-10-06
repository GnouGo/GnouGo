# Fresh Amazon retry after console cancellation

The user authorized one fresh live retry on October 6. The prior `consumerjournal20261006a-amazon-1` attempt remains cancelled and is not reset, revised or replayed. Its [failure and console correction](consumer-compaction-and-journals-2026-10-06.md#execution-outcome-and-narrow-harness-correction) remain separate evidence.

## Frozen candidate and readiness

This retry uses clean isolated candidate/harness **`716d4b5510a3759fb54283eb1e9d6476b0adcc5e`**, cohort **`consumerretry20261006b`**, run **`consumerretry20261006b-amazon-1`**. No production code, planner/mapping/runtime architecture, permissions, limits, prompt or execution oracle changed during this retry. The only harness difference from the preceding live candidate is the tested console-cancellation correction. The original artifact is untouched.

A fresh benchmark/MCP build passes with `-warnaserror` (zero warnings/errors). Actual local Browser navigation/cleanup and Document workbook writing with independent XLSX reading pass without inference. The exact configured model and limits are resolved through existing configuration. Starting campaign upper bound: **EUR 86.696331 / 150**, including unchanged **EUR 2.606753** unknown reservations. The new cohort and all six run labels were absent before collection.

CI originally failed in `CopilotTasksTests.CancellationReachesTaskOwnerAndRetainsPartialCheckpoint` during temporary-directory teardown (`Directory not empty`). Planner and persistence suites passed. The original failure is [retained](evidence/consumer-retry-2026-10-06/ci-retained-failure.json); the failed job was rerun without code/assertion changes. Current [CI snapshot](evidence/consumer-retry-2026-10-06/ci.json) is distinct from the earlier successful local 49-test console/campaign/approval validation.

## Planning and explicit review

The initial proposal compiled after two calls, but review rejected whole raw observations flowing into global interpretation and completeness expressed only in prose. Revision 4 added real completeness guards and compact views, but implemented typed field copies as whole-array learned extraction and duplicated search records. Both proposals and implementation-only feedback are retained. No rejected artifact was executed or approved.

**Revision 6**, artifact **`e4229b3d32b7375133feb9962e00a0bf0d517ee451d1fa15db887d445cb6ab0d`**, preserves accepted requirements and uses direct typed record exports, original manifest/page completeness guards, conditional observed consent, fresh post-consent capture, real bounded per-product navigation, the Document workbook write and root cleanup. No dynamic mapping or per-record inference is emitted. The normal three-product path contains seven explicit interpretations; actual observations and request sizes remain subject to unchanged admission checks and the independent oracle.

[Exact artifact, requirements and review](evidence/consumer-retry-2026-10-06/amazon-review.md). This is a newly generated artifact and awaits its own explicit revision/hash-bound approval and all four requirement acknowledgments. **Execution has not started.** The terminal will remain attached during execution; the existing deadline and cancellation behavior are preserved.

Planning totals: **4 logical calls / 4 physical attempts, 0 automatic repairs, 2 review revisions, 2 discovery reads**; **53,750 input / 19,354 output tokens**, **EUR 0.758095**, **200,439 ms**. All new usage is verified. Campaign upper bound: **EUR 87.454426 / 150**, leaving **EUR 62.545574**, with historical unknown reservations unchanged. [Frozen manifest, per-run accounting and six-slot report](evidence/consumer-retry-2026-10-06/cohort-awaiting-approval.json).

## Reproduction and live boundary

Use the clean frozen checkout and the existing campaign/workspace configuration:

```sh
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror -p:SkipClientBuild=true -p:SkipModelMetadataGeneration=true
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --campaign schema-portability-20261002 --workspace /path/to/workspace --run consumerretry20261006b-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --campaign schema-portability-20261002 --workspace /path/to/workspace --cohort consumerretry20261006b
```

Only after explicit review, the existing `execute` command takes `--case amazon --cohort consumerretry20261006b --run consumerretry20261006b-amazon-1 --review-command <approval.json>`, plus the same campaign/workspace. Its approval must name revision 6, the exact artifact hash and the four requirement IDs in the review. No approval command has been populated. Never reissue execution once started.

Product visits, observed XLSX values, actual consumer request sizes, workload journal measurements and workflow cleanup remain unverified. No code-review live or cohort expansion is included. PR #117 remains draft; a single Amazon success would not satisfy the six-run gate.
