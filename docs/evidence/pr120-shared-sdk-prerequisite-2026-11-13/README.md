# Shared validation and the next real SDK prerequisite

The production implementation is `71342c8e9c6a78566e3fd48e1f9f24eb25f54f0c`. This evidence changes no runtime code, provider, endpoint, model, retry, timeout, output allocation, permission or historical session. The original successful SDK05 remains the KeyVault OpenAi / `gnougo-structured-tools-v1` reference. Full Amazon and Code qualification remain **0/2**; final frozen Native/Collections qualification is pending.

## Shared checks

[Validation](validation.json): Release solution build with `-warnaserror`, zero warnings/errors; **9,200 passed, 13 existing skips, zero failures** across all 33 test projects. The 32 non-Server projects pass 6,759 cases. Server passes 2,441 with one existing opt-in skip; exact discovery coverage and unchanged binary fingerprints include all 218 workbook cases. Required existing NuGet files were initially absent; ordinary package restore resolved that environment failure without a source change. No paid call or live workflow success follows from these checks.

## Actual dependency probe, fresh identity

[`code-offline-dependencies-20261112-05`](sdk-offline05.json) executes exactly one fixed command in the unchanged SDK sandbox, verifies its unchanged script and matching terminal/file report, cold-reloads its completed encrypted receipt, and cleans up the checkout. Real Node 24.20.0, pnpm 10.34.5 and Python/Python3 3.11.13 match. Three actual HTTP 200 JSON replies terminate with `stop` and known usage. The exact captured-tariff receipt sum is EUR1.2382546701561230935836115874; per-HTTP timing is unavailable in this legacy SDK capture and is not inferred.

`uv sync --group tests` now exits **0**, including local packages. `pnpm install --frozen-lockfile` exits **1**: `ERR_PNPM_NO_OFFLINE_TARBALL` for `@chromatic-com/storybook` 5.0.1. The completed SDK command and receipt do not qualify the environment or the Code workflow. The package/workspace/lock hashes equal the pinned source; no project `.npmrc` or `.pnpmfile.cjs` is present. The cache-loss origin is not established.

## Cache reproduction and bounded provisioning

The [host red/green check](pnpm-cache-red-green.json) reproduces the same offline miss with zero model calls and repository scripts disabled. Normal registry provisioning initially encounters a TLS connection reset; one ordinary retry completes through the same route with normal TLS validation. A fresh manifest-only offline install then exits **0**, with every input hash unchanged. Host package-cache preparation does not execute or build repository source and does not qualify SDK lifecycle scripts. A new independent SDK identity must verify the complete install; probe05 is never replayed.

The isolated source-consumer experiment is prepared separately: the complete retained source is supplied once, the existing execution adapter keeps medium reasoning and the current 32,768-token runtime reservation, and an independent retained proof set determines all 13 required own-read occurrences/order/repetitions. No source probe, planning fragment, navigation, business execution or oracle pass is claimed here.

## Finite accounting

[Post-probe inspection](financial.json): the existing EUR1,700 ceiling and all six grants remain. All 50 unknown historical HTTP attempts, unresolved HTTP reservations and EUR89.39466414694980587609060346 retained application holds remain. Arithmetic headroom is EUR255.67190876910096758204989284; it is not provider-invoiced or globally available funding. No supplement, reset, Unlimited or reservation release. Stopped Amazon v14 and Code23 histories remain unchanged.
