# Explicit application LLM budgets — 2026-11-05

Implementation: `4c1091d8` (tracking and capture), `b4576e6c` (host setting,
Desktop and UI), `591e1736` (encrypted split-header recovery), `92746cd3`
(limiting units), `2357416e` (historical signed-capture compatibility), `4fb000b9`
(fresh-start context/operator access), `e65697a5` (incomplete-capture rejection),
and `1a81a769` (rendering fixture registration). Final qualification source is
`1a81a769`. No merge, release or live inference.
The user's staged solution and `.vscode/` changes are preserved outside these commits.

## Activation and behavior

Restart Desktop or Server normally. New plans and executions use **Unlimited — usage
and costs tracked**. In the protected operator window, open **Application LLM budget**
(`/settings/llm-budget`) to save Limited/Unlimited. The encrypted preference overrides
`ApplicationLlmBudget:Mode`, independently of environment. Limited uses the existing
configured planning/execution ceilings. Diagnostic targets still warn; Strict is
selectable. Existing sessions/runs retain their captured policies and reservations.

The reported stopped session keeps its €50 ceiling. **Start new plan with current
settings** is offered only after checking its lease, counters, request/receipt/HTTP
journals and accounting for absence of dispatch/reservation. It creates a new identity
and preserves the original. Uncertain work is never automatically replayed.

See [the limit inventory and activation rules](../../diagnostic-planning.md#application-llm-consumption).
Unlimited is an explicit tracking-only scope; no giant numeric bounds replace limits.
Finite workflow contracts, physical capacity, request timeouts, bounded retries,
cancellation, semantic checks, business review, approvals, permissions and execution
resource limits remain mandatory. Provider charges and account quotas still apply.

Missing pricing/FX and missing historical evidence are labelled unavailable. Usage,
captured-tariff estimates, physical attempts and uncertain reservations remain durable.
Schema-11 sessions authenticate their host capture; schema-9 runs retain a versioned
budget policy in the encrypted execution header. There is no historical migration.

## Controlled-provider evidence

The actual Agent.Server host runs against a controlled HTTP provider. The fixture
configures a private Sol deployment with USD30 input / USD180 output per million
tokens and 128,000-token output allocations. These are explicit private tariffs,
not claims about public OpenAI pricing. Admission checks three 16,000-input /
128,000-output allocations: USD70.56 before the controlled exchange quote.

Limited blocks before dispatch at €50. Saving Unlimited permits an explicit fresh
session to reach final review with the same model allocations and mandatory Judge.
The test checks reload, persistent setting precedence across restart, unauthorized
edits, antiforgery, foreign origins/identities, redaction and preservation of the
original stopped session. Independent tests retain explicit workflow limits under
Unlimited and test uncertain execution without replay in both modes.

- [Limited: admission stops before dispatch](controlled-limited-before-dispatch.png).
- [Unlimited: active generation](controlled-unlimited-active.png).
- [Unlimited: completed phases including warnings](controlled-unlimited-warning-continues.png).
- [Expanded exchange and threshold warnings permitting continuation](controlled-unlimited-llm-exchange.png).
- [Controlled session identities and Desktop handoff scope](controlled-browser-result.json).

The browser uses the Desktop bootstrap destination, exchanges the one-use fragment
for the protected cookie and returns to chat. The actual Desktop launcher source is
also compiled into its callback/queue test. **The native Photino window is not
automated by these checks.** Photino's complete navigation URL logger is disabled
before authority handoff; its behavior was checked against the source matching the
[installed 4.0.16 package commit](https://github.com/tryphotino/photino.NET/blob/4e8b40a3732d7bacedccc4c718865990c148823a/Photino.NET/PhotinoWindow.NET.cs#L1520).
Existing optional external-browser fallback uses a fresh single-use link.

The longer controlled-host browser regression also covers correction loops, skipped
phases, retries, partial/empty/invalid responses, cancellation, encrypted restart,
search/export and protected OTel input/output inspection. It explicitly selects
Limited to retain the earlier paired strict/diagnostic threshold fixtures.

## Source-bound validation

Final commands, counters, hashes and component source bindings are recorded in the
adjacent JSON reports. The completed earlier baseline at `51fed8a2` is documented
separately: [7,765 passed, 0 failed, 12 skipped](../default-diagnostics-2026-11-05/README.md).
It is not presented as validation of this budget change.

The complete `1a81a769` run finished on 2026-11-05 at 22:21:43 UTC:
**7,787 passed, 1 failed, 12 skipped, 33 assemblies**. Its solution build passed
with zero warnings/errors. The failure is
`BundledBrowserMcpPublishTests.DevelopmentBuild_StagesAllBundledMcpAppHostsAndAvoidsDotnetRunConfiguration`:
the fixture derives a configuration name from the parent output directory and
looks under `src/GnOuGo.Agent.Server/bin/GnOuGo.Agent.Server.Tests/net10.0/tools`
when qualification uses `--artifacts-path`. The binaries are staged in the
isolated test output. The original failure remains in
[the source-bound report](solution-validation-results.json) and compressed log;
the inspector refactor corrects and revalidates the path assertion separately.

The new published-storage smoke exposed a missing budget-policy copy in the split
execution header. `591e1736` fixes it and adds paired encrypted-store restart tests;
the corrected published binary passes policy and uncertain-accounting recovery.
The superseded test/publish attempts are retained as failures/interrupted attempts.

Rendering fixtures also needed the new policy dependency and the updated finite
counter assertion. All 37 pass after that correction. Historical signed-capture
shape, newly required run accounting and saved limits have dedicated regression
coverage. Focused reruns are recorded separately and are not added to the full-run
total.

The solution builds with warnings as errors. Server publish uses the existing
partial-trim boundary and excludes rebundling unchanged MCP tools. Compiler warnings
remain errors; only the two pre-existing EF Core Tasks notices (experimental query
precompilation and NativeAOT support in Files.Server) remain. Native AOT retains the
existing publish-local Jint dependency boundary; no new suppression is added.
Package files and binaries are local qualification artifacts, not released packages.

## Live-provider limitation

The frozen retained-campaign inspector was used read-only. Provider readiness still
fails before dispatch with: **The selected campaign configuration is missing, changed
or does not match this deployment.** The exact-decimal incident state remains
unchanged; no charges, reservations or retained uncertain work were modified.
See [the incident comparison](retained-incident.json). The frozen binary is not
evidence qualifying current source. No live-provider screenshots or paid calls are
claimed, and no configuration or replay barrier was bypassed.
