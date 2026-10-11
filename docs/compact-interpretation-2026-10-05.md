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

The existing 24,000-token retained-discovery regression still resolves the same contracts in seven calls with zero repairs; its last request estimates 23,997 tokens on final candidate `c1160e17`. Generation guidance is 1,777 UTF-8 bytes versus 1,775 before this correction; contracts and ceilings are unchanged. Tests use arbitrary business input/result names; no integration rule enters production planning.

## Validation and live execution

- Final solution with `-m:1 -warnaserror`: **4,544 passed, zero failed, 12 expected skips**, across 33 projects; this includes 978 planner and 663 Agent.Server tests. The skips remain seven Windows-only cases and five opt-in paid evaluations.
- All **22** local Flow/Browser/Document execution scenarios pass, including both new compaction variants. The frontend production build and existing browser UI check pass through the solution run.
- Planning Release package, published macOS arm64 Native AOT smoke (28 checks) and planning-skill validation pass. Persistence implementation is unchanged; the solution includes its encrypted recovery regressions and the published planning smoke retains its storage checks.
- The first full run at `6997cd46` retained two failures because prompt compression omitted the explicit “compatible alternatives” wording. `c1160e17` restores it; the assertions, contracts and allowances remain unchanged. The final full run passes.

Production/harness candidate is **`c1160e17`**. [Validation](evidence/compact-interpretation-2026-10-05/validation.json), [source and oracle hashes](evidence/compact-interpretation-2026-10-05/candidate.json), and [zero-inference readiness](evidence/compact-interpretation-2026-10-05/readiness.json) are retained separately from provider execution. No deterministic result is live-provider acceptance. PR #117 remains draft.

Reproduce deterministic checks:

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~CompactObservationTests|FullyQualifiedName~RecordedTargetedDiscoveryTests'
Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0 dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipModelMetadataGeneration=true --filter FullyQualifiedName~LocalProductOutcomeExecutionTests
PLAYWRIGHT_MODULE_PATH="$PWD/src/GnOuGo.Browser.Mcp/bin/Debug/net10.0/.playwright/package/index.mjs" Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0 dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -p:SkipModelMetadataGeneration=true
```

## Fresh Amazon review

Frozen candidate/harness `c1160e17`, cohort `compactlive20261005a`, run `compactlive20261005a-amazon-1` uses the unchanged prompt, three-product oracle, provider configuration and permissions. The initial campaign upper bound was EUR 82.503774 / 150, including EUR 2.606753 historical unknown reservations. No code-review run, uncertain invocation replay or cohort expansion was started.

Compilation succeeded for these proposals, but explicit implementation review rejected them before execution. All requested revisions preserve accepted requirements and share the original eight-attempt/two-repair session allowance:

| Proposal | Review finding | Retained evidence |
| --- | --- | --- |
| Revision 2 | Raw HTML directly to global interpretation; no producer completeness guards; placeholder product rows | [Proposal and revision request](evidence/compact-interpretation-2026-10-05/rejected-r2.json) |
| Revision 5 | Compaction added, but completeness was model-generated and missing product fields had fallback labels | [Proposal and revision request](evidence/compact-interpretation-2026-10-05/rejected-r5.json) |
| Revision 7 | Each whole page reduced to one record; nonnullable observation fields; extraction asked to manufacture status | [Proposal and revision request](evidence/compact-interpretation-2026-10-05/rejected-r7.json) |
| Revision 9 | Record-level extraction corrected; whole product-loop envelope still leaked into final interpretation and publication guards omitted search/blocker checks | [Proposal and revision request](evidence/compact-interpretation-2026-10-05/rejected-r9.json) |
| Revision 12 (final) | Compact exported products now reach global interpretation, but generation removed `writeWorkbook`; the last repair substituted a literal `filePath`. Required external work is absent. Search/blocker publication guards also remain incomplete. | [Final proposal, repair and rejection](evidence/compact-interpretation-2026-10-05/rejected-final-r12.json) · [Unapproved YAML](evidence/compact-interpretation-2026-10-05/rejected-final-r12.yaml) |

**Final result: incomplete, rejected before approval and execution.** Revision 12, artifact `2bb69386cb6d0df78a8d746d56fd6e5e4ee133a05e39f421b31c540bb891b3fa`, compiles and is stored at `final_review`, but does not implement `save_excel`. A literal path is not a written workbook. The review gate caught this; no acknowledgment was submitted, no Browser/Document execution began, and no workflow was approved. The original eight-call/two-repair allowance is exhausted. No further paid dispatch or new run was attempted.

| Measurement | Fresh Amazon planning | Live execution |
| --- | ---: | --- |
| Logical calls / physical attempts | 8 / 8 | 0 / 0 |
| Scoped repairs / explicit review revisions | 2 / 4 | Not started |
| Discovery reads | 2 | Not started |
| Verified input / output tokens | 113,337 / 48,915 | 0 / 0 |
| Accounted cost | EUR 1.815544 | EUR 0 |
| Planning latency | 741,646 ms | Not started |
| Mapping samples/items/cache and complete runtime request sizes | Not applicable | Unmeasured; execution never started |
| Independent execution oracle | Not applicable | Not run; cannot pass |

Campaign upper bound is **EUR 84.319318 / 150**, leaving EUR 65.680682. It includes unchanged EUR 2.606753 historical unknown reservations; this run adds no unknown completion. The [unchanged six-slot cohort report](evidence/compact-interpretation-2026-10-05/cohort.json) is **0/6, incomplete**; five slots were intentionally not attempted. [Machine-readable metrics](evidence/compact-interpretation-2026-10-05/live-result.json) distinguish planning from execution. Neither local success nor improved generated compaction establishes Amazon live acceptance.

These are retained failed proposals, not successful executions or evidence that generation met the one-call target. No production rule, oracle or mapping behavior was changed to accept them. Exact artifact approval and independent execution remain separate gates.

Reproduction uses the clean frozen checkout and existing encrypted campaign harness. `inspect-run` and `report` are read-only; never rerun `plan`, `revise` or `execute` against historical or already-started identities:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --campaign schema-portability-20261002 --run compactlive20261005a-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --campaign schema-portability-20261002 --cohort compactlive20261005a
```

Original requests, responses, schemas, revisions and cumulative accounting remain in encrypted storage. Published evidence omits credentials and captures no external page data before execution.
