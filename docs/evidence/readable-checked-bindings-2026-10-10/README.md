# Opaque identities and readable checked bindings

Production candidate `9f458e1a`, validation follow-up `ba350e26`, from `82255b1b`. Identity guidance is separate
from compiler/runtime changes (`136492c5`, `a29547c5`). No paid inference, MCP
live evaluation, campaign changes or historical workflow resumption occurred.
PR #117 remains draft. The retained upper bound is €127.994835 / €150, including
unknown reservations; this offline work does not reconcile or release them.

## Identity correction and exact offline replay

Generation/repair guidance requires opaque IDs and action references to be
copied unchanged (`key: record.reference`). Ordinary descriptive text extraction
remains supported. Existing strict lookup, rather than a field-name heuristic,
rejects IDs that cannot reconnect to the offered/original records.

[Replay measurements](identity-replay.json) use the saved four pages / 153
records and original script from `nullfacts20261010a-amazon-1`. The source and
receipt were read through `EncryptedWorkflowRunStore.ReadAsync`; only pure
in-memory mapping and deterministic lookup executed. The original shortened-ID
mapping still fails original-record lookup. Changing only its identity expression
to `r.reference` reconnects exactly. All observations remain unchanged. Both
paths use zero provider calls, zero MCP calls and zero journal writes.

This proves identity reconnection for the retained observations. It does not
claim that guidance guarantees every future model's choices, or that provenance
alone proves semantic relevance. Historical YAML, approvals and receipts were
not changed; the failed run was not resumed.

## Identical graph, different final lowering

Fresh sessions/explicit revisions use approval-fingerprinted
`compact-bindings-v8`; omitted/v1–v7 behavior and public compiler overloads remain.
The model still emits typed business bindings. Existing final lowering expands
nested programs, uses declared field names for locals and emits native multiline
JavaScript. Literal `expression_contracts` validate each checked constant in
order, before dependent computation and dispatch. Output validation remains.

[Compilation measurements](compilation-comparison.json) compare the same saved
graph/catalog, independently from the corrected identity script:

| Measurement | v7 | v8 |
|---|---:|---:|
| `checkedMapping` occurrences | 63 | 0 |
| Maximum nested program wrappers | 2 | 0 |
| `m.select` calls | 13 | 6 |
| Maximum line characters | 9,343 | 268 |
| YAML bytes | 111,448 | 157,581 |
| Steps / sets / workflows | 62 / 32 / 5 | 62 / 32 / 5 |

All six retained `m.select` calls implement first-present alternatives: a present
null or invalid value cannot fall through. Strict lookup remains host checked.
Step counts are intentionally unchanged by this readability correction. Literal,
indented schemas increase total YAML bytes; removing quoted programs does not
constitute a total-size reduction. The v7 SHA-256 matches the historical YAML
exactly: `82838029c5de182c9526d534f48e1cd31d32461b496cee31958b52995e17e332`.

[Six retained checked-input boundaries](checked-input-replay.json) produce
identical values after deterministic compiler-local alias renaming. No workflow
engine, inference or MCP client is resumed. Native evaluation resolves only
referenced data, preserves copied JSON numeric tokens and fails closed on
unsupported forms. Only bounded lookup/arithmetic operands enter Jint.

Counters are recorded separately from managed allocation measurements. Warm
measurements avoid attributing first-use/JIT costs to normal throughput; they
are diagnostic observations, not a statistical performance benchmark. An
unnecessary-import assembly falls from 153,832 to 65,456 bytes of warm managed
allocation; necessary whole-observation transport still has real costs and is
not universally cheaper. Scalar-selection allocation remains independent of
unrelated payload size in the eight-large-observation regression.

Traversal/materialization stays charged to the shared allowance. Static native
property chains resolve as a single selection, matching the existing path
operation, without imposing an additional statement per intermediate container.
The nested 10,000-statement / 5,000-ms / 50,000,000-byte ceilings and cumulative
expression allowance remain unchanged. The 1,602-record indexed regression also
checks that a smaller shared allowance still fails atomically.

## Validation

**Full solution: 5,154 passed, zero failed, 13 skipped across 33 projects**, with
`-warnaserror` ([per-project totals](solution-tests.json)). Existing skips are seven
Cmd cases, five Copilot E2E cases and the opt-in Designer browser test. No new skip
was introduced. Completed validation also includes 1,246 Flow tests; 63 profile/fusion/index/scope tests; 32 discovery/revision/profile checks;
four encrypted receipt fault/restart cases; all eight real local Browser/Document
execution cases (plus the separate composition-size test); Core, Planning,
Integrations and Persistence Release packages; and the final Native AOT smoke.
All used `-warnaserror`; no limits or assertions were relaxed. [CI state](ci-results.json)
was recorded for validation commit `ba350e26`: remaining build jobs were still
running/queued with no failure reported. This is not a claim that all remote
checks had finished; the PR remains draft.

