# Desktop repair diagnostic boundary — 2026-09-20

## Scope and reproduced failure

This targeted correction starts from `e27f7a7` on `feat/deterministic-planner-v2`.
It changes diagnostic ownership and locations only. Planner architecture, schema-7
storage, public contracts, capability eligibility, request schemas and budgets are
unchanged. The generic Desktop bootstrap is unchanged.

Desktop session `b5de7ed0…` stopped after one completed interpretation and zero
repairs. The proposal had unresolved capabilities and invalid bindings. Three
additional host-failure classifications prevented the existing repair protocol:
two findings on a generated loop capture's schema and one compiler finding on an
MCP-wrapped cleanup argument.

Five sanitized regressions failed before the fix: captures from an input, an
operation result and an iteration source were classified as host defects; malformed
nested cleanup templates lost their value location with and without confirmation
wrapping. These fixtures use generic collections and labels, without review-specific
wording or production branches.

## Correction

- `PlanningDiagnosticLocations.cs` follows generated block caller arguments back to
  their business source, recursively through nested blocks. It recomputes this
  relationship from the graph and intent. An established valid source with a broken
  generated input remains a host defect.
- `PlanningGraphCompiler.cs` retains relative value coordinates while a lowering
  exception unwinds through objects, arrays and expression parameters. Exception
  type and message remain unchanged; coordinates are transient exception metadata.
- Nested catalog-owned request bindings remain protected, as do confirmation guards.
  Invalid proposals still fail complete deterministic validation and compilation.

Thirteen focused regressions cover exact graph/intent locations, branches,
subflows, confirmation wrapping, host-owned fields, restart and bounded typed
corrections. A mocked valid correction reaches FinalReview after one repair;
unchanged corrections stop after one, and invalid targets stop after two. Approval
is not granted by correction.

## Read-only replay

A temporary local inspection executable used public encrypted KeyVault record APIs
and the original reserved response schema. It replayed the one durable receipt
through the current coordinator using an in-memory runtime with no provider or
persistence writes. Only redacted counts and locations were emitted.

| Observation | Result |
| --- | --- |
| Live model dispatches | 0 |
| Original calls / repairs | 1 / 0, unchanged |
| Blocking diagnostics after rebuild | 141 |
| False host-failure diagnostics | 0 |
| Issued typed correction targets | 25 |
| Generated YAML / execution | None |
| Loop capture source | `/operations/12/items` |
| Cleanup compiler failure | `/operations/19/operations/0/arguments/1/value` |

The original session, request, receipt and budget records retained identical SHA-256
hashes and update timestamps before and after replay. Original blocking findings
were retained; removing the three false host classifications did not make the
proposal executable. No stopped session was resumed or edited.

Replay exposed a separate limitation: the existing repair context for this proposal
is estimated at **47,840 input tokens**, exceeding its **12,000** limit. The
classification correction exposes repair targets but does not bypass that budget.
This work does not change context selection or increase limits. A new interpretation
may differ; the old proposal cannot dispatch this repair under its existing limit.

## Offline verification

Run in Release on the starting revision plus this correction:

```bash
dotnet test tests/GnOuGo.Flow.Planning.Tests/GnOuGo.Flow.Planning.Tests.csproj -c Release -m:1 --no-restore
dotnet test tests/GnOuGo.Flow.Integrations.Tests/GnOuGo.Flow.Integrations.Tests.csproj -c Release -m:1 --no-restore
dotnet test tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj -c Release -m:1 --no-restore
dotnet build tests/GnOuGo.Agent.Planning.Benchmark/GnOuGo.Agent.Planning.Benchmark.csproj -c Release -m:1 --no-restore -warnaserror
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark/GnOuGo.Agent.Planning.Benchmark.csproj -c Release --no-build
dotnet build src/GnOuGo.Agent.Desktop/GnOuGo.Agent.Desktop.csproj -c Release -m:1 --no-restore -warnaserror
git diff --check
```

Results: **163 planner, 67 integration and 345 Server tests passed**. This includes
existing original-schema receipt replay, accounting, restart, cancellation, approval
and safety tests. All **eight offline benchmark cases and 31 independent execution
variants passed**, with zero safety violations. Benchmark and Desktop builds,
including Server and invoked frontend targets, completed with zero warnings/errors.
These are offline results, not a new live reliability cohort.

## Controlled Desktop retry

The next validation uses the same read-only prompt in a fresh native Desktop chat
execution, retaining eight calls, two repairs, medium reasoning and 12,000/32,768
request token limits. The application must present the generated artifact for human
approval before real execution. Runtime publication confirmation remains separate.
No paid calls or external review actions were made during the offline work above.
