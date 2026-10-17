# Fresh conditional-interface validation

Candidate and harness: `ee24b8796c96a9da38b3adcdefea5e20cacfd5ca`.
Cohort: `typedbranch20261007a`. Run: `typedbranch20261007a-amazon-1`.
Ten products maximum. Provider/model, medium reasoning, 96,000 input / 32,768 output limits, eight physical planning attempts, two repairs and the independent oracle remain unchanged. See [frozen manifest and complete accounting](cohort-report.json).

**Result: planning reached review; the single execution failed.** No product visits or workbook. Workflow Browser cleanup passed. No code-review run, expanded cohort or uncertain invocation replay occurred.

## Planning and review

- Revision 3 passed compilation, but review rejected an unobserved hardcoded selector introduced during repair and raw snapshots passed into global interpretation.
- Revision 5 removed that selector, but extraction still operated globally on snapshot objects.
- Revision 7 declared independent per-page extraction; review still required matching branch result fields and source-completeness checks before mapping inference.
- Revision 9 changed only four extraction tasks to address those findings. Both alternatives expose matching typed ports and identical `pageFacts` item schemas. Nullable selectors have explicit null conditions. Product visits, document writing and cleanup remain explicit operations.

All proposals and three explicit revision commands are retained beside this report. These revisions use cumulative planning limits; no budget or repair counter was reset. The original conditional type failure was not observed in the final artifact. This run needed manual review revisions and does not demonstrate one-call live planning.

[Concrete review](amazon-review.md) acknowledges the six requirements individually against revision **9**, hash **`2a39b345f7b5715c9b124a688f3854992244d1ffa2df81ee50bed7cb32d1ad92`**. The user's authorization for this next validation was applied through the existing [revision/hash-bound command](approval-r9.json), without reusing an older approval. Stored revision 10 records approval. The YAML is 141,787 bytes / 3,772 lines.

A local revision-command file was initially missing from the detached worktree. The harness rejected it before dispatch; [the preflight failure](local-preflight-failure.json) has zero inference/accounting effect. It is separate from provider and execution failures.

## What execution established

1. Home-page complete acquisition returned 144 records in three pages, one attempt, no truncation or invalidation. Independent extraction processed all three pages after sampling two complete pages.
2. Extraction and interpretation misclassified the observed footer “Cookies” information link as consent. Clicking it navigated to the cookie-information help page. Its observed kind was `link`, its tag was `a`, and its group was in the footer. No consent acceptance is established. This is an incorrect business action despite using an observed reference.
3. Fresh complete acquisition of that page returned 208 records in four pages, one attempt, no truncation or invalidation. The three sampled pages fit the current request limit.
4. Mapping generation and its repair both used `source.records`; the approved named input was `afterCookiePages`, requiring `source.afterCookiePages.records`. The initial script also used a nonportable inline regex modifier; repair removed that modifier but retained the incorrect root path. Both attempts failed in the restricted mapping profile. The error is `CONTRACT_UNSATISFIED`, source index 0, attempts 2. No partial output was published and no allowance was increased.
5. The root finalizer closed the Browser. The oracle's subsequent read returned “No active page,” then its defensive close completed. This confirms workflow cleanup independently of the oracle's close.

The oracle retains `workflow_execution_failed` and `workbook_missing`. There was no search submission, product-page navigation or XLSX to inspect. The acquisition fix worked for these two coherent snapshots, but no navigation restart was exercised. See [sanitized events, mapping receipts and error](amazon-execution.json). Full observations remain in encrypted records; this report omits unrelated page contents and URL queries.

| Stage | Logical / physical calls | Input / output tokens | Cost EUR | Latency ms |
| --- | ---: | ---: | ---: | ---: |
| Planning | 6 / 6 | 88,877 / 37,342 | 1.388451 | 480,739 |
| Execution | 4 / 4 | 46,688 / 6,017 | 0.367335 | 108,545 |
| Total | 10 / 10 | 135,565 / 43,359 | 1.755786 | 589,284 |

Planning used two discovery reads, one automatic repair and three explicit review revisions. Execution used three mapping calls, including one repair, plus one interpretation call. Both mapping invocations were cold-cache. Initial mapping: 3 source / 2 sampled / 3 processed items, request telemetry 29,983 bytes. Failed mapping: 4 source / 3 sampled items, request telemetry 52,825 bytes, no successful complete result. Retained inspection estimates are conservative harness estimates and are distinct from measured provider usage above.

Final campaign accounting is **€93.476431 known + €2.606753 retained reservations = €96.083184 / €150**. Both historical unknown reservations remain. This run introduced zero unknown usage. The six-slot cohort reports **0/6**, one failed execution and five not started. Historical 33/33 evidence is unchanged and unrelated to this candidate's live acceptance.

## Deterministic follow-up and reproducibility

[The retained repaired script](mapping-source-replay/retained-script.json) fails against a small synthetic observation with the same named-input structure. A diagnostic-only source-path replacement succeeds and preserves the observed selector. This establishes the wrong path independently of inference and live Browser state. It is not an automatic rewrite, deployed mapping fix or replay of the failed workflow. [Captured output](mapping-source-replay.json).

Run the zero-inference diagnostic from the repository:

```bash
cd docs/evidence/conditional-interfaces-2026-10-07/mapping-source-replay
dotnet run --project replay.csproj -warnaserror
```

The live used the existing benchmark in a clean detached checkout of the frozen commit, rebuilt MCP binaries and passed zero-inference [provider readiness](provider-readiness.json) and [local execution readiness](execution-readiness.json). Invocation template:

```bash
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability plan --case amazon --run NEW_UNUSED_RUN_ID \
  --cohort NEW_FROZEN_COHORT --max-products 10 \
  --campaign schema-portability-20261002 --workspace "$GNOU_GO_WORKSPACE"
```

The same existing command supports `inspect-run`, `revise --revision-command <file>`, `execute --review-command <file>`, and `report`. Any future paid execution requires authorization, current admission, a new identity and a separately reviewed artifact; these retained files do not authorize another execution. The started run must not be replayed.

No runtime/mapping code, permission, compiler acceptance rule or oracle was changed to address the remaining live findings. PR #117 stays draft. Planning type safety and source origin checks alone do not prove consent semantics or successful business work.
