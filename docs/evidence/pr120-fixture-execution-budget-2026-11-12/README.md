# Fresh execution allowance in controlled workbook fixtures

## Demonstrated cause

The full Server check on `f207d8b5` encounters
`RetainedCompositionVisitsProductsAndWritesIndependentlyCheckedExcel(typed-lookup-repair)`
returning `LLM_BUDGET_EXCEEDED`. Static compilation takes about 24 seconds and
execution about 97 seconds; the two-minute execution scope was constructed
before catalog discovery and compilation. It therefore includes work that makes
no inference and precedes this new workflow execution. Only `MaxElapsed` is
configured on that scope. This is a controlled fixture failure, not a paid
provider response, incomplete SSE stream or live Amazon qualification.

The failing run and its output are retained. Remaining local partitions are
stopped after that first failure; the partial run is not counted as a successful
shared check. No campaign record, financial hold or historical live session is
changed by this test operation.

## Regression and correction

A new regression advances a controlled clock by three minutes during offline
preparation. With the old initialization boundary it fails at the first mapping
admission: `limit_kind=elapsed_ms`, `limit=120000`, `current=180000`, zero fixture
inference requests. This reproduces the cause without waiting three minutes or
changing limits.

Reuse the newer fixture's existing boundary in one private test helper: require
that the engine has no budget scope, then create the unchanged finite two-minute
allowance immediately before execution. Apply it to the three older fixture
families and the already-correct newer family. No existing scope is replaced or
restored accounting reset. Production hosts, admission, limits, clocks, provider,
transport and campaign journals are unchanged.

The regression executes the original typed-lookup repair, verifies every expected
workbook row/cell, cleanup and cold restoration, then advances the clock by two
minutes and requires ordinary hard elapsed-time rejection. This keeps expiry
coverage rather than raising the limit or changing it into a warning.

Both the new regression and the existing preparation-boundary regression pass in
Release with `-warnaserror`, zero skips. The [sanitized red/green evidence](regression.json)
preserves the failed partial run and log hashes. All 6,728 non-Server tests at
`f207d8b5` pass with 12 existing skips; this fixture-only correction changes no
non-Server source. Complete Server coverage must be rerun on the corrected build.

Complete final validation is reported separately. Controlled workbook execution
does not establish real Amazon extraction, a complete accepted live plan or an
independent live-oracle success. Amazon and Code remain **0/2** each.
