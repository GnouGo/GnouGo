# Fresh Amazon live: planning accepted, initial acquisition timed out

**Execution oracle: failed.** The new run `genericbrowser20261009a-amazon-1` was generated, explicitly reviewed/approved and executed once. Browser could not obtain a complete initial search observation within the existing shared 30-second deadline. There were **zero product visits, zero mapping executions, zero Document writes and no XLSX**. Browser cleanup succeeded and was independently verified. No consent action or instruction was generated.

Candidate: `1d94689655d08188fbd06c9b0696ddf681edb170`; campaign: `schema-portability-20261002`; ceiling: EUR 150; cohort: `genericbrowser20261009a`; maximum: ten products. No production code, architecture, oracle, permission or limit was changed for this evaluation. No historical request or uncertain invocation was replayed. PR #117 remains draft.

## Readiness and frozen inputs

The candidate was built in a clean detached checkout using `dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror -p:SkipClientBuild=true -p:SkipModelMetadataGeneration=true`: zero warnings/errors. [Frozen source/harness/oracle and binary hashes](freeze.json) still matched before artifact approval. The actual prompt comes from the updated harness and contains no injected consent guidance.

[Provider readiness](provider-readiness.jsonl) passed for the exact configured deployment, including pricing and the cached ECB quote under its existing freshness policy. Planning/mapping admission remains 96,000 input tokens and 32,768 output tokens. Conservative transport accounting reservations are separate from actual token measurements.

[Local readiness](readiness.json) used actual MCP transport: Browser read a disposable local page, Document wrote an XLSX independently inspected through OpenXML, and Browser cleanup released its active page. Zero inference calls. The [initial cohort inspection](before.json) confirmed unused identities. Only Amazon repetition one ran; all other slots remain unexecuted.

The source correction had passed 136 Browser and 57 affected host tests, including local navigation, independent workbook checks and informational banners receiving zero interactions. See [the correction evidence](../generic-browser-guidance-2026-10-09/README.md). These deterministic results are separate from this failed live. No additional solution/package/AOT campaign was run for this evidence-only turn. [CI snapshot](ci.json) records the frozen source checks separately; unfinished checks are not claimed passed.

## Planning and review

1. [Revision 2](plan-r2.json) stopped after two calls. Two `requires` used strings rather than booleans, and `flatten` received an array of row objects. Review also found an initial URL missing the query and whole snapshots sent to interpretation. [Diagnostics](diagnostics-r2.json), [result](planning-r2.json) and the [explicit revision command](revision-command.json) are retained.
2. [Revision 4](plan-r4.json) compiled. Review withheld approval because extraction required an unsupported `resultIndex` and reconstructed action records. The [second explicit revision](revision2-command.json) kept observed reference identities and reconnected selections to deterministic projections of actual original Browser records. It also kept incomplete acquisition as failure.
3. [Revision 6](plan-r6.json) reached final review with zero diagnostics. Requirements, public contracts and external operation identities/order remained unchanged. The user explicitly acknowledged all five requirements and approved the exact hash; [review](amazon-review.md), [approval command](approval.json), [YAML](amazon-r6.yaml) and [checks](review-checks.json) are retained.

The final composition uses bounded page extraction, compact ID selection, validation against offered candidates, deterministic lookup of original records, individual product navigation, compact fact extraction/reconciliation, bounded TSV formatting and actual Document writing. Typed rows remain non-null with nullable missing fields. Full snapshots never enter global interpretation. Four explicit interpretation definitions and two adaptive mapping definitions remain; runtime/campaign ceilings still apply.

The YAML contains 61 step definitions, including 39 sets, and 95,169 bytes / 2,055 lines. This is a composition measurement, not a compiler-improvement claim.

Planning: **4 logical calls / 7 physical attempts**, **0 automatic repairs**, **2 explicit revisions**, **2 discovery reads**, **58,124 verified input / 15,974 output tokens**, **1,140,375 ms**, **EUR 0.6831484603780282190078977726**. Three HTTP 500 responses delayed the second revision; its fourth physical attempt returned 200. [Transport metadata](planning-http-metadata.json) retains the status sequence without private prompts or credentials. All issued schemas and original encrypted records remain retained.

## Actual execution and independent oracle

[Execution evidence](execution-result.json) records the failure at `main/n_ee40e5cae35eab47`, Browser `browser_get_content` with `format=observation_complete`, `waitUntil=domcontentloaded`, `timeoutMs=30000` and `maxRecords=200`.

- The supplied URL correctly contained the query: `https://www.amazon.fr/s?k=chaussure%20geox%20homme%2045`.
- The first acquisition recorded an empty document and a successful GET response with HTTP 202. Browser's existing recovery discarded the snapshot and requested one reload.
- Acquisition metadata reports two attempts and a later HTTP 200 response, but the current document reached generation 8 without a publishable complete snapshot before the same deadline. Its generation-specific final status and title remain unknown. The error is **`TIMEOUT: Complete snapshot acquisition exceeded its shared timeout.`** A previous HTTP 200 does not establish current-document completeness.
- The workflow stopped before mapping, selection, product visits or writing. No data was invented and no artifact was reported successful. This evidence does not establish CAPTCHA, consent blockage or the underlying reason for navigation instability.
- The workflow's Browser finalizer succeeded. The independent oracle then received `No active page` and confirmed the workbook was absent. Findings: **`workflow_execution_failed`**, **`workbook_missing`**.

Execution: **1 logical call / 1 physical attempt**, **118 verified input / 145 output tokens**, **56,451 ms**, **EUR 0.0043837075161948708847280149**. Mapping calls/cache activity: zero, since extraction was never reached.

[Encrypted-journal readback](journal-recovery-check.json) confirms `failed`, revision 22, four normal steps and one finalization step, finalization completed, and known completion for every invocation including the Browser failure. This was a durable known failure, not unknown external completion. The run must not be replayed.

The next investigation should reproduce an empty initial GET followed by document-generation changes inside Browser acquisition, under the existing deadline. This live alone does not justify a timeout increase, planner rule or new runtime mechanism.

## Accounting and delivery

Total verified incremental cost: **EUR 0.6875321678942230898926257875**. [Final ledger](ledger-after.jsonl): known EUR 114.29504420096402797197618166 plus unchanged reserved EUR 6.5016147947905255187922165013 = **EUR 120.79665899575455349076839816 / 150**. Remaining authorized headroom: about EUR 29.20.

All five historical unknown physical attempts remain reserved. The only unreceipted historical logical requests retain their existing inconclusive closures; no new unclosed uncertain request remains. Nothing was reclassified, reset or released. No second execution, code-review run or cohort expansion occurred. The full six-slot acceptance gate remains unmet.

Read-only inspection from the frozen checkout:

```sh
tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark \
  --schema-portability inspect-run --workspace /Users/a115vc/Desktop/GnOuGo \
  --campaign schema-portability-20261002 --run genericbrowser20261009a-amazon-1
```

Original requests, responses, observations, approvals and receipts remain in the encrypted stores. Committed evidence contains reviewed proposals, public-site navigation diagnostics and bounded metadata, not credentials or private provider prompts.
