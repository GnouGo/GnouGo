# Pure indexing and bounded extraction inputs

The [retained execution](../mapping-lifecycle-2026-10-08/README.md) reached a complete 58-page/1,602-record snapshot, then exhausted the unchanged 1,000 workflow-iteration ceiling while attaching original indices. A later flat independent extraction would also exceed its unchanged item limit. That run and its approval remain untouched.

## Correction

Fresh sessions and explicit revisions select approval-fingerprinted `compact-bindings-v3`. Pure typed copy loops may also carry the existing original index. They lower to one checked structural projection, preserving the legacy result envelope without per-record workflow calls. Sole-property wrappers are removed only when their validation is equivalent. Business operations, inference, per-item guards, cleanup and prior-state loops remain ordinary bounded loops.

The change adds no executor or planning phase and makes no change to `mapping.dynamic`, its sampling/specialization, source-grounding or allowances. Known typed fields use deterministic bindings. Learned extraction must use an explicit compatible input, such as complete bounded pages, and validate every nested record. Flat 1,602-item extraction still fails before inference. No automatic chunking, filtering, truncation or allowance resets occur.

## Deterministic evidence

[Measurements](deterministic-measurements.json) compare identical TaskPlans. At 1,602 records, indexed YAML falls from 10,609 to 8,220 bytes and runs in four checked-set invocations, with zero model calls and zero finalization steps. On the 12-record journal comparison, invocations fall from 29 to 4 and logical serialized bytes from 445,395 to 45,345. This is not a physical-storage benchmark or a journal reconstruction optimization.

The 58-page/1,602-record decision fixture retains 147 candidates and exact original action arguments. Its complete framed global request is 17,906 bytes (2,799 estimated input tokens), with one deterministic mapping-adapter call. All candidates and records remain covered; this is local evidence, not provider execution.

Historical omitted/v1/v2 YAML hashes were measured separately against untouched commit `65b1c315` and match exactly. The new profile invalidates approval through the existing option fingerprint. Learned scripts still cannot manufacture positional scalar values.

Focused validation: Flow 1,164 tests and Planning 1,132 tests pass with `-warnaserror`. The 18-test measurement subset passes. Local Browser/Document, broader solution, packages and live evidence are recorded below as they complete.

## Reproduction

```sh
dotnet test tests/GnOuGo.Flow.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests --no-build --filter 'FullyQualifiedName~IndexedProjectionCompilationTests|FullyQualifiedName~LookupSelectionTests' --logger 'console;verbosity=detailed'
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests'
```

Only one fresh Amazon validation is authorized, maximum ten products, after local/readiness checks and concrete revision/hash-bound requirement review. Historical invocations must never be replayed. The historical 33/33 benchmark remains unchanged; PR #117 stays draft.
