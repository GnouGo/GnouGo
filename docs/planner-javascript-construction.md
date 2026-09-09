# JavaScript construction experiment

`javascript-v1` replaces only executable construction/repair in planner v2. It is
opt-in and uses a separately packaged Jint/Acornima adapter through Flow.Core's
`IPlanningSourceCompiler`. Discovery, intent clarification, behavior approval,
runtime contracts, scenario validation, saving and external execution are shared
with `typed-units-v2`. JSDoc guides the model; .NET validates contracts and provenance.

The experiment does not establish a general JavaScript-to-YAML round-trip language
or a static JavaScript type checker. Runtime effects remain native YAML nodes.

## Reproduce local checks

```sh
dotnet test tests/GnOuGo.Flow.Authoring.JavaScript.Tests/GnOuGo.Flow.Authoring.JavaScript.Tests.csproj -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests/GnOuGo.Flow.Planning.Tests.csproj -warnaserror
dotnet test tests/GnOuGo.Flow.Tests/GnOuGo.Flow.Tests.csproj -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj -m:1 -warnaserror /p:SkipClientBuild=true --filter 'FullyQualifiedName~Planning|FullyQualifiedName~LiveIntentAgentGenerationTests'
dotnet publish tests/GnOuGo.Flow.Planning.Smoke/GnOuGo.Flow.Planning.Smoke.csproj -c Release -r linux-x64 --self-contained true -warnaserror -o artifacts/planning-smoke
artifacts/planning-smoke/GnOuGo.Flow.Planning.Smoke
```

Use `osx-arm64` for the native publish on Apple Silicon. The existing publish-local
Jint CLR-interop warning boundary also covers the authoring interpreter; no CLR
objects are exposed. The focused `JavaScript planning validation` GitHub Actions
workflow runs on pull requests to feature branches as well as `main`.

## Live comparison protocol

The opt-in test `ConstructionStrategies_CompareSamePullRequestScenario` runs three
fresh logical sessions per strategy, alternating strategies. Every successful
generation executes the existing independent disposable-fixture acceptance checks.
It uses `AcceptancePrompt` and scripted clarification answers unchanged: clone once,
restore dependencies, run tests/linters, review changed code, confirm publication,
never push, and clean up on success/failure/cancellation.

Both strategies use the existing configured provider/model, low reasoning, the same
catalog access and 12,000/8,192 construction token ceilings. Each generation has a
100-call limit and a 15-minute active planning deadline; five minutes is the initial
performance target. The whole comparison retains the existing elapsed-time ceiling.
Generation runs are driven explicitly through the normal session service so the
harness can cancel in-flight work at the deadline. Failed sessions are not retried.

An existing cumulative ledger is mandatory. Its exclusive lease covers all six
attempts. Existing reservations and limits remain intact; amendments are rejected.
The configured KeyVault integration and existing-configuration authorization must
match that ledger. For the existing authorized EUR 300 campaign:

```sh
GNOU_GO_LIVE_PLANNING_COMPARISON=1 \
GNOU_GO_LIVE_EXISTING_CONFIGURATION_AUTHORIZED=1 \
GNOU_GO_LIVE_PRIOR_COST_RESERVE=50 \
GNOU_GO_LIVE_INTENT_AGENT_BUDGET_AMOUNT=300 \
GNOU_GO_LIVE_INTENT_AGENT_BUDGET_CURRENCY=EUR \
GNOU_GO_LIVE_INTENT_AGENT_MAX_CALLS=3000 \
GNOU_GO_LIVE_INTENT_AGENT_MAX_TOTAL_TOKENS=20000000 \
GNOU_GO_LIVE_INTENT_AGENT_BUDGET_STATE_PATH="$PLANNING_CAMPAIGN_LEDGER" \
dotnet test tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj \
  -m:1 /p:SkipClientBuild=true /p:SkipModelMetadataGeneration=true \
  --filter FullyQualifiedName~ConstructionStrategies_CompareSamePullRequestScenario
```

Set `PLANNING_CAMPAIGN_LEDGER` to the existing ledger location in the resolved
GnOuGo workspace. These flags retain prior authorization; they do not create a new
budget or attest to a provider-side hard cap. The separate legacy live test can
select a strategy with `GNOU_GO_LIVE_TYPED_PLANNING_CONSTRUCTION_STRATEGY`.

Reports are written beside the ledger as `.comparison-<id>.json`, including failed
and unstarted attempts, phases/codes, model/catalog identity, generation calls/tokens,
cost, duration, repair counts and total attempt cost/reservation deltas. Private
source and prompts stay in encrypted snapshots and receipts. Opt-in tests that
return without activation are not evidence of live acceptance.

## Results

Local verification on 2026-09-09: 17 authoring tests, 542 planning tests, 1,449 Flow
tests and 50 relevant Server tests passed. The macOS ARM64 Native AOT smoke passed,
including JavaScript authoring followed by native YAML execution. Live comparison
results will be recorded here after the bounded campaign completes.

Functional acceptance requires all three JavaScript agents to be generated, saved
through exact artifact approval, and pass the independent execution checks. Fast
failure is not convergence. Three successes are an initial acceptance signal, not
a statistically strong reliability estimate; keep the strategy opt-in until reviewed.
