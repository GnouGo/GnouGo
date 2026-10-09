# Approved execution: failed before product visits

Run `repairvocabulary20261009a-amazon-1` executed once on frozen candidate `99cc3dcaa56755be2de7de7cafb6ffd32b7a5e11`, after explicit approval of revision 11, artifact `a9fc1508a7c623cfb3140ed846c7755e2538c537921e931c18df0a0d32b5d058`, and requirements `success`, `blocked`, `cleanup`. The exact YAML, original review, accepted requirements and frozen checkout remain unchanged. Execution authorization and configured pricing/currency readiness are retained alongside these results.

## Planning, execution and independent oracles

| Stage | Observed result |
| --- | --- |
| Planning | Valid revision 11; seven logical calls, eight physical attempts, one repair, four explicit revisions, two discovery reads. EUR 0.802946. |
| Browser acquisition | Successful complete acquisition; one attempt, 50 matching pages, 1,530 records, no capture/manifest truncation or invalidation. |
| Independent extraction | 50/50 pages processed and validated; one generation, no specialization or program repair, two complete prompt examples, 48 omitted examples still executed. |
| Compact interpretation | All 273 extracted candidates and 17 notice records supplied. Input candidate fields are exactly `candidateId` and `nameText`; no raw snapshot or action URL. Prompt: 33,971 UTF-8 bytes, estimated 21,560 input tokens through existing request accounting. |
| Decision | Ten selected IDs all occur in offered candidates and original records, but `blocked: true`, `blockage: "Cookies et choix publicitaires"`. |
| Execution | `INPUT_VALIDATION` at `main/n_ed27e42a862310f9`, compiled value `v1`: `not(blocked)` is false, while its assertion requires true. |
| Business result | Zero product visits, zero Document calls, no XLSX. Independent oracle findings: `workflow_execution_failed`, `workbook_missing`. |
| Cleanup | One workflow `browser_close` succeeded. The oracle independently verified `No active page`; its subsequent best-effort close also succeeded. |
| Durability | Encrypted run is terminal `failed`, journal revision 58, finalization completed; 13 normal steps and one finalizer. All admitted inference has a completed receipt. No reconciliation or replay. |

The Browser reports the final document's generation as 4, with its matching URL/title but unknown generation-specific method/status. A separate last response was a GET with HTTP 200. Do not promote that last-response status into authoritative status for generation 4.

## Exact failure and minimal next correction

The generated extraction copied observed consent text into `blockingEvidence`. The subsequent interpretation treated the banner title as a blocking condition while also selecting ten valid candidate IDs. The runtime honored the explicit assertion and stopped before attempting navigation. The captured source proves that a banner and product records were present; it does **not** prove that permitted product navigation was prevented.

The immediate cause is an unsupported blockage classification in the generated composition; whether navigation would really have been blocked remains unverified. It is not a context overflow, mapping resource exhaustion, Browser cursor failure, missing receipt or failed workbook write. Mapping completed within the unchanged cumulative limits: 3,500/10,000 statements, 32.4/5,000 ms, 14,365,736 allocated bytes and depth 4/64. The declared extraction contract passed; that establishes shape and source origin, not the truth of a later business classification.

The next minimal generic correction should separate descriptive notices from verified inability to perform an authorized action. A notice heading or an LLM boolean alone should not establish operational failure. Preserve explicit assertions, permission denials, producer action errors and unknown-completion protection. Add a regression distinguishing an informational notice on a usable page from an actually blocked or denied action, then revise and review the composition. No automatic consent, scenario-specific bypass or extra architecture is proposed. This turn changed no production code or saved artifact and made no retry.

## Accounting and current CI

Execution lasted 105,201 ms and made **three logical calls / three physical attempts**, using 33,702 input and 5,266 output tokens for **EUR 0.289724**. Runtime prompts were estimated at 4,204 (URL construction), 28,505 (mapping generation) and 21,560 (candidate decision) tokens, all below the unchanged 96,000 allowance. Mapping generation request metadata reports 49,604 bytes; this differs from the decision prompt-only byte measurement above.

Campaign `schema-portability-20261002` upper bound is **EUR 120.109127 / 150**: EUR 113.607512 known cost plus unchanged EUR 6.501615 reserved cost. All five historical unknown physical attempts remain retained. This execution introduces no unknown usage or reservation. Full observations, provider responses and receipts remain in encrypted storage; committed evidence contains only the selected diagnostic data.

Local full-solution validation passed 4,998 tests with 13 skips. The current remote CI has two failed jobs, separately from this live: six local product fixture variants exceed the shared two-minute inference allowance during planning, before any mapping dispatch. On `validate`, the two new repair variants spend 128,337.7 and 125,760.7 ms planning; mapping call count is zero. Their runtime execution lasts about 2.4–2.8 seconds before admission rejects it. No assertion or time limit was changed. Both jobs finish at 721 passed / six failed / one skipped. The other 27 checks succeeded and four were skipped. PR #117 remains draft.

## Reproducible inspection

Use the frozen benchmark binary and existing encrypted workspace. These commands only read the retained run/accounting; do not run `execute` again:

```sh
tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark --schema-portability inspect-run --workspace /Users/a115vc/Desktop/GnOuGo --campaign schema-portability-20261002 --run repairvocabulary20261009a-amazon-1
tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark --schema-portability inspect --workspace /Users/a115vc/Desktop/GnOuGo --campaign schema-portability-20261002
```

The decision response was read through `EncryptedWorkflowRunStore.ReadAsync("benchmark", "repairvocabulary20261009a-amazon-1")`, invocation `/workflow/main/step/n_ff7ed9a0e3dd66d7`, without executing a step or calling a provider. The independent evidence check verified the snapshot identity/counts, complete candidate input, selected-ID membership, absent workbook and unchanged saved YAML. See [structured execution evidence](live-execution-result.json) and [issued mapping response](live-mapping-response.json).
