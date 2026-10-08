# Pure data-adaptation lifecycle

Starting point: `4c848b34c52c0563e7df16a63ae0176eadef0a27`, PR #117. Mapping owns syntax/helper validation, global program repair, data-specific specialization, source grounding and atomic target validation. Business selection and external actions remain explicit. No limits, permissions, planning architecture or execution oracles change.

## Deterministic reproduction

The retained product extraction contained 55 pages / 1,563 records. A read-only replay used its encrypted observations, original assigned indices and completed script receipts, with **zero inference and zero external actions**. It neither resumed nor modified the saved run.

The instrumented original implementation exhausted **allocated memory**, not statements or time: its error receipt reports over 50 MB of cumulative allocation against the unchanged 50,000,000-byte ceiling. Wrong helper signatures reached item evaluation and were repeatedly mistaken for shape failures. Reparsing each candidate for every item amplified allocations. See [before](retained-replay-before.json).

With preflight classification and one prepared expression per candidate, the same retained sequence uses **26,403,960 allocated bytes**, 2,485 statements and about 108 ms active sandbox time. Invalid programs fail before item execution; no resource exhaustion occurs. See [after](retained-replay-after.json). This is a replay of retained scripts, **not successful complete extraction**: rejected scripts remain invalid and no replacement inference was called. Timing is informational; validation does not depend on a timing threshold.

## Regression coverage

`DynamicMappingLifecycleTests` embeds the sanitized historical script receipts and covers unsupported operations, invalid helper arity/types/patterns, late fabricated values, whole-assignment repair, genuine heterogeneous specialization, exact closed targets, mandatory failing-item packing, cumulative resource diagnostics and durable failure recovery. Existing collection tests cover 53 pages / 1,504 records, unsampled items, every-item correctness, cache, cancellation and immutable restart receipts.

```sh
dotnet test tests/GnOuGo.Flow.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~DynamicMapping'
dotnet test tests/GnOuGo.Flow.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests&DisplayName~pages-compact-decision-complete-flat-lookup'
```

The full Flow suite passes **1,160 tests**, Planning passes **1,118**, and both real local Browser/Document fixtures pass independent visit, XLSX and cleanup assertions. The skill validator passes. Initial test development caught a quoted-pattern diagnostic compatibility assertion and two test-side resource-counter setup issues; these were corrected without weakening production checks or raising limits.

The complete frozen solution passes **4,853 tests, 13 skipped, zero failures**, with `-warnaserror`. Its first run retained one environmental failure: a pre-existing host service owns port 4317. The same binaries passed after selecting an ephemeral gRPC endpoint through test-process configuration; no service or production defaults changed. CI separately exposed duplicate ephemeral-port selection in a telemetry fixture. Test-only commit `875f2bbe` holds both reservations until distinct ports are selected; all 13 affected host tests pass. Existing assertions and production port validation remain unchanged. See [validation](validation.json).

Release Core/Planning packages, planning Native AOT execution and published encrypted recovery pass. The CLI publish retains its two established EF Tasks experimental-feature notices, with no new suppression. [Commands and results](release-validation.json) record these separately from the warning-as-error solution build.

New private adaptation assignments identify the failure-classification version. Historical pending assignments require explicit revision; completed receipts and unknown invocations are preserved. All runtime counters share existing ceilings across cache recovery, repair and specialization. No partial result or new cache artifact is published before the complete target passes.

## One fresh live: execution failed outside mapping

Frozen production/harness: `c12fee3c34504c9865da7727bb7132fb033781f0`. Cohort `mappinglifecycle20261008a`, run `mappinglifecycle20261008a-amazon-1`, ten-product maximum. Configured pricing/currency and local execution readiness passed before dispatch. The unchanged [manifest](manifest.json), [binary/oracle hashes](frozen-binaries.json), [readiness](provider-readiness.json), [requirements](requirements.json), [responses](planning-responses.json), [revision diagnostics](review-revisions.json), [review](amazon-review.md), [approval](approval.json) and exact [YAML](amazon-r13.yaml) are retained.

