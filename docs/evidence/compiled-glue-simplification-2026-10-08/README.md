# Runtime guards and compact checked glue

Base: `a49243bd`. The historical `emptyacquire20261008a-amazon-1` execution and
approval remain unchanged. Its second interpretation was rejected before dispatch:
108,576 estimated input tokens exceeded the unchanged 96,000 allowance. It produced
no XLSX. This correction does not replay it.

## Changes

- Removed the conditional implication engine and automatic guard strengthening.
  Explicit `requires` remains a local runtime assertion, immutable during automatic
  repair. Retired guard repairs require explicit revision; original receipts and
  schemas restore accounting without applying the obsolete proposal.
- Fresh `compact-bindings-v4` artifacts fuse consecutive pure sets, selections,
  assemblies and assertions. A closed compiler-owned JavaScript sequence validates
  each intermediate before evaluating the next one. Only later-consumed values
  are published; errors retain the original compiler node identity. Existing
  expression limits apply to the entire sequence. Learned mappings are unchanged.
- Effects, inference, branches, scopes, retries, error handlers, presence checks
  and values required for failure preservation remain boundaries.
- Explicit extraction contracts and selected bindings define consumer views.
  Full observations remain separately available; global interpretation receives
  only its selected view and explicit shared context. No field-relevance heuristic,
  truncation, sampling of global decisions or increased limit was added.

## Same-plan comparison

The [read-only comparison](compilation-comparison.json) recompiles the identical
retained TaskPlan and contracts, independently of extraction composition changes.

| Metric | v3 | v4 |
| --- | ---: | ---: |
| Steps | 81 | 51 |
| Sets | 55 | 25 |
| Workflows | 5 | 5 |
| YAML bytes | 81,800 | 79,506 |
| YAML lines | 2,351 | 1,666 |
| Adjacent set pairs | 32 | 2 |
| Eligible unfused pairs | 30 | 0 |

The two retained pairs are within conditional results consumed through physical
child identities. Fusing them would change that observed result contract. The v3
output matches stored YAML byte-for-byte. These compilation measurements do not
claim successful business execution or solve an oversized input composition by
themselves.

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability replay-compile --compare-bindings \
  --workspace "$GNOU_GO_WORKSPACE" --campaign schema-portability-20261002 \
  --run emptyacquire20261008a-amazon-1
