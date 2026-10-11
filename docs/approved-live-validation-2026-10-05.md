# Approved live execution and the EUR 150 extension

Production and harness remain frozen at `34bfd6040181cf789a5472506821c5a7f5e2e552`. No production, planner, mapping, permission, oracle or inference-limit changes were made in this iteration. The added tests cover page-initiated navigation invalidating both observation cursor formats.

The user approved the retained Amazon revision 6 and subsequently approved the new cohort's Amazon revision 5, each with its exact artifact hash and all accepted requirement IDs. The campaign extension from EUR 100 to **EUR 150** was recorded through the existing encrypted operator command after the first execution. Historical manifests and unknown reservations were retained.

## Execution results

| Run | Planning | Execution and independent oracle |
| --- | --- | --- |
| `focusedlive20261005a-amazon-1` | Historical 5 calls / 1 repair; revision 6 explicitly approved | Failed on the first observation cursor read after an HTTP 202 capture. Browser cleanup passed; no product visit or XLSX. One runtime call, 116 input / 110 output tokens, EUR 0.003463, 40,385 ms. |
| `approvedlive20261005a-amazon-1` | 4 calls / 0 repairs / 1 review revision; revision 5 explicitly approved | Read the initial snapshot and all six pages of a fresh snapshot (271 records). The next interpretation request exceeded the unchanged input allowance and was rejected before dispatch. Browser cleanup passed; search submission, product visits and XLSX were not reached. One runtime call, 4,192 input / 178 output tokens, EUR 0.023474, 74,020 ms. |
| `approvedlive20261005a-code-1` | 8 calls / 2 repairs / 3 review revisions; stopped at revision 12 | Invalid cross-scope dependencies and a presence reference remain. Zero-inference replay reproduces rejection. No Copilot session, commands, report or cleanup executed. |

The fresh cohort remains **0/6, incomplete**. Repetitions two and three were not attempted because neither first execution gate passed. The previous small Copilot producer probe and 11/11 mapping matrix remain separate evidence; they are not full code-review or Amazon acceptance. No uncertain invocation was resumed and no GitHub feedback was published.

### Browser cursor failure

The first execution sent exactly the cursor returned by the manifest, with the correct format and no page-limit overrides. The live record does not contain the navigation event needed to establish exactly when the snapshot expired. Page-initiated navigation is a reproducible cause of this rejection, not a reason to weaken it: the new local regressions capture a snapshot, release a controlled page redirect, reject the old cursor and successfully capture the new document. Existing navigation/interaction invalidation remains unchanged. No automatic recovery or stale-snapshot reuse was added.

An initial harness launch also rejected a `PATH` fingerprint mismatch before approval or inference. The interactive shell omitted two path entries. Restoring the exact recorded environment allowed execution; the frozen manifest was not changed.

### Collection input limit

Inspection through the public encrypted run-store API confirms that the rejected interpretation receives only its declared manifest and six observation pages, containing all 271 records. It does not receive unrelated loop results or duplicate the page collection.

The prompt is 127,565 UTF-8 bytes with a prompt-only estimate of 68,315 tokens. Reconstructing the complete request, including its schema and serialized envelope, gives 167,301 bytes and approximately **97,398 estimated tokens**, above the campaign's **96,000** allowance. This reconstruction omits the durable request identity, so it is diagnostic rather than an exact wire receipt. Estimates are not measured provider usage. The rejected request incurred no provider call; the conservative admission rule and reservations were preserved.

The generated workflow chose whole-collection interpretation for control selection. It did not use the already-supported independent extraction/compaction mode before that interpretation. An explicitly revised, newly approved workflow could use that existing composition; the runtime must not partition a global interpretation automatically. No compiler duplication defect was demonstrated, and no production change was made merely to admit this request.

### Code-review proposal failures

The first proposal compiled in two calls with zero repairs, but saved the report only on the normal path. Review rejected it because verified failure could reach cleanup without report preservation. Further proposals contained duplicate branch-local task identities, missing exports and report selection supplied only with presence booleans instead of actual evidence. Those failures and review commands remain retained.

The final proposal still references `select_preservation_report` from a scope where it is unavailable. The compiler correctly rejects the two dependencies and presence check; the eight-call allowance then stops dispatch. The existing deterministic finalizer tests pass with actual payload preservation on success and verified failure, nested cleanup, and no cleanup after unknown completion. These results do not justify weakening scope checks or resetting the stopped session's allowance.

## Measurements and validation

| Fresh cohort planning | Amazon | Code review |
| --- | ---: | ---: |
| Logical calls / physical attempts | 4 / 4 | 8 / 8 |
| Repairs / review revisions | 0 / 1 | 2 / 3 |
| Discovery reads | 2 | 4 |
| Verified input / output tokens | 49,458 / 13,738 | 173,959 / 30,171 |
| Planning latency | 171,128 ms | 355,192 ms |
| Planning cost | EUR 0.588567 | EUR 1.584189 |

- Full solution with `-m:1 -warnaserror`: **4,515 passed, zero failed, 13 skipped across 33 projects**. The browser UI test passes separately with packaged Playwright, leaving twelve historical skips.
- Browser suite: **50 passed**, including two new page-initiated-navigation regressions.
- Targeted scope/repair/finalizer tests: **66 passed**.
- Real Flow/Browser/Document local execution: **20 passed**, with independent XLSX checks, pagination, consent, per-product visits, failure and denial cases.
- `git diff --check` passes. Production packaging, Native AOT and encrypted-recovery code did not change; the earlier frozen candidate's checks remain historical evidence and were not rerun as new package validation.

Campaign upper bound is **EUR 82.503774 / 150**, leaving **EUR 67.496226**. Known cost is EUR 79.897022; the two historical unknown reservations still total EUR 2.606753. This iteration added fourteen paid calls with complete receipts, 227,725 input / 44,197 output tokens and EUR 2.199692. No additional unknown reservation was introduced. Campaign budget availability must not be confused with the per-request input limit or a session's exhausted planning calls.

## Reproduction and retained evidence

Use the [existing harness commands](../tests/GnOuGo.Agent.Planning.Benchmark/README.md#authorized-schema-portability-live-campaign), the exact environment fingerprint in the [cohort manifest](evidence/approved-live-2026-10-05/cohort.json), and candidate `34bfd604` for read-only inspection. The isolated checkout is `/private/tmp/gnougo-live-34bfd604`. Both Amazon executions have started and must not be replayed. The code session's allowance is exhausted.

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --campaign schema-portability-20261002 --cohort approvedlive20261005a
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability replay-compile --campaign schema-portability-20261002 --run approvedlive20261005a-code-1
Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0 dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -p:SkipModelMetadataGeneration=true
```

[First approved execution](evidence/approved-live-2026-10-05/amazon-approved-execution.json), [fresh approved execution](evidence/approved-live-2026-10-05/amazon-new-execution.json), [stopped code review](evidence/approved-live-2026-10-05/code-stopped.json), [request-size inspection](evidence/approved-live-2026-10-05/request-inspection.json), [validation evidence](evidence/approved-live-2026-10-05/validation.json) and [campaign accounting](evidence/approved-live-2026-10-05/campaign.json) retain sanitized results. Complete requests, responses, observations and receipts remain encrypted in the existing stores. PR #117 remains draft; historical cohorts and the 33/33 benchmark are unchanged.