Planning reached review at revision 13, artifact `5704070b69bf42680d2bfeb8355992cd8cb2e600c33c40751fd993ac4746833d`, after **eight calls, two automatic repairs, two discovery reads and four explicit revisions**. Earlier proposals included a missing task, malformed lookup IDs, array field access and incompatible nullability. No planning budgets were reset. The one-call target remains unmet.

Both executed mappings passed every original item with one model call each. Neither needed a program repair or specialization; their verified [receipts](runtime-mapping-receipts.json) and [resource counters](mapping-telemetry.json) remain separate from the failed E2E oracle.

| Measurement | Initial observation mapping | Search-control mapping |
| --- | ---: | ---: |
| Source / validated items | 137 / 137 | 259 / 259 |
| Complete generation examples | 6 | 7 |
| Unsampled items still executed | 131 | 252 |
| Serialized request bytes | 5,726 | 6,119 |
| Model calls / repairs / specializations | 1 / 0 / 0 | 1 / 0 / 0 |
| Cumulative statements | 411 | 777 |
| Active sandbox milliseconds | 7.166 | 2.1482 |
| Allocated bytes | 1,515,256 | 2,889,224 |
| Materialized bytes | 424,935 | 854,267 |
| Output bytes | 12,403 | 26,343 |
| Maximum observed nesting | 3 | 3 |

The limits remain 10,000 statements, 5,000 active milliseconds, 50,000,000 bytes and nesting 64, or stricter host limits. Resource timings are observations, not performance assertions.

Browser acquired a complete search snapshot of **58 pages / 1,602 records**, with no capture/manifest truncation or acquisition restart. Before product extraction, the generated pure record-indexing `foreach` reached the unchanged **1,000-iteration host ceiling**. Its body creates `{id:index, record:item}` through scoped calls; this index-dependent composition was not fused by the existing compiler. `LOOP_LIMIT` is a separate workflow limit, not mapping resource exhaustion. No product page was visited and no workbook was written. The existing oracle failed with `workflow_execution_failed` and `workbook_missing`.

**Review error retained:** the original review describes the declared 10,000-item bound as a host bound. That was incorrect: the effective host iteration limit is 1,000. The saved approval, YAML and review are preserved, and this correction is recorded separately. A later correction must preserve original indices and every observation while avoiding per-record workflow execution for a pure adaptation; increasing the host ceiling or dropping records is not an acceptable fix.

The journal has a durable terminal failure and completed Browser cleanup: **2,883 normal steps, one finalization step, 2,884 invocations**. No uncertain invocation was resumed and no further paid run was attempted. A separate read-only full-journal inspector exhausted memory while expanding the large logical journal. The owner completed normally. Final [checkpoint evidence](journal-checkpoint.json) was collected using owner- and hash-validated small records without expanding historical payloads; the two mapping receipts required only 3,485 encoded bytes. The inspection limitation remains open and is not mislabeled as a sandbox failure.

| Accounting | Planning | Execution |
| --- | ---: | ---: |
| Logical calls / physical attempts | 8 / 8 | 4 / 4 |
| Verified input tokens | 122,588 | 15,320 |
| Verified output tokens | 51,836 | 693 |
| Cost, EUR | 1.923880 | 0.086423 |
| Latency, milliseconds | 575,616 | 343,005 |

Execution contains two mapping calls and two explicit interpretations. Run cost is **EUR 2.010303**; the campaign upper bound is **EUR 109.213000 / 150**, including **EUR 5.203327** of unchanged unknown reservations. Total measured latency is 918,621 ms. [Accounting](accounting.json) and the unchanged [six-slot report](cohort-report.json) retain **0/6 passing**, one failed and five unexecuted slots. No code-review evaluation or cohort expansion occurred. Historical 33/33 evidence is unchanged; PR #117 remains draft.

Safe read-only campaign reporting (do not replay this execution):

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability report --workspace "$GN_OUGO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort mappinglifecycle20261008a
```

Do not use full `inspect-run` reconstruction for this retained run until its materialization cost is addressed. Encrypted source observations and durable receipts remain available in the workspace; published evidence contains summaries, approved plans and scripts, not raw page contents or credentials.
