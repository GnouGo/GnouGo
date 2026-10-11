# Compact candidates before global extraction

The retained `exportglue20261006a-amazon-1` failure consumed all 52 observation pages, then supplied the projected collection to one global extraction. Its target was a single consent decision, so independent-item mapping could not apply. The complete request exceeded the existing allowance and stopped before paid mapping dispatch. That failed invocation, approval and receipts remain unchanged. [Retained execution](evidence/export-glue-2026-10-06/amazon-execution.md).

Frozen production candidate: `4d0337b589aada4406531923c7596895c8246253`. The production correction changes compact planning guidance only: compact before **either global extraction or interpretation**. An explicit independent extract produces one result per complete bounded item; that result can contain observed candidates, including an empty candidate array after checking absence. Global selection or interpretation then consumes those compact facts. Group context, order, nesting, completeness evidence and necessary references remain explicit. A whole page that cannot fit must use its declared record collection or existing loops. Missing data and oversized necessary context still fail.

There is no runtime, mapping, schema, compiler, permission or limit change. Response schemas retain their historical authority fingerprints. The planning skill and runtime-mapping documentation explain the distinction; a collection input alone never changes a global transform into independent extraction.

## Deterministic evidence

The sanitized reproduction uses arbitrary input/port names, 52 complete pages and 1,488 records. Relevant duplicate candidates occur outside the generation examples. The original composition exceeds the unchanged 96,000 allowance with zero model dispatches. The corrected composition processes every page, preserves nested empties and exact references, and uses one mapping generation plus one global decision. An incomplete source blocks both calls.

| Composition | Complete request bytes | Test token estimate |
| --- | ---: | ---: |
| Global extraction, rejected | 617,543 | 166,058 |
| Independent mapping generation | 28,681 | 7,931 |
| Global decision on candidates | 1,251 | 526 |

These are serialized-request measurements from the deterministic test estimator, not verified provider usage. Renamed fields and the genuinely absent-candidate variant also pass. Existing narrower 12,000-limit regressions remain. [Measurements](evidence/global-extraction-2026-10-06/deterministic-measurements.json).

Two real Flow/Browser/Document local fixtures use noisy observations, multiple snapshot pages, record-level independent extraction, an explicit global consent decision, product visits and actual XLSX writing. Consent-present and consent-absent variants pass the independent cell, visit and cleanup assertions, each with one deterministic planning call and zero repairs. Mapping request estimates remain below their existing 12,000 allowance. Deterministic adapters supply inference; this is not live-provider acceptance.

During development, whole-page examples exceeded that narrower fixture allowance and were rejected. The corrected fixture extracts from complete records within each page. Draft schema-description edits also failed historical-fingerprint validation and were reverted; the prompt was shortened to preserve the 24,000-token discovery regression. No allowance or assertion was relaxed.

The full solution passes **4,640 tests, zero failures and 12 existing skips** across 33 projects with warnings as errors. The skips are seven Windows command cases and five paid Copilot cases. Planning Release packaging, Native AOT smoke and skill validation pass; no frontend or persistence implementation changed. The isolated candidate build has zero warnings/errors. [Solution](evidence/global-extraction-2026-10-06/solution-validation.json) · [Other checks](evidence/global-extraction-2026-10-06/validation-checks.json).

## Reproduction and live boundary

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests'
PLAYWRIGHT_MODULE_PATH="$PWD/src/GnOuGo.Browser.Mcp/bin/Debug/net10.0/.playwright/package/index.mjs" dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true
```

One fresh Amazon evaluation with at most ten products is authorized after deterministic validation. Fresh generation must retain complete observations, actual product visits, observed workbook values and cleanup. Its artifact requires its own revision/hash-bound approval and explicit requirement acknowledgments before execution. No code-review run, cohort expansion or replay of an uncertain invocation is included. PR #117 remains draft; historical cohorts are not pooled with this candidate.

Provider metadata and disposable Browser/Document readiness pass with zero inference. The rechecked campaign upper bound before fresh planning is **EUR 91.906340 / 150**, including the unchanged EUR 2.606753 unknown reservations. New cohort `boundeddecision20261006a` was verified unused. Its only authorized run is `boundeddecision20261006a-amazon-1`; the other five slots remain unexecuted. [Readiness](evidence/global-extraction-2026-10-06/readiness.json) · [Ledger](evidence/global-extraction-2026-10-06/ledger-before.json).

## Fresh planning result

The new artifact reaches review at revision **7**, after five calls, zero repairs and two concrete review revisions. The initial proposal still repeated the raw global extraction; the next declared one object per source record despite requesting optional candidates. Both were rejected during review without execution. The final proposal has explicit nested candidate arrays, consumer-specific fields, deterministic product assembly and guards on original completeness metadata. Requirements and unrelated work remain preserved. This demonstrates a reviewable corrected composition, **not reliable first-proposal generation**.

Known planning usage is 72,806 input / 21,846 output tokens, EUR 0.904614 and 331,564 ms. The campaign upper bound is EUR 92.810955 / 150 with historical unknown reservations intact. [Review the exact new artifact](evidence/global-extraction-2026-10-06/amazon-review.md). At that checkpoint it had not executed. The subsequent approved execution is recorded below. PR #117 remains draft.

## Approved execution result

The user approved the exact revision-7 hash; the artifact executed once and remains byte-for-byte unchanged. Browser returned HTTP 202 and a three-page snapshot manifest, then rejected the matching first cursor as expired. No mapping, product visit or workbook write occurred. The failed read has a verified durable completion, and Browser cleanup passed with one finalization step. The oracle failed (`workflow_execution_failed`, `workbook_missing`). [Full result, diagnosis and proposed next investigation](evidence/global-extraction-2026-10-06/amazon-execution.md).

Existing local tests reproduce navigation invalidating snapshots and fresh capture succeeding: three tests pass without source changes or paid inference. The live run did not retain its triggering navigation event, so its specific navigation cause is not established. Stale-cursor rejection remains intact.

Execution used one URL-construction call, 116 verified input / 96 output tokens and EUR 0.003070; execution including oracle was 17,167 ms. Campaign upper bound is **EUR 92.814025 / 150**, including unchanged unknown reservations. The cohort remains **0/6**, with one failed and five unexecuted slots. No automatic replay, new code-review run or cohort expansion is authorized by this result.
