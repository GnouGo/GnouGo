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

Local verification on 2026-09-09: 17 authoring tests, 543 planning tests, 1,449 Flow
tests and 50 relevant Server tests passed (the final planner total includes the
additional native subworkflow-argument/finalizer regression). The macOS ARM64 Native
AOT smoke passed, including JavaScript authoring followed by native YAML execution.
GitHub Actions also passed the Linux test, packaging and Native AOT checks.

The bounded live campaign ran from 15:45 to 16:17 UTC on 2026-09-09 using
`gpt-5.5-2026-04-24`, low reasoning, and the original scenario. All six fresh sessions
completed their bounded attempts; **both strategies accepted 0/3 workflows**.

| Strategy | Run | Stopping phase | Calls | Active minutes | Generation estimate (EUR) |
|---|---:|---|---:|---:|---:|
| typed-units-v2 | 1 | Behavior contract | 8 | 2.34 | 0.7135 |
| javascript-v1 | 1 | Behavior contract | 8 | 2.28 | 0.7018 |
| typed-units-v2 | 2 | Executable validation | 45 | 8.73 | 2.2486 |
| javascript-v1 | 2 | Construction input budget | 8 | 3.03 | 0.6029 |
| typed-units-v2 | 3 | Construction repair | 32 | 8.32 | 1.6672 |
| javascript-v1 | 3 | Construction input budget | 7 | 2.01 | 0.5875 |

The common behavior failures reported `BEHAVIOR_CONTRACT_INVALID` and
`BEHAVIOR_REPAIR_EXHAUSTED`. The second control run failed on
`OPAQUE_DATA_VARIABLE_DEEP_ACCESS`, `MCP_REQUEST_SELECTOR_NOT_LITERAL` and
`WORKFLOW_PLAN_REPAIR_STALLED`; the third reported `OPERATION_INPUT_BINDING_MISSING`.

The two JavaScript sessions that passed behavior review both stopped with
`JS_CONTEXT_TOO_LARGE` before dispatching an authoring request. Consequently **no
live JavaScript candidate was generated**, and this campaign cannot establish the
quality or convergence benefit of LLM-written JavaScript. Shorter failures are not a
performance improvement. Construction context packaging remains a prerequisite;
the shared behavior stage also failed in fresh attempts. The 12,000-token input
budget was not increased and failed sessions were not resumed.

The campaign consumed **108 model calls and 962,826 tokens**, adding **EUR 6.5383**
to the cumulative ledger estimate. The raw report retains per-session generation
estimates and cumulative ledger deltas separately; the headline spend uses the
ledger delta. The unchanged EUR 300 ledger ended at 1,732 calls, EUR 157.0163 estimated
usage (including the pre-existing reserve) and EUR 36.1521 in unchanged unverified
reservations, leaving EUR 106.8317 conservatively available. No additional unverified
reservation remained. Harness cleanup completed for all six attempts; none reached
generated-agent execution acceptance.

The [raw measured report](../evaluations/workflow-planning/results/javascript-construction-2026-09-09.json)
is preserved as emitted by the initial implementation committed as `e751de1`.
Its `catalogFingerprint` field is a **preparation-state hash**, not proof of an
identical immutable catalog. Later reporting names this field `preparationFingerprint`,
records source dispatch counts explicitly, and exposes the estimated size and affected
workflow for input-budget failures. Those reporting changes and the constructor
compatibility fix do not change the campaign's prompts, limits or stopping decisions.

Functional acceptance requires all three JavaScript agents to be generated, saved
through exact artifact approval, and pass the independent execution checks. Fast
failure is not convergence. Three successes are an initial acceptance signal, not
a statistically strong reliability estimate; keep the strategy opt-in until reviewed.
