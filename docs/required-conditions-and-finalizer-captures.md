# Required conditions and guarded finalizer captures

This correction addresses the two retained generation failures in
[cohort contractsreview20261004e](contracts-and-review-corrections-2026-10-04.md).
It preserves the planning architecture, runtime executors, `mapping.dynamic`,
permissions, execution oracles and historical uncertain invocations.

## Required business conditions

An optional TaskPlan `requires` value expresses a Boolean condition that must hold
before a task begins. False fails the task; it does not skip requested work and
produce a substitute output. The compiler checks the condition's type, dependencies
and availability, then emits an existing checked `set` before inputs, runtime
inference or effects. Predicates remain structured until final YAML lowering.
Selected-field checks on the right of `and`/`or` retain short-circuit behavior.

For example, a publication sequence may require observed completeness and return
its actual written path. Ordinary `conditional` tasks still need matching explicit
branch outputs; the compiler never invents a workbook path for an incomplete branch.
Checking a flag does not prove the quality of an observation: requirements review,
continuation handling and independent execution oracles remain necessary.

New model-facing schemas include nullable `requires` on tasks. Stored TaskPlans omit
it when absent, preserving historical serialization and hashes. Issued schemas and
saved artifacts are not rewritten. Scoped repair cannot add, remove or alter a
precondition, including by replacing or removing its task; use an explicit revision.
Review diagrams display the condition. Prompt presentation omits duplicate schema
descriptions and default `required: true` port flags without removing constraints or
authoritative descriptions from operation context.

## Finalization across lexical scopes

Nested finalizers may inspect `present` for an available preceding lexical ancestor.
The compiler captures presence separately and snapshots a payload only if present,
using existing switches, sets and workflow inputs. An omitted internal payload is
not a business null. A successful producer containing null still has a present result.
Checked consumption occurs inside the selected branch, retaining artifact origin.
Sibling branches, unavailable later ancestors and reusable group boundaries remain
inaccessible.

To preserve a report after verified failure, put reporting in an `always` sequence
and cleanup in that sequence's nested `always`. A failed finalizer stops its ordinary
sequential block; the nested finalizer is what permits cleanup after report failure.
Unknown completion still blocks finalization and replay. Presence proves neither
payload non-nullability nor successful external work.

## Reproduction and checks

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests|FullyQualifiedName~ReportFinalizationExecutionTests'
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Core -c Release -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release -m:1 -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 \
  --self-contained true -m:1 -warnaserror
tests/GnOuGo.Flow.Planning.Smoke/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Flow.Planning.Smoke
```

`TaskPreconditionTests` and `FinalizerCaptureTests` exercise real Flow execution with
deterministic adapters, absent/null payloads, short-circuit reads, sequential/parallel
loops, effect exclusion, nested cleanup, artifact origin and restricted repairs.
The local Browser/Document fixture independently inspects visited pages and XLSX cells;
an incomplete observation fails before extraction inference and publication.
The Native AOT smoke covers serialization and both condition outcomes.

The retained code proposal can be compiled without inference or saved-session changes:

```sh
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability replay-compile --campaign schema-portability-20261002 \
  --workspace "$GNOU_GO_WORKSPACE" --run contractsreview20261004e-code-1
```

The original Amazon proposal retains its invalid asymmetric conditional interface.
It needs an explicit revision; this correction does not rewrite or approve it.
Deterministic validation and retained-proposal replay are separate from live-provider
acceptance. Fresh evaluations must use new IDs, the same oracles and the existing
campaign spending gate. Historical benchmarks remain unchanged.
