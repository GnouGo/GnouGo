# Browser-owned complete snapshot acquisition

The retained approved Amazon execution rejected the first cursor of its three-page / 79-record snapshot, before extraction or workbook writing. Cleanup completed and the failed MCP call had a verified receipt. That run and its approval remain unchanged. Its precise navigation trigger was not captured; HTTP 202 alone does not identify one. [Historical execution](evidence/global-extraction-2026-10-06/amazon-execution.md).

Browser now records generation invalidation instead of silently forgetting the snapshot. Known expired cursors return `SNAPSHOT_EXPIRED` with a bounded reason record. Unknown or malformed cursors remain invalid. Legacy pagination never switches generations.

The additive `browser_get_content(format: "observation_complete")` acquires all bounded pages inside the producer and returns `observationSnapshot`. Navigation during acquisition discards the entire attempt. At most two restarts share one deadline; the requested URL is navigated once. No page escapes before the generation check. Capture/page limits, host restrictions and cancellation remain enforced. Complete acquisition never broadens the requested selector, clicks consent or invokes inference. HTTP status is null because an earlier navigation response cannot certify the captured document.

Consumers use ordinary loops over `observationSnapshot.pages` and existing extraction. No planner guidance, compiler, mapping, executor or runtime permission changes are included. Snapshot coverage is not proof of business completeness or future page stability. [Producer contract and limits](../src/GnOuGo.Browser.Mcp/README.md#compact-observations).

## Deterministic regressions

Controlled local navigation tests cover capture/publication boundaries, same-URL reloads, exhausted restarts, shared deadlines, cancellation, closure, replacement, malformed/foreign cursors and pre-navigation limit checks. Fidelity checks compare all records, ordering, groups and exact links with existing paged observations. Truncated captures and oversized values never publish partial complete results.

Two local Flow/Browser/Document fixtures consume complete acquisitions with consent present/absent, compact every observed record, visit each selected product and independently inspect XLSX cells and cleanup. Both reach review in one deterministic planning call with zero repairs. These are deterministic-adapter executions, not live-provider evidence.

The integration fixtures exposed an SDK schema-export issue: the reused record collection becomes a local `$ref` with an empty `items` sibling. Browser discovery now publishes its identical authoritative record definition at the new snapshot page path, so consumers can use that subtree directly. No planner schema resolution or validation rule was relaxed.

A real stdio MCP/Flow regression preserves `SNAPSHOT_EXPIRED` and its cause in an encrypted completion receipt. A new store reconstructs the same completed failure without dispatch; cleanup succeeds and another tenant cannot read the run. Existing unknown-completion safety tests remain unchanged.

## Reproduce

```sh
dotnet test tests/GnOuGo.Browser.Mcp.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true --filter 'FullyQualifiedName~BrowserSnapshotReceiptTests|FullyQualifiedName~LiveObservationOracleTests|FullyQualifiedName~LocalProductOutcomeExecutionTests'
PLAYWRIGHT_MODULE_PATH="$PWD/src/GnOuGo.Browser.Mcp/bin/Debug/net10.0/.playwright/package/index.mjs" dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true
dotnet publish src/GnOuGo.Browser.Mcp -c Release -r osx-arm64 --self-contained true -warnaserror -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false
```

Browser supports managed self-contained publishing, not Native AOT. No frontend implementation or persistence format changed.

Validation on candidate `45473fdafe19da1e770c48c05759df1b516faabf`:

- Full solution: **4,668 passed, 12 skipped, zero failures**, across 33 test projects with `-warnaserror`. The skips retain their existing platform/environment guards. Client rebuilding and model-metadata regeneration were skipped; neither changed.
- Browser: **67 passed**, including the new deterministic navigation/expiration cases.
- Browser managed self-contained Release publish (`osx-arm64`): successful. Actual published-process acquisition, document writing and encrypted-receipt regressions: **3 passed**.
- Configured Browser/Document readiness: local page acquisition, independent XLSX read and Browser cleanup passed with **zero inference**.

[Validation counters and limitations](evidence/browser-snapshot-recovery-2026-10-07/validation.json). These results establish deterministic/local behavior, not live Amazon acceptance.

## Live boundary

One fresh Amazon E2E, at most ten products, is authorized after deterministic validation. The rechecked campaign upper bound is EUR 92.814025 / 150, including the unchanged EUR 2.606753 reserved for two unknown historical completions. Those reservations and invocations remain untouched. Fresh generation and execution use new identities and the existing revision/hash-bound requirement-review gate. No code-review run or cohort expansion is included; PR #117 remains draft.
