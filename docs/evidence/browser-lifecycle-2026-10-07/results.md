# Browser lifecycle correction

The retained `follow` failure is a lost lifecycle notification, not an incompatible target or failed navigation. Two local probes reproduced the 30-second timeout on baseline `e9934813`. In the second, `/help` was reached, DOMContentLoaded and load both fired, and the current document was complete. Another readiness wait returned immediately without another click. [Sanitized reproduction](reproduction.json) retains both failures and the original CI job.

The existing navigation observation now subscribes before interaction, retains lifecycle events and checks the current document after subscribing. Version checks discard readiness read across navigation. One unchanged deadline bounds all rechecks. The implementation does not repeat clicks, change target identity or relax follow/activate compatibility. Explicit networkidle still uses Playwright's network-quiescence condition. Public MCP contracts, planner guidance, mapping and historical workflows are unchanged.

The Browser suite passes **110 tests**, including 22 added cases for lifecycle ordering, delayed readiness, reloads, redirects, same-document and non-navigation actions, cancellation, closure, disposal, genuine timeout, network idle and all affected action entrypoints. The original external probe passes 80/80 after the correction; this supports, rather than replaces, controlled regression tests. Host tests also cover actual reference-based follow, encrypted receipts and restart without another interaction.

The self-contained osx-arm64 Browser publish and four real MCP/encrypted receipt checks pass. The full solution passed **4,750 tests, zero failures and 12 existing skips across 33 suites**, including 702 host tests. That run includes all production changes; the subsequent test-only closure-ordering follow-up passed the complete 110-test Browser suite. The first Linux CI attempt passed the original follow test but caught ambiguous closure ordering in a new test. The follow-up controls closure before versus during the read, asserts each exact exception type and structured error, and retains the first failure in [CI evidence](ci-first-attempt.json). No production behavior, timeout or safety assertion changed.

Frozen candidate **603e7926154d3f55b8f812497a03bffa1bce5993** passed **29 non-skipped CI checks**, with four intentional skips, before paid dispatch. This includes supported package, AOT and encrypted-recovery checks. [Exact check results](ci-frozen-candidate.json) retain job links; [validation details](validation.json) distinguish the full run and follow-up. Local full-solution validation skipped optional metadata regeneration and frontend rebuilding; CI covered its configured build jobs. Browser itself does not support Native AOT.

[Configured readiness](provider-readiness.json) passed without inference. The existing ECB quote is fresh under the unchanged policy. [Campaign accounting](accounting-before.json) retains EUR 96.942719 / 150, including EUR 2.606753 reserved for two historical unknown completions. No reservation was released or uncertain invocation resumed.

## One fresh Amazon execution: failed, cleanup verified

The disposable zero-inference readiness probe verified actual Browser navigation/closure and Document XLSX writing with an independent read. Cohort `lifecycle20261007a` then froze source, harness, configuration, prompt, oracle and environment fingerprints in [the manifest](live-report.json). Only `lifecycle20261007a-amazon-1` was attempted, with ten products maximum. The other five report slots remain unexecuted; there was no code-review run or cohort expansion.

Initial generation reached review in two logical calls with zero repairs. Review rejected raw-snapshot interpretation and extraction targets that requested synthetic counts/status labels. One explicit revision used existing independent page extraction and compact interpretation, preserving all requirements, operations and cleanup. No production planner or mapping change was made. The [concrete review](amazon-review.md) explicitly acknowledges six requirements against revision **4**, artifact **8213fa3b5db1d54acddc78d052d901b88bde08de0ece32523cf41b46df3ebfeb**, applying the user's authorization for this next validation. Its [approval command](amazon-approval.json), both TaskPlans, feedback and [executed YAML](amazon-r4.yaml) are retained; no older approval was reused.

| Stage | Result | Logical calls / physical attempts | Verified input / output tokens | Cost EUR | Latency |
| --- | --- | --- | --- | --- | --- |
| Planning, including one explicit revision | Review passed; two discovery reads, zero automatic repairs | 3 / 4 | 36,732 / 12,421 | 0.493646 | 469.179 s |
| Execution | Failed at first independent extraction; cleanup passed | 1 / 1 | 122 / 135 | 0.004135 | 20.189 s |
| Total | Execution oracle failed | 4 / 5 | 36,854 / 12,556 | 0.497782 | 489.368 s |

Browser successfully acquired **1,504 records in 53 coherent, complete pages** from the search page. The first mapping stopped with **`CONTRACT_UNSATISFIED`: a required complete mapping example exceeds the safe request allowance**, at source index 2. The retained pages total approximately **1.10 MB** under the explicitly labelled compact Python UTF-8 measurement; that is not exact .NET request size or billed tokens. Request packing applies the existing conservative whole-request allowance and failed while adding a required example. Its message does not establish that the individual page alone exceeded the allowance. The exact rejected request size/sample count was not emitted and remains unknown.

No mapping provider request was dispatched, no mapping repair ran, and no cache artifact was published. The mapping telemetry's local `model_attempts: 1` counter is not a provider call: the only execution inference was URL construction. Consent clicking, product visits and workbook writing were never reached, so **this live did not exercise the corrected post-click lifecycle path**. It neither proves that path failed nor establishes Amazon success.

The workflow's `browser_close` succeeded; the independent oracle then received “No active page.” Its defensive final close is recorded separately. No `RUN_NEEDS_RECONCILIATION` occurred and no new unknown reservation was created. The unchanged oracle rejected `workflow_execution_failed` and `workbook_missing`. [Sanitized execution evidence](live-execution.json) retains error, page measurements, mapping counters and cleanup ordering; complete responses, observations and receipts remain encrypted.

Final campaign upper bound: **EUR 97.440501 / 150**, comprising EUR 94.833748 known cost and the unchanged EUR 2.606753 historical unknown reservation. This failed execution was not replayed. The remaining example-packing limitation is outside this Browser lifecycle correction; neither mapping nor its limits were changed. PR #117 remains draft, with **0/1 attempted live oracles passing and five unexecuted slots** in this frozen cohort. Historical cohorts and the historical 33/33 benchmark are unchanged.

Reproduce the focused gate with:

```sh
dotnet test tests/GnOuGo.Browser.Mcp.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  -p:SkipClientBuild=true -p:SkipModelMetadataGeneration=true \
  --filter 'FullyQualifiedName~BrowserSnapshotReceiptTests|FullyQualifiedName~BenchmarkAdmissionTests'
```

The existing live harness commands used `--schema-portability plan`, then `revise --revision-command amazon-revision-r2.json`, then `execute --review-command amazon-approval.json`, with campaign `schema-portability-20261002`, cohort `lifecycle20261007a`, case `amazon`, run `lifecycle20261007a-amazon-1` and `--max-products 10`. The workspace is the configured encrypted host workspace, outside the checkout. **Do not replay this execution identity.** Read-only `report --cohort lifecycle20261007a` and `inspect-run --run lifecycle20261007a-amazon-1` preserve it; raw inspection contains private evidence and must not be published. Any later paid execution needs a fresh identity and authorization.
