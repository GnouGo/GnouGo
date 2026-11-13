# Agent.Server startup and Amazon qualification — 2026-11-06

Startup and configuration are corrected. **Funded real-provider qualification and
Amazon generation to final review remain unachieved.** No approval or execution was
performed. This evidence distinguishes controlled browser tests, actual-host
pre-dispatch trials and paid provider responses; there were no new paid responses.

The implementation commits are `2a0f15f8` (shared selection diagnostics),
`26970050` (ordered startup/readiness), `84a9e271` (explicit funding-refusal
inspection), `12f622aa` (probe exception evidence) and `c73009ba` (existing
zero-attempt retention for financial incidents). User-staged solution/`.vscode`
changes were preserved and excluded from these commits. No merge/release occurred.

## Demonstrated failures and corrections

The latest original Amazon attempt was session
`11386753f6ce4c4eb456deb20376e057`, launched by the Benchmark entry assembly
hosting the actual `GnOuGoAgentWebHost` in process. Its command, directory/source,
loaded assembly hashes and earliest exception are bound in
[the original failure evidence](original-failure.json) and
[the earlier attempt manifest](../amazon-live-admission-2026-11-06/attempt.json).
Missing generated assets caused `Microsoft.JSInterop.JSException` at
`PlanningPage.OnAfterRenderAsync`: `GnOuGo.Agent.markdown.enhance` was unavailable.
Financial admission later refused its first request. **Zero provider HTTP attempts;
discovery was never reached.** This was not `DISCOVERY_INCOMPLETE`.

Blazor autostart is now disabled. Startup waits for the versioned Vite module,
verifies helper registration and stylesheet loading, then starts Blazor. A failed
load shows a visible error. The PlanningPage interop exception is not suppressed.
Delayed-module tests also assert that the circuit has not started while the module
is pending. Successful startup survives reload. The live harness checks delivered
JavaScript/CSS and an interactive circuit before creating any planning session.
Ordinary Server API planning remains independent of browser readiness.

