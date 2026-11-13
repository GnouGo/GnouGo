# Approved prerequisites observed on e6445a04

These results precede full workflow qualification. Amazon and Code remain **0/2**; no source check, native test or SDK probe establishes business execution. The production source is `e6445a041c4eddc6cbe7152101df9138d705f072`.

## Actual owning-Browser source

The [two independent source checks](amazon-source.json) use the actual configured Release MCP, one owning Browser, its delegated permission guard and the separately approved v10/v14 proof rules. Source04 has 1,472 original records and no AAX entry. Source05 has 1,439 original records, including four AAX records: three navigable occurrences with their own ordered GET 302 → GET 200 product proof and distinct captures, plus one exact same-card review alias claiming no HTTP receipt. All 13 own resolutions and every raw record/repetition remain retained. Warm and cold qualification agree. Browser close succeeds; the subsequent probe error is expected evidence that its page is unavailable. Zero model calls, later business visits, extracted workbook rows or full oracle passes.

This validates the source prerequisite after the exact-response lifecycle correction in `e6445a04`; it does not qualify a generated classifier or an execution artifact.

## SDK installs and offline cache

The original SDK05 reference remains immutable. The earlier [actual toolchain success](../pr120-approved-toolchains-2026-11-12/README.md) uses the unchanged KeyVault OpenAi provider and `gnougo-structured-tools-v1`.

[SDK03](sdk03.json) and [SDK04](sdk04.json) each execute the fixed probe exactly once, verify its unchanged script and matching terminal/file report, cold-reload the completed durable receipt, and clean up their own checkout. Both observe real Node 24.20.0, pnpm 10.34.5 and Python/Python3 3.11.13. Both complete `pnpm install --frozen-lockfile`. The Python dependency prerequisite fails: SDK03 lacks registry metadata; after metadata-only provisioning SDK04 reaches an uncached registry distribution metadata lookup. These are recorded failures, with no SDK network/bypass extension and no full Code qualification. The exact private harness source and inputs were retained before dispatch; those probe preparations were not committed before dispatch.

Debugging the pinned manifests shows a declared dependency group missing from the existing lock metadata, so ordinary `uv sync --group tests` re-resolves across supported Python/platform branches. Merely copying source archives into a flat directory does not populate native registry cache identity. [Cache preparation](cache-preparation.json) builds only four exact hash-pinned third-party distributions, never repository packages, and warms additional registry/build-backend metadata. All 17 local repository-package builds are prohibited during host lock checks and offline installation. The original captured manifests remain unchanged; generated lock metadata is confined to disposable staging.

The [native-cache offline check](native-cache-compatibility.json) succeeds for all third-party packages with every local package excluded. All 384 package/version/registry identities remain unchanged. A temporary flat-index comparison also installs successfully but rewrites eight registry origins, so the prepared next SDK profile uses the native cache without that hint. Its actual full local-package install is still pending a fresh independent admission. No previous failed session is replayed.

The [financial audit](financial-before-amazon.json) is **before** the newly admitted Amazon attempt, not current available funding: EUR1,700, all six grants and all historical charges/holds remain. There is no supplement or release. Global invoiced/available funding stays unavailable.

## Frozen-source validation

[Complete disjoint solution coverage](validation-e6445a04.json): 9,189 passed, 13 existing platform/opt-in skips, zero failures; Release build has zero warnings/errors with `-warnaserror`. Server inventory coverage is exact with unchanged binaries, including all 218 workbook fixtures. These results belong to e6445a04.

[All three CI workflows](ci-e6445a04.json) are green on that exact HEAD: [planner](https://github.com/GnouGo/GnouGo/actions/runs/43307265230), [standalone](https://github.com/GnouGo/GnouGo/actions/runs/43307265305), [Native AOT](https://github.com/GnouGo/GnouGo/actions/runs/43307265250). Later changes require their own validation. The earlier failed-load run is not combined with this result.
