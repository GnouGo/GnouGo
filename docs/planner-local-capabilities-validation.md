# Local planner capability resolution validation

Validated on 2026-09-12, macOS arm64, .NET SDK 10.0.300, on
`feat/deterministic-planner-v2` based on
`04ae5f0ff0ec5d533c2f4648b6aab70d495d2779` plus the local capability-resolution change.
This is offline implementation validation, not a new live campaign or convergence claim.

Agent.Server's existing injected `ILLMCapabilityResolver` reads the current options
snapshot and resolves declared metadata through AI.Core. Flow.Integrations uses the
same operation. Capability checks do not use a model catalog or HTTP transport.
The regression for a deployment endpoint registers a catalog that throws HTTP 404;
resolution succeeds from saved metadata with zero catalog calls.

## Regression coverage

- Exact entries and declared aliases, provider separation, and embedded → file → saved
  override precedence, including explicit false values and empty reasoning lists.
- Partial declarations remain unknown even after editor suggestion resolution;
  fuzzy matches cannot prove support. Missing or malformed metadata files stop proof.
- `/llm edit` acceptance, immediate override updates, actual host startup hydration,
  and benchmark hydration across reopened persistence providers.
- Agent.Server's planner records synthetic intent requests through a fake generation
  transport and the real injected resolver. Missing declarations or unreadable metadata
  stop before generation; cancellation propagates. These receipts are synthetic tests,
  not historical or live model evidence.
- The archived campaign guard prevents restart. Its read-only report is unchanged.
- The published integration adapter resolves local declarations without any registered
  transport and preserves unknown fields; the existing published planning, receipt
  replay, encryption, optimistic revision, and tenant isolation smokes remain passing.

## Completed checks

| Check | Result |
|---|---|
| Full solution tests | 2,990 passed; one opt-in live E2E test skipped; zero failures across 29 projects |
| AI.Core tests, included above | 193 passed |
| Flow.Integrations tests, included above | 65 passed |
| Flow.Planning tests, included above | 602 passed |
| Agent.Server tests, included above | 351 passed |
| Full solution build | Zero warnings and errors |
| Progressive benchmark build | Zero warnings and errors |
| AI.Core and Flow.Integrations Release packages | Both built successfully |
| Planning Native AOT publish and executable, `osx-arm64` | Passed, including local capability resolution and encrypted receipt replay |
| Agent.Server trimmed single-file publish and persistence executable, `osx-arm64` | Passed against an isolated temporary directory |

Commands used for the final checks:

```sh
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -v quiet
dotnet build GnOuGo.Agent.sln -m:1 -warnaserror -v quiet
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror -v quiet
dotnet pack src/GnOuGo.AI.Core -c Release -o /tmp/local-capabilities-packages -m:1 -warnaserror -v quiet
dotnet pack src/GnOuGo.Flow.Integrations -c Release -o /tmp/local-capabilities-packages -m:1 -warnaserror -v quiet
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 \
  -o /tmp/local-capabilities-aot -m:1 -warnaserror -v quiet
/tmp/local-capabilities-aot/GnOuGo.Flow.Planning.Smoke
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true \
  -p:PublishTrimmed=true -p:PublishSingleFile=true -p:PublishAot=false \
  -p:SkipBundledServerTools=true -p:SkipClientBuild=true \
  -o /tmp/local-capabilities-server -m:1 -warnaserror -v quiet
/tmp/local-capabilities-server/GnOuGo.Agent.Server --planning-persistence-smoke \
  /tmp/local-capabilities-server-persistence
```

Publication used the repository's existing documented Jint Native AOT and
Agent.Server partial-trim framework exceptions; no new warning suppression was added.
The persistence-only Agent.Server publish omitted bundled tools and reused built
frontend assets. No frontend sources changed. The first server smoke invocation
omitted the required directory argument, entered normal startup, and exited on an
occupied OTLP port. The corrected isolated invocation above passed without warnings.

Flow.Core, planner orchestration, `SecureWorkflowRuntimeFactory`, production DI
registration, provider URLs/authentication, and schema-5 persistence were unchanged.
The failed progressive campaign and its evidence remain archived. No new live planning
session, refreeze, reasoning change, or automatic session restart was performed.