Selection and `inspect-deployment` now share the original five read-only predicates:
record existence, exact material hash, configuration equality, host fingerprint and
authorization binding. Fingerprint recipes and rejection conditions are unchanged.
[Current amendment `75abc1…`](configuration-current.json) passed all five checks
with `OpenAi / gpt-6.1-sol-2026-09-29`; no amendment was registered.
[Today's comparison with `9bcafa…`](configuration-old-current-comparison.json)
fails configuration equality and host fingerprint. Its only visible field
differences are missing billing defaults `standard`, `false`, `30m`, sourced from
code defaults. Provider/model/endpoint match. KeyVault supplies the current model,
endpoint and foreground protocol; persisted user metadata overrides remain applied.
The old `ed9a3a3e` binary is unavailable: today's comparison does not establish
which predicate that old binary originally failed. Missing historical options remain
unavailable; credentials and endpoint values are withheld.

Actual-host readiness then passed. The first two fresh probes stopped at the
campaign uncertainty guard, not the provider. The
[second probe's exact stack](probe-replay-barrier.json) locates
`BenchmarkCampaign.CallAsync`, line 481. The
[owned pending-request audit](zero-attempt-audit.json) identified exactly one
unclosed request, from `113867…`, with a complete empty HTTP journal and an explicit
first-attempt financial refusal. The existing `unstarted-planning-request` retention
path now recognizes that bound financial evidence. It checks request, ceiling,
currency, review/external reserves, transport, attempts and terminal ownership;
conflicting evidence remains ineligible. Interrupted publication and substituted
fields have regressions. The [preview](retention-preview.json) and
[applied immutable closure](retention-applied.json) bind unchanged historical
records. No money was released; old continuation/replay is forbidden.

The shared inspector now distinguishes newly recorded request-bound financial
refusals before dispatch from uncertain missing completions. It does not infer
historical dispatch from receipt absence or alter financial/replay authority.

## Fresh funding result

[The final fresh probe](probe-funding-refusal.json), source `c73009ba`, passed
frontend readiness and reached the unchanged campaign financial guard. It used the
configured model, endpoint, foreground client and nonce-based structured schema,
with 1,024 output tokens. Its first exception came from
`BenchmarkHttpJournal.SaveAsync`, before the first physical attempt was persisted
or sent. **Zero HTTP attempts, no durable completion receipt.** Logical request
reservation alone is not evidence of provider dispatch.

| Captured amount | EUR |
| --- | ---: |
| Unchanged global ceiling | 740.0301038327063045424449473 |
| Known campaign tariff estimate | 475.28093332377412455040481684 |
| Existing campaign reservations | 222.7236032944974286315167358 |
| Campaign exposure | 698.00453661827155318192155264 |
| Headroom before external sessions | 42.02556721443475136052339466 |
| Provisional external hold, rounded upward for admission | 733.8902769679137437881016912 |
| Fresh probe total required exposure | 1434.6359532230043326619455983 |
| Admission shortfall | 694.6058493902980281195006510 |

The external hold retains original nominal estimates for unpriced consumption,
pending requests, local-01 and the additional 1.9655709807433945364979847738 EUR
application reservation from the blocked Amazon attempt. It is **not a provider
invoice or a newly verified charge**. Fresh captured-tariff reads for
[9fce…](external-session-9fce.json) and [1a54…](external-session-1a54.json) still
find 325 completed receipts without a captured cache-write tariff and two unresolved
requests. Neither history nor tariffs were repriced. The second read overlapped
appended trial reports, so its general campaign evidence hash changed; financial
totals did not. **Verified global available funding remains unavailable.**

After this fresh refusal, no further probe or Amazon dispatch was attempted. A new
Amazon planning session was not created: the required preceding funded real
structured-output response did not succeed. Amazon final review, a real exchange
screenshot and provider convergence must remain unachieved. The retained probe
refusal is not replayed. No Unlimited setting, ceiling increase, allocation reduction,
reservation release or manual plan edit was used.

## Validation and evidence

Controlled browser screenshots: [missing JavaScript](controlled-browser/missing_script/startup.png),
[missing CSS](controlled-browser/missing_css/startup.png),
[successful delayed initialization/reload](controlled-browser/delayed/startup.png),
[normal initialization](controlled-browser/normal/startup.png). They are actual
Agent.Server fixtures with controlled assets and zero inference, not live-provider
exchange evidence. The same browser readiness observer passed in the real configured
host. No second inspector, trace store, planning protocol or catalog was introduced.

Final-source validation (`c73009ba`):

| Check | Result |
| --- | --- |
| Full solution Release build | Passed, zero warnings/errors |
| Planner suite | 2,763 passed, zero failures/skips |
| Integration suite | 116 passed, zero failures/skips |
| Focused Server startup/configuration/retention/inspection suites | 307 passed, zero failures/skips |
| ClientApp production build and actual-host browser checks | Passed |
| Planner package, native CLI publication and exact diagnostic audit | Passed; only the two already audited EF Core experimental notices |
| Published CLI and Server encrypted-storage smoke checks | Passed |
| Broad solution tests, compiled at `84a9e271` | Interrupted during Server suite; 10 completed assemblies, 2,432 passed and 7 Windows-only skips; no final solution pass |

The broad test process did not survive the turn interruption. Its partial result is
retained explicitly; it is not combined with overlapping focused runs. Windows and
the complete PowerShell publish matrix were unavailable on this macOS host. The
Server uses its existing managed partial-trim release profile; no whole-Server
Native AOT success is claimed.

Validation commands, source/binary/log hashes, results and limitations are retained
in [validation.json](validation.json). Monetary decimals in the public API/log projections are exact
strings; [original and public hashes](source-evidence-hashes.json) identify each
projection. The existing 7,936-pass `2a83c9c0` baseline remains separately documented
and is not relabelled as this change's result.
