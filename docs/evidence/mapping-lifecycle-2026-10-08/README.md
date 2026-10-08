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

The full Flow suite passes **1,160 tests**. The skill validator passes. Initial test development caught a quoted-pattern diagnostic compatibility assertion and two test-side resource-counter setup issues; these were corrected without weakening production checks or raising limits. Broader validation and the fresh live result will be recorded separately.

New private adaptation assignments identify the failure-classification version. Historical pending assignments require explicit revision; completed receipts and unknown invocations are preserved. All runtime counters share existing ceilings across cache recovery, repair and specialization. No partial result or new cache artifact is published before the complete target passes.