```

## Deterministic validation

Focused tests exercise ordered intermediate checks, atomic failure, shared consumers,
exact JSON copying, nullable values, presence/failure preservation, unchanged old
lowering and expression resource exhaustion. Runtime-authority tests cover absent
resources, incompatible actions and denied authorization before affected operations.

The v4 consumer fixtures process complete collections at 6-page/263-record,
52-page/1,474-record and 53-page/1,504-record scales. Relevant observations outside
generation examples survive; raw noise stays outside the global request. The same
raw composition remains rejected before inference. Native AOT exercises 1,602
original indices and records without per-record workflow invocations, plus encrypted
recovery and adaptive receipt replay.

The [deterministic measurements](deterministic-compaction.json) retain the complete
serialized global request sizes: 846–2,113 bytes, with every source page processed.
These small necessary views are fixture-specific, not a promise for arbitrary data.
The raw version still rejects before dispatch. The minimal four-set equivalence
fixture compiles to one set (1,881 → 1,864 YAML characters), retaining intermediate
failures and their original order.

Validation: 1,165 planner tests, 1,164 Flow tests, 99 integration tests and
17 persistence tests passed with `-warnaserror`. Subsequent focused tests cover the
final shared-consumer assertion and v4 fixture updates (9 fusion cases, 25
fusion/compaction cases). Core, Planning and Integrations Release packages passed.
Planning Native AOT published and executed, including encrypted recovery. The skill
validator passed. Full solution and local-host final counts are recorded below.

Two indexing fixture assertions were updated to locate the compiled operation across
child workflows; both corrected cases passed through real Browser/Document execution
and independent XLSX inspection. These were test identity assumptions, not weakened
execution oracles. Production code has a net reduction of 46 lines against the base.
Deterministic adapters remain separate from live-provider execution evidence.

## Live gate

[Configured readiness](provider-readiness.json) confirms pricing, currency and exact
deployment metadata with zero model calls. The unchanged campaign upper bound is
EUR 113.094955 / 150, including EUR 5.203327 of conservative unknown reservations.
Only one fresh Amazon evaluation, at most ten products, is authorized. It needs its
own revision/hash-bound requirement review before execution. Historical runs and
benchmarks remain untouched; PR #117 stays draft.

## Fresh planning

Frozen candidate: `8fc8fd0c`; cohort `gluev420261008b`, run
`gluev420261008b-amazon-1`. [Execution readiness](execution-readiness.json) passed
with zero model calls. The first proposal reached review in two calls, zero repairs,
two discovery reads and 85,595 ms: 19,248 input / 6,010 output tokens, EUR 0.245399.
Review rejected its remaining whole-snapshot interpretation inputs before execution.
The [original proposal](amazon-r2-plan.json), [YAML](amazon-r2.yaml) and
[explicit revision feedback](amazon-r2-review.json) are retained. No artifact was
approved or executed at revision 2; cumulative planning limits remain unchanged.

The first explicit revision still used one learned invocation per page and broad
records; [revision 4](amazon-r4-review.json) was retained and rejected before
execution. The next proposal used `each` and lookup but invalid object-field
access on arrays; its two automatic repairs did not resolve immutable guards
([revision 9](amazon-r9-review.json)). An explicit revision using existing bounded
loops reached review; [revision 11](amazon-r11-review.json) still described a
nonempty selection in prose rather than its actual contract. The final correction
requested one required string ID and a deterministic singleton selected-ID array.
None of these review revisions changed accepted requirements or reset counters.

The [final planning result](amazon-planning-result.json) stopped at revision 13:
**`MODEL_DISPATCH_UNVERIFIABLE`**, eight logical calls/physical attempts, two repairs,
two discovery reads and four explicit review revisions. Seven completions have
measured usage: **105,573 input / 39,920 output tokens**, **EUR 1.531161**. The eighth
has no retained completion; missing usage is unknown, not zero. Active planning
latency was **655.297 seconds**. No further dispatch or reconciliation was attempted.

The ledger's recorded upper bound is **EUR 114.626116 / 150**, including the retained
EUR 5.203327 unknown reservations. This is the recorded ledger value, not a claim
that the newly unverified request has confirmed usage. No reservation was released.
No artifact approval was submitted, no workflow execution started, no product
visits or workbook were produced, and no execution oracle passed. The paid fresh
attempt therefore remains **incomplete at planning**, not successful E2E evidence.
Historical invocations and the 33/33 benchmark are unchanged. No code-review run
or cohort expansion occurred.

## Broader validation limitation

The full solution command exited 1 after the stalled Agent.Server process was
stopped. Its last unfinished case was the local compact/flat product fixture;
that case had passed in the focused suite. No managed stack was obtained from the
stalled process, so its exact cause is not established by this run. The earlier
baseline's telemetry-queue hang remains separate evidence. No assertion, timeout,
concurrency rule in production or execution oracle was changed.

All 33 projects were reached; [partial solution accounting](solution-partial.json)
records **4,625 passed, 13 skipped, zero failed assertions**, including only the
398 completed host cases. These are partial counts, not a green full-suite claim.
The remaining projects, including the 1,165-test planner suite, completed. The
unchanged complete host suite was then checked in a fresh serial runner.

PR #117 remains draft. Deterministic compilation improvements do not establish
live execution success; the stopped planning request must not be replayed.

A [read-only stack sample](host-reconstruction-sample.json) from the active serial
run identifies encrypted journal reconstruction and per-record audit writes after
workflow execution. This is a separate performance boundary, not proof of the
interrupted process's exact cause. Persistence changes remain outside this patch.

## Reproducible inspection and focused validation

The [frozen cohort manifest and six-slot report](cohort-report.json) retain source,
harness, configuration and prompt hashes. Only Amazon repetition one was attempted;
all other slots remain unstarted. These commands inspect retained evidence and do
not dispatch or resume the uncertain request:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability inspect-run --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --run gluev420261008b-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability report --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort gluev420261008b

dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror \
  --filter 'FullyQualifiedName~FusedBindingCompilationTests|FullyQualifiedName~ConditionalEntryGuardTests|FullyQualifiedName~CompactObservationTests|FullyQualifiedName~ConsumerInputContractTests'
env Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0 \
  dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests|FullyQualifiedName~BrowserSnapshotReceiptTests|FullyQualifiedName~PlanningModelRecoveryTests'
```

That serial runner subsequently reproduced the [synchronous telemetry wait](host-serial-interruption.json)
in `CollectorTracePersistence.Persist` from `Activity.Stop`, the same boundary
recorded in the earlier baseline. It was stopped without changing telemetry or
assertions. The other host classes were run separately; the 33 local product
cases use the focused run's 31 passing cases plus the two corrected indexing
assertions rerun successfully on the frozen candidate. The initial two assertion
failures remain part of the validation history. An aggregate isolated pass does
not establish that the uninterrupted suite issue is fixed.

Final [isolated coverage accounting](host-coverage.json): **676 passed and one
existing skip** in the other host classes, plus [33 passed local product cases](local-fixture-coverage.json).
All **710 host cases** are accounted for. Across 33 projects this totals **4,936
passed, 13 existing skips and no remaining failed assertions**. This aggregate
coverage must not be reported as an uninterrupted solution pass. Frontend
production builds completed; no frontend source changed.

At evidence publication, [frozen-candidate CI](ci-status.json) reports 8 in_progress, 3 skipped, 21 success. Pending jobs are not counted as passing. The PR remains draft.
