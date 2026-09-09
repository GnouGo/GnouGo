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

## Paired whole-subworkflow comparison

The opt-in `ConstructionFormats_ComparePairedApprovedSubworkflows` test compares
`typed-workflows-v1` with `javascript-v1`. Both generate one complete subworkflow
per candidate through a shared dependency scheduler and validator. JSON uses a
strict typed workflow response; JavaScript uses `{source}` and Jint/Acornima. Both
receive the same graph template and contract evidence, with format-specific
documentation and response schemas included in the prompt-size estimate.

Three fresh preparation sessions use the original prompt and answers. Each approved
checkpoint produces two fresh construction sessions with identical graph, preparation,
answers and approval fingerprints. Their construction state and receipts are separate.
The checkpoint fork exists only in the test harness and uses the encrypted store.
A preparation failure blocks both arms; it is reported rather than retried.
Model and discovery-catalog drift stops further planning. Each arm still needs exact
artifact approval, saving, and the original independent disposable-fixture checks.

The paired harness explicitly applies **32,000 input / 8,192 output tokens** and low
reasoning. Application defaults and existing sessions remain unchanged. Each logical
generation has 100 calls and 15 active minutes including its shared preparation;
preparation usage is deducted from both arms' allowances but billed once to the
cumulative campaign ledger. The existing exclusive lease, reservations, authorized
limits and campaign deadline remain in force.

```sh
GNOU_GO_LIVE_PAIRED_PLANNING_COMPARISON=1 \
GNOU_GO_LIVE_EXISTING_CONFIGURATION_AUTHORIZED=1 \
GNOU_GO_LIVE_PRIOR_COST_RESERVE=50 \
GNOU_GO_LIVE_INTENT_AGENT_BUDGET_AMOUNT=300 \
GNOU_GO_LIVE_INTENT_AGENT_BUDGET_CURRENCY=EUR \
GNOU_GO_LIVE_INTENT_AGENT_MAX_CALLS=3000 \
GNOU_GO_LIVE_INTENT_AGENT_MAX_TOTAL_TOKENS=20000000 \
GNOU_GO_LIVE_INTENT_AGENT_BUDGET_STATE_PATH="$PLANNING_CAMPAIGN_LEDGER" \
dotnet test tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj \
  -m:1 -warnaserror /p:SkipClientBuild=true /p:SkipModelMetadataGeneration=true \
  --filter FullyQualifiedName~ConstructionFormats_ComparePairedApprovedSubworkflows
```

Reports beside the ledger use `.paired-comparison-<id>.json`. Shared preparation,
per-arm generation usage and total ledger deltas remain separate. Reports retain
blocked/unstarted arms, effective limits, checkpoint hashes, format, subworkflow
prompt estimates, construction calls, repairs, diagnostics and active duration.
The five-minute target includes preparation; successful functional acceptance
requires all three arms of a format to pass. This measures construction conditional
on shared preparation, not independent end-to-end planner reliability.

## Paired-run verification and harness incident

Local checks for the paired implementation passed: 565 Planning tests, 1,449 Flow
tests, 65 relevant Server tests and 17 authoring tests. The macOS Native AOT smoke
executes both whole-workflow strategies through native YAML validation; packaging
also passed. GitHub Actions passed tests, packaging and Linux Native AOT on the
initial shared-pipeline commit.

The first paired launch (`1a6e283`, 18:12 UTC) exposed a harness naming defect: arm
cohort names exceeded the existing 32-character limit. The first preparation passed
in seven calls, but both arms stopped at harness preflight with no construction
dispatch. The runner was interrupted during the second preparation. Its report is
[retained unchanged](../evaluations/workflow-planning/results/paired-construction-2026-09-09-interrupted.json);
a [receipt audit](../evaluations/workflow-planning/results/paired-construction-2026-09-09-interrupted-audit.json)
records the interrupted preparation and ledger totals that the unfinished report
could not capture. This is an incomplete harness run, not evidence about either
construction format.

The audit found **12 verified calls, 119,282 tokens and EUR 0.8160** in ledger charges.
No construction arm or disposable fixture executed. Encrypted sessions and receipts
remain retained; reservations were not released. The interrupted telemetry database
was removed, and no helper process remained. Cohort names are now shortened and all
pairs are validated before any paid preparation, covered by a regression test. The
corrected comparison uses fresh sessions and includes this incident in reporting.

## Corrected paired results — 32,000 input tokens

The corrected campaign ran on `f461581` from **18:23 to 18:38 UTC on 2026-09-09**,
using `gpt-5.5-2026-04-24`, low reasoning, 32,000 estimated input tokens and 8,192
output tokens. **Both formats accepted 0/3 workflows.** All three preparations and
all six arm outcomes are included in the [measured report](../evaluations/workflow-planning/results/paired-construction-2026-09-09.json).
Only static generator guidance is omitted from the published report; measurements
are unchanged. Subsequent reporting changes omit all generator guidance, retain
shared-prefix totals for blocked arms, and verify effective configuration before
starting a worker. They do not change this campaign's prompts or results.

| Pair | Shared preparation | Calls | Input tokens | Output tokens | Active minutes | Ledger estimate (EUR) |
|---|---|---:|---:|---:|---:|---:|
| 1 | Behavior contract invalid; repair exhausted | 9 | 85,351 | 11,101 | 2.34 | 0.6537 |
| 2 | Behavior approved | 9 | 98,309 | 17,353 | 3.28 | 0.8709 |
| 3 | Behavior contract invalid; repair exhausted | 8 | 80,747 | 17,715 | 5.58 | 0.8047 |

| Pair | JSON arm | JavaScript arm | Construction calls / repairs |
|---|---|---|---:|
| 1 | Blocked by shared preparation | Blocked by shared preparation | 0 / 0 each |
| 2 | Context too large: **37,097** estimated input tokens | Context too large: **37,331** estimated input tokens | 0 / 0 each |
| 3 | Blocked by shared preparation | Blocked by shared preparation | 0 / 0 each |

Pair 2's arms have the same starting checkpoint fingerprint and the same single
`pr_review_agent` subworkflow. JSON construction preflight took 14.98 ms and
JavaScript 24.33 ms; these are local rejection times, not generation performance.
Including shared preparation, both took about 3.28 active minutes and made zero
construction model calls. Both exceeded the 32,000 ceiling, by 5,097 and 5,331
estimated tokens respectively. The 234-token difference between formats is small
relative to their shared graph and contract context. This is evidence of a context
packaging/admission blocker; **it does not establish either format's generation
quality or convergence**. No agent reached saving or independent execution acceptance.

The corrected campaign recorded **26 verified calls, 310,576 tokens and EUR 2.3293**
in ledger charges. All five started sessions (three preparations and two arms)
completed cleanup. Four arms were explicitly blocked before session creation.
No new unverified reservation remained from the corrected campaign.

Including the interrupted harness run, this follow-up used **38 verified calls,
429,858 tokens and EUR 3.1453**. One interrupted request remains unverified with
**EUR 0.7927 reserved**; it is not counted as a verified call or released. The
cumulative ledger ended at 1,770 verified calls, 15,295,071 tokens and EUR 160.1616
estimated usage (including the pre-existing EUR 50 reserve), plus EUR 36.9447
across 17 unresolved reservations. The existing
EUR 300 ceiling leaves EUR 102.8937 conservatively available. Authorized limits and
the previous reservations are unchanged, the exclusive lock is released, and
`final_acceptance_completed` remains false.

The original 12,000-token campaign below used different construction granularity
and independent preparation. Its results remain historical evidence, not a matched
language-performance baseline for this paired experiment.

## Original end-to-end results

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
