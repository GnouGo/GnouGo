# Compact observations before global interpretation

This correction starts from `cecb1c94657f686d6c2496aa63478eff2f9d6f1a` on PR #117. Only generation guidance and the existing TaskPlan review formatter change in production. TaskPlan, compiler, runtime, `mapping.dynamic`, contracts, repair schemas, approvals and budgets are unchanged.

## Retained cause and correction

The [retained request inspection](evidence/approved-live-2026-10-05/request-inspection.json) records six complete pages with 271 observations bound directly to a global interpretation. Its reconstructed complete envelope was 167,301 bytes and approximately 97,398 estimated tokens, exceeding the unchanged 96,000 allowance before dispatch. The source collection was neither duplicated nor polluted by unrelated loop results. The historical execution and its approval remain unchanged and cannot be replayed.

New generation guidance prefers complete observations → typed field selection where possible → explicit independent extraction where needed → global interpretation of compact business observations. It uses existing tasks and bindings only. Complete bounded records avoid moving an oversized page into the extraction examples. All original items still execute; examples do not establish completeness. Necessary source references, grouping and business context survive compaction. Original producer metadata continues to govern completeness. Global interpretation must not receive the raw collection again.

Comparisons, ranking and synthesis remain global. Neither prompt guidance nor shape validation proves that arbitrary compaction preserves meaning: review and independent execution oracles remain required. Unsafe or insufficient reduction fails under the existing limits. No tool-name heuristic, new semantic proof, automatic task rewriting, inferred fallback or relaxed admission was added.

Review diagrams now show each transform's explicit or historical-default mode, named input sources and result fields. Object, array and JSON-encoding inputs expose their nested sources instead of hiding them behind a value-kind label. Historical artifacts remain unchanged; any generated artifact has a new revision/hash-bound review.

## Deterministic evidence

`CompactObservationTests` retains a sanitized structural reproduction with 120 complete observations and unrelated large fields. The corrected composition reaches review in one scripted planning call, zero repairs, and executes through real Flow with deterministic model adapters.

| Composition | Complete global request bytes | Planner input estimate | Mapping calls | Global interpretation calls |
| --- | ---: | ---: | ---: | ---: |
| Raw encoded observations | 204,272 | 66,160 | 0 | 0 (rejected) |
| Independent extraction, then interpretation | 14,794 | 2,684 | 1 | 1 |
| Raw typed observations | 199,107 | 63,321 | 0 | 0 (rejected) |
| Typed field selection, then interpretation | 14,787 | 2,681 | 0 | 1 |

These are synthetic fixture measurements, not provider tokens or a replacement for the campaign estimator. The test adapter enforces a fixed 12,000 input allowance using the existing planner estimator; serialized request bytes include the complete Flow request. The local Browser integration additionally measures complete serialized requests with the unchanged campaign estimator.

The fixture verifies unsampled middle observations, all 120 results, duplicates, source order, explicit nulls, source references and the global comparison's shared context. Empty inputs require no extraction call. Missing observations, incomplete coverage, an oversized mandatory example and a still-oversized compact result fail without downstream interpretation. The missing-observation case shares two attempts across the whole collection.

Two new variants extend the existing real Flow/Browser/Document fixture: noisy paginated observations, with and without explicit cookie consent. Every page is consumed, each record is independently reduced, and global selection receives only nested compact candidates. Independent assertions inspect actual product visits, exact XLSX cells and Browser cleanup. Existing failure, denial and completeness variants are retained.

The existing 24,000-token retained-discovery regression still resolves the same contracts in seven calls with zero repairs; its last request estimates 23,994 tokens. Guidance was compacted rather than increasing this ceiling or dropping contracts. Tests use arbitrary business input/result names; no integration rule enters production planning.

## Validation and live execution

Validation and fresh-run evidence will be recorded after their completion. No deterministic result is live-provider acceptance. PR #117 remains draft.

Reproduce deterministic checks:

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~CompactObservationTests|FullyQualifiedName~RecordedTargetedDiscoveryTests'
Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0 dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipModelMetadataGeneration=true --filter FullyQualifiedName~LocalProductOutcomeExecutionTests
PLAYWRIGHT_MODULE_PATH="$PWD/src/GnOuGo.Browser.Mcp/bin/Debug/net10.0/.playwright/package/index.mjs" Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0 dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -p:SkipModelMetadataGeneration=true
```

After deterministic gates, freeze a clean candidate and a fresh cohort, use the unchanged Amazon prompt and three-product oracle, and request explicit revision/hash/requirement approval before one execution. Continue campaign `schema-portability-20261002` under EUR 150, retaining all unknown reservations. No code-review run, uncertain invocation replay, historical cohort rewrite or cohort expansion is authorized by this correction.
