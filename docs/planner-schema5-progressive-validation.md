# Progressive Schema-5 validation — 2026-09-12

The campaign stopped during model-capability preflight. **No planning session was
started and no model generation request was dispatched.** This is a technical
prerequisite failure, not `Unsupported` or a demonstrated convergence failure.

Production remains at `ee487c8d998721368b20c89653526c919a70f881`. Changes are confined
to the benchmark harness, audit tooling, tests and documentation. No production
fix, configuration change, reasoning increase or additional live attempt was made.

## Campaign and evidence

The isolated harness uses Agent.Server's `PlanningSessionService`, capability
resolver, KeyVault configuration and encrypted schema-5 persistence. Automatic
background advancement is disabled. Routine, behavior and semantic-review profiles
are all `low`; limits remain 12,000 input / 8,192 output, a 9,600 estimated-input
dispatch target, concurrency four and five repairs per workflow/gate.

The KeyVault configuration inspected after the failure selects provider `OpenAi`
and model `gpt-5.5-2026-04-24`. Reasoning and Structured Output capability proof is
unavailable because discovery failed. No effective reasoning level was dispatched.

The encrypted manifest is `agent-planning-progressive-campaigns-v5`, tenant
`planner-progressive`, key `schema5-ee487c8`. It records the attempted binary hashes,
configured model attribution, catalog/policy/scenario/fixture fingerprints and
limits. It was archived after the failed freeze, before any session start. The
exception and endpoint are encrypted separately under `:preflight-evidence`.
All stages remain `not_run`; the archived preflight stop prevents further provider
calls or starts. The audit addition changed only the harness binary; production
binary hashes remained identical to the attempted freeze.

Selected SHA-256 hashes from the attempted freeze:

| Binary | Hash |
|---|---|
| Flow.Core | `76d9612e2318261b21eb8e8272e9a29440efbbcc26259eba630be3d5fc132071` |
| Flow.Planning | `5cf700e035c9b3a6aabd4a4680f8debaf0b2df416c470c31339736110a1b8c8e` |
| Agent.Server | `3ba33da4e049c61c1993b6774b4ec6337adae4dc0005f8ada877e931cf97b32c` |
| Benchmark harness | `ac5c28d12bab9e1b65e3a03d88211de21e0cd0e2ce9575d5f872dd692b6c96f2` |

Historical scenario inputs were recovered with the isolated pinned schema-4 audit
tool through public KeyVault APIs. Only immutable inputs were exported; historical
sessions, reservations and budgets were not migrated. Source snapshot hashes were
verified unchanged. The approved record scenario is revision 184, artifact
`ef2873f6d888f6523a00b21473a0e2568a727129f4d5dd096a2808399e0b601c`.
The frozen CodeReview catalog hash is
`792eebc5c700c51dd30fa69fd4680dee8b65fb8b8bc35ed9611bd2a61762fc43`.

The retained classifier callee previously passed execution. Standalone planning
would be a new test: it adopts the approved caller's optional threshold default of
100; the original callee required an explicit threshold. Medium processing uses a
declared read-only fixture capability and preserves the original classification,
aggregation and finalizer rules. CodeReview retains the full frozen catalog and
the existing benchmark-only implementation policy.

## Results

| Stage | Session | Typed outcome | Execution / approval |
|---|---|---|---|
| 1 — standalone classifier | Not run | None | Not run; no artifact or revision |
| 2 — record batch | Not run | None | Not run; Stage 1 gate unopened |
| 3 — CodeReview | Not run | None | Not run; Stage 2 gate unopened |

Campaign accounting: zero planning or independent-execution model calls, zero
reservations, zero unverifiable generation dispatches, zero decision pages,
zero exposed model decision IDs, zero executable-hole exposures, zero user
clarifications and zero repairs. There are no session revisions, validation-gate
results or approved hashes. Engine decision counts and request input sizes are
not applicable because planning never began. No token usage or effective reasoning
is attributed to the metadata lookup. It is separate from model generation.

The optional `medium` diagnostic was **not run**. A metadata/provider failure is
ineligible, and no completed low semantic receipt exists.

## First blocker

- Harness code: `MODEL_CAPABILITY_DISCOVERY_FAILED`.
- Phase and canonical location: `preflight`, `/model/capabilities`.
- Evidence: one observed model-list operation failed with HTTP 404 and provider
  error code `NotFound` before session creation. There is no model request,
  reservation or receipt to replay.
- Captured exception fingerprint:
  `597615be43253793faa46291c0b82afed7847b89608f6e79f2ac28a45945c55b`.
- Classification: provider metadata discovery / execution prerequisite.

`FlowLlmCapabilityResolver.SupportedReasoningLevelsAsync` asks the model catalog
for the configured model. `RoutingLLMModelCatalog` awaits the provider's model
list before enriching it with configured metadata. `OpenAiLLMProvider.ListModelsAsync`
receives HTTP 404 and throws, so the configured reasoning capability cannot be
proved. This path does not reach the planner or model generation.

The evidence establishes an unavailable catalog route. It does not establish
whether the cause is provider configuration, routing or lack of model-list support,
and it says nothing about generation availability. Those possibilities were not
tested through additional live requests. No metadata was fabricated to bypass
the prerequisite, and the failure was not represented as business unsupportedness.

## Offline checks and harness regressions

Before preflight, the warning-free full solution suite passed **2,965 tests** across
29 projects, including 602 planning tests. One unrelated opt-in live test was
skipped. The benchmark and isolated audit tool built with zero warnings/errors.

The reference execution self-check passed six classifier/batch case families,
including boundaries, rejection, defaults and invalid inputs. All 18 frozen
CodeReview fixture contract checks passed. These checks use the retained artifact
or synthetic fixture contracts; they are not new live planning successes and do
not count as the Stage 3 independent execution suite.

There are 22 harness regressions covering stage gates, single-start reservations
across restart, exact revisions/hashes, complete unique fixture evidence, all-low
profiles with unchanged budgets/defaults, diagnostic bounds, receipt-safe counting,
redaction and unknown usage. The two added after preflight reproduce HTTP 404
through the real model catalog/capability resolver with a synthetic HTTP handler
(one GET, no retry or generation) and reject restarting an archived preflight stop.
The final full offline rerun passed **2,967 tests**, with the same one unrelated
live test skipped and no warnings. Reading the encrypted campaign report in a new
process produced identical reporting and did not create a session or request.

The first blocker remains unresolved by design. Continuing requires a separately
authorized resolution of model-capability discovery and a new validation decision;
this campaign does not automatically retry it.
