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

Validation commands and final results are recorded below when collection completes.
Deterministic adapters are separate from live-provider execution evidence.

## Live gate

[Configured readiness](provider-readiness.json) confirms pricing, currency and exact
deployment metadata with zero model calls. The unchanged campaign upper bound is
EUR 113.094955 / 150, including EUR 5.203327 of conservative unknown reservations.
Only one fresh Amazon evaluation, at most ten products, is authorized. It needs its
own revision/hash-bound requirement review before execution. Historical runs and
benchmarks remain untouched; PR #117 stays draft.