The new cases cover exact numbers, missing/null, first-present alternatives,
invalid unused intermediates, ordered checks before effects, malformed metadata,
JSON pointers/duplicates, Unicode/backticks/interpolation-looking literals,
nested scopes, branches, cleanup, committed receipt reuse, and unknown completion.
The trimmed self-contained osx-arm64 Agent.Server published encrypted-recovery
smoke passed using `SkipBundledServerTools=true`: it tests the published host,
not a Browser bundle download. Local workbook cases independently inspect exact
cells, row order, visits, output path and cleanup, retaining incomplete/denied/
fabricated-data failure oracles. Neither check is live Amazon acceptance.

Earlier validation caught an overcount of structural traversal and repeated
property-chain evaluation in the new evaluator. Both have deterministic
regressions. The old 1,602-item v7 native chain itself can exceed its stricter
nested statement window; its behavior is retained. The v7/v8 comparison uses
1,000 items, and the v8 case additionally verifies all 1,602 original positions.
A first mixed restore/publish invocation hit `NETSDK1047` because RID and non-RID
restores shared an `obj` directory. Publish was moved to an isolated checkout;
no project target or warning policy was weakened. A concurrent host-test rebuild also encountered a locked reference assembly and was rerun in the isolated checkout.

Validation commands (publish/package work ran in an isolated checkout):

```sh
Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0 dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true --filter FullyQualifiedName~BusinessComposition
dotnet test tests/GnOuGo.Flow.Persistence.Tests -m:1 -warnaserror --filter FullyQualifiedName~RealFlowRecoveryDoesNotRepeatEffects
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -m:1 -c Release -r osx-arm64 --self-contained true -warnaserror -p:SkipModelMetadataGeneration=true -o "$NATIVE_OUTPUT"
"$NATIVE_OUTPUT/GnOuGo.Flow.Planning.Smoke"
dotnet publish src/GnOuGo.Agent.Server -m:1 -c Release -r osx-arm64 --self-contained true -warnaserror -p:SkipClientBuild=true -p:SkipBundledServerTools=true -p:PublishAot=false -p:PublishTrimmed=true -p:PublishSingleFile=true -p:SkipModelMetadataGeneration=true -p:UseAppHost=true -p:DebugType=None -p:DebugSymbols=false -o "$HOST_OUTPUT"
"$HOST_OUTPUT/GnOuGo.Agent.Server" --planning-persistence-smoke "$DISPOSABLE_SMOKE_ROOT"
```

Release `dotnet pack -c Release -warnaserror` passed independently for Flow.Core,
Flow.Planning, Flow.Integrations and Flow.Persistence. The existing skill validator
passes. Frontend generation was skipped because no frontend code changed; existing
model metadata was retained to avoid unrelated network generation. The ephemeral
gRPC test port avoids the developer's existing service; production configuration
and all test/runtime limits are unchanged.

## Reproducible offline commands

The three small console probes use existing compiler/evaluator/storage APIs.
`REPOSITORY_ROOT` is this checkout; `WORKSPACE_ROOT` is the existing encrypted
workspace; `SESSION_EXPORT` is the retained public session export containing
`session.graph` and `session.catalog`. They do not recover or resume a workflow.
Use a disposable `PROBE_OUTPUT` directory. The identity probe's historical run
and invocation identities are fixed intentionally; it fails closed if the saved
script no longer matches the retained identity expression.

```sh
dotnet run --project docs/evidence/readable-checked-bindings-2026-10-10/compile-measurements/probe.csproj -p:RepositoryRoot="$REPOSITORY_ROOT" -p:SkipModelMetadataGeneration=true -- "$SESSION_EXPORT" "$PROBE_OUTPUT"
dotnet run --project docs/evidence/readable-checked-bindings-2026-10-10/identity-replay/probe.csproj -p:RepositoryRoot="$REPOSITORY_ROOT" -p:SkipModelMetadataGeneration=true -- "$WORKSPACE_ROOT" docs/evidence/nullable-mapping-facts-2026-10-10/amazon-r7.yaml
dotnet run --project docs/evidence/readable-checked-bindings-2026-10-10/checked-replay/probe.csproj -p:RepositoryRoot="$REPOSITORY_ROOT" -p:SkipModelMetadataGeneration=true -- "$WORKSPACE_ROOT" "$PROBE_OUTPUT/readable-v7.yaml" "$PROBE_OUTPUT/readable-v8.yaml"
```

The probes use friend test assembly names for existing internal lowering/counter
access. No production public API is introduced. The retained sources stay in
encrypted storage; evidence contains scripts, hashes and measurements only.

## Live boundary

A new candidate can be frozen from this correction after all required checks
pass. New paid planning needs new user authorization. A subsequently generated
workflow needs its own revision/hash-bound artifact approval and explicit
requirement acknowledgments. No historical approval or uncertain invocation
will be reused. These offline results do not establish Amazon-to-XLSX live
acceptance or the PR's full execution gates.
